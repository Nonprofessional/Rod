use rsa::signature::Verifier as _;
use sha2::Sha256;
use x509_parser::prelude::FromDer;

// X.509 helpers for the two trust anchors the implant holds: the certificate
// bytes (the https root-store pin) and the RSA public keys the tasking
// verifier needs. Parsing is x509-parser's; the signature math lives in the
// same rsa/p256 crates the tasking verifier uses.

#[derive(Clone)]
pub struct Certificate {
    /// The certificate's full DER bytes (the root-store pin).
    pub raw: Vec<u8>,
    /// The SubjectPublicKeyInfo DER (the key material).
    pub spki: Vec<u8>,
}

pub fn parse_der(der: &[u8]) -> Option<Certificate> {
    let (_, cert) = x509_parser::certificate::X509Certificate::from_der(der).ok()?;
    Some(Certificate {
        raw: der.to_vec(),
        spki: cert.public_key().raw.to_vec(),
    })
}

/// Splits a PEM bundle into its member DER certificates.
pub fn parse_pem(pem: &str) -> Vec<Vec<u8>> {
    use base64::Engine;
    let mut ders = Vec::new();
    let mut accumulating = false;
    let mut body = String::new();
    for line in pem.lines() {
        let line = line.trim();
        if line.starts_with("-----BEGIN") {
            accumulating = true;
            body.clear();
        } else if line.starts_with("-----END") {
            if let Ok(der) = base64::engine::general_purpose::STANDARD.decode(&body) {
                ders.push(der);
            }
            accumulating = false;
        } else if accumulating {
            body.push_str(line);
        }
    }
    ders
}

fn rsa_of(spki: &[u8]) -> Option<rsa::RsaPublicKey> {
    use rsa::pkcs8::DecodePublicKey;
    rsa::RsaPublicKey::from_public_key_der(spki).ok()
}

/// Extracts an RSA public key from a CA certificate (the tasking signer),
/// None when the certificate carries some other key family.
pub fn rsa_key_of(cert: &Certificate) -> Option<rsa::RsaPublicKey> {
    rsa_of(&cert.spki)
}

/// Verifies an RSASSA-PSS/SHA-256 signature (the tasking CA's scheme) over
/// the canonical bytes.
pub fn verify_pss_sha256(key: &rsa::RsaPublicKey, message: &[u8], signature: &[u8]) -> bool {
    let Ok(signature) = rsa::pss::Signature::try_from(signature) else {
        return false;
    };
    rsa::pss::VerifyingKey::<Sha256>::new(key.clone())
        .verify(message, &signature)
        .is_ok()
}
