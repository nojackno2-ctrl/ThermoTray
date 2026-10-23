//! ThermoTray：Windows 系統匣 CPU/GPU 溫度與使用率監測。

pub mod app;
pub mod instance;
pub mod localizer;
pub mod model;
pub mod reading;
pub mod sensors;
pub mod settings;
pub mod startup;
pub mod tray;
pub mod ui;
pub mod win;

pub use app::run;
