//! Reads the (frame id, capture timestamp) barcode that stream-probe stamps into the luma plane.

const BITS: usize = 64;
const ROWS: usize = 3;
const MAGIC: u64 = 0x534E_4143_4B41_5052; // "SNACKAPR"

fn block_size(width: usize) -> usize {
    (width / (BITS + 8)).clamp(8, 32)
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
