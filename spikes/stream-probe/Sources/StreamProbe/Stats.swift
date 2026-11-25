import Foundation

/// Collects per-stage latency samples (nanoseconds) and reports percentiles.
final class Stats {
    private let lock = NSLock()
    private var samples: [String: [UInt64]] = [:]   // cumulative (after warm-up), for the summary
    private var counters: [String: Int] = [:]
    private var interval: [String: [UInt64]] = [:]  // since the last printed line
    private var intervalCounters: [String: Int] = [:]
    var recording = false                            // false during warm-up
    let stageOrder = ["encode", "network", "decode", "total"]

    func add(_ stage: String, _ ns: UInt64) {
        lock.lock()
        interval[stage, default: []].append(ns)
        if recording { samples[stage, default: []].append(ns) }
        lock.unlock()
    }
    func count(_ name: String, _ n: Int = 1) {
        lock.lock()
        intervalCounters[name, default: 0] += n
        if recording { counters[name, default: 0] += n }
        lock.unlock()
    }

    private func takeInterval() -> (samples: [String: [UInt64]], counters: [String: Int]) {
        lock.lock(); defer { lock.unlock() }
        let s = (interval, intervalCounters)
        interval = [:]; intervalCounters = [:]
        return s
    }

    private func cumulative() -> (samples: [String: [UInt64]], counters: [String: Int]) {
        lock.lock(); defer { lock.unlock() }
        return (samples, counters)
    }

    static func pct(_ sorted: [UInt64], _ p: Double) -> Double {
        guard !sorted.isEmpty else { return .nan }
        return ms(sorted[min(sorted.count - 1, Int(Double(sorted.count) * p))])
    }

    func line(seconds: Double) -> String {
        let (s, c) = takeInterval()
        var parts: [String] = []
        let frames = s["total"]?.count ?? 0
        parts.append(String(format: "fps %5.1f", Double(frames) / seconds))
        for stage in stageOrder {
            guard let v = s[stage]?.sorted(), !v.isEmpty else { continue }
            parts.append(String(format: "%@ p50 %5.2f p99 %5.2f", stage, Stats.pct(v, 0.5), Stats.pct(v, 0.99)))
        }
        if let bytes = c["wireBytes"] { parts.append(String(format: "%.1f Mbps", Double(bytes) * 8 / seconds / 1e6)) }
        for k in ["badBarcode", "dropped", "decodeErrors"] where (c[k] ?? 0) > 0 { parts.append("\(k) \(c[k]!)") }
        return parts.joined(separator: " | ")
    }

    func summaryJSON(config: [String: Any], seconds: Double) -> String {
        let (s, c) = cumulative()
        var out: [String: Any] = ["config": config]
        var stages: [String: Any] = [:]
        for (k, v) in s {
            let sorted = v.sorted()
            stages[k] = ["p50": Stats.pct(sorted, 0.5), "p95": Stats.pct(sorted, 0.95),
                         "p99": Stats.pct(sorted, 0.99), "max": Stats.pct(sorted, 1.0), "n": sorted.count]
        }
        out["stages_ms"] = stages
        out["fps"] = Double(s["total"]?.count ?? 0) / seconds
        out["mbps"] = Double(c["wireBytes"] ?? 0) * 8 / seconds / 1e6
        out["counters"] = c
        let data = try! JSONSerialization.data(withJSONObject: out, options: [.sortedKeys])
        return String(data: data, encoding: .utf8)!
    }
}
