// The checked-in stub: compiles empty so the implant runs from environment
// variables during development; the Rust build unit overwrites it with the
// per-build profile in its staging copy (the same mechanism the .NET trees'
// BakedProfile uses). The generated shape is a base64url-encoded JSON object
// carrying the same language-neutral keys the .NET implant decodes.
pub const PROFILE: &str = "";
