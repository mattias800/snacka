cmake_minimum_required(VERSION 3.20)
project(SnackaCaptureLinux LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Find required packages
find_package(PkgConfig REQUIRED)
pkg_check_modules(LIBVA REQUIRED libva libva-drm)
pkg_check_modules(X11 REQUIRED x11 xext xrandr)
pkg_check_modules(PULSE REQUIRED libpulse)

# RNNoise noise suppression library (Mozilla, BSD-3-Clause)
set(RNNOISE_SOURCES
    src/rnnoise/denoise.c
    src/rnnoise/rnn.c
    src/rnnoise/nnet.c
    src/rnnoise/nnet_default.c
    src/rnnoise/rnnoise_data.c
    src/rnnoise/rnnoise_tables.c
    src/rnnoise/pitch.c
    src/rnnoise/kiss_fft.c
    src/rnnoise/celt_lpc.c
    src/rnnoise/parse_lpcnet_weights.c
)

add_executable(SnackaCaptureLinux
    src/main.cpp
    src/VaapiEncoder.cpp
    src/VaapiEncoder.h
    src/X11Capturer.cpp
    src/X11Capturer.h
    src/V4L2Capturer.cpp
    src/V4L2Capturer.h
    src/PulseAudioCapturer.cpp
    src/PulseAudioCapturer.h
    src/PulseMicrophoneCapturer.cpp
    src/PulseMicrophoneCapturer.h
    src/SourceLister.cpp
    src/SourceLister.h
    src/Protocol.h
    ${RNNOISE_SOURCES}
)

target_include_directories(SnackaCaptureLinux PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${CMAKE_CURRENT_SOURCE_DIR}/src/rnnoise
    ${LIBVA_INCLUDE_DIRS}
    ${X11_INCLUDE_DIRS}
    ${PULSE_INCLUDE_DIRS}
)

# RNNoise compile definitions
target_compile_definitions(SnackaCaptureLinux PRIVATE HAVE_STDINT_H)

target_link_libraries(SnackaCaptureLinux PRIVATE
    ${LIBVA_LIBRARIES}
    ${X11_LIBRARIES}
    ${PULSE_LIBRARIES}
    pthread
)

target_compile_options(SnackaCaptureLinux PRIVATE
    ${LIBVA_CFLAGS_OTHER}
    ${X11_CFLAGS_OTHER}
    ${PULSE_CFLAGS_OTHER}
)

# Output to a predictable location
set_target_properties(SnackaCaptureLinux PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin"
)

# Install target
install(TARGETS SnackaCaptureLinux
    RUNTIME DESTINATION bin
)
