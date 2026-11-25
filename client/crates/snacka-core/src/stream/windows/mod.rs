//! Windows game-streaming backend (spike, see #49 and docs/handoff/WINDOWS.md):
//! D3D11 devices, a GPU synthetic frame source, NVENC encode and Media Foundation decode.

pub mod d3d11;
pub mod nvenc;
pub mod source;

/// Errors from the Windows media backend, with the call that failed.
#[derive(Debug)]
pub struct Error(pub String);

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for Error {}

impl From<windows::core::Error> for Error {
    fn from(e: windows::core::Error) -> Error {
        Error(format!("{e}"))
    }
}

impl From<Error> for std::io::Error {
    fn from(e: Error) -> std::io::Error {
        std::io::Error::other(e.0)
    }
}

pub type Result<T> = std::result::Result<T, Error>;

/// Adds context to a failing Windows call: `ctx(device.CreateTexture2D(..), "CreateTexture2D")?`.
pub(crate) fn ctx<T>(r: windows::core::Result<T>, what: &str) -> Result<T> {
    r.map_err(|e| Error(format!("{what}: {e}")))
}
