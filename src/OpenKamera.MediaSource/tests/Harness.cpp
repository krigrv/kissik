// Test harness: loads the media source through COM (per-user registration, no admin), starts it, pulls
// samples, and checks the slate and a live frame written through the shared-memory channel.
#include "../Common.h"
#include "../FrameChannel.h"
#include <cstdio>

static int g_failures = 0;
#define CHECK(cond, msg) do { if (!(cond)) { printf("FAIL: %s\n", msg); g_failures++; } else printf("ok:   %s\n", msg); } while (0)

void ModuleAddRef() {}
void ModuleRelease() {}

static HRESULT WaitEvent(IMFMediaEventGenerator* gen, MediaEventType want, IMFMediaEvent** out)
{
    for (int i = 0; i < 20; i++)
    {
        ComPtr<IMFMediaEvent> e;
        HRESULT hr = gen->GetEvent(MF_EVENT_FLAG_NO_WAIT, &e);
        if (hr == MF_E_NO_EVENTS_AVAILABLE) { Sleep(50); continue; }
        RETURN_IF_FAILED(hr);
        MediaEventType t{}; e->GetType(&t);
        if (t == want) { *out = e.Detach(); return S_OK; }
    }
    return E_FAIL;
}

static HRESULT PullSample(IMFMediaStream* stream, IMFSample** sample)
{
    RETURN_IF_FAILED(stream->RequestSample(nullptr));
    ComPtr<IMFMediaEvent> e;
    RETURN_IF_FAILED(WaitEvent(stream, MEMediaSample, &e));
    PROPVARIANT v; PropVariantInit(&v);
    RETURN_IF_FAILED(e->GetValue(&v));
    HRESULT hr = v.punkVal->QueryInterface(IID_PPV_ARGS(sample));
    PropVariantClear(&v);
    return hr;
}

static BYTE FirstLuma(IMFSample* sample)
{
    ComPtr<IMFMediaBuffer> b; sample->ConvertToContiguousBuffer(&b);
    BYTE* d = nullptr; DWORD len = 0; b->Lock(&d, nullptr, &len);
    BYTE y = d[len / 3]; // middle of the Y plane (NV12: Y is the first 2/3)
    b->Unlock();
    return y;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2) { printf("usage: VirtualCameraHarness <path-to-dll>\n"); return 2; }
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    MFStartup(MF_VERSION);

    // Load the DLL directly (no registry needed for the test).
    HMODULE dll = LoadLibraryW(argv[1]);
    CHECK(dll != nullptr, "DLL loads");
    if (!dll) return 1;
    auto getClass = reinterpret_cast<HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**)>(GetProcAddress(dll, "DllGetClassObject"));
    ComPtr<IClassFactory> factory;
    CHECK(getClass && SUCCEEDED(getClass(CLSID_OpenKameraVirtualCamera, IID_PPV_ARGS(&factory))), "class factory");
    ComPtr<IMFMediaSource> source;
    CHECK(factory && SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&source))), "media source created");
    if (!source) return 1;

    ComPtr<IMFMediaSourceEx> ex; CHECK(SUCCEEDED(source.As(&ex)), "IMFMediaSourceEx");
    ComPtr<IKsControl> ks; CHECK(SUCCEEDED(source.As(&ks)), "IKsControl");
    ComPtr<IMFAttributes> streamAttrs;
    CHECK(SUCCEEDED(ex->GetStreamAttributes(0, &streamAttrs)), "stream attributes");
    GUID cat{}; streamAttrs->GetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, &cat);
    CHECK(cat == PINNAME_VIDEO_CAPTURE, "stream category is video capture");

    ComPtr<IMFPresentationDescriptor> pd;
    CHECK(SUCCEEDED(source->CreatePresentationDescriptor(&pd)), "presentation descriptor");
    BOOL sel; ComPtr<IMFStreamDescriptor> sd; pd->GetStreamDescriptorByIndex(0, &sel, &sd);
    ComPtr<IMFMediaTypeHandler> handler; sd->GetMediaTypeHandler(&handler);
    DWORD types = 0; handler->GetMediaTypeCount(&types);
    CHECK(types == 6, "6 media types (NV12/RGB32 x 1080p/720p/360p)");

    PROPVARIANT start; PropVariantInit(&start);
    CHECK(SUCCEEDED(source->Start(pd.Get(), nullptr, &start)), "Start");
    ComPtr<IMFMediaEvent> newStream;
    CHECK(SUCCEEDED(WaitEvent(source.Get(), MENewStream, &newStream)), "MENewStream");
    PROPVARIANT v; PropVariantInit(&v); newStream->GetValue(&v);
    ComPtr<IMFMediaStream> stream; v.punkVal->QueryInterface(IID_PPV_ARGS(&stream)); PropVariantClear(&v);
    ComPtr<IMFMediaEvent> started;
    CHECK(SUCCEEDED(WaitEvent(stream.Get(), MEStreamStarted, &started)), "MEStreamStarted");

    // 1. Nobody writing: slate (dark background + bright ring/text).
    ComPtr<IMFSample> s1;
    CHECK(SUCCEEDED(PullSample(stream.Get(), &s1)), "slate sample delivered");
    DWORD len = 0; ComPtr<IMFMediaBuffer> b1; s1->ConvertToContiguousBuffer(&b1); b1->GetCurrentLength(&len);
    CHECK(len == 1280 * 720 * 3 / 2, "NV12 720p sample size");

    // 2. Write a solid red frame through the channel (test process: Local\ namespace) and read it back.
    HANDLE map = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, L"Local\\OpenKameraVirtualCamera");
    if (!map) map = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, okvc::kMappingName);
    CHECK(map != nullptr, "channel mapping exists");
    if (map)
    {
        auto* view = static_cast<BYTE*>(MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, okvc::kMappingSize));
        auto* h = reinterpret_cast<okvc::FrameHeader*>(view);
        InterlockedIncrement(&h->sequence);
        h->width = 640; h->height = 360; h->stride = 640 * 4;
        BYTE* px = view + okvc::kHeaderSize;
        for (int i = 0; i < 640 * 360; i++) { px[i * 4] = 0; px[i * 4 + 1] = 0; px[i * 4 + 2] = 255; px[i * 4 + 3] = 255; }
        FILETIME ft; GetSystemTimeAsFileTime(&ft);
        h->timestamp = (int64_t(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
        InterlockedIncrement(&h->sequence);
        ComPtr<IMFSample> s2;
        CHECK(SUCCEEDED(PullSample(stream.Get(), &s2)), "live sample delivered");
        BYTE y = FirstLuma(s2.Get());
        printf("      red frame luma = %d (expected ~81)\n", y);
        CHECK(y >= 78 && y <= 84, "live frame converted to NV12 (red -> Y 81)");
        UnmapViewOfFile(view); CloseHandle(map);
    }

    CHECK(SUCCEEDED(source->Stop()), "Stop");
    CHECK(SUCCEEDED(source->Shutdown()), "Shutdown");
    printf(g_failures ? "\n%d check(s) FAILED\n" : "\nALL CHECKS PASSED\n", g_failures);
    source.Reset(); stream.Reset(); factory.Reset();
    MFShutdown();
    return g_failures ? 1 : 0;
}
