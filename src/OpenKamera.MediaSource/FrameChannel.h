// Shared-memory frame channel between OpenKamera (writer) and this media source (reader).
// Keep in sync with src/OpenKamera.Core/VirtualCameraChannel.cs.
#pragma once
#include <windows.h>
#include <cstdint>

namespace okvc
{
    // Created by the media source (it runs in the Windows camera service, which may create Global\ objects);
    // the app opens it when a meeting app is using the camera.
    constexpr wchar_t kMappingName[] = L"Global\\OpenKameraVirtualCamera";
    constexpr uint32_t kMagic = 0x43564B4F; // "OKVC"
    constexpr uint32_t kVersion = 1;
    constexpr uint32_t kMaxWidth = 1920, kMaxHeight = 1080;

#pragma pack(push, 8)
    struct FrameHeader
    {
        uint32_t magic;
        uint32_t version;
        volatile LONG sequence;   // seqlock: odd while the writer is copying, even when stable
        uint32_t width;           // pixels of the BGRA frame below (<= kMaxWidth/kMaxHeight)
        uint32_t height;
        uint32_t stride;          // bytes per row (width * 4)
        int64_t timestamp;        // writer's FILETIME (UTC, 100 ns) of the frame
        uint32_t writerPid;
        uint32_t clients;         // incremented by the source while a meeting app is reading
        uint32_t reserved[8];
    };
#pragma pack(pop)

    constexpr size_t kHeaderSize = 256;
    constexpr size_t kMappingSize = kHeaderSize + size_t(kMaxWidth) * kMaxHeight * 4;
    static_assert(sizeof(FrameHeader) <= kHeaderSize, "header too large");
}
