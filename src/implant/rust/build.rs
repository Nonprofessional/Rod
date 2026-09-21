use std::path::PathBuf;

fn main() {
    // The proto is the single source of truth for the wire contract and is
    // referenced, not copied (the same relative reference the .NET implant's
    // csproj carries). rod.proto has no imports, so only its directory is on
    // the include path.
    let manifest = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap());
    let proto = manifest.join("../../teamserver/Rod.Protocol/protos/rod.proto");
    println!("cargo:rerun-if-changed={}", proto.display());
    prost_build::Config::new()
        .compile_protos(
            std::slice::from_ref(&proto),
            &[proto.parent().unwrap().to_path_buf()],
        )
        .expect("rod.proto compiles");
}
