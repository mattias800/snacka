import Darwin

/// System-wide monotonic clock in nanoseconds. Shared by all processes on the machine,
/// so a sender and receiver on the same Mac can compare timestamps directly.
@inline(__always)
func nowNs() -> UInt64 { clock_gettime_nsec_np(CLOCK_UPTIME_RAW) }

@inline(__always)
func ms(_ ns: UInt64) -> Double { Double(ns) / 1_000_000 }

/// Sleeps until an absolute deadline on the same clock as `nowNs()`.
func sleepUntil(_ deadlineNs: UInt64) {
    while true {
        let now = nowNs()
        if now >= deadlineNs { return }
        let remaining = deadlineNs - now
        if remaining > 2_000_000 {
            usleep(useconds_t((remaining - 1_000_000) / 1000))
        } else if remaining > 200_000 {
            usleep(100)
        } else {
            // spin for the last ~0.2 ms for precise frame pacing
        }
    }
}
