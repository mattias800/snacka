//! Rust port of stream-probe's `send` mode for Windows: synthetic GPU frames -> NVENC -> UDP.
//!
//!   cargo run --release --example send -- --width 3840 --height 2160 --fps 120 --codec hevc --bitrate 100
//!   options: --host 127.0.0.1 --port 9000 --seconds 10 --preset 1..7 --json path
//!
//! Prints per-second sender stats and a `SUMMARY {json}` line at the end. "encode" is capture
//! timestamp -> bitstream available (GPU copy + NVENC), like the Swift probe's `encodeOnly`.

#[cfg(windows)]
fn main() -> Result<(), Box<dyn std::error::Error>> {
    use snacka_core::clock::{ms, now_ns, sleep_until};
    use snacka_core::stream::windows::d3d11::create_device;
    use snacka_core::stream::windows::nvenc::{EncoderConfig, NvencEncoder};
    use snacka_core::stream::windows::source::GpuFrameSource;
    use snacka_core::stream::wire::{write_payload_annexb, OutgoingFrame, Sender};
    use snacka_core::stream::Codec;
    use std::net::ToSocketAddrs;

    let args: Vec<String> = std::env::args().skip(1).collect();
    let opt = |name: &str, fallback: &str| -> String {
        args.iter().position(|a| a == &format!("--{name}")).and_then(|i| args.get(i + 1)).cloned().unwrap_or_else(|| fallback.to_string())
    };
    let width: usize = opt("width", "1920").parse()?;
    let height: usize = opt("height", "1080").parse()?;
    let fps: u32 = opt("fps", "60").parse()?;
    let codec = Codec::parse(&opt("codec", "hevc")).ok_or("--codec must be h264, hevc or av1")?;
    let bitrate_mbps: f64 = opt("bitrate", "20").parse()?;
    let seconds: f64 = opt("seconds", "10").parse()?;
    let warmup: f64 = opt("warmup", "1").parse()?;
    let preset: u8 = opt("preset", "1").parse()?;
    let dest = format!("{}:{}", opt("host", "127.0.0.1"), opt("port", "9000")).to_socket_addrs()?.next().ok_or("bad --host")?;
    let json_path = opt("json", "");

    let dev = create_device(None)?;
    let mut source = GpuFrameSource::new(&dev.device, &dev.context, width, height, 4)?;
    let config = EncoderConfig {
        codec,
        width: width as u32,
        height: height as u32,
        fps,
        bitrate_bps: (bitrate_mbps * 1e6) as u32,
        preset,
    };
    let mut encoder = NvencEncoder::new(&dev.device, &source.frames, config)?;
    let mut sender = Sender::connect(dest)?;
    println!(
        "send: {width}x{height}@{fps} {} {bitrate_mbps} Mbps P{preset} -> {dest} | {} | NVENC API {}.{} | stream {:04x}",
        codec.name(), dev.adapter_name, encoder.api_version.0, encoder.api_version.1, sender.stream_id()
    );

    let period = 1_000_000_000 / u64::from(fps);
    let start = now_ns() + 50_000_000;
    let total_frames = (seconds * f64::from(fps)) as u64;
    let record_from = (warmup * f64::from(fps)) as u64;
    let (mut bitstream, mut payload) = (Vec::with_capacity(4 << 20), Vec::with_capacity(4 << 20));
    let (mut window, mut all) = (Vec::with_capacity(1024), Vec::with_capacity(total_frames as usize));
    let (mut late, mut keyframes, mut bytes_window, mut sent_window) = (0u64, 0u64, 0u64, 0u64);
    let mut window_start = start;
    let mut recorded_bytes = 0u64;
    for frame_id in 0..total_frames {
        let deadline = start + frame_id * period;
        if now_ns() > deadline + period {
            late += 1;
        } else {
            sleep_until(deadline);
        }
        let (input, capture_ns) = source.next(frame_id);
        let force_idr = frame_id == 0 || sender.take_keyframe_request();
        let pic = encoder.encode(input, frame_id, force_idr, &mut bitstream)?;
        let encode_ns = pic.encoded_ns - capture_ns;
        keyframes += u64::from(pic.keyframe);
        write_payload_annexb(codec, &bitstream, &mut payload);
        sender.send(&OutgoingFrame { frame_id, capture_ns, encoded_ns: pic.encoded_ns, codec, keyframe: pic.keyframe, payload: &payload })?;
        window.push(encode_ns);
        bytes_window += payload.len() as u64;
        sent_window += 1;
        if frame_id >= record_from {
            all.push(encode_ns);
            recorded_bytes += payload.len() as u64;
        }
        let now = now_ns();
        if now - window_start >= 1_000_000_000 {
            let secs = (now - window_start) as f64 / 1e9;
            window.sort_unstable();
            let p = |q: f64| ms(window[((window.len() as f64 * q) as usize).min(window.len() - 1)]);
            println!(
                "sender: {:5.1} fps | encode p50 {:5.2} p99 {:5.2} max {:5.2} ms | {:6.1} Mbps | keyframes {keyframes} (requests {}) | late {late} | send retries {}",
                sent_window as f64 / secs, p(0.5), p(0.99), p(1.0), bytes_window as f64 * 8.0 / secs / 1e6,
                sender.keyframe_requests(), sender.send_retries
            );
            window.clear();
            bytes_window = 0;
            sent_window = 0;
            window_start = now;
        }
    }
    let elapsed = (now_ns() - start) as f64 / 1e9 - warmup;
    all.sort_unstable();
    let p = |q: f64| all.get(((all.len() as f64 * q) as usize).min(all.len().saturating_sub(1))).copied().map(ms).unwrap_or(f64::NAN);
    let summary = format!(
        "{{\"encoder\":\"nvenc\",\"gpu\":\"{}\",\"codec\":\"{}\",\"width\":{width},\"height\":{height},\"fps_target\":{fps},\"preset\":{preset},\"bitrate_mbps\":{bitrate_mbps},\"fps\":{:.1},\"encode_ms\":{{\"p50\":{:.2},\"p95\":{:.2},\"p99\":{:.2},\"max\":{:.2}}},\"mbps\":{:.1},\"late\":{late},\"keyframes\":{keyframes},\"keyframe_requests\":{},\"send_retries\":{}}}",
        dev.adapter_name, codec.name(), all.len() as f64 / elapsed, p(0.5), p(0.95), p(0.99), p(1.0),
        recorded_bytes as f64 * 8.0 / elapsed / 1e6, sender.keyframe_requests(), sender.send_retries
    );
    println!("SUMMARY {summary}");
    if !json_path.is_empty() {
        std::fs::write(&json_path, &summary)?;
    }
    Ok(())
}

#[cfg(not(windows))]
fn main() {
    eprintln!("send example is Windows-only for now (macOS: use spikes/stream-probe)");
}
