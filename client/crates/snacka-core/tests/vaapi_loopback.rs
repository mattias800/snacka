//! VAAPI round trip on the real GPU: synthetic source -> encoder -> wire (UDP loopback) ->
//! decoder, for every codec the GPU encodes. Checks the barcode and the picture itself (scroll
//! position and RGB -> BT.709 conversion), so the whole Linux pipeline is verified without
//! looking at it. Skipped when there is no VAAPI device (e.g. CI without a GPU).

#![cfg(target_os = "linux")]

use snacka_core::stream::linux::decode::receiver;
use snacka_core::stream::linux::device::VaDevice;
use snacka_core::stream::linux::encode::{EncoderConfig, VaapiEncoder};
use snacka_core::stream::linux::source::VaFrameSource;
use snacka_core::stream::synthetic::{render_scroll_texture, SCROLL_SPEED};
use snacka_core::stream::wire::{write_payload_annexb, OutgoingFrame, Sender};
use snacka_core::stream::{barcode, Codec};
use std::net::SocketAddr;
use std::time::Duration;

/// BT.709 limited-range luma of a BGRA pixel.
fn luma_709(p: &[u8]) -> f64 {
    16.0 + (0.0722 * f64::from(p[0]) + 0.7152 * f64::from(p[1]) + 0.2126 * f64::from(p[2])) * 219.0 / 255.0
}

#[test]
fn vaapi_round_trip_preserves_barcode_and_picture() {
    let device = match VaDevice::open() {
        Ok(d) => d,
        Err(e) => {
            eprintln!("skipped: no VAAPI device ({e})");
            return;
        }
    };
    let (w, h) = (1280usize, 720usize);
    let texture = render_scroll_texture(w * 2, h);
    let mut tested = 0;
    // AV1 encode from this pipeline hung the VCN (ring reset) at 1080p+ on Mesa 26.2 / Radeon
    // 8060S (see client/results/linux-radeon-8060s.md); opt in with SNACKA_TEST_AV1=1.
    let codecs: &[Codec] = if std::env::var_os("SNACKA_TEST_AV1").is_some() { &[Codec::H264, Codec::Hevc, Codec::Av1] } else { &[Codec::H264, Codec::Hevc] };
    for &codec in codecs {
        let mut source = VaFrameSource::new(&device, w, h, 4).expect("frame source");
        let config = EncoderConfig { codec, width: w as u32, height: h as u32, fps: 60, bitrate_bps: 20_000_000, quality: 0 };
        let mut encoder = match VaapiEncoder::new(source.frames_context(), &config) {
            Ok(e) => e,
            Err(e) => {
                eprintln!("skipped {codec:?}: {e}");
                continue;
            }
        };
        let mut rx = receiver(0, &device).expect("bind receiver");
        rx.set_timeout(Some(Duration::from_secs(5))).expect("timeout");
        let mut tx = Sender::connect(SocketAddr::from(([127, 0, 0, 1], rx.local_port().expect("port")))).expect("sender");
        let (mut bitstream, mut payload) = (Vec::new(), Vec::new());
        for frame_id in 0..8u64 {
            let (frame, capture_ns) = source.next(frame_id).expect("render");
            let pic = encoder.encode(frame, frame_id, frame_id == 0, &mut bitstream).expect("encode");
            assert!(!bitstream.is_empty(), "{codec:?}: no bitstream for frame {frame_id}");
            assert_eq!(pic.keyframe, frame_id == 0, "{codec:?}: keyframe flag on frame {frame_id}");
            write_payload_annexb(codec, &bitstream, &mut payload);
            tx.send(&OutgoingFrame { frame_id, capture_ns, encoded_ns: pic.encoded_ns, codec, keyframe: pic.keyframe, payload: &payload })
                .expect("send");
            let decoded = rx.next().expect("decode");
            assert_eq!(decoded.meta.frame_id, frame_id);
            assert_eq!((decoded.frame.width(), decoded.frame.height()), (w, h));
            let code = decoded.frame.with_luma(barcode::read).flatten();
            assert_eq!(code, Some((frame_id, capture_ns)), "{codec:?}: barcode of frame {frame_id}");

            // The picture under the barcode: the scroll texture's window at this frame's offset.
            let offset = (frame_id as usize * SCROLL_SPEED) % w;
            let (x0, y0, rw, rh) = (96, 96, 512, 256);
            let mae = decoded
                .frame
                .with_luma_region(x0, y0, rw, rh, |luma, stride| {
                    let mut err = 0.0;
                    for y in 0..rh {
                        for x in 0..rw {
                            let i = ((y0 + y) * w * 2 + offset + x0 + x) * 4;
                            err += (f64::from(luma[y * stride + x]) - luma_709(&texture[i..i + 4])).abs();
                        }
                    }
                    err / (rw * rh) as f64
                })
                .expect("read picture");
            // Measured 0.3-0.5 on AMD; tagging the input as sRGB (gamma conversion) gives ~6.5.
            assert!(mae < 2.0, "{codec:?} frame {frame_id}: luma differs from the source by {mae:.2} on average");
        }
        assert_eq!(rx.frames_dropped(), 0);
        assert_eq!(rx.decode_errors(), 0);
        tested += 1;
    }
    assert!(tested > 0, "no codec could be encoded");
}
