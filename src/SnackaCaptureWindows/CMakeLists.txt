cmake_minimum_required(VERSION 3.20)
project(SnackaCaptureWindows LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Windows 10+ required for Desktop Duplication and Windows.Graphics.Capture
add_definitions(-D_WIN32_WINNT=0x0A00 -DWINVER=0x0A00 -DUNICODE -D_UNICODE)

# Enable WinRT/C++ support
if(MSVC)
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} /await /EHsc /permissive-")
    # C++/WinRT requires these
    add_compile_options(/Zc:twoPhase-)
endif()

add_executable(SnackaCaptureWindows
    src/main.cpp
    src/SourceLister.cpp
    src/SourceLister.h
    src/DisplayCapturer.cpp
    src/DisplayCapturer.h
    src/WindowCapturer.cpp
    src/WindowCapturer.h
    src/CameraCapturer.cpp
    src/CameraCapturer.h
    src/AudioCapturer.cpp
    src/AudioCapturer.h
    src/ColorConverter.cpp
    src/ColorConverter.h
    src/MediaFoundationEncoder.cpp
    src/MediaFoundationEncoder.h
    src/Protocol.h
)

# Link required Windows libraries
target_link_libraries(SnackaCaptureWindows PRIVATE
    d3d11
    dxgi
    d3dcompiler
    windowsapp  # For WinRT
    ole32
    uuid
    # Media Foundation for hardware H.264 encoding
    mf
    mfplat
    mfuuid
    mfreadwrite
    ws2_32  # For htonl in AVCC format
)

# Include directories
target_include_directories(SnackaCaptureWindows PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
)

# Find and use C++/WinRT
find_package(cppwinrt QUIET)
if(NOT cppwinrt_FOUND)
    # Download cppwinrt if not found
    include(FetchContent)
    FetchContent_Declare(
        cppwinrt
        URL https://github.com/microsoft/cppwinrt/releases/download/2.0.230524.4/Microsoft.Windows.CppWinRT.2.0.230524.4.nupkg
        URL_HASH SHA256=2f81a88b9d1e5e7b8d8e9a9e8a8e7a9b8c7d6e5f4a3b2c1d0e9f8a7b6c5d4e3f
    )
endif()

# Output to a predictable location
set_target_properties(SnackaCaptureWindows PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin"
)

# Install target
install(TARGETS SnackaCaptureWindows
    RUNTIME DESTINATION bin
)
