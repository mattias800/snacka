//! The (frame id, capture timestamp) barcode that stream-probe stamps into the top-left corner
//! of every frame: three rows of 64 black/white blocks (frame id, capture ns, checksum). Each bit
//! is a large flat block read by averaging its centre, so it survives lossy compression.

const BITS: usize = 64;
const ROWS: usize = 3;
const MAGIC: u64 = 0x534E_4143_4B41_5052; // "SNACKAPR"

/// Limited-range luma for a set and a cleared bit (what RGB white/black become after encoding).
const LUMA_ON: u8 = 235;
const LUMA_OFF: u8 = 16;

fn block_size(width: usize) -> usize {
    (width / (BITS + 8)).clamp(8, 32)
}

/// Size in pixels of the barcode area for a frame `width` pixels wide.
pub fn area(width: usize) -> (usize, usize) {
    let b = block_size(width);
    (BITS * b, ROWS * b)
}

/// Calls `block(x, y, size, on)` for every block of the barcode.
fn for_each_block(width: usize, frame_id: u64, capture_ns: u64, mut block: impl FnMut(usize, usize, usize, bool)) {
    let b = block_size(width);
    let values = [frame_id, capture_ns, frame_id ^ capture_ns ^ MAGIC];
    for (row, value) in values.iter().enumerate() {
        for bit in 0..BITS {
            block(bit * b, row * b, b, (value >> (BITS - 1 - bit)) & 1 == 1);
        }
    }
}

/// Stamps the barcode into an NV12/I420 luma plane. Callers should also set the chroma under
/// the barcode area to neutral (128) so it stays crisp after 4:2:0 subsampling.
pub fn stamp_luma(luma: &mut [u8], stride: usize, width: usize, frame_id: u64, capture_ns: u64) {
    for_each_block(width, frame_id, capture_ns, |x0, y0, b, on| {
        let v = if on { LUMA_ON } else { LUMA_OFF };
        for y in y0..y0 + b {
            luma[y * stride + x0..y * stride + x0 + b].fill(v);
        }
    });
}

/// Stamps the barcode as white/black pixels into a 4-byte-per-pixel buffer (BGRA or RGBA) whose
/// top-left corner is the frame's top-left corner. `width` is the frame width, which sets the
/// block size; the buffer only needs to cover [`area`].
pub fn stamp_bgra(pixels: &mut [u8], stride: usize, width: usize, frame_id: u64, capture_ns: u64) {
    for_each_block(width, frame_id, capture_ns, |x0, y0, b, on| {
        let px: [u8; 4] = if on { [255, 255, 255, 255] } else { [0, 0, 0, 255] };
        for y in y0..y0 + b {
            for p in pixels[y * stride + x0 * 4..y * stride + (x0 + b) * 4].chunks_exact_mut(4) {
                p.copy_from_slice(&px);
            }
        }
    });
}

/// `luma` is the Y plane, `stride` its bytes per row, `width` the visible width in pixels.
/// Returns `None` if the checksum doesn't match.
pub fn read(luma: &[u8], stride: usize, width: usize) -> Option<(u64, u64)> {
    let b = block_size(width);
    let inset = b / 4;
    if luma.len() < ROWS * b * stride || width < BITS * b {
        return None;
    }
    let mut values = [0u64; ROWS];
    for (row, value) in values.iter_mut().enumerate() {
        for bit in 0..BITS {
            let mut sum = 0u32;
            let mut n = 0u32;
            for y in (row * b + inset)..((row + 1) * b - inset) {
                let start = y * stride + bit * b;
                for &p in &luma[start + inset..start + b - inset] {
                    sum += p as u32;
                    n += 1;
                }
            }
            *value = (*value << 1) | u64::from(sum / n > 126);
        }
    }
    (values[0] ^ values[1] ^ MAGIC == values[2]).then_some((values[0], values[1]))
}

#[cfg(test)]
mod tests {
    use super::*;

    const ID: u64 = 0x0000_0001_2345_6789;
    const NS: u64 = 987_654_321_012_345;

    #[test]
    fn luma_round_trip_at_every_tier_width() {
        for (w, h) in [(1280, 720), (1920, 1080), (2560, 1440), (3840, 2160)] {
            let stride = w + 64;
            let mut luma = vec![90u8; stride * h];
            stamp_luma(&mut luma, stride, w, ID, NS);
            assert_eq!(read(&luma, stride, w), Some((ID, NS)), "{w}x{h}");
        }
    }

    #[test]
    fn bgra_round_trip_through_bt709_limited_range() {
        let w = 1920;
        let (aw, ah) = area(w);
        let stride = aw * 4;
        let mut bgra = vec![0u8; stride * ah];
        stamp_bgra(&mut bgra, stride, w, ID, NS);
        // What an encoder's RGB -> YUV conversion produces.
        let luma: Vec<u8> = bgra
            .chunks_exact(4)
            .map(|p| {
                let y = 0.0722 * p[0] as f32 + 0.7152 * p[1] as f32 + 0.2126 * p[2] as f32;
                (16.0 + y * 219.0 / 255.0).round() as u8
            })
            .collect();
        assert_eq!(read(&luma, aw, w), Some((ID, NS)));
    }

    #[test]
    fn noise_in_block_edges_is_ignored_and_corruption_is_detected() {
        let w = 1920;
        let stride = w;
        let mut luma = vec![0u8; stride * 200];
        stamp_luma(&mut luma, stride, w, ID, NS);
        // Ringing at block edges (outside the read inset) must not matter.
        for y in 0..area(w).1 {
            luma[y * stride] = 128;
        }
        assert_eq!(read(&luma, stride, w), Some((ID, NS)));
        // Flipping a whole block breaks the checksum.
        let b = block_size(w);
        for y in 0..b {
            for x in 0..b {
                luma[y * stride + x] = 255 - luma[y * stride + x];
            }
        }
        assert_eq!(read(&luma, stride, w), None);
    }
}
