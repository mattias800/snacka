#include "VaapiEncoder.h"

#include <fcntl.h>
#include <unistd.h>
#include <cstring>
#include <iostream>
#include <arpa/inet.h>  // For htonl

namespace snacka {

VaapiEncoder::VaapiEncoder(int width, int height, int fps, int bitrateMbps)
    : m_width(width)
    , m_height(height)
    , m_fps(fps)
    , m_bitrate(bitrateMbps * 1000000)
    , m_gopSize(fps)  // Keyframe every second
{
}

VaapiEncoder::~VaapiEncoder() {
    Stop();
}

bool VaapiEncoder::Initialize() {
    if (m_initialized) {
        return true;
    }

    if (!OpenDrmDevice()) {
        std::cerr << "SnackaCaptureLinux: Failed to open DRM device\n";
        return false;
    }

    if (!CreateConfig()) {
        std::cerr << "SnackaCaptureLinux: Failed to create VAAPI config\n";
        Cleanup();
        return false;
    }

    if (!CreateSurfaces()) {
        std::cerr << "SnackaCaptureLinux: Failed to create VAAPI surfaces\n";
        Cleanup();
        return false;
    }

    if (!CreateContext()) {
        std::cerr << "SnackaCaptureLinux: Failed to create VAAPI context\n";
        Cleanup();
        return false;
    }

    if (!CreateCodedBuffer()) {
        std::cerr << "SnackaCaptureLinux: Failed to create coded buffer\n";
        Cleanup();
        return false;
    }

    m_initialized = true;
    std::cerr << "SnackaCaptureLinux: VAAPI encoder initialized (" << m_encoderName << ")\n";
    return true;
}

bool VaapiEncoder::OpenDrmDevice() {
    // Try common DRM device paths
    const char* drmPaths[] = {
        "/dev/dri/renderD128",  // Primary render node (preferred)
        "/dev/dri/renderD129",  // Secondary render node
        "/dev/dri/card0",       // Legacy path
        "/dev/dri/card1",       // Secondary card
    };

    for (const char* path : drmPaths) {
        m_drmFd = open(path, O_RDWR);
        if (m_drmFd < 0) {
            continue;
        }

        m_vaDisplay = vaGetDisplayDRM(m_drmFd);
        if (!m_vaDisplay) {
            close(m_drmFd);
            m_drmFd = -1;
            continue;
        }

        int major, minor;
        VAStatus status = vaInitialize(m_vaDisplay, &major, &minor);
        if (status != VA_STATUS_SUCCESS) {
            vaTerminate(m_vaDisplay);
            m_vaDisplay = nullptr;
            close(m_drmFd);
            m_drmFd = -1;
            continue;
        }

        // Get vendor string for encoder name
        const char* vendor = vaQueryVendorString(m_vaDisplay);
        if (vendor) {
            m_encoderName = "VAAPI ";
            m_encoderName += vendor;
        }

        std::cerr << "SnackaCaptureLinux: Using VAAPI " << major << "." << minor
                  << " on " << path << "\n";
        return true;
    }

    return false;
}

bool VaapiEncoder::CreateConfig() {
    // Query supported profiles
    int numProfiles = vaMaxNumProfiles(m_vaDisplay);
    std::vector<VAProfile> profiles(numProfiles);
    int actualProfiles = 0;
    VAStatus status = vaQueryConfigProfiles(m_vaDisplay, profiles.data(), &actualProfiles);
    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: Failed to query profiles\n";
        return false;
    }

    // Find H.264 encode profile (prefer Constrained Baseline for low latency)
    VAProfile desiredProfiles[] = {
        VAProfileH264ConstrainedBaseline,
        VAProfileH264Main,
        VAProfileH264High,
    };

    bool foundProfile = false;
    for (VAProfile desired : desiredProfiles) {
        for (int i = 0; i < actualProfiles; i++) {
            if (profiles[i] == desired) {
                m_profile = desired;
                foundProfile = true;
                break;
            }
        }
        if (foundProfile) break;
    }

    if (!foundProfile) {
        std::cerr << "SnackaCaptureLinux: No H.264 encode profile found\n";
        return false;
    }

    // Check for encode entrypoint
    int numEntrypoints = vaMaxNumEntrypoints(m_vaDisplay);
    std::vector<VAEntrypoint> entrypoints(numEntrypoints);
    int actualEntrypoints = 0;
    status = vaQueryConfigEntrypoints(m_vaDisplay, m_profile, entrypoints.data(), &actualEntrypoints);
    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: Failed to query entrypoints\n";
        return false;
    }

    bool hasEncodeEntrypoint = false;
    for (int i = 0; i < actualEntrypoints; i++) {
        if (entrypoints[i] == VAEntrypointEncSlice ||
            entrypoints[i] == VAEntrypointEncSliceLP) {
            hasEncodeEntrypoint = true;
            break;
        }
    }

    if (!hasEncodeEntrypoint) {
        std::cerr << "SnackaCaptureLinux: No encode entrypoint found\n";
        return false;
    }

    // Create config with rate control attribute
    VAConfigAttrib attribs[2];
    attribs[0].type = VAConfigAttribRTFormat;
    attribs[0].value = VA_RT_FORMAT_YUV420;
    attribs[1].type = VAConfigAttribRateControl;
    attribs[1].value = VA_RC_CBR;

    status = vaCreateConfig(m_vaDisplay, m_profile, VAEntrypointEncSlice,
                            attribs, 2, &m_configId);
    if (status != VA_STATUS_SUCCESS) {
        // Try low-power entrypoint
        status = vaCreateConfig(m_vaDisplay, m_profile, VAEntrypointEncSliceLP,
                                attribs, 2, &m_configId);
        if (status != VA_STATUS_SUCCESS) {
            std::cerr << "SnackaCaptureLinux: Failed to create config: " << vaErrorStr(status) << "\n";
            return false;
        }
    }

    return true;
}

bool VaapiEncoder::CreateSurfaces() {
    m_surfaces.resize(NUM_SURFACES);

    VAStatus status = vaCreateSurfaces(
        m_vaDisplay,
        VA_RT_FORMAT_YUV420,
        m_width,
        m_height,
        m_surfaces.data(),
        NUM_SURFACES,
        nullptr,
        0
    );

    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: Failed to create surfaces: " << vaErrorStr(status) << "\n";
        return false;
    }

    return true;
}

bool VaapiEncoder::CreateContext() {
    VAStatus status = vaCreateContext(
        m_vaDisplay,
        m_configId,
        m_width,
        m_height,
        VA_PROGRESSIVE,
        m_surfaces.data(),
        static_cast<int>(m_surfaces.size()),
        &m_contextId
    );

    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: Failed to create context: " << vaErrorStr(status) << "\n";
        return false;
    }

    return true;
}

bool VaapiEncoder::CreateCodedBuffer() {
    // Allocate buffer large enough for worst case (uncompressed frame)
    unsigned int codedBufSize = m_width * m_height * 3 / 2;

    VAStatus status = vaCreateBuffer(
        m_vaDisplay,
        m_contextId,
        VAEncCodedBufferType,
        codedBufSize,
        1,
        nullptr,
        &m_codedBuf
    );

    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: Failed to create coded buffer: " << vaErrorStr(status) << "\n";
        return false;
    }

    return true;
}

bool VaapiEncoder::EncodeNV12(const uint8_t* nv12Data, size_t size, int64_t timestampMs) {
    if (!m_initialized) {
        return false;
    }

    // Get current surface
    VASurfaceID surface = m_surfaces[m_currentSurface];

    // Upload NV12 data to surface
    VAImage image;
    VAStatus status = vaDeriveImage(m_vaDisplay, surface, &image);
    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: Failed to derive image: " << vaErrorStr(status) << "\n";
        return false;
    }

    void* imageData = nullptr;
    status = vaMapBuffer(m_vaDisplay, image.buf, &imageData);
    if (status != VA_STATUS_SUCCESS) {
        vaDestroyImage(m_vaDisplay, image.image_id);
        return false;
    }

    // Copy NV12 data (Y plane then UV plane)
    size_t ySize = static_cast<size_t>(m_width) * m_height;
    size_t uvSize = ySize / 2;

    // Copy Y plane
    uint8_t* dst = static_cast<uint8_t*>(imageData) + image.offsets[0];
    const uint8_t* src = nv12Data;
    for (int y = 0; y < m_height; y++) {
        memcpy(dst, src, m_width);
        dst += image.pitches[0];
        src += m_width;
    }

    // Copy UV plane
    dst = static_cast<uint8_t*>(imageData) + image.offsets[1];
    for (int y = 0; y < m_height / 2; y++) {
        memcpy(dst, src, m_width);
        dst += image.pitches[1];
        src += m_width;
    }

    vaUnmapBuffer(m_vaDisplay, image.buf);
    vaDestroyImage(m_vaDisplay, image.image_id);

    // Determine if this should be a keyframe
    bool isKeyframe = (m_frameCount % m_gopSize == 0);

    // Encode the frame
    if (!EncodeFrame(timestampMs, isKeyframe)) {
        return false;
    }

    // Get encoded data and output
    GetEncodedData(isKeyframe);

    // Update state
    m_refSurfaceIndex = m_currentSurface;
    m_refSurface = surface;
    m_currentSurface = (m_currentSurface + 1) % NUM_SURFACES;
    m_frameCount++;
    m_frameNumInGop++;

    if (isKeyframe) {
        m_frameNumInGop = 0;
        m_idrPicId++;
    }

    return true;
}

bool VaapiEncoder::EncodeFrame(int64_t timestampMs, bool forceKeyframe) {
    VASurfaceID currentSurface = m_surfaces[m_currentSurface];
    bool isIdr = forceKeyframe || (m_frameCount == 0);

    // Begin picture
    VAStatus status = vaBeginPicture(m_vaDisplay, m_contextId, currentSurface);
    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: vaBeginPicture failed: " << vaErrorStr(status) << "\n";
        return false;
    }

    // Render picture (creates parameter buffers and submits them)
    if (!RenderPicture(currentSurface, isIdr)) {
        vaEndPicture(m_vaDisplay, m_contextId);
        return false;
    }

    // End picture
    status = vaEndPicture(m_vaDisplay, m_contextId);
    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: vaEndPicture failed: " << vaErrorStr(status) << "\n";
        return false;
    }

    // Wait for encoding to complete
    status = vaSyncSurface(m_vaDisplay, currentSurface);
    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: vaSyncSurface failed: " << vaErrorStr(status) << "\n";
        return false;
    }

    return true;
}

bool VaapiEncoder::RenderPicture(VASurfaceID surface, bool isIdr) {
    VAStatus status;

    // Sequence parameter buffer (SPS) - only for IDR frames
    if (isIdr) {
        VAEncSequenceParameterBufferH264 seqParam = {};

        seqParam.level_idc = 41;  // Level 4.1
        seqParam.intra_period = m_gopSize;
        seqParam.intra_idr_period = m_gopSize;
        seqParam.ip_period = 1;  // No B-frames

        seqParam.bits_per_second = m_bitrate;
        seqParam.max_num_ref_frames = 1;

        seqParam.picture_width_in_mbs = (m_width + 15) / 16;
        seqParam.picture_height_in_mbs = (m_height + 15) / 16;

        seqParam.seq_fields.bits.chroma_format_idc = 1;  // 4:2:0
        seqParam.seq_fields.bits.frame_mbs_only_flag = 1;
        seqParam.seq_fields.bits.direct_8x8_inference_flag = 1;

        seqParam.bit_depth_luma_minus8 = 0;
        seqParam.bit_depth_chroma_minus8 = 0;

        seqParam.vui_parameters_present_flag = 1;
        seqParam.vui_fields.bits.timing_info_present_flag = 1;
        seqParam.num_units_in_tick = 1;
        seqParam.time_scale = m_fps * 2;

        status = vaCreateBuffer(m_vaDisplay, m_contextId, VAEncSequenceParameterBufferType,
                                sizeof(seqParam), 1, &seqParam, &m_seqParamBuf);
        if (status != VA_STATUS_SUCCESS) {
            std::cerr << "SnackaCaptureLinux: Failed to create seq param buffer\n";
            return false;
        }

        status = vaRenderPicture(m_vaDisplay, m_contextId, &m_seqParamBuf, 1);
        if (status != VA_STATUS_SUCCESS) {
            vaDestroyBuffer(m_vaDisplay, m_seqParamBuf);
            return false;
        }
        vaDestroyBuffer(m_vaDisplay, m_seqParamBuf);
    }

    // Picture parameter buffer (PPS)
    VAEncPictureParameterBufferH264 picParam = {};

    picParam.CurrPic.picture_id = surface;
    picParam.CurrPic.TopFieldOrderCnt = m_frameCount * 2;
    picParam.CurrPic.flags = 0;

    if (!isIdr && m_refSurface != VA_INVALID_SURFACE) {
        picParam.ReferenceFrames[0].picture_id = m_refSurface;
        picParam.ReferenceFrames[0].TopFieldOrderCnt = (m_frameCount - 1) * 2;
        picParam.ReferenceFrames[0].flags = 0;
    }
    for (int i = (isIdr ? 0 : 1); i < 16; i++) {
        picParam.ReferenceFrames[i].picture_id = VA_INVALID_SURFACE;
        picParam.ReferenceFrames[i].flags = VA_PICTURE_H264_INVALID;
    }

    picParam.coded_buf = m_codedBuf;
    picParam.pic_fields.bits.idr_pic_flag = isIdr ? 1 : 0;
    picParam.pic_fields.bits.reference_pic_flag = 1;
    picParam.pic_fields.bits.entropy_coding_mode_flag = 0;  // CAVLC for baseline
    picParam.pic_fields.bits.transform_8x8_mode_flag = 0;
    picParam.pic_fields.bits.deblocking_filter_control_present_flag = 1;

    picParam.frame_num = isIdr ? 0 : m_frameNumInGop;
    picParam.pic_init_qp = 26;

    status = vaCreateBuffer(m_vaDisplay, m_contextId, VAEncPictureParameterBufferType,
                            sizeof(picParam), 1, &picParam, &m_picParamBuf);
    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: Failed to create pic param buffer\n";
        return false;
    }

    status = vaRenderPicture(m_vaDisplay, m_contextId, &m_picParamBuf, 1);
    if (status != VA_STATUS_SUCCESS) {
        vaDestroyBuffer(m_vaDisplay, m_picParamBuf);
        return false;
    }
    vaDestroyBuffer(m_vaDisplay, m_picParamBuf);

    // Slice parameter buffer
    VAEncSliceParameterBufferH264 sliceParam = {};

    sliceParam.macroblock_address = 0;
    sliceParam.num_macroblocks = ((m_width + 15) / 16) * ((m_height + 15) / 16);
    sliceParam.slice_type = isIdr ? 2 : 0;  // I-slice or P-slice
    sliceParam.idr_pic_id = m_idrPicId;
    sliceParam.pic_order_cnt_lsb = (m_frameCount * 2) % 256;
    sliceParam.direct_spatial_mv_pred_flag = 0;
    sliceParam.num_ref_idx_l0_active_minus1 = isIdr ? 0 : 0;
    sliceParam.num_ref_idx_l1_active_minus1 = 0;
    sliceParam.cabac_init_idc = 0;
    sliceParam.slice_qp_delta = 0;
    sliceParam.disable_deblocking_filter_idc = 0;
    sliceParam.slice_alpha_c0_offset_div2 = 0;
    sliceParam.slice_beta_offset_div2 = 0;

    if (!isIdr && m_refSurface != VA_INVALID_SURFACE) {
        sliceParam.RefPicList0[0].picture_id = m_refSurface;
        sliceParam.RefPicList0[0].TopFieldOrderCnt = (m_frameCount - 1) * 2;
        sliceParam.RefPicList0[0].flags = 0;
    }
    for (int i = (isIdr ? 0 : 1); i < 32; i++) {
        sliceParam.RefPicList0[i].picture_id = VA_INVALID_SURFACE;
        sliceParam.RefPicList0[i].flags = VA_PICTURE_H264_INVALID;
    }
    for (int i = 0; i < 32; i++) {
        sliceParam.RefPicList1[i].picture_id = VA_INVALID_SURFACE;
        sliceParam.RefPicList1[i].flags = VA_PICTURE_H264_INVALID;
    }

    status = vaCreateBuffer(m_vaDisplay, m_contextId, VAEncSliceParameterBufferType,
                            sizeof(sliceParam), 1, &sliceParam, &m_sliceParamBuf);
    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: Failed to create slice param buffer\n";
        return false;
    }

    status = vaRenderPicture(m_vaDisplay, m_contextId, &m_sliceParamBuf, 1);
    if (status != VA_STATUS_SUCCESS) {
        vaDestroyBuffer(m_vaDisplay, m_sliceParamBuf);
        return false;
    }
    vaDestroyBuffer(m_vaDisplay, m_sliceParamBuf);

    return true;
}

bool VaapiEncoder::GetEncodedData(bool isKeyframe) {
    VACodedBufferSegment* bufferSegment = nullptr;

    VAStatus status = vaMapBuffer(m_vaDisplay, m_codedBuf, reinterpret_cast<void**>(&bufferSegment));
    if (status != VA_STATUS_SUCCESS) {
        std::cerr << "SnackaCaptureLinux: Failed to map coded buffer: " << vaErrorStr(status) << "\n";
        return false;
    }

    // Process all segments
    while (bufferSegment != nullptr) {
        if (bufferSegment->buf && bufferSegment->size > 0) {
            // Convert Annex-B to AVCC and invoke callback
            ConvertAnnexBToAVCC(
                static_cast<const uint8_t*>(bufferSegment->buf),
                bufferSegment->size,
                isKeyframe
            );
        }
        bufferSegment = reinterpret_cast<VACodedBufferSegment*>(bufferSegment->next);
    }

    vaUnmapBuffer(m_vaDisplay, m_codedBuf);
    return true;
}

void VaapiEncoder::ConvertAnnexBToAVCC(const uint8_t* annexB, size_t size, bool isKeyframe) {
    m_avccBuffer.clear();

    // Parse Annex-B format and convert to AVCC (4-byte length prefix)
    size_t i = 0;
    while (i < size) {
        // Find start code (0x000001 or 0x00000001)
        size_t startCodeLen = 0;

        if (i + 4 <= size &&
            annexB[i] == 0 && annexB[i+1] == 0 &&
            annexB[i+2] == 0 && annexB[i+3] == 1) {
            startCodeLen = 4;
        } else if (i + 3 <= size &&
                   annexB[i] == 0 && annexB[i+1] == 0 && annexB[i+2] == 1) {
            startCodeLen = 3;
        }

        if (startCodeLen == 0) {
            i++;
            continue;
        }

        size_t nalStart = i + startCodeLen;

        // Find next start code or end of data
        size_t nalEnd = size;
        for (size_t j = nalStart; j + 3 <= size; j++) {
            if (annexB[j] == 0 && annexB[j+1] == 0) {
                if (annexB[j+2] == 1) {
                    nalEnd = j;
                    break;
                } else if (j + 4 <= size && annexB[j+2] == 0 && annexB[j+3] == 1) {
                    nalEnd = j;
                    break;
                }
            }
        }

        // Get NAL unit type
        if (nalStart < nalEnd) {
            uint8_t nalType = annexB[nalStart] & 0x1F;

            // Store SPS/PPS for later use
            if (nalType == 7) {  // SPS
                m_sps.assign(annexB + nalStart, annexB + nalEnd);
            } else if (nalType == 8) {  // PPS
                m_pps.assign(annexB + nalStart, annexB + nalEnd);
                m_haveSpsPs = true;
            }

            // Write NAL unit in AVCC format: 4-byte BE length + NAL data
            size_t nalSize = nalEnd - nalStart;
            uint32_t beLength = htonl(static_cast<uint32_t>(nalSize));

            size_t offset = m_avccBuffer.size();
            m_avccBuffer.resize(offset + 4 + nalSize);
            memcpy(m_avccBuffer.data() + offset, &beLength, 4);
            memcpy(m_avccBuffer.data() + offset + 4, annexB + nalStart, nalSize);
        }

        i = nalEnd;
    }

    // Invoke callback with AVCC data
    if (!m_avccBuffer.empty() && m_callback) {
        m_callback(m_avccBuffer.data(), m_avccBuffer.size(), isKeyframe);
    }
}

void VaapiEncoder::Flush() {
    // Nothing to flush in synchronous mode
}

void VaapiEncoder::Stop() {
    Cleanup();
}

void VaapiEncoder::Cleanup() {
    if (m_codedBuf != VA_INVALID_ID && m_vaDisplay) {
        vaDestroyBuffer(m_vaDisplay, m_codedBuf);
        m_codedBuf = VA_INVALID_ID;
    }

    if (m_contextId != VA_INVALID_ID && m_vaDisplay) {
        vaDestroyContext(m_vaDisplay, m_contextId);
        m_contextId = VA_INVALID_ID;
    }

    for (auto& surface : m_surfaces) {
        if (surface != VA_INVALID_SURFACE && m_vaDisplay) {
            vaDestroySurfaces(m_vaDisplay, &surface, 1);
        }
    }
    m_surfaces.clear();

    if (m_configId != VA_INVALID_ID && m_vaDisplay) {
        vaDestroyConfig(m_vaDisplay, m_configId);
        m_configId = VA_INVALID_ID;
    }

    if (m_vaDisplay) {
        vaTerminate(m_vaDisplay);
        m_vaDisplay = nullptr;
    }

    if (m_drmFd >= 0) {
        close(m_drmFd);
        m_drmFd = -1;
    }

    m_initialized = false;
}

bool VaapiEncoder::IsHardwareEncoderAvailable() {
    // Try to open DRM device and check for H.264 encode support
    const char* drmPaths[] = {
        "/dev/dri/renderD128",
        "/dev/dri/renderD129",
        "/dev/dri/card0",
    };

    for (const char* path : drmPaths) {
        int fd = open(path, O_RDWR);
        if (fd < 0) continue;

        VADisplay display = vaGetDisplayDRM(fd);
        if (!display) {
            close(fd);
            continue;
        }

        int major, minor;
        if (vaInitialize(display, &major, &minor) != VA_STATUS_SUCCESS) {
            close(fd);
            continue;
        }

        // Query profiles for H.264 encode support
        int numProfiles = vaMaxNumProfiles(display);
        std::vector<VAProfile> profiles(numProfiles);
        int actualProfiles = 0;
        vaQueryConfigProfiles(display, profiles.data(), &actualProfiles);

        bool hasH264 = false;
        for (int i = 0; i < actualProfiles; i++) {
            if (profiles[i] == VAProfileH264ConstrainedBaseline ||
                profiles[i] == VAProfileH264Main ||
                profiles[i] == VAProfileH264High) {

                // Check for encode entrypoint
                int numEntrypoints = vaMaxNumEntrypoints(display);
                std::vector<VAEntrypoint> entrypoints(numEntrypoints);
                int actualEntrypoints = 0;
                vaQueryConfigEntrypoints(display, profiles[i], entrypoints.data(), &actualEntrypoints);

                for (int j = 0; j < actualEntrypoints; j++) {
                    if (entrypoints[j] == VAEntrypointEncSlice ||
                        entrypoints[j] == VAEntrypointEncSliceLP) {
                        hasH264 = true;
                        break;
                    }
                }
                if (hasH264) break;
            }
        }

        vaTerminate(display);
        close(fd);

        if (hasH264) {
            return true;
        }
    }

    return false;
}

}  // namespace snacka
