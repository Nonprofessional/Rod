use rsa::signature::Verifier as _;
use sha2::Sha256;
use x509_parser::prelude::FromDer;

// X.509 helpers for the trust anchor the implant holds: the certificate's
// SubjectPublicKeyInfo -- the RSA key material the tasking verifier needs.
// (The TLS root-store pin goes PEM to DER directly in the transport layer;
// parsing here is x509-parser's, the signature math the rsa/p256 crates the
// tasking verifier uses.)

#[derive(Clone)]
pub struct Certificate {
    /// The SubjectPublicKeyInfo DER (the key material).
    pub spki: Vec<u8>,
}

pub fn parse_der(der: &[u8]) -> Option<Certificate> {
    let (_, cert) = x509_parser::certificate::X509Certificate::from_der(der).ok()?;
    Some(Certificate {
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

#[cfg(test)]
mod tests {
    use super::*;
    use rsa::signature::{RandomizedSigner, SignatureEncoding};

    #[test]
    fn pem_bundles_split_into_their_members() {
        use base64::Engine;
        let encode = |bytes: &[u8]| base64::engine::general_purpose::STANDARD.encode(bytes);
        // The splitter is shape-driven: preamble and interstitial noise
        // are ignored, and only the guarded blocks decode in.
        let pem = format!(
            "preamble noise\n-----BEGIN CERTIFICATE-----\n{}\n-----END CERTIFICATE-----\nnoise\n-----BEGIN CERTIFICATE-----\n{}\n-----END CERTIFICATE-----\n",
            encode(&[1, 2, 3]),
            encode(&[4, 5, 6]),
        );
        assert_eq!(parse_pem(&pem), vec![vec![1, 2, 3], vec![4, 5, 6]]);
    }

    #[test]
    fn pss_signatures_verify_and_refuse_tampering() {
        let mut rng = rand::thread_rng();
        let private = rsa::RsaPrivateKey::new(&mut rng, 2048).expect("rsa keygen");
        let key = private.to_public_key();
        let signing = rsa::pss::SigningKey::<Sha256>::new(private);
        let signature = signing.sign_with_rng(&mut rng, b"canonical").to_vec();
        assert!(verify_pss_sha256(&key, b"canonical", &signature));
        assert!(!verify_pss_sha256(&key, b"tampered", &signature));
        let mut flipped = signature.clone();
        let last = flipped.len() - 1;
        flipped[last] ^= 1;
        assert!(!verify_pss_sha256(&key, b"canonical", &flipped));
        assert!(!verify_pss_sha256(&key, b"canonical", b"short"));
    }

    #[test]
    fn rsa_keys_read_from_the_certificate_spki() {
        use rsa::pkcs8::EncodePublicKey;
        let mut rng = rand::thread_rng();
        let private = rsa::RsaPrivateKey::new(&mut rng, 2048).expect("rsa keygen");
        let spki = private
            .to_public_key()
            .to_public_key_der()
            .expect("spki der");
        let cert = Certificate {
            spki: spki.as_bytes().to_vec(),
        };
        assert!(rsa_key_of(&cert).is_some());
        let garbage = Certificate { spki: vec![0u8; 8] };
        assert!(rsa_key_of(&garbage).is_none());
    }
}
