// Produces the pixels for each sample: the app's latest frame from shared memory, or the slate.
#pragma once
#include "Common.h"
#include "FrameChannel.h"

class FrameSource
{
public:
    FrameSource();
    ~FrameSource();

    // Fills an NV12 or RGB32 (top-down) buffer of width x height. Returns true if a live frame was used.
    bool Fill(BYTE* dst, LONG stride, UINT32 width, UINT32 height, const GUID& subtype);

private:
    bool CopyLive(std::vector<BYTE>& bgra, UINT32& w, UINT32& h);
    const std::vector<BYTE>& Slate(UINT32 width, UINT32 height);
    static void BgraToNv12(const BYTE* src, UINT32 sw, UINT32 sh, BYTE* dst, LONG stride, UINT32 dw, UINT32 dh);
    static void BgraToRgb32(const BYTE* src, UINT32 sw, UINT32 sh, BYTE* dst, LONG stride, UINT32 dw, UINT32 dh);

    HANDLE _mapping = nullptr;
    BYTE* _view = nullptr;
    std::vector<BYTE> _live;      // last complete live frame
    std::vector<BYTE> _scratch;   // copy in progress (a torn copy never replaces _live)
    UINT32 _liveW = 0, _liveH = 0;
    ULONGLONG _liveAt = 0;        // GetTickCount64 when _live was copied
    std::vector<BYTE> _slate;
    UINT32 _slateW = 0, _slateH = 0;
};
