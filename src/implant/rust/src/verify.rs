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
        let Some(key) = trust::rsa_key_of(ca) else {
            continue;
        };
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::trust::Certificate;
    use rsa::pkcs8::EncodePublicKey;
    use rsa::signature::{RandomizedSigner, SignatureEncoding};

    /// A throwaway tasking CA: the same RSA/PSS scheme the server signs
    /// with, minted per test so no key material ever lands in the tree.
    fn tasking_ca() -> (rsa::RsaPrivateKey, Certificate) {
        let mut rng = rand::thread_rng();
        let private = rsa::RsaPrivateKey::new(&mut rng, 2048).expect("rsa keygen");
        let spki = private
            .to_public_key()
            .to_public_key_der()
            .expect("spki der");
        (
            private,
            Certificate {
                raw: Vec::new(),
                spki: spki.as_bytes().to_vec(),
            },
        )
    }

    fn signed_task(
        private: &rsa::RsaPrivateKey,
        implant_id: &str,
        nonce: Option<u64>,
    ) -> TaskRequest {
        let mut task = TaskRequest {
            task_id: "t-1".into(),
            verb: "shell.exec".into(),
            arguments: "id".into(),
            task_nonce: nonce,
            ..Default::default()
        };
        let canonical = canonical_bytes(
            implant_id,
            &task.task_id,
            &task.verb,
            &task.arguments,
            nonce,
        );
        let signing = rsa::pss::SigningKey::<sha2::Sha256>::new(private.clone());
        let mut rng = rand::thread_rng();
        task.signature = signing.sign_with_rng(&mut rng, &canonical).to_vec();
        task
    }

    #[test]
    fn canonical_bytes_pin_the_length_prefixed_tuple() {
        let bytes = canonical_bytes("implant", "t-1", "shell.exec", "id", Some(7));
        let mut expected = Vec::new();
        for part in ["implant", "t-1", "shell.exec", "id", "7"] {
            expected.extend_from_slice(&(part.len() as u32).to_le_bytes());
            expected.extend_from_slice(part.as_bytes());
        }
        assert_eq!(bytes, expected);
        // The nonce is the fifth element only when present.
        assert_ne!(
            canonical_bytes("i", "t", "v", "a", Some(1)),
            canonical_bytes("i", "t", "v", "a", None)
        );
    }

    #[test]
    fn the_nonce_floor_tracks_the_high_water_mark() {
        let mut nonces = NonceTracker::default();
        assert!(!nonces.is_replay(1));
        nonces.observed(5);
        assert!(nonces.is_replay(5));
        assert!(nonces.is_replay(4));
        assert!(!nonces.is_replay(6));
        // An out-of-order arrival never lowers the floor.
        nonces.observed(3);
        assert!(nonces.is_replay(4));
    }

    #[test]
    fn signed_tasking_is_accepted_exactly_once() {
        let (private, ca) = tasking_ca();
        let task = signed_task(&private, "implant-1", Some(10));
        let mut nonces = NonceTracker {
            negotiated: true,
            ..Default::default()
        };
        assert!(matches!(
            verify("implant-1", &task, std::slice::from_ref(&ca), &mut nonces),
            Verdict::Accepted
        ));
        // The same nonce again is a replay whatever the signature says.
        assert!(matches!(
            verify("implant-1", &task, &[ca], &mut nonces),
            Verdict::RejectedReplay
        ));
    }

    #[test]
    fn foreign_signers_and_targets_are_refused() {
        let (private, ca) = tasking_ca();
        let (other, _) = tasking_ca();
        let mut nonces = NonceTracker::default();
        // Signed by a key the implant does not hold.
        let foreign = signed_task(&other, "implant-1", Some(10));
        assert!(matches!(
            verify("implant-1", &foreign, std::slice::from_ref(&ca), &mut nonces),
            Verdict::RejectedSignature
        ));
        // Signed by the held CA but over another implant's tuple: captured
        // tasking must not replay cross-implant.
        let retargeted = signed_task(&private, "implant-2", Some(10));
        assert!(matches!(
            verify("implant-1", &retargeted, std::slice::from_ref(&ca), &mut nonces),
            Verdict::RejectedSignature
        ));
        // An empty signature is unsigned tasking.
        let mut unsigned = signed_task(&private, "implant-1", Some(10));
        unsigned.signature.clear();
        assert!(matches!(
            verify("implant-1", &unsigned, &[ca], &mut nonces),
            Verdict::RejectedSignature
        ));
    }

    #[test]
    fn nonceless_tasking_follows_the_negotiated_arm() {
        let (private, ca) = tasking_ca();
        let task = signed_task(&private, "implant-1", None);
        let mut unnegotiated = NonceTracker::default();
        assert!(matches!(
            verify("implant-1", &task, std::slice::from_ref(&ca), &mut unnegotiated),
            Verdict::Accepted
        ));
        let mut negotiated = NonceTracker {
            negotiated: true,
            ..Default::default()
        };
        assert!(matches!(
            verify("implant-1", &task, &[ca], &mut negotiated),
            Verdict::RejectedNoNonce
        ));
    }

    #[test]
    fn cas_without_rsa_keys_are_skipped_not_fatal() {
        let (private, ca) = tasking_ca();
        let garbage = Certificate {
            raw: Vec::new(),
            spki: vec![0u8; 8],
        };
        let task = signed_task(&private, "implant-1", Some(1));
        let mut nonces = NonceTracker::default();
        assert!(matches!(
            verify("implant-1", &task, &[garbage, ca], &mut nonces),
            Verdict::Accepted
        ));
    }
}
