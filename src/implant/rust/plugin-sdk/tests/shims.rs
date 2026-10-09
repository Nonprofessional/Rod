//! Drives the emitted shims in-process: the same extern "C" symbols the
//! implant's loader resolves, called directly. Pins the ABI constants, the
//! init/name/verb_name/run family, the allocation discipline (out strings
//! die through rod_plugin_free), and the panic fence.

use rod_plugin_sdk::{rod_plugin, Plugin, RodStr, Verb};

pub struct Demo;

impl Plugin for Demo {
    fn name(&self) -> &'static str {
        "demo"
    }
    fn verbs(&self) -> Vec<Verb> {
        vec![
            Verb::new("demo.echo", echo),
            Verb::new("demo.fail", fail),
            Verb::new("demo.panic", panic_handler),
        ]
    }
}

fn echo(arguments: &str) -> Result<String, String> {
    // Touch the allocator so the test exercises module-owned memory, not
    // just borrowed slices.
    let owned = format!("echo:{arguments}");
    Ok(owned + &"-".repeat(3))
}

fn fail(_arguments: &str) -> Result<String, String> {
    Err("demo.fail: refused by the handler".into())
}

fn panic_handler(_arguments: &str) -> Result<String, String> {
    panic!("handler exploded");
}

rod_plugin!(Demo);

unsafe fn read(string: RodStr) -> String {
    let bytes = string.as_slice().expect("a valid RodStr").to_vec();
    rod_plugin_free(string.ptr, string.len);
    String::from_utf8(bytes).unwrap_or_default()
}

#[test]
fn the_shim_family_speaks_the_documented_abi() {
    assert_eq!(unsafe { rod_plugin_abi() }, rod_plugin_sdk::ABI_VERSION);
    let count = unsafe { rod_plugin_init() };
    assert_eq!(count, 3);
    assert_eq!(unsafe { read(rod_plugin_name()) }, "demo");
    assert_eq!(unsafe { read(rod_plugin_verb_name(0)) }, "demo.echo");
    assert_eq!(unsafe { read(rod_plugin_verb_name(1)) }, "demo.fail");
    // An out-of-range index is the empty string, not a crash.
    assert_eq!(unsafe { read(rod_plugin_verb_name(99)) }, "");
}

#[test]
fn run_carries_strings_both_ways() {
    unsafe { rod_plugin_init() };
    let mut out = RodStr::empty();
    let status = unsafe { rod_plugin_run(0, RodStr::from_slice(b"payload"), &mut out) };
    assert_eq!(status, 0);
    assert_eq!(unsafe { read(out) }, "echo:payload---");
    // A bad index is a shim error with the empty out string.
    let mut none = RodStr::empty();
    let status = unsafe { rod_plugin_run(42, RodStr::from_slice(b"payload"), &mut none) };
    assert_eq!(status, -1);
    assert_eq!(none.len, 0);
}

#[test]
fn handler_failures_and_panics_report_through_the_boundary() {
    unsafe { rod_plugin_init() };
    let mut failed = RodStr::empty();
    let status = unsafe { rod_plugin_run(1, RodStr::from_slice(b""), &mut failed) };
    assert_eq!(status, 1);
    assert_eq!(unsafe { read(failed) }, "demo.fail: refused by the handler");
    let mut panicked = RodStr::empty();
    let status = unsafe { rod_plugin_run(2, RodStr::from_slice(b""), &mut panicked) };
    assert_eq!(status, -2);
    let report = unsafe { read(panicked) };
    assert!(report.contains("panicked"), "{report}");
    assert!(report.contains("demo.panic"), "{report}");
    // The fence held: the test runner is still alive and the table still
    // answers.
    assert_eq!(unsafe { read(rod_plugin_verb_name(0)) }, "demo.echo");
}
