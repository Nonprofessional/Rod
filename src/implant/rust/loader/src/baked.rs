// The checked-in stub: compiles empty so the loader tree builds without a
// bake; the build unit overwrites it with the per-build fetch constants in
// its staging copy (the same generated-source mechanism the implant's
// PROFILE bake uses). The generated shape is plain const arrays and strings
// -- the dial the loader owns, the credential it presents, and the R1 key
// pair the sealed payload must open under.
//
// The stub's shape is deliberately inert: a loopback dial nobody serves, a
// blank token every fetch route refuses, and a zero key no R1 body opens.
// Running an unbaked loader fails closed at the first exchange.

/// The dial's address shape: 0 IPv4, 1 IPv6, 2 a name to resolve.
pub const HOST_KIND: u8 = 0;

/// The front's literal IPv4 address (HOST_KIND 0).
pub const HOST_V4: [u8; 4] = [127, 0, 0, 1];

/// The front's literal IPv6 address (HOST_KIND 1).
pub const HOST_V6: [u8; 16] = [0; 16];

/// The front's name to resolve (HOST_KIND 2) -- a minimal DNS A/AAAA
/// query, answered by RESOLVER when baked, else the system's
/// resolv.conf nameservers.
pub const HOST_NAME: &str = "";

/// The resolver a HOST_NAME dial queries, as a literal IPv4. Empty: the
/// system's /etc/resolv.conf nameservers, in order.
pub const RESOLVER: &str = "";

/// The front's TCP port.
pub const PORT: u16 = 1;

/// The loader payload fetch route, including the loader's own artifact id.
pub const PATH: &str = "/implants/loaders/00000000000000000000000000000000/payload";

/// The deployment credential presented as the X-Deploy-Token header.
pub const TOKEN: &str = "";

/// The delivery seal's key id, the R1 body's plaintext prefix.
pub const KEY_ID: [u8; 16] = [0; 16];

/// The per-build AES-256 delivery seal key.
pub const KEY: [u8; 32] = [0; 32];
