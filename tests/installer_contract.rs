//! 驗證安裝器持續提供一致的開始功能表、升級、解除安裝與執行中偵測體驗，且版本與 Cargo.toml 同步。

use std::path::PathBuf;

fn repository_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
}

fn installer_script() -> String {
    std::fs::read_to_string(repository_root().join("installer").join("ThermoTray.iss")).expect("installer script")
}

fn lines(script: &str) -> Vec<&str> {
    script.lines().map(str::trim_end).filter(|line| !line.is_empty()).collect()
}

#[test]
fn installer_provides_consistent_upgrade_and_diagnostics_metadata() {
    let script = installer_script();
    let lines = lines(&script);
    for directive in [
        "AllowNoIcons=no",
        "DisableProgramGroupPage=yes",
        "UsePreviousAppDir=yes",
        "UsePreviousGroup=yes",
        "UsePreviousTasks=yes",
        "CreateUninstallRegKey=yes",
        "SetupLogging=yes",
        "UninstallLogging=yes",
        "CloseApplications=no",
        "RestartApplications=no",
        "AppPublisherURL={#AppURL}",
        "AppSupportURL={#AppURL}/issues",
        "AppUpdatesURL={#AppURL}/releases",
    ] {
        assert!(lines.contains(&directive), "missing {directive}");
    }
}

#[test]
fn start_menu_and_optional_shortcut_policy_is_preserved() {
    let script = installer_script();
    let lines = lines(&script);
    let single = |predicate: &dyn Fn(&str) -> bool| {
        let matches: Vec<&str> = lines.iter().copied().filter(|line| predicate(line)).collect();
        assert_eq!(matches.len(), 1, "{matches:?}");
        matches[0]
    };

    let start_menu = single(&|line| line.starts_with("Name: \"{group}\\{#AppName}\";"));
    let uninstall = single(&|line| line.contains("{cm:UninstallProgram,{#AppName}}"));
    let desktop_task = single(&|line| line.starts_with("Name: \"desktopicon\";"));

    assert!(!start_menu.contains("Tasks:"));
    assert!(!uninstall.contains("Tasks:"));
    assert!(start_menu.contains("WorkingDir: \"{app}\""));
    assert!(start_menu.contains("AppUserModelID:"));
    assert!(desktop_task.contains("Flags: unchecked"));
}

#[test]
fn the_installer_waits_on_the_mutex_the_application_creates() {
    let expected = format!("AppMutex={}", thermotray::instance::SETUP_MUTEX_NAME);
    assert!(installer_script().lines().any(|line| line.trim() == expected), "{expected}");
}

#[test]
fn the_installer_default_version_matches_cargo_toml() {
    let script = installer_script();
    let expected = format!("#define AppVersion \"{}\"", env!("CARGO_PKG_VERSION"));
    assert!(script.lines().any(|line| line.trim() == expected), "{expected}");
    assert!(script.contains(&format!("/DAppVersion={}", env!("CARGO_PKG_VERSION"))), "comment example is stale");
}

#[test]
fn the_installer_ships_the_sensor_sidecar_from_the_publish_directory() {
    let script = installer_script();
    assert!(script.contains("Source: \"..\\publish\\win-x64\\*\""));
    assert!(script.contains(thermotray::sensors::SIDECAR_FILE_NAME));
}
