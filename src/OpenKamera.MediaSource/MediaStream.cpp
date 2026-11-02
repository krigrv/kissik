#include "MediaStream.h"
#include "MediaSource.h"

namespace
{
    constexpr UINT32 kFps = 30;
    struct Mode { UINT32 w, h; };
    constexpr Mode kModes[] = { { 1920, 1080 }, { 1280, 720 }, { 640, 360 } };

    HRESULT MakeType(const GUID& subtype, UINT32 w, UINT32 h, IMFMediaType** out)
    {
        ComPtr<IMFMediaType> t;
        RETURN_IF_FAILED(MFCreateMediaType(&t));
        RETURN_IF_FAILED(t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        RETURN_IF_FAILED(t->SetGUID(MF_MT_SUBTYPE, subtype));
        RETURN_IF_FAILED(MFSetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, w, h));
        RETURN_IF_FAILED(MFSetAttributeRatio(t.Get(), MF_MT_FRAME_RATE, kFps, 1));
        RETURN_IF_FAILED(MFSetAttributeRatio(t.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
        RETURN_IF_FAILED(t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        RETURN_IF_FAILED(t->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
        UINT32 stride = subtype == MFVideoFormat_NV12 ? w : w * 4;
        UINT32 size = subtype == MFVideoFormat_NV12 ? w * h * 3 / 2 : w * h * 4;
        RETURN_IF_FAILED(t->SetUINT32(MF_MT_DEFAULT_STRIDE, stride)); // positive = top-down
        RETURN_IF_FAILED(t->SetUINT32(MF_MT_SAMPLE_SIZE, size));
        RETURN_IF_FAILED(t->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE));
        *out = t.Detach();
        return S_OK;
    }
}

HRESULT MediaStream::RuntimeClassInitialize(MediaSource* source)
{
    _source = source;
    RETURN_IF_FAILED(MFCreateEventQueue(&_queue));
    std::vector<ComPtr<IMFMediaType>> types;
    for (const GUID& sub : { MFVideoFormat_NV12, MFVideoFormat_RGB32 })
        for (auto m : kModes)
        {
            ComPtr<IMFMediaType> t;
            RETURN_IF_FAILED(MakeType(sub, m.w, m.h, &t));
            types.push_back(t);
        }
    std::vector<IMFMediaType*> raw;
    for (auto& t : types) raw.push_back(t.Get());
    RETURN_IF_FAILED(MFCreateStreamDescriptor(0, DWORD(raw.size()), raw.data(), &_descriptor));
    ComPtr<IMFMediaTypeHandler> handler;
    RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
    RETURN_IF_FAILED(handler->SetCurrentMediaType(raw[1])); // NV12 720p by default
    // What the camera service needs to treat this as a shareable colour capture pin.
    RETURN_IF_FAILED(_descriptor->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
    RETURN_IF_FAILED(_descriptor->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0));
    RETURN_IF_FAILED(_descriptor->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
    RETURN_IF_FAILED(_descriptor->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color));
    return S_OK;
}

HRESULT MediaStream::Start()
{
    std::lock_guard lock(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    _state = MF_STREAM_STATE_RUNNING;
    _startTime = MFGetSystemTime();
    return _queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
}

HRESULT MediaStream::Stop()
{
    std::lock_guard lock(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    _state = MF_STREAM_STATE_STOPPED;
    return _queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
}

void MediaStream::Shutdown()
{
    std::lock_guard lock(_lock);
    if (_shutdown) return;
    _shutdown = true;
    if (_queue) _queue->Shutdown();
    _source = nullptr;
}

IFACEMETHODIMP MediaStream::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    ComPtr<IMFMediaEventQueue> q;
    { std::lock_guard lock(_lock); if (_shutdown) return MF_E_SHUTDOWN; q = _queue; }
    return q->GetEvent(flags, event); // may block: call outside the lock
}
IFACEMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state)
{ std::lock_guard lock(_lock); return _shutdown ? MF_E_SHUTDOWN : _queue->BeginGetEvent(cb, state); }
IFACEMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{ std::lock_guard lock(_lock); return _shutdown ? MF_E_SHUTDOWN : _queue->EndGetEvent(result, event); }
IFACEMETHODIMP MediaStream::QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value)
{ std::lock_guard lock(_lock); return _shutdown ? MF_E_SHUTDOWN : _queue->QueueEventParamVar(type, ext, status, value); }

IFACEMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** source)
{
    std::lock_guard lock(_lock);
    if (!source) return E_POINTER;
    if (_shutdown || !_source) return MF_E_SHUTDOWN;
    return _source->QueryInterface(IID_PPV_ARGS(source));
}

IFACEMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor)
{
    std::lock_guard lock(_lock);
    if (!descriptor) return E_POINTER;
    if (_shutdown) return MF_E_SHUTDOWN;
    return _descriptor.CopyTo(descriptor);
}

IFACEMETHODIMP MediaStream::RequestSample(IUnknown* token)
{
    std::lock_guard lock(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    if (_state != MF_STREAM_STATE_RUNNING) return MF_E_INVALIDREQUEST;

    ComPtr<IMFMediaTypeHandler> handler;
    ComPtr<IMFMediaType> type;
    RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
    RETURN_IF_FAILED(handler->GetCurrentMediaType(&type));
    GUID subtype{}; UINT32 w = 0, h = 0;
    RETURN_IF_FAILED(type->GetGUID(MF_MT_SUBTYPE, &subtype));
    RETURN_IF_FAILED(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h));
    bool nv12 = subtype == MFVideoFormat_NV12;
    LONG stride = LONG(nv12 ? w : w * 4);
    DWORD size = nv12 ? w * h * 3 / 2 : w * h * 4;

    // Steady pace: one frame per 1/kFps on a fixed grid (no faster, and evenly spaced timestamps).
    constexpr LONGLONG period = 10'000'000 / kFps;
    LONGLONG now = MFGetSystemTime();
    if (_nextFrame == 0 || now - _nextFrame > period * 2) _nextFrame = now; // first frame, or we fell far behind
    if (_nextFrame > now) Sleep(DWORD((_nextFrame - now) / 10'000));
    LONGLONG frameTime = _nextFrame;
    _nextFrame += period;

    ComPtr<IMFMediaBuffer> buffer;
    RETURN_IF_FAILED(MFCreateMemoryBuffer(size, &buffer));
    BYTE* data = nullptr;
    RETURN_IF_FAILED(buffer->Lock(&data, nullptr, nullptr));
    _frames.Fill(data, stride, w, h, subtype);
    buffer->Unlock();
    RETURN_IF_FAILED(buffer->SetCurrentLength(size));

    ComPtr<IMFSample> sample;
    RETURN_IF_FAILED(MFCreateSample(&sample));
    RETURN_IF_FAILED(sample->AddBuffer(buffer.Get()));
    RETURN_IF_FAILED(sample->SetSampleTime(frameTime));
    RETURN_IF_FAILED(sample->SetSampleDuration(period));
    if (token) RETURN_IF_FAILED(sample->SetUnknown(MFSampleExtension_Token, token));
    return _queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
}

IFACEMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE state)
{
    std::lock_guard lock(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    if (state == _state) return S_OK;
    switch (state)
    {
    case MF_STREAM_STATE_RUNNING: _state = state; _startTime = MFGetSystemTime(); return _queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
    case MF_STREAM_STATE_STOPPED: _state = state; return _queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    case MF_STREAM_STATE_PAUSED: _state = state; return _queue->QueueEventParamVar(MEStreamPaused, GUID_NULL, S_OK, nullptr);
    default: return E_INVALIDARG;
    }
}

IFACEMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* state)
{
    std::lock_guard lock(_lock);
    if (!state) return E_POINTER;
    *state = _state;
    return _shutdown ? MF_E_SHUTDOWN : S_OK;
}
