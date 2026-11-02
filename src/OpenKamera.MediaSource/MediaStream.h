#pragma once
#include "Common.h"
#include "FrameSource.h"

class MediaSource;

/// One video stream (NV12 or RGB32 at 1080p/720p/360p, 30 fps). Pixels come from FrameSource.
class MediaStream : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
    Microsoft::WRL::ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>>
{
public:
    HRESULT RuntimeClassInitialize(MediaSource* source);
    HRESULT Start();
    HRESULT Stop();
    void Shutdown();
    IMFStreamDescriptor* Descriptor() const { return _descriptor.Get(); }

    // IMFMediaEventGenerator
    IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
    IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value) override;
    // IMFMediaStream
    IFACEMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** descriptor) override;
    IFACEMETHODIMP RequestSample(IUnknown* token) override;
    // IMFMediaStream2
    IFACEMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
    IFACEMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

private:
    std::recursive_mutex _lock;
    ComPtr<IMFMediaEventQueue> _queue;
    ComPtr<IMFStreamDescriptor> _descriptor;
    MediaSource* _source = nullptr; // weak: the source owns the stream
    MF_STREAM_STATE _state = MF_STREAM_STATE_STOPPED;
    bool _shutdown = false;
    LONGLONG _startTime = 0;
    FrameSource _frames;
};
