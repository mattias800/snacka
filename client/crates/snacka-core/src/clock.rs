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

/// QueryPerformanceCounter is system-wide on Windows, so separate processes share it.
#[cfg(windows)]
pub fn now_ns() -> u64 {
    use std::sync::OnceLock;
    use windows::Win32::System::Performance::{QueryPerformanceCounter, QueryPerformanceFrequency};
    static FREQ: OnceLock<u64> = OnceLock::new();
    let freq = *FREQ.get_or_init(|| {
        let mut f = 0i64;
        // SAFETY: valid out pointer; cannot fail on Windows XP and later.
        let _ = unsafe { QueryPerformanceFrequency(&mut f) };
        f.max(1) as u64
    });
    let mut c = 0i64;
    // SAFETY: valid out pointer.
    let _ = unsafe { QueryPerformanceCounter(&mut c) };
    let c = c as u64;
    // Split to avoid overflowing u64 at 10 MHz after ~29 hours of uptime.
    (c / freq) * 1_000_000_000 + (c % freq) * 1_000_000_000 / freq
}

/// CLOCK_MONOTONIC is system-wide, so separate processes share it. It is also the clock Wayland
/// compositors use for presentation feedback, so on-screen times compare directly.
#[cfg(any(target_os = "linux", target_os = "android"))]
pub fn now_ns() -> u64 {
    let mut ts = libc::timespec { tv_sec: 0, tv_nsec: 0 };
    // SAFETY: valid out pointer; CLOCK_MONOTONIC always exists on Linux.
    unsafe { libc::clock_gettime(libc::CLOCK_MONOTONIC, &mut ts) };
    ts.tv_sec as u64 * 1_000_000_000 + ts.tv_nsec as u64
}

#[cfg(not(any(target_os = "macos", windows, target_os = "linux", target_os = "android")))]
pub fn now_ns() -> u64 {
    use std::sync::OnceLock;
    use std::time::Instant;
    static START: OnceLock<Instant> = OnceLock::new();
    START.get_or_init(Instant::now).elapsed().as_nanos() as u64
}

/// Sleeps until `deadline_ns` (on the [`now_ns`] clock): an OS sleep for most of the wait, then
/// a short spin, because OS timers are only accurate to about a millisecond.
pub fn sleep_until(deadline_ns: u64) {
    const SPIN_NS: u64 = 1_500_000;
    loop {
        let now = now_ns();
        if now >= deadline_ns {
            return;
        }
        let left = deadline_ns - now;
        if left > SPIN_NS {
            std::thread::sleep(std::time::Duration::from_nanos(left - SPIN_NS));
        } else {
            std::hint::spin_loop();
        }
    }
}

pub fn ms(ns: u64) -> f64 {
    ns as f64 / 1_000_000.0
}
