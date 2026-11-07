#include "FrameSource.h"
#include <sddl.h>
#include <algorithm>
#include <cmath>

using namespace okvc;

FrameSource::FrameSource()
{
    // Everyone (and low-integrity / app-container callers) may open and write the mapping: it only ever holds
    // the user's own camera picture, and the app runs as the signed-in user, not as the camera service.
    PSECURITY_DESCRIPTOR sd = nullptr;
    SECURITY_ATTRIBUTES sa{ sizeof(sa), nullptr, FALSE };
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;WD)(A;;GA;;;AC)S:(ML;;NW;;;LW)", SDDL_REVISION_1, &sd, nullptr))
        sa.lpSecurityDescriptor = sd;
    _mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, sa.lpSecurityDescriptor ? &sa : nullptr, PAGE_READWRITE,
        static_cast<DWORD>(kMappingSize >> 32), static_cast<DWORD>(kMappingSize & 0xFFFFFFFF), kMappingName);
    if (!_mapping) // not allowed to create Global\ (e.g. running in a test process): try the session namespace
        _mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
            static_cast<DWORD>(kMappingSize >> 32), static_cast<DWORD>(kMappingSize & 0xFFFFFFFF), L"Local\\OpenKameraVirtualCamera");
    if (sd) LocalFree(sd);
    if (_mapping)
    {
        _view = static_cast<BYTE*>(MapViewOfFile(_mapping, FILE_MAP_ALL_ACCESS, 0, 0, kMappingSize));
        if (_view)
        {
            auto* h = reinterpret_cast<FrameHeader*>(_view);
            if (h->magic != kMagic) { ZeroMemory(h, sizeof(FrameHeader)); h->magic = kMagic; h->version = kVersion; }
            InterlockedIncrement(reinterpret_cast<volatile LONG*>(&h->clients));
        }
    }
}

FrameSource::~FrameSource()
{
    if (_view)
    {
        InterlockedDecrement(reinterpret_cast<volatile LONG*>(&reinterpret_cast<FrameHeader*>(_view)->clients));
        UnmapViewOfFile(_view);
    }
    if (_mapping) CloseHandle(_mapping);
}

bool FrameSource::CopyLive(std::vector<BYTE>& bgra, UINT32& w, UINT32& h)
{
    if (!_view) return false;
    auto* hdr = reinterpret_cast<FrameHeader*>(_view);
    for (int attempt = 0; attempt < 8; attempt++)
    {
        LONG before = hdr->sequence;
        if (before & 1) { Sleep(1); continue; } // a full-HD write takes a few ms
        MemoryBarrier();
        w = hdr->width; h = hdr->height;
        if (w == 0 || h == 0 || w > kMaxWidth || h > kMaxHeight || hdr->stride != w * 4) return false;
        // Stale after 1.5 s: no longer a new frame (Fill holds the last one briefly, then shows the slate).
        FILETIME ft; GetSystemTimeAsFileTime(&ft);
        int64_t now = (int64_t(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
        if (now - hdr->timestamp > 15'000'000) return false;
        size_t bytes = size_t(w) * h * 4;
        bgra.resize(bytes);
        memcpy(bgra.data(), _view + kHeaderSize, bytes);
        MemoryBarrier();
        if (hdr->sequence == before) return true;
    }
    return false;
}

const std::vector<BYTE>& FrameSource::Slate(UINT32 width, UINT32 height)
{
    if (_slateW == width && _slateH == height && !_slate.empty()) return _slate;
    _slateW = width; _slateH = height;
    _slate.assign(size_t(width) * height * 4, 0);

    // Dark gradient + the KISSIK framing mark (orange corners around a lens) + two lines of text, drawn with GDI into a top-down DIB.
    BITMAPINFO bi{}; bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = LONG(width); bi.bmiHeader.biHeight = -LONG(height);
    bi.bmiHeader.biPlanes = 1; bi.bmiHeader.biBitCount = 32; bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HDC dc = CreateCompatibleDC(nullptr);
    HBITMAP bmp = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (!dc || !bmp || !bits) { if (bmp) DeleteObject(bmp); if (dc) DeleteDC(dc); return _slate; }
    HGDIOBJ old = SelectObject(dc, bmp);
    auto* px = static_cast<BYTE*>(bits);
    for (UINT32 y = 0; y < height; y++)
    {
        double t = double(y) / height;
        BYTE r = BYTE(28 - 16 * t), g = BYTE(32 - 18 * t), b = BYTE(40 - 22 * t);
        for (UINT32 x = 0; x < width; x++) { BYTE* p = px + (size_t(y) * width + x) * 4; p[0] = b; p[1] = g; p[2] = r; p[3] = 255; }
    }
    int cx = int(width) / 2, cy = int(height) * 2 / 5, ring = int(height) / 7, band = std::max(4, int(height) / 40);
    HPEN pen = CreatePen(PS_SOLID, band, RGB(0xFF, 0x7A, 0x1A));
    HGDIOBJ oldPen = SelectObject(dc, pen);
    HGDIOBJ oldBrush = SelectObject(dc, GetStockObject(NULL_BRUSH));
    int lens = ring / 2, arm = ring * 3 / 4;
    Ellipse(dc, cx - lens, cy - lens, cx + lens, cy + lens);
    // Framing corners: top right and bottom left, as on the app icon.
    MoveToEx(dc, cx + ring - arm, cy - ring, nullptr); LineTo(dc, cx + ring, cy - ring); LineTo(dc, cx + ring, cy - ring + arm);
    MoveToEx(dc, cx - ring, cy + ring - arm, nullptr); LineTo(dc, cx - ring, cy + ring); LineTo(dc, cx - ring + arm, cy + ring);
    HBRUSH dot = CreateSolidBrush(RGB(0xFF, 0x7A, 0x1A));
    SelectObject(dc, dot);
    SelectObject(dc, GetStockObject(NULL_PEN));
    int d = std::max(3, ring / 6);
    Ellipse(dc, cx - d, cy - d, cx + d, cy + d);
    SelectObject(dc, oldBrush); SelectObject(dc, oldPen);
    DeleteObject(dot); DeleteObject(pen);

    SetBkMode(dc, TRANSPARENT);
    auto text = [&](const wchar_t* s, int size, int weight, COLORREF color, int top)
    {
        HFONT f = CreateFontW(-size, 0, 0, 0, weight, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
        HGDIOBJ of = SelectObject(dc, f);
        SetTextColor(dc, color);
        RECT rc{ 0, top, LONG(width), top + size * 2 };
        DrawTextW(dc, s, -1, &rc, DT_CENTER | DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(dc, of); DeleteObject(f);
    };
    text(L"KISSIK", int(height) / 14, FW_SEMIBOLD, RGB(0xF4, 0xF5, 0xF7), cy + ring + int(height) / 20);
    text(L"Open the KISSIK app to start your camera", int(height) / 30, FW_NORMAL, RGB(0xB8, 0xBC, 0xC2), cy + ring + int(height) / 20 + int(height) / 9);
    GdiFlush();
    memcpy(_slate.data(), bits, _slate.size());
    for (size_t i = 3; i < _slate.size(); i += 4) _slate[i] = 255; // GDI leaves alpha at 0
    SelectObject(dc, old);
    DeleteObject(bmp); DeleteDC(dc);
    return _slate;
}

bool FrameSource::Fill(BYTE* dst, LONG stride, UINT32 width, UINT32 height, const GUID& subtype)
{
    UINT32 w = 0, h = 0;
    ULONGLONG now = GetTickCount64();
    if (CopyLive(_scratch, w, h)) { _live.swap(_scratch); _liveW = w; _liveH = h; _liveAt = now; }
    if (_firstFill == 0) _firstFill = now;
    // A short gap (the app caught mid-write, or switching camera): hold the last good frame, so no card flashes up.
    bool live = _liveAt != 0 && now - _liveAt < kHoldMs;
    // Just opened: the app takes a moment to notice the meeting and start sending. Black until then, not the card.
    bool waiting = _liveAt == 0 && now - _firstFill < kHoldMs;
    const BYTE* src; UINT32 sw, sh;
    if (live) { src = _live.data(); sw = _liveW; sh = _liveH; }
    else if (waiting)
    {
        _black.resize(size_t(width) * height * 4);
        for (size_t i = 0; i < _black.size(); i += 4) { _black[i] = 0; _black[i + 1] = 0; _black[i + 2] = 0; _black[i + 3] = 255; }
        src = _black.data(); sw = width; sh = height;
    }
    else { const auto& s = Slate(width, height); src = s.data(); sw = width; sh = height; }
    if (subtype == MFVideoFormat_NV12) BgraToNv12(src, sw, sh, dst, stride, width, height);
    else BgraToRgb32(src, sw, sh, dst, stride, width, height);
    return live;
}

// Cover-fit nearest-neighbour sampling: the source fills the output, centre-cropped.
static inline void MapCover(UINT32 sw, UINT32 sh, UINT32 dw, UINT32 dh, double& scale, double& offX, double& offY)
{
    scale = std::max(double(dw) / sw, double(dh) / sh);
    offX = (sw - dw / scale) / 2.0; offY = (sh - dh / scale) / 2.0;
}

// Source column/row for each output column/row, computed once per size pair (was a division per pixel,
// which at 1080p took longer than a frame and made the meeting picture stutter).
void FrameSource::Maps(UINT32 sw, UINT32 sh, UINT32 dw, UINT32 dh)
{
    if (sw == _mapSW && sh == _mapSH && dw == _mapDW && dh == _mapDH) return;
    _mapSW = sw; _mapSH = sh; _mapDW = dw; _mapDH = dh;
    double s, ox, oy; MapCover(sw, sh, dw, dh, s, ox, oy);
    _xmap.resize(dw); _ymap.resize(dh);
    for (UINT32 x = 0; x < dw; x++) _xmap[x] = std::min(sw - 1, UINT32(ox + (x + 0.5) / s));
    for (UINT32 y = 0; y < dh; y++) _ymap[y] = std::min(sh - 1, UINT32(oy + (y + 0.5) / s));
}

void FrameSource::BgraToRgb32(const BYTE* src, UINT32 sw, UINT32 sh, BYTE* dst, LONG stride, UINT32 dw, UINT32 dh)
{
    Maps(sw, sh, dw, dh);
    for (UINT32 y = 0; y < dh; y++)
    {
        auto* row = reinterpret_cast<UINT32*>(dst + LONG(y) * stride);
        const auto* srow = reinterpret_cast<const UINT32*>(src + size_t(_ymap[y]) * sw * 4);
        if (sw == dw) { for (UINT32 x = 0; x < dw; x++) row[x] = srow[x] | 0xFF000000u; continue; }
        for (UINT32 x = 0; x < dw; x++) row[x] = srow[_xmap[x]] | 0xFF000000u;
    }
}

void FrameSource::BgraToNv12(const BYTE* src, UINT32 sw, UINT32 sh, BYTE* dst, LONG stride, UINT32 dw, UINT32 dh)
{
    Maps(sw, sh, dw, dh);
    const UINT32* xm = _xmap.data();
    BYTE* yPlane = dst;
    BYTE* uv = dst + size_t(stride) * dh;
    for (UINT32 y = 0; y < dh; y++)
    {
        const BYTE* srow = src + size_t(_ymap[y]) * sw * 4;
        BYTE* yrow = yPlane + size_t(y) * stride;
        for (UINT32 x = 0; x < dw; x++)
        {
            const BYTE* p = srow + size_t(xm[x]) * 4;
            yrow[x] = BYTE(((66 * p[2] + 129 * p[1] + 25 * p[0] + 128) >> 8) + 16);
        }
        if ((y & 1) == 0)
        {
            BYTE* uvrow = uv + size_t(y / 2) * stride;
            for (UINT32 x = 0; x + 1 < dw + 1; x += 2)
            {
                const BYTE* p = srow + size_t(xm[x]) * 4;
                int r = p[2], g = p[1], b = p[0];
                uvrow[x] = BYTE(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                if (x + 1 < dw) uvrow[x + 1] = BYTE(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
        }
    }
}
