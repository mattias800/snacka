//! Hardware H264/HEVC decoding with VideoToolbox, producing NV12 frames (IOSurface-backed, so
//! they can be shown on a CALayer without copying).
//!
//! FFI is declared by hand to keep the exact C signatures under our control.
#![allow(non_upper_case_globals, non_snake_case)]

use super::Codec;
use crate::clock::now_ns;
use core_foundation::base::{CFType, TCFType};
use core_foundation::boolean::CFBoolean;
use core_foundation::dictionary::CFDictionary;
use core_foundation::number::CFNumber;
use core_foundation::string::CFString;
use core_foundation_sys::base::{kCFAllocatorDefault, CFAllocatorRef, CFRelease, CFTypeRef};
use core_foundation_sys::dictionary::CFDictionaryRef;
use core_foundation_sys::string::CFStringRef;
use std::ffi::c_void;
use std::ptr;

type OSStatus = i32;
type CMFormatDescriptionRef = *const c_void;
type CMBlockBufferRef = *const c_void;
type CMSampleBufferRef = *const c_void;
type VTDecompressionSessionRef = *const c_void;
pub type CVPixelBufferRef = *const c_void;

#[repr(C)]
#[derive(Clone, Copy)]
struct CMTime {
    value: i64,
    timescale: i32,
    flags: u32,
    epoch: i64,
}

type OutputCallback = extern "C" fn(*mut c_void, *mut c_void, OSStatus, u32, CVPixelBufferRef, CMTime, CMTime);

#[repr(C)]
struct VTDecompressionOutputCallbackRecord {
    callback: OutputCallback,
    ref_con: *mut c_void,
}

#[link(name = "CoreMedia", kind = "framework")]
extern "C" {
    fn CMVideoFormatDescriptionCreateFromH264ParameterSets(
        allocator: CFAllocatorRef, count: usize, pointers: *const *const u8, sizes: *const usize,
        nal_unit_header_length: i32, out: *mut CMFormatDescriptionRef) -> OSStatus;
    fn CMVideoFormatDescriptionCreateFromHEVCParameterSets(
        allocator: CFAllocatorRef, count: usize, pointers: *const *const u8, sizes: *const usize,
        nal_unit_header_length: i32, extensions: CFDictionaryRef, out: *mut CMFormatDescriptionRef) -> OSStatus;
    fn CMFormatDescriptionEqual(a: CMFormatDescriptionRef, b: CMFormatDescriptionRef) -> u8;
    fn CMBlockBufferCreateWithMemoryBlock(
        structure_allocator: CFAllocatorRef, memory_block: *mut c_void, block_length: usize,
        block_allocator: CFAllocatorRef, custom_block_source: *const c_void, offset_to_data: usize,
        data_length: usize, flags: u32, out: *mut CMBlockBufferRef) -> OSStatus;
    fn CMBlockBufferReplaceDataBytes(source: *const c_void, dest: CMBlockBufferRef, offset: usize, len: usize) -> OSStatus;
    fn CMSampleBufferCreateReady(
        allocator: CFAllocatorRef, data_buffer: CMBlockBufferRef, format: CMFormatDescriptionRef,
        num_samples: isize, num_timing: isize, timing: *const c_void, num_sizes: isize,
        sizes: *const usize, out: *mut CMSampleBufferRef) -> OSStatus;
}

#[link(name = "VideoToolbox", kind = "framework")]
extern "C" {
    static kVTVideoDecoderSpecification_RequireHardwareAcceleratedVideoDecoder: CFStringRef;
    static kVTDecompressionPropertyKey_RealTime: CFStringRef;
    fn VTDecompressionSessionCreate(
        allocator: CFAllocatorRef, format: CMFormatDescriptionRef, decoder_spec: CFDictionaryRef,
        image_attrs: CFDictionaryRef, callback: *const VTDecompressionOutputCallbackRecord,
        out: *mut VTDecompressionSessionRef) -> OSStatus;
    fn VTDecompressionSessionDecodeFrame(
        session: VTDecompressionSessionRef, sample: CMSampleBufferRef, flags: u32,
        source_ref_con: *mut c_void, info_out: *mut u32) -> OSStatus;
    fn VTDecompressionSessionInvalidate(session: VTDecompressionSessionRef);
    fn VTSessionSetProperty(session: *const c_void, key: CFStringRef, value: CFTypeRef) -> OSStatus;
}

#[link(name = "CoreVideo", kind = "framework")]
extern "C" {
    static kCVPixelBufferPixelFormatTypeKey: CFStringRef;
    static kCVPixelBufferIOSurfacePropertiesKey: CFStringRef;
    fn CVPixelBufferRetain(pb: CVPixelBufferRef) -> CVPixelBufferRef;
    fn CVPixelBufferRelease(pb: CVPixelBufferRef);
    fn CVPixelBufferLockBaseAddress(pb: CVPixelBufferRef, flags: u64) -> i32;
    fn CVPixelBufferUnlockBaseAddress(pb: CVPixelBufferRef, flags: u64) -> i32;
    fn CVPixelBufferGetBaseAddressOfPlane(pb: CVPixelBufferRef, plane: usize) -> *mut c_void;
    fn CVPixelBufferGetBytesPerRowOfPlane(pb: CVPixelBufferRef, plane: usize) -> usize;
    fn CVPixelBufferGetWidthOfPlane(pb: CVPixelBufferRef, plane: usize) -> usize;
    fn CVPixelBufferGetHeightOfPlane(pb: CVPixelBufferRef, plane: usize) -> usize;
    fn CVPixelBufferGetIOSurface(pb: CVPixelBufferRef) -> *const c_void;
}

const NV12_VIDEO_RANGE: i32 = 0x3432_3076; // '420v'
const LOCK_READ_ONLY: u64 = 1;
const ASSURE_MEMORY_NOW: u32 = 1;

/// A decoded frame. Retains the underlying CVPixelBuffer until dropped.
pub struct DecodedFrame {
    pixel_buffer: CVPixelBufferRef,
    pub decoded_ns: u64,
}

// SAFETY: CVPixelBuffer is a thread-safe, reference-counted CF object.
unsafe impl Send for DecodedFrame {}

impl DecodedFrame {
    pub fn width(&self) -> usize {
        unsafe { CVPixelBufferGetWidthOfPlane(self.pixel_buffer, 0) }
    }

    pub fn height(&self) -> usize {
        unsafe { CVPixelBufferGetHeightOfPlane(self.pixel_buffer, 0) }
    }

    /// The IOSurface backing this frame, for zero-copy display (`CALayer.contents`).
    pub fn io_surface(&self) -> *const c_void {
        unsafe { CVPixelBufferGetIOSurface(self.pixel_buffer) }
    }

    pub fn pixel_buffer(&self) -> CVPixelBufferRef {
        self.pixel_buffer
    }

    /// Calls `f(luma, stride, width)` with the Y plane mapped for reading.
    pub fn with_luma<R>(&self, f: impl FnOnce(&[u8], usize, usize) -> R) -> R {
        unsafe {
            CVPixelBufferLockBaseAddress(self.pixel_buffer, LOCK_READ_ONLY);
            let base = CVPixelBufferGetBaseAddressOfPlane(self.pixel_buffer, 0) as *const u8;
            let stride = CVPixelBufferGetBytesPerRowOfPlane(self.pixel_buffer, 0);
            let height = CVPixelBufferGetHeightOfPlane(self.pixel_buffer, 0);
            let width = CVPixelBufferGetWidthOfPlane(self.pixel_buffer, 0);
            let result = f(std::slice::from_raw_parts(base, stride * height), stride, width);
            CVPixelBufferUnlockBaseAddress(self.pixel_buffer, LOCK_READ_ONLY);
            result
        }
    }
}

impl Clone for DecodedFrame {
    fn clone(&self) -> Self {
        unsafe { CVPixelBufferRetain(self.pixel_buffer) };
        DecodedFrame { pixel_buffer: self.pixel_buffer, decoded_ns: self.decoded_ns }
    }
}

impl Drop for DecodedFrame {
    fn drop(&mut self) {
        unsafe { CVPixelBufferRelease(self.pixel_buffer) }
    }
}

struct Sink {
    last: Option<DecodedFrame>,
    errors: u64,
}

extern "C" fn on_decoded(
    ref_con: *mut c_void, _source: *mut c_void, status: OSStatus, _flags: u32,
    image: CVPixelBufferRef, _pts: CMTime, _dur: CMTime,
) {
    // SAFETY: ref_con is the Box<Sink> owned by the Decoder, alive for the session's lifetime;
    // decoding is synchronous (flags = 0), so this runs on the decoding thread.
    let sink = unsafe { &mut *(ref_con as *mut Sink) };
    if status != 0 || image.is_null() {
        sink.errors += 1;
        return;
    }
    unsafe { CVPixelBufferRetain(image) };
    sink.last = Some(DecodedFrame { pixel_buffer: image, decoded_ns: now_ns() });
}

/// Synchronous hardware decoder: `decode` returns the decoded frame (or `None` while waiting
/// for the first keyframe / on errors).
pub struct Decoder {
    codec: Codec,
    format: CMFormatDescriptionRef,
    session: VTDecompressionSessionRef,
    sink: Box<Sink>,
}

// SAFETY: the session is used from one thread at a time (Decoder is not Sync).
unsafe impl Send for Decoder {}

impl Decoder {
    pub fn new(codec: Codec) -> Decoder {
        Decoder { codec, format: ptr::null(), session: ptr::null(), sink: Box::new(Sink { last: None, errors: 0 }) }
    }

    pub fn errors(&self) -> u64 {
        self.sink.errors
    }

    pub fn decode(&mut self, keyframe: bool, parameter_sets: &[Vec<u8>], avcc: &[u8]) -> Option<DecodedFrame> {
        if keyframe && !parameter_sets.is_empty() {
            self.update_format(parameter_sets);
        }
        if self.session.is_null() {
            return None;
        }
        unsafe {
            let mut block: CMBlockBufferRef = ptr::null();
            if CMBlockBufferCreateWithMemoryBlock(kCFAllocatorDefault, ptr::null_mut(), avcc.len(), kCFAllocatorDefault,
                ptr::null(), 0, avcc.len(), ASSURE_MEMORY_NOW, &mut block) != 0 {
                return None;
            }
            CMBlockBufferReplaceDataBytes(avcc.as_ptr() as *const c_void, block, 0, avcc.len());
            let mut sample: CMSampleBufferRef = ptr::null();
            let size = avcc.len();
            let st = CMSampleBufferCreateReady(kCFAllocatorDefault, block, self.format, 1, 0, ptr::null(), 1, &size, &mut sample);
            CFRelease(block);
            if st != 0 {
                return None;
            }
            let st = VTDecompressionSessionDecodeFrame(self.session, sample, 0, ptr::null_mut(), ptr::null_mut());
            CFRelease(sample);
            if st != 0 {
                self.sink.errors += 1;
                return None;
            }
        }
        self.sink.last.take()
    }

    fn update_format(&mut self, params: &[Vec<u8>]) {
        let pointers: Vec<*const u8> = params.iter().map(|p| p.as_ptr()).collect();
        let sizes: Vec<usize> = params.iter().map(Vec::len).collect();
        let mut format: CMFormatDescriptionRef = ptr::null();
        let st = unsafe {
            match self.codec {
                Codec::H264 => CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    kCFAllocatorDefault, params.len(), pointers.as_ptr(), sizes.as_ptr(), 4, &mut format),
                Codec::Hevc => CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                    kCFAllocatorDefault, params.len(), pointers.as_ptr(), sizes.as_ptr(), 4, ptr::null(), &mut format),
            }
        };
        if st != 0 || format.is_null() {
            self.sink.errors += 1;
            return;
        }
        if !self.format.is_null() && !self.session.is_null() && unsafe { CMFormatDescriptionEqual(self.format, format) } != 0 {
            unsafe { CFRelease(format) };
            return;
        }
        self.teardown();
        self.format = format;
        unsafe {
            let attrs = CFDictionary::<CFType, CFType>::from_CFType_pairs(&[
                (CFString::wrap_under_get_rule(kCVPixelBufferPixelFormatTypeKey).as_CFType(), CFNumber::from(NV12_VIDEO_RANGE).as_CFType()),
                (CFString::wrap_under_get_rule(kCVPixelBufferIOSurfacePropertiesKey).as_CFType(),
                 CFDictionary::<CFType, CFType>::from_CFType_pairs(&[]).as_CFType()),
            ]);
            let spec = CFDictionary::<CFType, CFType>::from_CFType_pairs(&[(
                CFString::wrap_under_get_rule(kVTVideoDecoderSpecification_RequireHardwareAcceleratedVideoDecoder).as_CFType(),
                CFBoolean::true_value().as_CFType(),
            )]);
            let record = VTDecompressionOutputCallbackRecord { callback: on_decoded, ref_con: &mut *self.sink as *mut Sink as *mut c_void };
            let mut session: VTDecompressionSessionRef = ptr::null();
            if VTDecompressionSessionCreate(kCFAllocatorDefault, format, spec.as_concrete_TypeRef(), attrs.as_concrete_TypeRef(), &record, &mut session) == 0 {
                VTSessionSetProperty(session, kVTDecompressionPropertyKey_RealTime, CFBoolean::true_value().as_CFTypeRef());
                self.session = session;
            } else {
                self.sink.errors += 1;
            }
        }
    }

    fn teardown(&mut self) {
        unsafe {
            if !self.session.is_null() {
                VTDecompressionSessionInvalidate(self.session);
                CFRelease(self.session);
                self.session = ptr::null();
            }
            if !self.format.is_null() {
                CFRelease(self.format);
                self.format = ptr::null();
            }
        }
    }
}

impl Drop for Decoder {
    fn drop(&mut self) {
        self.teardown();
    }
}
