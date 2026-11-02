// COM in-process server for the OpenKamera virtual camera media source.
#include "MediaSource.h"
#include <string>

static HMODULE g_module = nullptr;
static volatile LONG g_refs = 0;

void ModuleAddRef() { InterlockedIncrement(&g_refs); }
void ModuleRelease() { InterlockedDecrement(&g_refs); }

class ClassFactory : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IClassFactory>
{
public:
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object) override
    {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        ComPtr<MediaSource> source;
        RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<MediaSource>(&source));
        return source->QueryInterface(riid, object);
    }
    IFACEMETHODIMP LockServer(BOOL lock) override { lock ? ModuleAddRef() : ModuleRelease(); return S_OK; }
};

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_module = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** object)
{
    if (!object) return E_POINTER;
    *object = nullptr;
    if (clsid != CLSID_OpenKameraVirtualCamera) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = Microsoft::WRL::Make<ClassFactory>();
    if (!factory) return E_OUTOFMEMORY;
    return factory->QueryInterface(riid, object);
}

STDAPI DllCanUnloadNow() { return g_refs == 0 ? S_OK : S_FALSE; }

// Registration goes to HKLM: the camera service only sees machine-wide COM classes. Needs admin.
static HRESULT WriteKey(HKEY root, const std::wstring& path, const wchar_t* name, const std::wstring& value)
{
    HKEY key = nullptr;
    LSTATUS s = RegCreateKeyExW(root, path.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (s != ERROR_SUCCESS) return HRESULT_FROM_WIN32(s);
    s = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value.c_str()), DWORD((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return HRESULT_FROM_WIN32(s);
}

STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(g_module, path, MAX_PATH);
    std::wstring clsidKey = std::wstring(L"Software\\Classes\\CLSID\\") + kClsidString;
    // Per-user registration is used by the test harness (HKCU, no admin); machine registration by the installer.
    HKEY root = HKEY_LOCAL_MACHINE;
    wchar_t mode[16]{};
    if (GetEnvironmentVariableW(L"OPENKAMERA_REGISTER_USER", mode, 16) > 0) root = HKEY_CURRENT_USER;
    RETURN_IF_FAILED(WriteKey(root, clsidKey, nullptr, L"KISSIK Virtual Camera"));
    RETURN_IF_FAILED(WriteKey(root, clsidKey + L"\\InprocServer32", nullptr, path));
    RETURN_IF_FAILED(WriteKey(root, clsidKey + L"\\InprocServer32", L"ThreadingModel", L"Both"));
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    std::wstring clsidKey = std::wstring(L"Software\\Classes\\CLSID\\") + kClsidString;
    HKEY root = HKEY_LOCAL_MACHINE;
    wchar_t mode[16]{};
    if (GetEnvironmentVariableW(L"OPENKAMERA_REGISTER_USER", mode, 16) > 0) root = HKEY_CURRENT_USER;
    LSTATUS s = RegDeleteTreeW(root, clsidKey.c_str());
    return s == ERROR_SUCCESS || s == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(s);
}
