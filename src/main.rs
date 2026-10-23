// 視窗應用程式，不開啟主控台。
#![windows_subsystem = "windows"]

fn main() {
    std::process::exit(thermotray::run());
}
