// The checked-in stub: compiles empty so the implant runs from environment
// variables during development; the build unit overwrites it with the
// per-build profile in its staging copy (the generated-source mechanism every
// build unit uses). The generated shape is a base64url-encoded JSON object
// carrying the language-neutral keys of the profile contract.
pub const PROFILE: &str = "";
