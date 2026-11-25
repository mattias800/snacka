//! D3D11 device selection. Encoding, decoding and presentation want the discrete GPU, not a
//! virtual display adapter (Parsec, Remote Desktop) or the Basic Render Driver.

use super::{ctx, Error, Result};
use windows::core::Interface;
use windows::Win32::Foundation::HMODULE;
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_UNKNOWN, D3D_FEATURE_LEVEL_11_1};
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, ID3D11Device, ID3D11DeviceContext, ID3D11Multithread, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    D3D11_CREATE_DEVICE_VIDEO_SUPPORT, D3D11_SDK_VERSION,
};
use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, IDXGIAdapter, IDXGIAdapter1, IDXGIFactory1, DXGI_ADAPTER_FLAG_SOFTWARE};

pub const VENDOR_NVIDIA: u32 = 0x10DE;
pub const VENDOR_AMD: u32 = 0x1002;
pub const VENDOR_INTEL: u32 = 0x8086;

pub struct Device {
    pub device: ID3D11Device,
    pub context: ID3D11DeviceContext,
    pub adapter_name: String,
    pub vendor_id: u32,
}

/// Creates a video-capable, multithread-protected device on the hardware adapter with the most
/// dedicated video memory (the discrete GPU), or on the first adapter from `vendor` if given.
pub fn create_device(vendor: Option<u32>) -> Result<Device> {
    // SAFETY: plain COM factory/enumeration calls; returned interfaces are reference counted.
    unsafe {
        let factory: IDXGIFactory1 = ctx(CreateDXGIFactory1(), "CreateDXGIFactory1")?;
        let mut best: Option<(IDXGIAdapter1, u64, String, u32)> = None;
        let mut i = 0;
        while let Ok(adapter) = factory.EnumAdapters1(i) {
            i += 1;
            let desc = ctx(adapter.GetDesc1(), "GetDesc1")?;
            if desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32 != 0 {
                continue;
            }
            if vendor.is_some_and(|v| v != desc.VendorId) {
                continue;
            }
            let len = desc.Description.iter().position(|&c| c == 0).unwrap_or(desc.Description.len());
            let name = String::from_utf16_lossy(&desc.Description[..len]);
            let memory = desc.DedicatedVideoMemory as u64;
            if best.as_ref().is_none_or(|b| memory > b.1) {
                best = Some((adapter, memory, name, desc.VendorId));
            }
        }
        let (adapter, _, adapter_name, vendor_id) =
            best.ok_or_else(|| Error("no hardware D3D11 adapter found".into()))?;
        let adapter: IDXGIAdapter = ctx(adapter.cast(), "IDXGIAdapter")?;
        let mut device = None;
        let mut context = None;
        ctx(
            D3D11CreateDevice(
                &adapter,
                D3D_DRIVER_TYPE_UNKNOWN,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                Some(&[D3D_FEATURE_LEVEL_11_1]),
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                Some(&mut context),
            ),
            "D3D11CreateDevice",
        )?;
        let device: ID3D11Device = device.ok_or_else(|| Error("D3D11CreateDevice returned no device".into()))?;
        let context = context.ok_or_else(|| Error("D3D11CreateDevice returned no context".into()))?;
        // The decoder (Media Foundation) and the presenter use the device from other threads.
        let mt: ID3D11Multithread = ctx(device.cast(), "ID3D11Multithread")?;
        let _ = mt.SetMultithreadProtected(true);
        Ok(Device { device, context, adapter_name, vendor_id })
    }
}
