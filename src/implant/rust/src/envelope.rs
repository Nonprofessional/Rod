use aes_gcm::aead::{Aead, KeyInit, Payload};
use aes_gcm::Aes256Gcm;
use base64::Engine;
use rand::RngCore;

// The sealed-body shapes (the teamserver's AesGcmEnvelope contract,
// extending/implants.md): base64 of
// b"R1" || keyId(16) || nonce(12) || ciphertext || tag(16), AES-256-GCM under
// the per-artifact key with a purpose tag binding each direction -- the byte
// layout the sealed-body contract pins (extending/implants.md).

pub const ENROLL_AAD: &str = "rod-envelope-v1";
pub const CONTACT_REQUEST_AAD: &str = "rod-contact-v1";
pub const CONTACT_RESPONSE_AAD: &str = "rod-contact-response-v1";
/// The socket family's enroll exchange rides its own purpose tags (the same
/// key, a different binding): the frame grammar carries the web route's JSON
/// body, so one exchange's ciphertext must not replay as the other's.
pub const SOCKET_ENROLL_REQUEST_AAD: &str = "rod-enroll-v1";
pub const SOCKET_ENROLL_RESPONSE_AAD: &str = "rod-enroll-response-v1";
/// The DNS carriage's purpose tags: one per direction the datagram wire
/// carries, so no purpose's ciphertext reflects down the resolver chain as
/// another's.
pub const DNS_POLL_AAD: &str = "rod-dns-poll-v1";
pub const DNS_RESULT_AAD: &str = "rod-dns-result-v1";
pub const DNS_CHANNEL_AAD: &str = "rod-dns-channel-v1";

/// The baked envelope key split into its halves: standard base64 of
/// keyId(16) || key(32); None when absent or malformed -- a bad bake falls
/// back to the plaintext frame rather than contacting undecodably.
pub fn parse_baked_key(baked: &str) -> Option<([u8; 16], [u8; 32])> {
    if baked.is_empty() {
        return None;
    }
    let packed = base64::engine::general_purpose::STANDARD
        .decode(baked.trim())
        .ok()?;
    if packed.len() != 16 + 32 {
        return None;
    }
    let mut key_id = [0u8; 16];
    key_id.copy_from_slice(&packed[..16]);
    let mut key = [0u8; 32];
    key.copy_from_slice(&packed[16..]);
    Some((key_id, key))
}

fn seal_body(plaintext: &[u8], key_id: &[u8; 16], key: &[u8; 32], aad: &str) -> Vec<u8> {
    let mut nonce = [0u8; 12];
    rand::thread_rng().fill_bytes(&mut nonce);
    let cipher = Aes256Gcm::new_from_slice(key).expect("32-byte key");
    let sealed = cipher
        .encrypt(
            &nonce.into(),
            Payload {
                msg: plaintext,
                aad: aad.as_bytes(),
            },
        )
        .expect("aes-gcm encrypt");
    let mut body = Vec::with_capacity(2 + 16 + 12 + sealed.len());
    body.extend_from_slice(b"R1");
    body.extend_from_slice(key_id);
    body.extend_from_slice(&nonce);
    body.extend_from_slice(&sealed);
    body
}

/// The sealed contact wire shape: the R1 body base64-encoded as text, the
/// body an opaque string rather than structured binary.
pub fn seal_contact_body(
    plaintext: &[u8],
    key_id: &[u8; 16],
    key: &[u8; 32],
    aad: &str,
) -> Vec<u8> {
    base64::engine::general_purpose::STANDARD
        .encode(seal_body(plaintext, key_id, key, aad))
        .into_bytes()
}

/// The raw R1 body (no base64): the DNS carriage's TXT payloads and
/// upstream chunks carry the sealed shape as raw bytes inside their base32
/// labels.
pub fn seal_raw_body(plaintext: &[u8], key_id: &[u8; 16], key: &[u8; 32], aad: &str) -> Vec<u8> {
    seal_body(plaintext, key_id, key, aad)
}

/// Opens what [`seal_raw_body`] sealed.
pub fn try_open_raw_body(
    body: &[u8],
    key_id: &[u8; 16],
    key: &[u8; 32],
    aad: &str,
) -> Option<Vec<u8>> {
    try_open_body(body, key_id, key, aad)
}

/// Opens what [`seal_contact_body`] sealed: None on any mismatch (wrong key,
/// tampered bytes, foreign shape) -- the caller drops the whole cycle rather
/// than acting on a partial read.
pub fn try_open_contact_body(
    body: &[u8],
    key_id: &[u8; 16],
    key: &[u8; 32],
    aad: &str,
) -> Option<Vec<u8>> {
    let text = std::str::from_utf8(body).ok()?;
    let packed = base64::engine::general_purpose::STANDARD
        .decode(text.trim())
        .ok()?;
    try_open_body(&packed, key_id, key, aad)
}

fn try_open_body(body: &[u8], key_id: &[u8; 16], key: &[u8; 32], aad: &str) -> Option<Vec<u8>> {
    if body.len() < 2 + 16 + 12 + 16 {
        return None;
    }
    if &body[..2] != b"R1" || &body[2..18] != key_id {
        return None;
    }
    let nonce = &body[18..30];
    let sealed = &body[30..];
    let cipher = Aes256Gcm::new_from_slice(key).expect("32-byte key");
    cipher
        .decrypt(
            nonce.into(),
            Payload {
                msg: sealed,
                aad: aad.as_bytes(),
            },
        )
        .ok()
}
