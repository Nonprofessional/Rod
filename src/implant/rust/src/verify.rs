use crate::trust::{self, Certificate};
use crate::wire::TaskRequest;

// The implant's independent side of the command-signing contract (rod.proto,
// TaskRequest): RSASSA-PSS over SHA-256 with the tasking CA's RSA key, over
// the canonical length-prefixed tuple, plus the replay-nonce floor. The
// canonical encoding is built here, not borrowed from the proto runtime, so
// every implant language verifies the same way.

pub enum Verdict {
    Accepted,
    RejectedSignature,
    RejectedReplay,
    RejectedNoNonce,
}

#[derive(Default)]
pub struct NonceTracker {
    pub negotiated: bool,
    highest: Option<u64>,
}

impl NonceTracker {
    pub fn is_replay(&self, nonce: u64) -> bool {
        self.highest.map(|floor| nonce <= floor).unwrap_or(false)
    }

    pub fn observed(&mut self, nonce: u64) {
        self.highest = Some(self.highest.map_or(nonce, |floor| floor.max(nonce)));
    }
}

pub fn verify(
    implant_id: &str,
    task: &TaskRequest,
    cas: &[Certificate],
    nonces: &mut NonceTracker,
) -> Verdict {
    if task.signature.is_empty() {
        return Verdict::RejectedSignature;
    }
    let canonical = match task.task_nonce {
        Some(nonce) => canonical_bytes(
            implant_id,
            &task.task_id,
            &task.verb,
            &task.arguments,
            Some(nonce),
        ),
        None => canonical_bytes(implant_id, &task.task_id, &task.verb, &task.arguments, None),
    };
    for ca in cas {
        let Some(key) = trust::rsa_key_of(ca) else { continue };
        if !trust::verify_pss_sha256(&key, &canonical, &task.signature) {
            continue;
        }
        return match task.task_nonce {
            Some(nonce) => {
                if nonces.is_replay(nonce) {
                    Verdict::RejectedReplay
                } else {
                    nonces.observed(nonce);
                    Verdict::Accepted
                }
            }
            None => {
                if nonces.negotiated {
                    Verdict::RejectedNoNonce
                } else {
                    Verdict::Accepted
                }
            }
        };
    }
    Verdict::RejectedSignature
}

/// The canonical signed encoding: for each tuple element, the little-endian
/// u32 UTF-8 byte length followed by the bytes; the nonce, when present, is
/// the fifth element as its unsigned decimal string.
pub fn canonical_bytes(
    implant_id: &str,
    task_id: &str,
    verb: &str,
    arguments: &str,
    nonce: Option<u64>,
) -> Vec<u8> {
    let mut buffer = Vec::new();
    let mut push = |value: &str| {
        buffer.extend_from_slice(&(value.len() as u32).to_le_bytes());
        buffer.extend_from_slice(value.as_bytes());
    };
    push(implant_id);
    push(task_id);
    push(verb);
    push(arguments);
    if let Some(nonce) = nonce {
        push(&nonce.to_string());
    }
    buffer
}
