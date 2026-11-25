//! Snacka client core. Owns everything latency-sensitive or OS-specific (see docs/adr/0001).

pub mod clock;
pub mod stream;

/// Human-readable description of this build of the core.
pub fn info() -> String {
    format!("snacka-core {} ({}-{})", env!("CARGO_PKG_VERSION"), std::env::consts::OS, std::env::consts::ARCH)
}
