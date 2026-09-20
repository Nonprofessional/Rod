use std::sync::Arc;
use std::time::Duration;

use rustls::crypto::ring as ring_provider;
use rustls::pki_types::CertificateDer;
use rustls::RootCertStore;

use crate::trust::Certificate;

// The HTTP carriage: plain http for the documented cleartext web posture
// (the sealed contact body carries the confidentiality), https with the
// teamserver CA pinned for the TLS front. Pinning rides the standard
// webpki path with the baked CAs as the only roots -- full chain and
// validity validation, no system store consulted. (The dev CA issues
// SAN-less listener leafs the webpki name check refuses; those deployments
// dial the http posture, which the sealed body makes confidential by
// construction.)

pub fn build_agent(url: &str, pinned_cas: &[Certificate], timeout_seconds: f64) -> ureq::Agent {
    let mut builder = ureq::AgentBuilder::new()
        .timeout(Duration::from_secs_f64(timeout_seconds.max(1.0)));
    if url.starts_with("https://") && !pinned_cas.is_empty() {
        let mut roots = RootCertStore::empty();
        for ca in pinned_cas {
            let _ = roots.add(CertificateDer::from(ca.raw.clone()));
        }
        let config = rustls::ClientConfig::builder_with_provider(Arc::new(
            ring_provider::default_provider(),
        ))
        .with_safe_default_protocol_versions()
        .expect("rustls protocol versions")
        .with_root_certificates(roots)
        .with_no_client_auth();
        builder = builder.tls_config(Arc::new(config));
    }
    builder.build()
}
