//! Monotonic nanosecond clock shared by all processes on the machine, so a sender and a
//! receiver on the same host can compare timestamps directly (same clock as stream-probe).

#[cfg(target_os = "macos")]
pub fn now_ns() -> u64 {
    extern "C" {
        fn clock_gettime_nsec_np(clock_id: u32) -> u64;
    }
    const CLOCK_UPTIME_RAW: u32 = 8;
    // SAFETY: plain libc call with a valid clock id.
    unsafe { clock_gettime_nsec_np(CLOCK_UPTIME_RAW) }
}

#[cfg(not(target_os = "macos"))]
pub fn now_ns() -> u64 {
    use std::sync::OnceLock;
    use std::time::Instant;
    static START: OnceLock<Instant> = OnceLock::new();
    START.get_or_init(Instant::now).elapsed().as_nanos() as u64
}

pub fn ms(ns: u64) -> f64 {
    ns as f64 / 1_000_000.0
}
