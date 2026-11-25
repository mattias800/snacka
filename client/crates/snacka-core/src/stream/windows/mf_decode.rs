//! Hardware H.264/HEVC/AV1 decoding through Media Foundation decoder MFTs with a D3D11 device
//! manager (DXVA), producing NV12 frames in D3D11 textures that the presenter can use without a
//! copy. Vendor-neutral: NVIDIA, AMD and Intel all decode through the same path. HEVC and AV1
//! need the "HEVC Video Extensions" / "AV1 Video Extension" from the Microsoft Store.
//!
//! The MFT runs in low-latency mode (`MF_LOW_LATENCY`), so it returns each picture as soon as
//! it is decoded instead of buffering for reordering.

use super::{ctx, Error, Result};
use crate::clock::now_ns;
use crate::stream::{Codec, VideoDecoder};
use std::mem::ManuallyDrop;
use std::sync::{Arc, Mutex, Once};
use windows::core::{Interface, GUID};
use windows::Win32::Graphics::Direct3D11::{
    ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D, D3D11_CPU_ACCESS_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_MAP_READ,
    D3D11_TEXTURE2D_DESC, D3D11_USAGE_STAGING,
};
use windows::Win32::Media::MediaFoundation::*;
use windows::Win32::System::Com::{CoInitializeEx, CoTaskMemFree, COINIT_MULTITHREADED};

pub struct MfDecoder {
    transform: IMFTransform,
    _manager: IMFDXGIDeviceManager,
    device: ID3D11Device,
    readback: Arc<Readback>,
    codec: Codec,
    size: (usize, usize),
    output_ready: bool,
    input: Vec<u8>,
    errors: u64,
    skipped: u64,
    frames_in: i64,
    pub name: String,
}

// SAFETY: the MFT and the D3D11 device (multithread protected) are used from one thread at a
// time; Media Foundation objects are free-threaded.
unsafe impl Send for MfDecoder {}

/// One decoded picture: slice `subresource` of the decoder's NV12 texture array. Holding it
/// keeps that slot out of the decoder's pool, so drop frames promptly.
pub struct DecodedFrame {
    pub decoded_ns: u64,
    pub texture: ID3D11Texture2D,
    pub subresource: u32,
    width: usize,
    height: usize,
    _sample: IMFSample,
    readback: Arc<Readback>,
}

// SAFETY: see MfDecoder.
unsafe impl Send for DecodedFrame {}

impl DecodedFrame {
    pub fn width(&self) -> usize {
        self.width
    }

    pub fn height(&self) -> usize {
        self.height
    }

    /// Calls `f(luma, stride, width)` with the Y plane, copied back to the CPU through a staging
    /// texture. For tests and the barcode only: it stalls until the GPU has finished the frame.
    pub fn with_luma<R>(&self, f: impl FnOnce(&[u8], usize, usize) -> R) -> Option<R> {
        self.readback.with_luma(&self.texture, self.subresource, self.width, self.height, f)
    }
}

/// CPU readback for the barcode (test mode only).
struct Readback {
    context: ID3D11DeviceContext,
    device: ID3D11Device,
    staging: Mutex<Option<(ID3D11Texture2D, u32, u32)>>,
}

impl Readback {
    fn with_luma<R>(
        &self,
        texture: &ID3D11Texture2D,
        subresource: u32,
        width: usize,
        height: usize,
        f: impl FnOnce(&[u8], usize, usize) -> R,
    ) -> Option<R> {
        let mut staging = self.staging.lock().unwrap_or_else(|e| e.into_inner());
        // SAFETY: COM calls on live objects; the mapped pointer is only read while mapped and
        // within RowPitch * height bytes.
        unsafe {
            let mut desc = D3D11_TEXTURE2D_DESC::default();
            texture.GetDesc(&mut desc);
            if staging.as_ref().is_none_or(|s| s.1 != desc.Width || s.2 != desc.Height) {
                let sd = D3D11_TEXTURE2D_DESC {
                    ArraySize: 1,
                    MipLevels: 1,
                    Usage: D3D11_USAGE_STAGING,
                    BindFlags: 0,
                    CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
                    MiscFlags: 0,
                    ..desc
                };
                let mut t = None;
                self.device.CreateTexture2D(&sd, None, Some(&mut t)).ok()?;
                *staging = Some((t?, desc.Width, desc.Height));
            }
            let (tex, _, _) = staging.as_ref()?;
            self.context.CopySubresourceRegion(tex, 0, 0, 0, 0, texture, subresource, None);
            let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
            self.context.Map(tex, 0, D3D11_MAP_READ, 0, Some(&mut mapped)).ok()?;
            let stride = mapped.RowPitch as usize;
            let luma = std::slice::from_raw_parts(mapped.pData as *const u8, stride * height);
            let r = f(luma, stride, width);
            self.context.Unmap(tex, 0);
            Some(r)
        }
    }
}

fn startup() {
    static START: Once = Once::new();
    START.call_once(|| {
        // SAFETY: process-wide MF initialisation, done once and never shut down.
        unsafe {
            let _ = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        }
    });
}

fn subtype(codec: Codec) -> GUID {
    match codec {
        Codec::H264 => MFVideoFormat_H264,
        Codec::Hevc => MFVideoFormat_HEVC,
        Codec::Av1 => MFVideoFormat_AV1,
    }
}

impl MfDecoder {
    pub fn new(codec: Codec, device: &ID3D11Device, context: &ID3D11DeviceContext) -> Result<MfDecoder> {
        startup();
        // SAFETY: COM/MF calls with valid arguments; the activate array is freed below.
        unsafe {
            // MFTEnumEx activates COM objects; MTA is what MF expects. Ignore "already initialised".
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
            let input = MFT_REGISTER_TYPE_INFO { guidMajorType: MFMediaType_Video, guidSubtype: subtype(codec) };
            let mut activates: *mut Option<IMFActivate> = std::ptr::null_mut();
            let mut count = 0u32;
            ctx(
                MFTEnumEx(
                    MFT_CATEGORY_VIDEO_DECODER,
                    MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER,
                    Some(&input),
                    None,
                    &mut activates,
                    &mut count,
                ),
                "MFTEnumEx",
            )?;
            let list = std::slice::from_raw_parts_mut(activates, count as usize);
            let mut found = None;
            for a in list.iter_mut() {
                if let Some(a) = a.take() {
                    if found.is_none() {
                        let mut p = windows::core::PWSTR::null();
                        let mut len = 0u32;
                        let name = a.GetAllocatedString(&MFT_FRIENDLY_NAME_Attribute, &mut p, &mut len).ok().map(|()| {
                            let s = p.to_string().unwrap_or_default();
                            CoTaskMemFree(Some(p.0 as *const _));
                            s
                        });
                        if let Ok(t) = a.ActivateObject::<IMFTransform>() {
                            found = Some((t, name.unwrap_or_default()));
                        }
                    }
                }
            }
            CoTaskMemFree(Some(activates as *const _));
            let (transform, name) = found.ok_or_else(|| {
                Error(format!("no {} decoder MFT (install the Microsoft Store video extension?)", codec.name()))
            })?;

            let attrs = ctx(transform.GetAttributes(), "GetAttributes")?;
            if attrs.GetUINT32(&MF_SA_D3D11_AWARE).unwrap_or(0) == 0 {
                return Err(Error(format!("{name} is not D3D11-aware")));
            }
            ctx(attrs.SetUINT32(&MF_LOW_LATENCY, 1), "MF_LOW_LATENCY")?;

            let mut token = 0u32;
            let mut manager = None;
            ctx(MFCreateDXGIDeviceManager(&mut token, &mut manager), "MFCreateDXGIDeviceManager")?;
            let manager: IMFDXGIDeviceManager = manager.ok_or_else(|| Error("no DXGI device manager".into()))?;
            ctx(manager.ResetDevice(device, token), "ResetDevice")?;
            ctx(transform.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, manager.as_raw() as usize), "SET_D3D_MANAGER")?;

            let media_type = ctx(MFCreateMediaType(), "MFCreateMediaType")?;
            ctx(media_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video), "MF_MT_MAJOR_TYPE")?;
            ctx(media_type.SetGUID(&MF_MT_SUBTYPE, &subtype(codec)), "MF_MT_SUBTYPE")?;
            // Placeholder size: some decoders (HEVC extension) need an output type before the
            // first input. The real size comes with the first sequence header as a stream change.
            ctx(media_type.SetUINT64(&MF_MT_FRAME_SIZE, (1920u64 << 32) | 1080), "MF_MT_FRAME_SIZE")?;
            ctx(transform.SetInputType(0, &media_type, 0), "SetInputType")?;

            let mut me = MfDecoder {
                transform,
                _manager: manager,
                device: device.clone(),
                readback: Arc::new(Readback { context: context.clone(), device: device.clone(), staging: Mutex::new(None) }),
                codec,
                size: (0, 0),
                output_ready: false,
                input: Vec::with_capacity(1 << 20),
                errors: 0,
                skipped: 0,
                frames_in: 0,
                name,
            };
            me.set_output_type()?;
            let _ = me.transform.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            let _ = me.transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
            Ok(me)
        }
    }

    pub fn device(&self) -> &ID3D11Device {
        &self.device
    }

    /// Picks NV12 output (after the input type, and again on every stream change).
    fn set_output_type(&mut self) -> Result<()> {
        // SAFETY: COM calls on a live MFT.
        unsafe {
            let mut i = 0;
            while let Ok(t) = self.transform.GetOutputAvailableType(0, i) {
                i += 1;
                if t.GetGUID(&MF_MT_SUBTYPE).ok() == Some(MFVideoFormat_NV12) {
                    // Fails until the decoder has seen a sequence header (no frame size yet);
                    // it then signals MF_E_TRANSFORM_STREAM_CHANGE and we come back here.
                    if self.transform.SetOutputType(0, &t, 0).is_err() {
                        break;
                    }
                    if let Ok(size) = t.GetUINT64(&MF_MT_FRAME_SIZE) {
                        self.size = ((size >> 32) as usize, (size & 0xFFFF_FFFF) as usize);
                    }
                    // The decoded picture may be smaller than the coded size (1080 vs 1088).
                    if let Ok(blob) = aperture(&t) {
                        self.size = blob;
                    }
                    self.output_ready = true;
                    return Ok(());
                }
            }
        }
        // Before the first keyframe the MFT may not offer output types yet.
        self.output_ready = false;
        Ok(())
    }

    fn decode_inner(&mut self, keyframe: bool, parameter_sets: &[Vec<u8>], data: &[u8]) -> Result<Option<DecodedFrame>> {
        // MF's H.264/HEVC decoders take Annex B; AV1 takes the OBUs as they are.
        self.input.clear();
        if self.codec == Codec::Av1 {
            self.input.extend_from_slice(data);
        } else {
            for ps in parameter_sets {
                self.input.extend_from_slice(&[0, 0, 0, 1]);
                self.input.extend_from_slice(ps);
            }
            let mut pos = 0;
            while pos + 4 <= data.len() {
                let len = u32::from_be_bytes([data[pos], data[pos + 1], data[pos + 2], data[pos + 3]]) as usize;
                pos += 4;
                let Some(nal) = data.get(pos..pos + len) else { return Err(Error("truncated AVCC NAL unit".into())) };
                self.input.extend_from_slice(&[0, 0, 0, 1]);
                self.input.extend_from_slice(nal);
                pos += len;
            }
        }
        // SAFETY: COM calls on live objects; the buffer is locked only while copying into it.
        unsafe {
            let buffer = ctx(MFCreateMemoryBuffer(self.input.len() as u32), "MFCreateMemoryBuffer")?;
            let mut ptr = std::ptr::null_mut();
            ctx(buffer.Lock(&mut ptr, None, None), "Lock")?;
            std::ptr::copy_nonoverlapping(self.input.as_ptr(), ptr, self.input.len());
            ctx(buffer.Unlock(), "Unlock")?;
            ctx(buffer.SetCurrentLength(self.input.len() as u32), "SetCurrentLength")?;
            let sample = ctx(MFCreateSample(), "MFCreateSample")?;
            ctx(sample.AddBuffer(&buffer), "AddBuffer")?;
            ctx(sample.SetSampleTime(self.frames_in * 83_333), "SetSampleTime")?;
            ctx(sample.SetSampleDuration(83_333), "SetSampleDuration")?;
            if keyframe {
                ctx(sample.SetUINT32(&MFSampleExtension_CleanPoint, 1), "CleanPoint")?;
            }
            self.frames_in += 1;
            ctx(self.transform.ProcessInput(0, &sample, 0), "ProcessInput")?;
        }
        self.drain()
    }

    /// Pulls every picture the decoder has ready and returns the newest (older ones would only
    /// add latency). Stops when the decoder wants more input.
    fn drain(&mut self) -> Result<Option<DecodedFrame>> {
        let mut newest = None;
        while let Some(frame) = self.pull()? {
            if newest.is_some() {
                self.skipped += 1;
            }
            newest = Some(frame);
        }
        Ok(newest)
    }

    /// Pictures the decoder produced but [`MfDecoder::drain`] skipped because a newer one was
    /// ready too (a decoder that holds pictures back).
    pub fn skipped(&self) -> u64 {
        self.skipped
    }

    /// Pulls the next decoded picture, if any.
    fn pull(&mut self) -> Result<Option<DecodedFrame>> {
        for _ in 0..4 {
            if !self.output_ready {
                self.set_output_type()?;
                if !self.output_ready {
                    return Ok(None);
                }
            }
            let mut out = [MFT_OUTPUT_DATA_BUFFER { dwStreamID: 0, pSample: ManuallyDrop::new(None), dwStatus: 0, pEvents: ManuallyDrop::new(None) }];
            let mut status = 0u32;
            // SAFETY: the MFT provides its own (D3D11) samples; we take ownership of whatever it
            // put into `out` exactly once.
            let r = unsafe { self.transform.ProcessOutput(0, &mut out, &mut status) };
            let sample = unsafe { ManuallyDrop::take(&mut out[0].pSample) };
            drop(unsafe { ManuallyDrop::take(&mut out[0].pEvents) });
            match r {
                Ok(()) => {
                    let decoded_ns = now_ns();
                    let sample = sample.ok_or_else(|| Error("decoder returned no sample".into()))?;
                    return self.frame_from(sample, decoded_ns).map(Some);
                }
                Err(e) if e.code() == MF_E_TRANSFORM_NEED_MORE_INPUT => return Ok(None),
                Err(e) if e.code() == MF_E_TRANSFORM_STREAM_CHANGE => {
                    self.output_ready = false;
                    continue;
                }
                Err(e) => return Err(Error(format!("ProcessOutput: {e}"))),
            }
        }
        Ok(None)
    }

    fn frame_from(&self, sample: IMFSample, decoded_ns: u64) -> Result<DecodedFrame> {
        // SAFETY: COM calls on a live sample.
        unsafe {
            let buffer = ctx(sample.GetBufferByIndex(0), "GetBufferByIndex")?;
            let dxgi: IMFDXGIBuffer = ctx(buffer.cast(), "IMFDXGIBuffer (decoder output is not a D3D11 texture)")?;
            let mut texture: Option<ID3D11Texture2D> = None;
            ctx(dxgi.GetResource(&ID3D11Texture2D::IID, &mut texture as *mut _ as *mut _), "GetResource")?;
            let subresource = ctx(dxgi.GetSubresourceIndex(), "GetSubresourceIndex")?;
            Ok(DecodedFrame {
                decoded_ns,
                texture: texture.ok_or_else(|| Error("no texture".into()))?,
                subresource,
                width: self.size.0,
                height: self.size.1,
                _sample: sample,
                readback: self.readback.clone(),
            })
        }
    }
}

/// The display aperture (visible area) if the media type has one.
unsafe fn aperture(t: &IMFMediaType) -> windows::core::Result<(usize, usize)> {
    let mut area = MFVideoArea::default();
    let mut size = 0u32;
    t.GetBlob(
        &MF_MT_MINIMUM_DISPLAY_APERTURE,
        std::slice::from_raw_parts_mut(&mut area as *mut _ as *mut u8, std::mem::size_of::<MFVideoArea>()),
        Some(&mut size),
    )?;
    Ok((area.Area.cx as usize, area.Area.cy as usize))
}

/// A [`VideoReceiver`](crate::stream::VideoReceiver) that decodes on `device` with Media
/// Foundation, one decoder per stream.
pub fn receiver(
    port: u16,
    device: &ID3D11Device,
    context: &ID3D11DeviceContext,
) -> std::io::Result<crate::stream::VideoReceiver<MfDecoder>> {
    struct SendDevice(ID3D11Device, ID3D11DeviceContext);
    // SAFETY: the device is multithread protected (see d3d11::create_device).
    unsafe impl Send for SendDevice {}
    let shared = SendDevice(device.clone(), context.clone());
    crate::stream::VideoReceiver::with_decoder(port, move |codec| {
        let d = &shared;
        MfDecoder::new(codec, &d.0, &d.1).map_err(std::io::Error::from)
    })
}

impl VideoDecoder for MfDecoder {
    type Frame = DecodedFrame;

    fn decode(&mut self, keyframe: bool, parameter_sets: &[Vec<u8>], data: &[u8]) -> Option<DecodedFrame> {
        match self.decode_inner(keyframe, parameter_sets, data) {
            Ok(frame) => frame,
            Err(e) => {
                self.errors += 1;
                if self.errors <= 5 {
                    eprintln!("decode error ({}): {e}", self.name);
                }
                None
            }
        }
    }

    fn errors(&self) -> u64 {
        self.errors
    }
}
