// The standalone entry (architecture.md Sec 12.2): the machine is the
// library core beside this file, shared with the Android carrier crate;
// this side of it stays a plain process -- load the profile, run the
// state machine, exit with its answer.

fn main() {
    std::process::exit(rod_implant::standalone_main());
}
