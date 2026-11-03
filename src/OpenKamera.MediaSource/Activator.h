#pragma once
#include "MediaSource.h"

/// What the Windows camera service creates from our CLSID: it asks for IMFActivate and then activates the
/// media source through it. Without this, MFCreateVirtualCamera's Start fails with E_NOINTERFACE.
class Activator : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
    Microsoft::WRL::ChainInterfaces<IMFActivate, IMFAttributes>>
{
public:
    Activator() { ModuleAddRef(); }
    ~Activator() override { ModuleRelease(); }
    HRESULT RuntimeClassInitialize() { return MFCreateAttributes(&_attributes, 4); }

    // IMFActivate
    IFACEMETHODIMP ActivateObject(REFIID riid, void** object) override
    {
        if (!object) return E_POINTER;
        *object = nullptr;
        std::lock_guard lock(_lock);
        if (!_source) RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<MediaSource>(&_source));
        return _source->QueryInterface(riid, object);
    }
    IFACEMETHODIMP ShutdownObject() override
    {
        std::lock_guard lock(_lock);
        if (_source) { _source->Shutdown(); _source.Reset(); }
        return S_OK;
    }
    IFACEMETHODIMP DetachObject() override
    {
        std::lock_guard lock(_lock);
        _source.Reset();
        return S_OK;
    }

    // IMFAttributes: kept on an inner attribute store (the camera service may read or set values here).
    IFACEMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override { return _attributes->GetItem(k, v); }
    IFACEMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override { return _attributes->GetItemType(k, t); }
    IFACEMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override { return _attributes->CompareItem(k, v, r); }
    IFACEMETHODIMP Compare(IMFAttributes* t, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override { return _attributes->Compare(t, m, r); }
    IFACEMETHODIMP GetUINT32(REFGUID k, UINT32* v) override { return _attributes->GetUINT32(k, v); }
    IFACEMETHODIMP GetUINT64(REFGUID k, UINT64* v) override { return _attributes->GetUINT64(k, v); }
    IFACEMETHODIMP GetDouble(REFGUID k, double* v) override { return _attributes->GetDouble(k, v); }
    IFACEMETHODIMP GetGUID(REFGUID k, GUID* v) override { return _attributes->GetGUID(k, v); }
    IFACEMETHODIMP GetStringLength(REFGUID k, UINT32* n) override { return _attributes->GetStringLength(k, n); }
    IFACEMETHODIMP GetString(REFGUID k, LPWSTR v, UINT32 n, UINT32* l) override { return _attributes->GetString(k, v, n, l); }
    IFACEMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* v, UINT32* l) override { return _attributes->GetAllocatedString(k, v, l); }
    IFACEMETHODIMP GetBlobSize(REFGUID k, UINT32* n) override { return _attributes->GetBlobSize(k, n); }
    IFACEMETHODIMP GetBlob(REFGUID k, UINT8* b, UINT32 n, UINT32* l) override { return _attributes->GetBlob(k, b, n, l); }
    IFACEMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* n) override { return _attributes->GetAllocatedBlob(k, b, n); }
    IFACEMETHODIMP GetUnknown(REFGUID k, REFIID r, LPVOID* v) override { return _attributes->GetUnknown(k, r, v); }
    IFACEMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { return _attributes->SetItem(k, v); }
    IFACEMETHODIMP DeleteItem(REFGUID k) override { return _attributes->DeleteItem(k); }
    IFACEMETHODIMP DeleteAllItems() override { return _attributes->DeleteAllItems(); }
    IFACEMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { return _attributes->SetUINT32(k, v); }
    IFACEMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { return _attributes->SetUINT64(k, v); }
    IFACEMETHODIMP SetDouble(REFGUID k, double v) override { return _attributes->SetDouble(k, v); }
    IFACEMETHODIMP SetGUID(REFGUID k, REFGUID v) override { return _attributes->SetGUID(k, v); }
    IFACEMETHODIMP SetString(REFGUID k, LPCWSTR v) override { return _attributes->SetString(k, v); }
    IFACEMETHODIMP SetBlob(REFGUID k, const UINT8* b, UINT32 n) override { return _attributes->SetBlob(k, b, n); }
    IFACEMETHODIMP SetUnknown(REFGUID k, IUnknown* v) override { return _attributes->SetUnknown(k, v); }
    IFACEMETHODIMP LockStore() override { return _attributes->LockStore(); }
    IFACEMETHODIMP UnlockStore() override { return _attributes->UnlockStore(); }
    IFACEMETHODIMP GetCount(UINT32* n) override { return _attributes->GetCount(n); }
    IFACEMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override { return _attributes->GetItemByIndex(i, k, v); }
    IFACEMETHODIMP CopyAllItems(IMFAttributes* d) override { return _attributes->CopyAllItems(d); }

private:
    std::mutex _lock;
    ComPtr<IMFAttributes> _attributes;
    ComPtr<MediaSource> _source;
};
