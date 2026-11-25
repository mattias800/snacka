//! NVENC encoder on a D3D11 device, configured for minimum latency:
//! `ULTRA_LOW_LATENCY` tuning, no B-frames, infinite GOP with IDR on request, CBR with a
//! one-frame VBV, synchronous encode (the bitstream is locked as soon as the frame is done).
//!
//! Input is registered D3D11 BGRA textures (`NV_ENC_BUFFER_FORMAT_ARGB` is B,G,R,A in memory);
//! NVENC converts to YUV on the GPU. Output is Annex-B (H.264/HEVC) or AV1 OBUs.

use super::{Error, Result};
use crate::clock::now_ns;
use crate::stream::Codec;
use mini_nvcodec::guids::*;
use mini_nvcodec::nvenc_sys::*;
use mini_nvcodec::{require_nvenc, NvencApi, NvencApiVersion, API_VERSION_LADDER};
use std::ffi::{c_void, CStr};
use windows::core::Interface;
use windows::Win32::Graphics::Direct3D11::{ID3D11Device, ID3D11Texture2D};

#[derive(Clone, Copy, Debug)]
pub struct EncoderConfig {
    pub codec: Codec,
    pub width: u32,
    pub height: u32,
    pub fps: u32,
    pub bitrate_bps: u32,
    /// NVENC preset P1 (fastest) .. P7 (best quality).
    pub preset: u8,
}

pub struct EncodedPicture {
    pub keyframe: bool,
    /// Time the bitstream was available (`now_ns` clock).
    pub encoded_ns: u64,
}

pub struct NvencEncoder {
    api: &'static NvencApi,
    encoder: *mut c_void,
    rung: NvencApiVersion,
    config: EncoderConfig,
    registered: Vec<NV_ENC_REGISTERED_PTR>,
    bitstream: NV_ENC_OUTPUT_PTR,
    pub api_version: (u32, u32),
}

// SAFETY: the session is used from one thread at a time (the owner); NVENC sessions are not
// bound to the creating thread.
unsafe impl Send for NvencEncoder {}

fn check(api: &NvencApi, encoder: *mut c_void, status: NVENCSTATUS, what: &str) -> Result<()> {
    if status == NVENCSTATUS::SUCCESS {
        return Ok(());
    }
    let detail = if encoder.is_null() {
        String::new()
    } else {
        // SAFETY: the driver returns a NUL-terminated string owned by the session.
        unsafe {
            let p = (api.nvEncGetLastErrorString)(encoder);
            if p.is_null() { String::new() } else { CStr::from_ptr(p).to_string_lossy().into_owned() }
        }
    };
    Err(Error(format!("{what} failed: NVENCSTATUS {} {detail}", status.0)))
}

impl NvencEncoder {
    /// Opens a session on `device` and registers `inputs` (BGRA textures of the configured size).
    pub fn new(device: &ID3D11Device, inputs: &[ID3D11Texture2D], config: EncoderConfig) -> Result<NvencEncoder> {
        let api = require_nvenc().map_err(|e| Error(format!("NVENC unavailable: {e}")))?;
        let (encoder, rung) = open_session(api, device)?;
        let mut me = NvencEncoder {
            api,
            encoder,
            rung,
            config,
            registered: Vec::new(),
            bitstream: std::ptr::null_mut(),
            api_version: rung.major_minor(),
        };
        me.initialize()?;
        for tex in inputs {
            // SAFETY: zeroed + version per the NVENC struct contract; the texture outlives the
            // registration (the caller keeps the pool alive for the encoder's lifetime).
            let mut reg: NV_ENC_REGISTER_RESOURCE = unsafe { std::mem::zeroed() };
            reg.version = rung.register_resource_ver();
            reg.resourceType = NV_ENC_INPUT_RESOURCE_TYPE::DIRECTX;
            reg.width = config.width;
            reg.height = config.height;
            reg.resourceToRegister = tex.as_raw();
            reg.bufferFormat = NV_ENC_BUFFER_FORMAT::ARGB;
            reg.bufferUsage = NV_ENC_BUFFER_USAGE::INPUT_IMAGE;
            check(api, encoder, unsafe { (api.nvEncRegisterResource)(encoder, &mut reg) }, "nvEncRegisterResource")?;
            me.registered.push(reg.registeredResource);
        }
        // SAFETY: as above.
        let mut bs: NV_ENC_CREATE_BITSTREAM_BUFFER = unsafe { std::mem::zeroed() };
        bs.version = rung.create_bitstream_buffer_ver();
        check(api, encoder, unsafe { (api.nvEncCreateBitstreamBuffer)(encoder, &mut bs) }, "nvEncCreateBitstreamBuffer")?;
        me.bitstream = bs.bitstreamBuffer;
        Ok(me)
    }

    fn initialize(&mut self) -> Result<()> {
        let (api, enc, rung, c) = (self.api, self.encoder, self.rung, self.config);
        let codec_guid = match c.codec {
            Codec::H264 => NV_ENC_CODEC_H264_GUID,
            Codec::Hevc => NV_ENC_CODEC_HEVC_GUID,
            Codec::Av1 => NV_ENC_CODEC_AV1_GUID,
        };
        let preset_guid = match c.preset {
            0 | 1 => NV_ENC_PRESET_P1_GUID,
            2 => NV_ENC_PRESET_P2_GUID,
            3 => NV_ENC_PRESET_P3_GUID,
            4 => NV_ENC_PRESET_P4_GUID,
            5 => NV_ENC_PRESET_P5_GUID,
            6 => NV_ENC_PRESET_P6_GUID,
            _ => NV_ENC_PRESET_P7_GUID,
        };
        let tuning = NV_ENC_TUNING_INFO::ULTRA_LOW_LATENCY;
        // SAFETY: zeroed + versions per the NVENC struct contract; all pointers outlive the calls.
        unsafe {
            let mut preset: NV_ENC_PRESET_CONFIG = std::mem::zeroed();
            preset.version = rung.preset_config_ver();
            preset.presetCfg.version = rung.config_ver();
            check(
                api,
                enc,
                (api.nvEncGetEncodePresetConfigEx)(enc, codec_guid, preset_guid, tuning, &mut preset),
                "nvEncGetEncodePresetConfigEx",
            )?;
            let mut cfg = preset.presetCfg;
            cfg.gopLength = NVENC_INFINITE_GOPLENGTH;
            cfg.frameIntervalP = 1; // IPPP, no B-frames
            let rc = &mut cfg.rcParams;
            rc.rateControlMode = NV_ENC_PARAMS_RC_MODE::CBR;
            rc.averageBitRate = c.bitrate_bps;
            rc.maxBitRate = c.bitrate_bps;
            // One frame of VBV: frame sizes stay flat, so no frame takes longer than a frame
            // period on the wire. This is what low-latency game streaming uses.
            rc.vbvBufferSize = c.bitrate_bps / c.fps.max(1);
            rc.vbvInitialDelay = rc.vbvBufferSize;
            match c.codec {
                Codec::H264 => {
                    let h = &mut cfg.encodeCodecConfig.h264Config;
                    h.idrPeriod = NVENC_INFINITE_GOPLENGTH;
                    h.set_repeatSPSPPS(1);
                }
                Codec::Hevc => {
                    let h = &mut cfg.encodeCodecConfig.hevcConfig;
                    h.idrPeriod = NVENC_INFINITE_GOPLENGTH;
                    h.set_repeatSPSPPS(1);
                }
                Codec::Av1 => {
                    let a = &mut cfg.encodeCodecConfig.av1Config;
                    a.idrPeriod = NVENC_INFINITE_GOPLENGTH;
                    a.set_repeatSeqHdr(1);
                }
            }
            let mut init: NV_ENC_INITIALIZE_PARAMS = std::mem::zeroed();
            init.version = rung.initialize_params_ver();
            init.encodeGUID = codec_guid;
            init.presetGUID = preset_guid;
            init.tuningInfo = tuning;
            init.encodeWidth = c.width;
            init.encodeHeight = c.height;
            init.darWidth = c.width;
            init.darHeight = c.height;
            init.maxEncodeWidth = c.width;
            init.maxEncodeHeight = c.height;
            init.frameRateNum = c.fps;
            init.frameRateDen = 1;
            init.enablePTD = 1;
            init.enableEncodeAsync = 0;
            init.encodeConfig = &mut cfg;
            check(api, enc, (api.nvEncInitializeEncoder)(enc, &mut init), "nvEncInitializeEncoder")?;
        }
        Ok(())
    }

    pub fn config(&self) -> EncoderConfig {
        self.config
    }

    /// Encodes input texture `input` (an index into the textures given to [`NvencEncoder::new`])
    /// and appends the bitstream to `out` (cleared first). Blocks until the frame is encoded.
    pub fn encode(&mut self, input: usize, frame_id: u64, force_idr: bool, out: &mut Vec<u8>) -> Result<EncodedPicture> {
        let (api, enc, rung) = (self.api, self.encoder, self.rung);
        let registered = *self.registered.get(input).ok_or_else(|| Error("input index out of range".into()))?;
        // SAFETY: zeroed + versions per the NVENC struct contract; the mapped input is unmapped
        // on every path below, and the locked bitstream is copied before it is unlocked.
        unsafe {
            let mut map: NV_ENC_MAP_INPUT_RESOURCE = std::mem::zeroed();
            map.version = rung.map_input_resource_ver();
            map.registeredResource = registered;
            check(api, enc, (api.nvEncMapInputResource)(enc, &mut map), "nvEncMapInputResource")?;
            let result = self.encode_mapped(map.mappedResource, map.mappedBufferFmt, frame_id, force_idr, out);
            (api.nvEncUnmapInputResource)(enc, map.mappedResource);
            result
        }
    }

    unsafe fn encode_mapped(
        &mut self,
        input: NV_ENC_INPUT_PTR,
        format: NV_ENC_BUFFER_FORMAT,
        frame_id: u64,
        force_idr: bool,
        out: &mut Vec<u8>,
    ) -> Result<EncodedPicture> {
        let (api, enc, rung, c) = (self.api, self.encoder, self.rung, self.config);
        let mut pic: NV_ENC_PIC_PARAMS = std::mem::zeroed();
        pic.version = rung.pic_params_ver();
        pic.inputWidth = c.width;
        pic.inputHeight = c.height;
        pic.inputBuffer = input;
        pic.outputBitstream = self.bitstream;
        pic.bufferFmt = format;
        pic.pictureStruct = NV_ENC_PIC_STRUCT::FRAME;
        pic.inputTimeStamp = frame_id;
        if force_idr {
            pic.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
        }
        check(api, enc, (api.nvEncEncodePicture)(enc, &mut pic), "nvEncEncodePicture")?;
        let mut lock: NV_ENC_LOCK_BITSTREAM = std::mem::zeroed();
        lock.version = rung.lock_bitstream_ver();
        lock.outputBitstream = self.bitstream;
        check(api, enc, (api.nvEncLockBitstream)(enc, &mut lock), "nvEncLockBitstream")?;
        let encoded_ns = now_ns();
        out.clear();
        out.extend_from_slice(std::slice::from_raw_parts(lock.bitstreamBufferPtr as *const u8, lock.bitstreamSizeInBytes as usize));
        let keyframe = matches!(lock.pictureType, NV_ENC_PIC_TYPE::IDR | NV_ENC_PIC_TYPE::I);
        (api.nvEncUnlockBitstream)(enc, self.bitstream);
        Ok(EncodedPicture { keyframe, encoded_ns })
    }
}

impl Drop for NvencEncoder {
    fn drop(&mut self) {
        let (api, enc) = (self.api, self.encoder);
        // SAFETY: handles were created by this session and are released exactly once.
        unsafe {
            if !self.bitstream.is_null() {
                (api.nvEncDestroyBitstreamBuffer)(enc, self.bitstream);
            }
            for r in self.registered.drain(..) {
                (api.nvEncUnregisterResource)(enc, r);
            }
            (api.nvEncDestroyEncoder)(enc);
        }
    }
}

/// Opens an encode session on a D3D11 device at the newest API version the driver accepts.
fn open_session(api: &NvencApi, device: &ID3D11Device) -> Result<(*mut c_void, NvencApiVersion)> {
    for rung in API_VERSION_LADDER {
        if rung > api.created_with {
            continue;
        }
        let mut encoder: *mut c_void = std::ptr::null_mut();
        // SAFETY: zeroed + version per the NVENC struct contract; the device outlives the session.
        let status = unsafe {
            let mut open: NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS = std::mem::zeroed();
            open.version = rung.open_encode_session_ex_params_ver();
            open.deviceType = NV_ENC_DEVICE_TYPE::DIRECTX;
            open.device = device.as_raw();
            open.apiVersion = rung.nvencapi_version();
            (api.nvEncOpenEncodeSessionEx)(&mut open, &mut encoder)
        };
        if status == NVENCSTATUS::SUCCESS {
            return Ok((encoder, rung));
        }
        if !encoder.is_null() {
            // SAFETY: the header requires destroying even a failed-open handle.
            unsafe { (api.nvEncDestroyEncoder)(encoder) };
        }
        if status != NVENCSTATUS::ERR_INVALID_VERSION {
            return Err(Error(format!("nvEncOpenEncodeSessionEx failed: NVENCSTATUS {}", status.0)));
        }
    }
    Err(Error("no NVENC API version accepted by the driver".into()))
}
