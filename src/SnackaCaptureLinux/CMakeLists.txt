cmake_minimum_required(VERSION 3.20)
project(SnackaCaptureLinux LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Find required packages
find_package(PkgConfig REQUIRED)
pkg_check_modules(LIBVA REQUIRED libva libva-drm)
pkg_check_modules(X11 REQUIRED x11 xext xrandr)

add_executable(SnackaCaptureLinux
    src/main.cpp
    src/VaapiEncoder.cpp
    src/VaapiEncoder.h
    src/X11Capturer.cpp
    src/X11Capturer.h
    src/SourceLister.cpp
    src/SourceLister.h
    src/Protocol.h
)

target_include_directories(SnackaCaptureLinux PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${LIBVA_INCLUDE_DIRS}
    ${X11_INCLUDE_DIRS}
)

target_link_libraries(SnackaCaptureLinux PRIVATE
    ${LIBVA_LIBRARIES}
    ${X11_LIBRARIES}
    pthread
)

target_compile_options(SnackaCaptureLinux PRIVATE
    ${LIBVA_CFLAGS_OTHER}
    ${X11_CFLAGS_OTHER}
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
