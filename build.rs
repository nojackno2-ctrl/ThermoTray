// Embeds the application icon, version information and the requireAdministrator manifest into
// ThermoTray.exe only. Unit-test binaries must stay unelevated so `cargo test` can run them.
use std::path::PathBuf;

fn main() {
    println!("cargo:rerun-if-changed=assets/ThermoTray.rc");
    println!("cargo:rerun-if-changed=assets/ThermoTray.ico");
    println!("cargo:rerun-if-changed=assets/app.manifest");

    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }

    let manifest_dir = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap());
    let assets = manifest_dir.join("assets").to_string_lossy().replace('\\', "/");
    let template = std::fs::read_to_string(manifest_dir.join("assets/ThermoTray.rc")).unwrap();
    let script = template
        .replace("@ASSETS@", &assets)
        .replace("@VERSION_MAJOR@", env!("CARGO_PKG_VERSION_MAJOR"))
        .replace("@VERSION_MINOR@", env!("CARGO_PKG_VERSION_MINOR"))
        .replace("@VERSION_PATCH@", env!("CARGO_PKG_VERSION_PATCH"))
        .replace("@VERSION@", env!("CARGO_PKG_VERSION"));

    let output = PathBuf::from(std::env::var("OUT_DIR").unwrap()).join("ThermoTray.rc");
    std::fs::write(&output, script).unwrap();
    embed_resource::compile_for(&output, ["ThermoTray"], embed_resource::NONE)
        .manifest_required()
        .expect("failed to compile Windows resources");
}
