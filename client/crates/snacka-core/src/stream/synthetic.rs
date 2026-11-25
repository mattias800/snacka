//! The synthetic "game-like" content shared by the platform frame sources (stream-probe's
//! `FrameSource.swift`): a texture twice the frame width, scrolled horizontally every frame.

/// Pixels the visible window moves per frame.
pub const SCROLL_SPEED: usize = 12;

/// The scroll texture's pixels (BGRA, `tex_w * 4` bytes per row): diagonal gradient, random
/// rectangles, and a noisy band in the lower third (foliage/particles stand-in), so the
/// encoder has real work to do. Deterministic (fixed seed).
pub fn render_scroll_texture(tex_w: usize, height: usize) -> Vec<u8> {
    let mut px = vec![0u8; tex_w * height * 4];
    let mut rng = SplitMix64(42);
    for row in 0..height {
        for col in 0..tex_w {
            let g = (40 + (row + col) * 160 / (height + tex_w)) as u8;
            let i = (row * tex_w + col) * 4;
            px[i..i + 4].copy_from_slice(&[g, g.saturating_sub(10), g / 2 + 40, 255]);
        }
    }
    let frame_w = tex_w / 2;
    for _ in 0..(tex_w * height / 20_000) {
        let rw = (rng.next() % (frame_w / 8).max(8) as u64) as usize + 8;
        let rh = (rng.next() % (height / 8).max(8) as u64) as usize + 8;
        let rx = (rng.next() % (tex_w - rw) as u64) as usize;
        let ry = (rng.next() % (height - rh) as u64) as usize;
        let c = [(16 + rng.next() % 224) as u8, (16 + rng.next() % 224) as u8, (16 + rng.next() % 224) as u8, 255];
        for row in ry..ry + rh {
            for col in rx..rx + rw {
                let i = (row * tex_w + col) * 4;
                px[i..i + 4].copy_from_slice(&c);
            }
        }
    }
    for row in height * 2 / 3..height {
        for col in 0..tex_w {
            let n = (rng.next() % 110) as u8;
            let i = (row * tex_w + col) * 4;
            for c in &mut px[i..i + 3] {
                *c = *c / 2 + n;
            }
        }
    }
    px
}

struct SplitMix64(u64);

impl SplitMix64 {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }
}
