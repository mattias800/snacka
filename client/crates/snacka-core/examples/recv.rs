//! Receives a stream-probe / `send` stream, hardware-decodes it and reports latency from the
//! barcode (VideoToolbox on macOS, Media Foundation + D3D11 on Windows).
//!
//!   cargo run --release --example recv -- 9000 [--seconds 10 --warmup 1 --json path]
//!   (other terminal, macOS) spikes/stream-probe/.build/release/StreamProbe send --port 9000 --width 2560 --height 1440 --fps 120 --codec hevc --bitrate 50 --seconds 10
//!   (other terminal, Windows) cargo run --release --example send -- --port 9000 --width 3840 --height 2160 --fps 120 --codec hevc --bitrate 100
//!
//! Latency ("total" = capture timestamp in the pixels -> decoded) is only meaningful when sender
//! and receiver share a clock, i.e. run on the same machine.

#[cfg(any(target_os = "macos", windows))]
fn main() -> std::io::Result<()> {
    use snacka_core::clock::{ms, now_ns};
    use snacka_core::stream::barcode;
    use std::time::Duration;

    let args: Vec<String> = std::env::args().skip(1).collect();
    let opt = |name: &str| args.iter().position(|a| a == &format!("--{name}")).and_then(|i| args.get(i + 1)).cloned();
    let port: u16 = opt("port").or_else(|| args.first().filter(|a| !a.starts_with("--")).cloned()).and_then(|p| p.parse().ok()).unwrap_or(9000);
    let seconds: f64 = opt("seconds").and_then(|s| s.parse().ok()).unwrap_or(0.0);
    let warmup: f64 = opt("warmup").and_then(|s| s.parse().ok()).unwrap_or(1.0);
    let json_path = opt("json").unwrap_or_default();

    #[cfg(target_os = "macos")]
    let mut rx = snacka_core::stream::VideoReceiver::bind(port)?;
    #[cfg(windows)]
    let (mut rx, gpu) = {
        let dev = snacka_core::stream::windows::d3d11::create_device(None)?;
        (snacka_core::stream::windows::mf_decode::receiver(port, &dev.device, &dev.context)?, dev.adapter_name)
    };
    #[cfg(not(windows))]
    let gpu = String::from("apple");
    rx.set_timeout(Some(Duration::from_millis(500)))?;
    println!("listening on udp/{port}");

    let (mut net, mut enc, mut dec, mut total) = (Vec::new(), Vec::new(), Vec::new(), Vec::new());
    let (mut all_enc, mut all_dec, mut all_total) = (Vec::new(), Vec::new(), Vec::new());
    let (mut bad, mut bytes, mut all_bytes) = (0u64, 0u64, 0u64);
    let mut first_ns: Option<u64> = None;
    let started_ns = now_ns();
    let mut recorded_span: Option<(u64, u64)> = None;
    let mut window_start = now_ns();
    let mut size = (0, 0);
    let mut codec = "";
    loop {
        let now = now_ns();
        if seconds > 0.0 && first_ns.is_some_and(|f| now - f >= ((seconds + warmup) * 1e9) as u64) {
            break;
        }
        // Give up if nothing decodable arrives (no sender, or a decoder that never produces output).
        if seconds > 0.0 && now - started_ns >= ((seconds + warmup + 15.0) * 1e9) as u64 {
            break;
        }
        let decoded = match rx.next() {
            Ok(d) => d,
            Err(e) if matches!(e.kind(), std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut) => {
                if first_ns.is_some() && seconds > 0.0 {
                    break; // sender finished
                }
                continue;
            }
            // Windows reports ICMP "port unreachable" for earlier keyframe requests as a reset.
            Err(e) if e.kind() == std::io::ErrorKind::ConnectionReset => continue,
            Err(e) => return Err(e),
        };
        let (f, frame) = (&decoded.meta, &decoded.frame);
        let first = *first_ns.get_or_insert(f.received_ns);
        let recording = f.received_ns - first >= (warmup * 1e9) as u64;
        if f.new_stream {
            println!("new stream {:04x} ({:?})", f.stream_id, f.codec);
        }
        codec = f.codec.name();
        size = (frame.width(), frame.height());
        bytes += f.wire_bytes as u64;
        #[cfg(windows)]
        let code = frame.with_luma(barcode::read).flatten();
        #[cfg(not(windows))]
        let code = frame.with_luma(barcode::read);
        match code {
            Some((id, capture_ns)) if id & 0xFFFF_FFFF == f.frame_id && capture_ns == f.capture_ns => {
                net.push(f.received_ns - f.encoded_ns);
                enc.push(f.encoded_ns - capture_ns);
                dec.push(frame.decoded_ns - f.received_ns);
                total.push(frame.decoded_ns - capture_ns);
                if recording {
                    let span = recorded_span.get_or_insert((frame.decoded_ns, frame.decoded_ns));
                    span.1 = frame.decoded_ns;
                    all_enc.push(f.encoded_ns - capture_ns);
                    all_dec.push(frame.decoded_ns - f.received_ns);
                    all_total.push(frame.decoded_ns - capture_ns);
                    all_bytes += f.wire_bytes as u64;
                }
            }
            other => {
                bad += 1;
                if bad <= 3 {
                    eprintln!("bad barcode: frame {} read {:?}", f.frame_id, other.map(|(id, _)| id));
                }
            }
        }
        drop(decoded);
        let now = now_ns();
        if now - window_start >= 1_000_000_000 {
            let secs = ms(now - window_start) / 1000.0;
            println!(
                "fps {:5.1} | encode p50 {:5.2} | network p50 {:5.2} | decode p50 {:5.2} p99 {:5.2} | total p50 {:5.2} p99 {:5.2} | {:6.1} Mbps | {}x{} | dropped {} bad {} keyframe requests {}",
                total.len() as f64 / secs,
                pct(&mut enc, 0.5), pct(&mut net, 0.5), pct(&mut dec, 0.5), pct(&mut dec, 0.99), pct(&mut total, 0.5), pct(&mut total, 0.99),
                bytes as f64 * 8.0 / secs / 1e6, size.0, size.1, rx.frames_dropped(), bad, rx.keyframe_requests()
            );
            for v in [&mut net, &mut enc, &mut dec, &mut total] {
                v.clear();
            }
            bytes = 0;
            window_start = now;
        }
    }
    // Frames per second over the recorded frames (n frames span n - 1 intervals).
    let span = recorded_span.map_or(1e-9, |(a, b)| (b - a) as f64 / 1e9).max(1e-9);
    let stage = |v: &mut Vec<u64>| {
        format!("{{\"p50\":{:.2},\"p95\":{:.2},\"p99\":{:.2}}}", pct(v, 0.5), pct(v, 0.95), pct(v, 0.99)).replace("NaN", "null")
    };
    let summary = format!(
        "{{\"gpu\":\"{gpu}\",\"codec\":\"{codec}\",\"width\":{},\"height\":{},\"fps\":{:.1},\"encode_ms\":{},\"decode_ms\":{},\"total_ms\":{},\"mbps\":{:.1},\"dropped\":{},\"bad\":{bad},\"keyframe_requests\":{}}}",
        size.0, size.1, all_total.len().saturating_sub(1) as f64 / span, stage(&mut all_enc), stage(&mut all_dec), stage(&mut all_total),
        all_bytes as f64 * 8.0 / span / 1e6, rx.frames_dropped(), rx.keyframe_requests()
    );
    println!("SUMMARY {summary}");
    if !json_path.is_empty() {
        std::fs::write(&json_path, &summary)?;
    }
    Ok(())
}

fn pct(v: &mut [u64], q: f64) -> f64 {
    v.sort_unstable();
    v.get(((v.len() as f64) * q) as usize).copied().map(snacka_core::clock::ms).unwrap_or(f64::NAN)
}

#[cfg(not(any(target_os = "macos", windows)))]
fn main() {
    eprintln!("recv example needs a platform decoder (macOS or Windows)");
}
