//! Receives a stream-probe stream, hardware-decodes it and reports latency from the barcode.
//!
//!   cargo run --release --example recv -- 9000
//!   (other terminal) spikes/stream-probe/.build/release/StreamProbe send --port 9000 --width 2560 --height 1440 --fps 120 --codec hevc --bitrate 50 --seconds 10

#[cfg(target_os = "macos")]
fn main() -> std::io::Result<()> {
    use snacka_core::clock::{ms, now_ns};
    use snacka_core::stream::{barcode, VideoReceiver};

    let port: u16 = std::env::args().nth(1).and_then(|p| p.parse().ok()).unwrap_or(9000);
    let mut rx = VideoReceiver::bind(port)?;
    println!("listening on udp/{port}");
    let (mut net, mut dec, mut total) = (Vec::new(), Vec::new(), Vec::new());
    let mut bad = 0u64;
    let mut window_start = now_ns();
    loop {
        let decoded = rx.next()?;
        let (f, frame) = (&decoded.meta, &decoded.frame);
        if f.new_stream {
            println!("new stream {:04x}", f.stream_id);
        }
        match frame.with_luma(barcode::read) {
            Some((id, capture_ns)) if id & 0xFFFF_FFFF == f.frame_id && capture_ns == f.capture_ns => {
                net.push(f.received_ns - f.encoded_ns);
                dec.push(frame.decoded_ns - f.received_ns);
                total.push(frame.decoded_ns - capture_ns);
            }
            _ => bad += 1,
        }
        let now = now_ns();
        if now - window_start >= 1_000_000_000 {
            let p = |v: &mut Vec<u64>, q: f64| {
                v.sort_unstable();
                v.get(((v.len() as f64) * q) as usize).copied().map(ms).unwrap_or(f64::NAN)
            };
            println!(
                "fps {:5.1} | network p50 {:5.2} | decode p50 {:5.2} p99 {:5.2} | total p50 {:5.2} p99 {:5.2} | {}x{} | dropped {} bad {} keyframe requests {}",
                total.len() as f64 / ms(now - window_start) * 1000.0,
                p(&mut net, 0.5), p(&mut dec, 0.5), p(&mut dec, 0.99), p(&mut total, 0.5), p(&mut total, 0.99),
                frame.width(), frame.height(), rx.frames_dropped(), bad, rx.keyframe_requests()
            );
            net.clear();
            dec.clear();
            total.clear();
            window_start = now;
        }
    }
}

#[cfg(not(target_os = "macos"))]
fn main() {
    eprintln!("recv example is macOS-only for now");
}
