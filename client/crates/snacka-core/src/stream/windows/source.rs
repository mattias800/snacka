//! Synthetic "game-like" frames on the GPU, the Windows counterpart of stream-probe's
//! `FrameSource.swift`: a texture twice the frame width (gradient, hard-edged rectangles and a
//! noisy band, so the encoder has real work to do) scrolled horizontally every frame.
//!
//! Frames are BGRA textures, the format screen capture (Desktop Duplication / Windows Graphics
//! Capture) delivers, so the encoder input matches the real pipeline. Per frame the GPU copies
//! the visible window into a pool texture and only the small barcode strip is uploaded from the
//! CPU: no 4K CPU copy.

use super::{ctx, Result};
use crate::clock::now_ns;
use crate::stream::barcode;
use crate::stream::synthetic::{render_scroll_texture, SCROLL_SPEED};
use windows::Win32::Graphics::Direct3D11::{
    ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D, D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_BOX,
    D3D11_SUBRESOURCE_DATA, D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT,
};
use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SAMPLE_DESC};

pub struct GpuFrameSource {
    context: ID3D11DeviceContext,
    scroll: ID3D11Texture2D,
    /// Encoder input textures, used round-robin.
    pub frames: Vec<ID3D11Texture2D>,
    barcode: Vec<u8>,
    barcode_size: (usize, usize),
    pub width: usize,
    pub height: usize,
    offset: usize,
    speed: usize,
    next: usize,
}

pub fn bgra_texture_desc(width: usize, height: usize) -> D3D11_TEXTURE2D_DESC {
    D3D11_TEXTURE2D_DESC {
        Width: width as u32,
        Height: height as u32,
        MipLevels: 1,
        ArraySize: 1,
        Format: DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: (D3D11_BIND_RENDER_TARGET.0 | D3D11_BIND_SHADER_RESOURCE.0) as u32,
        CPUAccessFlags: 0,
        MiscFlags: 0,
    }
}

impl GpuFrameSource {
    pub fn new(device: &ID3D11Device, context: &ID3D11DeviceContext, width: usize, height: usize, pool: usize) -> Result<Self> {
        let tex_w = width * 2;
        let pixels = render_scroll_texture(tex_w, height);
        let init = D3D11_SUBRESOURCE_DATA { pSysMem: pixels.as_ptr().cast(), SysMemPitch: (tex_w * 4) as u32, SysMemSlicePitch: 0 };
        let mut scroll = None;
        let mut frames = Vec::with_capacity(pool);
        // SAFETY: descriptors and initial data outlive the calls; out pointers are valid.
        unsafe {
            ctx(device.CreateTexture2D(&bgra_texture_desc(tex_w, height), Some(&init), Some(&mut scroll)), "CreateTexture2D(scroll)")?;
            for _ in 0..pool {
                let mut t = None;
                ctx(device.CreateTexture2D(&bgra_texture_desc(width, height), None, Some(&mut t)), "CreateTexture2D(frame)")?;
                frames.extend(t);
            }
        }
        let barcode_size = barcode::area(width);
        Ok(GpuFrameSource {
            context: context.clone(),
            scroll: scroll.ok_or_else(|| super::Error("CreateTexture2D returned nothing".into()))?,
            frames,
            barcode: vec![0u8; barcode_size.0 * barcode_size.1 * 4],
            barcode_size,
            width,
            height,
            offset: 0,
            speed: SCROLL_SPEED,
            next: 0,
        })
    }

    /// Renders the next frame into a pool texture and stamps the barcode. Returns the texture's
    /// index in [`GpuFrameSource::frames`] and the capture timestamp. The GPU work is queued, not
    /// flushed; the encoder's input mapping orders it before the encode.
    pub fn next(&mut self, frame_id: u64) -> (usize, u64) {
        let index = self.next;
        self.next = (self.next + 1) % self.frames.len();
        let dst = &self.frames[index];
        let window = D3D11_BOX { left: self.offset as u32, top: 0, front: 0, right: (self.offset + self.width) as u32, bottom: self.height as u32, back: 1 };
        self.offset = (self.offset + self.speed) % self.width;
        let capture_ns = now_ns();
        let (bw, bh) = self.barcode_size;
        barcode::stamp_bgra(&mut self.barcode, bw * 4, self.width, frame_id, capture_ns);
        let area = D3D11_BOX { left: 0, top: 0, front: 0, right: bw as u32, bottom: bh as u32, back: 1 };
        // SAFETY: both textures are alive, boxes are in bounds, the barcode buffer covers `area`.
        unsafe {
            self.context.CopySubresourceRegion(dst, 0, 0, 0, 0, &self.scroll, 0, Some(&window));
            self.context.UpdateSubresource(dst, 0, Some(&area), self.barcode.as_ptr().cast(), (bw * 4) as u32, 0);
        }
        (index, capture_ns)
    }
}
