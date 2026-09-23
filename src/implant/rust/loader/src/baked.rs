// The checked-in stub: compiles empty so the loader tree builds without a
// bake; the build unit overwrites it with the per-build fetch constants in
// its staging copy (the same generated-source mechanism the implant's
// PROFILE bake uses). The generated shape is plain const arrays and strings
// -- the dial the loader owns, the credential it presents, and the R1 key
// pair the sealed stage must open under.
//
// The stub's shape is deliberately inert: a loopback dial nobody serves, a
// blank token every fetch route refuses, and a zero key no R1 body opens.
// Running an unbaked loader fails closed at the first exchange.

/// The front's literal IPv4 address the loader dials (cleartext HTTP).
pub const HOST: [u8; 4] = [127, 0, 0, 1];

/// The front's TCP port.
pub const PORT: u16 = 1;

/// The stage fetch route, including the loader's own artifact id.
pub const PATH: &str = "/implants/stages/00000000000000000000000000000000";

/// The deployment credential presented as the X-Deploy-Token header.
pub const TOKEN: &str = "";

/// The stage seal's key id, the R1 body's plaintext prefix.
pub const KEY_ID: [u8; 16] = [0; 16];

/// The per-build AES-256 stage seal key.
pub const KEY: [u8; 32] = [0; 32];
