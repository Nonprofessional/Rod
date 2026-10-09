//! The reference plugin module (architecture.md Sec 5.4): `recon.hostenum`
//! against the SDK. It reports the host an implant landed on -- name,
//! platform, architecture, the login context -- in the recon output
//! grammar the engagement's topology view parses (one JSON finding per
//! line, extending/tradecraft.md), and that is all it does: the worked
//! example an operator's own modules copy, not tradecraft itself.
//!
//! Build for the target's own platform (`cargo build --release`), deliver
//! the cdylib's bytes as a `module.load hostenum` task's content, then
//! task `recon.hostenum` like any compiled verb.

use std::collections::BTreeMap;

use rod_plugin_sdk::{rod_plugin, Plugin, Verb};

pub struct HostEnum;

impl Plugin for HostEnum {
    fn name(&self) -> &'static str {
        "hostenum"
    }

    fn verbs(&self) -> Vec<Verb> {
        vec![Verb::new("recon.hostenum", hostenum)]
    }
}

fn hostenum(_arguments: &str) -> Result<String, String> {
    // JSON by hand over a map: the finding grammar is flat, and a module
    // owes the loader no serde dependency for six fields.
    let mut finding: BTreeMap<&str, String> = BTreeMap::new();
    finding.insert("host", hostname());
    finding.insert("os", std::env::consts::OS.into());
    finding.insert("arch", std::env::consts::ARCH.into());
    if let Ok(user) = std::env::var("USER").or_else(|_| std::env::var("USERNAME")) {
        finding.insert("user", user);
    }
    if let Ok(path) = std::env::var("PATH") {
        // The shell's search path names the tooling the host actually
        // runs -- a cheap fingerprint the sweep grammar carries free.
        finding.insert("path", path);
    }
    let fields = finding
        .iter()
        .map(|(key, value)| format!("\"{key}\":{}", quote(value)))
        .collect::<Vec<_>>()
        .join(",");
    Ok(format!("{{{fields}}}"))
}

/// The hostname without libc's gethostname: /etc/hostname on Unix (the
/// file every mainstream distribution writes), the COMPUTERNAME variable
/// on Windows. Both are the documented platform sources, and neither
/// costs the module an FFI surface.
fn hostname() -> String {
    if cfg!(windows) {
        return std::env::var("COMPUTERNAME").unwrap_or_else(|_| "unknown".into());
    }
    std::fs::read_to_string("/etc/hostname")
        .map(|text| text.trim().to_string())
        .ok()
        .filter(|text| !text.is_empty())
        .or_else(|| std::env::var("HOSTNAME").ok())
        .unwrap_or_else(|| "unknown".into())
}

/// JSON string quoting for the finding's values: the two escapes the
/// grammar needs (quote, backslash), control characters dropped rather
/// than escaped -- a finding field is a name or a path, not arbitrary
/// binary.
fn quote(value: &str) -> String {
    let mut out = String::with_capacity(value.len() + 2);
    out.push('"');
    for character in value.chars() {
        match character {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            c if c.is_control() => {}
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

rod_plugin!(HostEnum);

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_finding_is_one_json_line_in_the_sweep_grammar() {
        let output = hostenum("").expect("the sweep runs");
        assert_eq!(output.lines().count(), 1, "{output}");
        // The three topology keys the view reads are always present.
        for key in ["\"host\":", "\"os\":", "\"arch\":"] {
            assert!(output.contains(key), "{key} missing: {output}");
        }
        // Values ride quoted; a stray quote cannot break the line.
        assert!(output.starts_with('{') && output.ends_with('}'), "{output}");
    }

    #[test]
    fn quoting_escapes_the_two_characters_that_break_json() {
        assert_eq!(quote("a\"b\\c"), "\"a\\\"b\\\\c\"");
        assert_eq!(quote("ctrl\u{1}cut"), "\"ctrlcut\"");
    }
}
