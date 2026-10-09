//! The authoring surface for Rod implant-side plugin modules
//! (architecture.md Sec 5.4). A module is normal Rust code against one
//! trait: implement [`Plugin`], register [`macro@rod_plugin`] for the
//! Windows cdylib shape (the extern "C" shims the manual PE map
//! resolves), and call [`serve`] from a `main` for the Linux executable
//! shape (the stdio loop the loader execs). One source, two artifacts.
//!
//! The seam's whole contract is the task grammar's own -- a verb name and
//! an opaque argument string in, an outcome and an output string back --
//! so a module verb reads on the operator console exactly like a compiled
//! one. The domain is the stateless long tail; a plugin cannot own a live
//! channel or a carriage, and the loader refuses verbs the artifact
//! already compiles.
//!
//! # Writing a module
//!
//! (The example is `ignore`-marked only because this host's rustdoc pairs
//! with a mismatched rustc build; `tests/shims.rs` compiles and runs the
//! same code.)
//!
//! ```ignore
//! use rod_plugin_sdk::{rod_plugin, Plugin, Verb};
//!
//! pub struct HostEnum;
//!
//! impl Plugin for HostEnum {
//!     fn name(&self) -> &'static str {
//!         "hostenum"
//!     }
//!     fn verbs(&self) -> Vec<Verb> {
//!         vec![Verb::new("recon.hostenum", hostenum)]
//!     }
//! }
//!
//! fn hostenum(arguments: &str) -> Result<String, String> {
//!     // One JSON finding per line, the recon output grammar the topology
//!     // view parses (extending/tradecraft.md).
//!     Ok(format!(
//!         "{{\"host\":\"{}\",\"os\":\"{}\",\"arch\":\"{}\"}}",
//!         std::env::var("HOSTNAME").unwrap_or_default(),
//!         std::env::consts::OS,
//!         std::env::consts::ARCH
//!     ))
//! }
//!
//! rod_plugin!(HostEnum);
//! ```
//!
//! One module source builds both delivery shapes. The Windows cdylib:
//! `crate-type = ["cdylib"]` plus `rod_plugin!`. The Linux executable:
//! a `[[bin]]` whose `main` calls [`serve`] over the same plugin (the
//! lib target needs `crate-type = ["cdylib", "rlib"]` so the bin can
//! link it) -- build it against a musl triple for a static module that
//! runs from any artifact. Deliver the artifact's bytes as a
//! `module.load` task's content either way. Panics are fenced: the
//! Windows shim catches and reports; on Linux the process edge is the
//! fence, and an aborted module merely reads as a dead child.

/// The C-ABI version this SDK speaks. The loader refuses a module whose
/// `rod_plugin_abi` answer it does not recognize, whole and named, so an
/// ABI change is a version bump here and a matching loader, never a
/// silent mismatch.
pub const ABI_VERSION: u32 = 1;

pub mod protocol;

/// A length-delimited byte string crossing the C ABI. Not a NUL-terminated
/// C string: the task grammar's arguments and outputs are opaque bytes and
/// cannot ride one.
///
/// Allocation discipline: a `RodStr` a module returns is module memory,
/// and the host returns it through the `rod_plugin_free` export the macro
/// emits -- the host never frees module memory itself.
#[repr(C)]
pub struct RodStr {
    /// The first byte. Read-only when the host built the string (a verb's
    /// arguments); owned when the module built it (a name or output).
    pub ptr: *mut u8,
    /// The byte count.
    pub len: usize,
}

impl RodStr {
    /// The empty string, the shape a shim returns when it has nothing to
    /// say (an out-of-range index, a caught panic).
    pub fn empty() -> RodStr {
        RodStr {
            ptr: std::ptr::null_mut(),
            len: 0,
        }
    }

    /// Borrows a slice as a read-only argument string. The host builds
    /// these; a module only reads them.
    pub fn from_slice(bytes: &[u8]) -> RodStr {
        RodStr {
            ptr: bytes.as_ptr() as *mut u8,
            len: bytes.len(),
        }
    }

    /// Copies a string into freshly allocated module memory, the shape the
    /// name and output shims return and the host frees through
    /// `rod_plugin_free`.
    pub fn to_owned_str(text: &str) -> RodStr {
        let bytes: Vec<u8> = text.as_bytes().to_vec();
        RodStr::from_vec(bytes)
    }

    /// Takes ownership of a vector, keeping its allocation as the string's
    /// storage.
    pub fn from_vec(bytes: Vec<u8>) -> RodStr {
        let len = bytes.len();
        let ptr = bytes.as_ptr() as *mut u8;
        std::mem::forget(bytes);
        RodStr { ptr, len }
    }

    /// The string as a byte slice, if the pointer/length pair is valid.
    ///
    /// # Safety
    /// The caller must only read strings the pointer of which it received
    /// from the other side of the boundary and not retain the slice past
    /// the free.
    pub unsafe fn as_slice(&self) -> Option<&[u8]> {
        if self.ptr.is_null() {
            return if self.len == 0 { Some(&[][..]) } else { None };
        }
        Some(unsafe { std::slice::from_raw_parts(self.ptr, self.len) })
    }
}

/// One verb's execution half: the opaque argument string in, the outcome
/// out. `Ok` is success, `Err` carries the failure the operator reads on
/// the task -- the same shape the compiled handlers speak.
pub type VerbFn = fn(&str) -> Result<String, String>;

/// A verb registration: its namespaced name and its handler.
pub struct Verb {
    /// The verb, `namespace.action` -- the grammar every Rod verb
    /// follows. The loader refuses a name without a namespace.
    pub name: &'static str,
    /// The handler the dispatcher calls.
    pub run: VerbFn,
}

impl Verb {
    pub fn new(name: &'static str, run: VerbFn) -> Verb {
        Verb { name, run }
    }
}

/// A plugin module: a name and the verbs it brings. One implementation
/// per module, handed to [`macro@rod_plugin`] (the Windows shape) and
/// [`serve`] (the Linux shape).
pub trait Plugin {
    /// The module's self-declared name. Short and lowercase -- it is the
    /// handle `module.list` reports and `module.unload` retracts.
    fn name(&self) -> &'static str;

    /// The verbs this module registers. Collected once, at init.
    fn verbs(&self) -> Vec<Verb>;
}

/// The Linux module shape's entry point (architecture.md Sec 5.4): answer
/// the loader's stdio protocol until stdin ends. A module's `main` is
/// exactly `rod_plugin_sdk::serve(MyPlugin);` -- the loop holds the verb
/// table, fences panics (a panicking handler answers a `PANICKED`
/// status rather than dying mid-frame), and keeps the process
/// exchange-per-request so the host can exec a fresh process per verb
/// dispatch.
pub fn serve<P: Plugin>(plugin: P) {
    let verbs = plugin.verbs();
    let stdin = std::io::stdin();
    let stdout = std::io::stdout();
    let mut input = stdin.lock();
    let mut output = stdout.lock();
    while let Some(request) = protocol::read_request(&mut input) {
        let response = answer(&plugin, &verbs, request);
        if protocol::write_response(&mut output, response.0, &response.1).is_err() {
            return; // the host went away; nothing to serve
        }
    }
}

// One request to one (status, payload) pair. Everything the loader can
// ask -- liveness, the name, the table, a run -- answers through here, so
// the loop above stays transport and this stays semantics.
fn answer<P: Plugin>(plugin: &P, verbs: &[Verb], request: protocol::Request) -> (u8, Vec<u8>) {
    use protocol::{op, status};
    match request.op {
        op::PING => (status::OK, Vec::new()),
        op::NAME => (status::OK, plugin.name().as_bytes().to_vec()),
        op::COUNT => (status::OK, (verbs.len() as u32).to_le_bytes().to_vec()),
        op::VERB_NAME => match verbs.get(request.arg as usize) {
            Some(verb) => (status::OK, verb.name.as_bytes().to_vec()),
            None => (status::BAD_REQUEST, Vec::new()),
        },
        op::RUN => match verbs.get(request.arg as usize) {
            None => (status::BAD_REQUEST, Vec::new()),
            Some(verb) => {
                let run = verb.run;
                let name = verb.name;
                let arguments = String::from_utf8_lossy(&request.payload).into_owned();
                // The panic fence: a handler must not die mid-frame, so the
                // loop answers a PANICKED status instead. An abort build
                // turns this off -- the process edge is the backstop, the
                // host reads a dead child and fails the task.
                match std::panic::catch_unwind(move || run(&arguments)) {
                    Ok(Ok(output)) => (status::OK, output.into_bytes()),
                    Ok(Err(cause)) => (status::FAILED, cause.into_bytes()),
                    Err(panic) => {
                        let message = panic
                            .downcast_ref::<&str>()
                            .map(|text| text.to_string())
                            .or_else(|| panic.downcast_ref::<String>().cloned())
                            .unwrap_or_else(|| "module handler panicked".to_string());
                        (status::PANICKED, format!("{name}: {message}").into_bytes())
                    }
                }
            }
        },
        _ => (status::BAD_REQUEST, Vec::new()),
    }
}

/// Emits the extern "C" shim a Rod loader resolves (architecture.md Sec
/// 5.4): `rod_plugin_abi`, `rod_plugin_name`, `rod_plugin_init`,
/// `rod_plugin_verb_name`, `rod_plugin_run`, and `rod_plugin_free`.
/// Call once per cdylib, at the crate root, naming the [`Plugin`]
/// implementation.
///
/// The verb table is built on init and cached for the process's life --
/// plugin verbs are load-time registrations, not per-call discoveries.
#[macro_export]
macro_rules! rod_plugin {
    ($plugin:ident) => {
        fn __rod_plugin_table() -> Vec<$crate::Verb> {
            let plugin = $plugin;
            plugin.verbs()
        }

        #[no_mangle]
        pub unsafe extern "C" fn rod_plugin_abi() -> u32 {
            $crate::ABI_VERSION
        }

        #[no_mangle]
        pub unsafe extern "C" fn rod_plugin_name() -> $crate::RodStr {
            let plugin = $plugin;
            $crate::RodStr::to_owned_str(plugin.name())
        }

        #[no_mangle]
        pub unsafe extern "C" fn rod_plugin_init() -> u32 {
            let table = __rod_plugin_table();
            let count = table.len() as u32;
            __rod_verbs(Some(table));
            count
        }

        #[no_mangle]
        pub unsafe extern "C" fn rod_plugin_verb_name(index: u32) -> $crate::RodStr {
            let table = __rod_verbs(None);
            match table.get(index as usize) {
                Some(verb) => $crate::RodStr::to_owned_str(verb.name),
                None => $crate::RodStr::empty(),
            }
        }

        #[no_mangle]
        pub unsafe extern "C" fn rod_plugin_run(
            index: u32,
            arguments: $crate::RodStr,
            out: &mut $crate::RodStr,
        ) -> i32 {
            let table = __rod_verbs(None);
            let Some(verb) = table.get(index as usize) else {
                *out = $crate::RodStr::empty();
                return -1;
            };
            let run = verb.run;
            let name = verb.name;
            // Panic fence: a module handler must never unwind into the
            // host, so the shim catches and reports. The unwind shape is
            // part of the contract -- building with panic = "abort" turns
            // this fence off.
            match std::panic::catch_unwind(move || {
                let text = unsafe { arguments.as_slice() }
                    .map(|bytes| String::from_utf8_lossy(bytes).into_owned())
                    .unwrap_or_default();
                run(&text)
            }) {
                Ok(Ok(output)) => {
                    *out = $crate::RodStr::to_owned_str(&output);
                    0
                }
                Ok(Err(cause)) => {
                    *out = $crate::RodStr::to_owned_str(&cause);
                    1
                }
                Err(panic) => {
                    let message = panic
                        .downcast_ref::<&str>()
                        .map(|text| text.to_string())
                        .or_else(|| panic.downcast_ref::<String>().cloned())
                        .unwrap_or_else(|| "module handler panicked".to_string());
                    *out = $crate::RodStr::to_owned_str(&format!(
                        "{name}: module handler panicked: {message}"
                    ));
                    -2
                }
            }
        }

        #[no_mangle]
        pub unsafe extern "C" fn rod_plugin_free(ptr: *mut u8, len: usize) {
            if ptr.is_null() {
                return;
            }
            // The allocation discipline's other half: a RodStr this module
            // returned was a leaked Vec, and the free rebuilds and drops
            // it in the allocator that made it.
            unsafe { drop(Vec::from_raw_parts(ptr, len, len)) };
        }

        // The process-lifetime verb table. Init fills it (Some); the
        // per-call shims read it (None). A OnceLock rather than static
        // mut: the loader may call init from any thread it owns.
        fn __rod_verbs(fill: Option<Vec<$crate::Verb>>) -> &'static [$crate::Verb] {
            use std::sync::OnceLock;
            static TABLE: OnceLock<Vec<$crate::Verb>> = OnceLock::new();
            if let Some(fill) = fill {
                let _ = TABLE.set(fill);
            }
            match TABLE.get() {
                Some(table) => table.as_slice(),
                None => &[],
            }
        }
    };
}
