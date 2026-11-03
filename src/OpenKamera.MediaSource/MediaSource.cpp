#include "MediaSource.h"

HRESULT MediaSource::RuntimeClassInitialize()
{
    RETURN_IF_FAILED(MFCreateEventQueue(&_queue));
    RETURN_IF_FAILED(MFCreateAttributes(&_attributes, 4));
    RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<MediaStream>(&_stream, this));
    IMFStreamDescriptor* sd = _stream->Descriptor();
    RETURN_IF_FAILED(MFCreatePresentationDescriptor(1, &sd, &_presentation));
    RETURN_IF_FAILED(_presentation->SelectStream(0));
    return S_OK;
}

IFACEMETHODIMP MediaSource::GetEvent(DWORD flags, IMFMediaEvent** event)
{
    ComPtr<IMFMediaEventQueue> q;
    { std::lock_guard lock(_lock); if (_shutdown) return MF_E_SHUTDOWN; q = _queue; }
    return q->GetEvent(flags, event);
}
IFACEMETHODIMP MediaSource::BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state)
{ std::lock_guard lock(_lock); return _shutdown ? MF_E_SHUTDOWN : _queue->BeginGetEvent(cb, state); }
IFACEMETHODIMP MediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
{ std::lock_guard lock(_lock); return _shutdown ? MF_E_SHUTDOWN : _queue->EndGetEvent(result, event); }
IFACEMETHODIMP MediaSource::QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value)
{ std::lock_guard lock(_lock); return _shutdown ? MF_E_SHUTDOWN : _queue->QueueEventParamVar(type, ext, status, value); }

IFACEMETHODIMP MediaSource::GetCharacteristics(DWORD* characteristics)
{
    std::lock_guard lock(_lock);
    if (!characteristics) return E_POINTER;
    if (_shutdown) return MF_E_SHUTDOWN;
    *characteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

IFACEMETHODIMP MediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor)
{
    std::lock_guard lock(_lock);
    if (!descriptor) return E_POINTER;
    if (_shutdown) return MF_E_SHUTDOWN;
    return _presentation->Clone(descriptor);
}

IFACEMETHODIMP MediaSource::Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition)
{
    std::lock_guard lock(_lock);
    if (!descriptor || !startPosition) return E_INVALIDARG;
    if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;
    if (_shutdown) return MF_E_SHUTDOWN;

    // Apply the media type the caller picked on its copy of the descriptor.
    BOOL selected = FALSE;
    ComPtr<IMFStreamDescriptor> chosen;
    RETURN_IF_FAILED(descriptor->GetStreamDescriptorByIndex(0, &selected, &chosen));
    if (selected)
    {
        ComPtr<IMFMediaTypeHandler> theirs, ours;
        ComPtr<IMFMediaType> type;
        if (SUCCEEDED(chosen->GetMediaTypeHandler(&theirs)) && SUCCEEDED(theirs->GetCurrentMediaType(&type)) &&
            SUCCEEDED(_stream->Descriptor()->GetMediaTypeHandler(&ours)))
            ours->SetCurrentMediaType(type.Get());
    }

    PROPVARIANT time; PropVariantInit(&time);
    time.vt = VT_I8; time.hVal.QuadPart = MFGetSystemTime();
    ComPtr<IUnknown> streamUnknown;
    RETURN_IF_FAILED(_stream.As(&streamUnknown));
    RETURN_IF_FAILED(_queue->QueueEventParamUnk(_started ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK, streamUnknown.Get()));
    _started = true;
    RETURN_IF_FAILED(_queue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time));
    return _stream->Start();
}

IFACEMETHODIMP MediaSource::Stop()
{
    std::lock_guard lock(_lock);
    if (_shutdown) return MF_E_SHUTDOWN;
    RETURN_IF_FAILED(_stream->Stop());
    return _queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
}

IFACEMETHODIMP MediaSource::Pause() { return MF_E_INVALID_STATE_TRANSITION; }

IFACEMETHODIMP MediaSource::Shutdown()
{
    std::lock_guard lock(_lock);
    if (_shutdown) return S_OK;
    _shutdown = true;
    if (_stream) _stream->Shutdown();
    if (_queue) _queue->Shutdown();
    return S_OK;
}

IFACEMETHODIMP MediaSource::GetSourceAttributes(IMFAttributes** attributes)
{
    std::lock_guard lock(_lock);
    if (!attributes) return E_POINTER;
    if (_shutdown) return MF_E_SHUTDOWN;
    return _attributes.CopyTo(attributes);
}

IFACEMETHODIMP MediaSource::GetStreamAttributes(DWORD streamId, IMFAttributes** attributes)
{
    std::lock_guard lock(_lock);
    if (!attributes) return E_POINTER;
    if (_shutdown) return MF_E_SHUTDOWN;
    if (streamId != 0) return MF_E_INVALIDSTREAMNUMBER;
    return _stream->Descriptor()->QueryInterface(IID_PPV_ARGS(attributes));
}

IFACEMETHODIMP MediaSource::SetD3DManager(IUnknown*) { return S_OK; }

IFACEMETHODIMP MediaSource::GetService(REFGUID, REFIID, LPVOID* object)
{
    if (object) *object = nullptr;
    return MF_E_UNSUPPORTED_SERVICE;
}

IFACEMETHODIMP MediaSource::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG* bytes)
{ if (bytes) *bytes = 0; return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
IFACEMETHODIMP MediaSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG* bytes)
{ if (bytes) *bytes = 0; return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
IFACEMETHODIMP MediaSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG* bytes)
{ if (bytes) *bytes = 0; return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
