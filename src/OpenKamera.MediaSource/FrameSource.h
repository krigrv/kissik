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
    void Maps(UINT32 sw, UINT32 sh, UINT32 dw, UINT32 dh);
    void BgraToNv12(const BYTE* src, UINT32 sw, UINT32 sh, BYTE* dst, LONG stride, UINT32 dw, UINT32 dh);
    void BgraToRgb32(const BYTE* src, UINT32 sw, UINT32 sh, BYTE* dst, LONG stride, UINT32 dw, UINT32 dh);
    std::vector<UINT32> _xmap, _ymap;
    UINT32 _mapSW = 0, _mapSH = 0, _mapDW = 0, _mapDH = 0;

    HANDLE _mapping = nullptr;
    BYTE* _view = nullptr;
    std::vector<BYTE> _live;      // last complete live frame
    std::vector<BYTE> _scratch;   // copy in progress (a torn copy never replaces _live)
    UINT32 _liveW = 0, _liveH = 0;
    static constexpr ULONGLONG kHoldMs = 5000; // how long a gap stays hidden before the slate shows
    ULONGLONG _firstFill = 0;     // GetTickCount64 of the first sample
    std::vector<BYTE> _black;     // shown while waiting for the first frame
    ULONGLONG _liveAt = 0;        // GetTickCount64 when _live was copied
    std::vector<BYTE> _slate;
    UINT32 _slateW = 0, _slateH = 0;
};
