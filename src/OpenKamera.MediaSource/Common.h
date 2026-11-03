#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfobjects.h>
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>
#include <wrl.h>
#include <wrl/implements.h>
#include <mutex>
#include <vector>

using Microsoft::WRL::ComPtr;

// {53B8FDEE-DF3B-440D-9C66-399F14BC6C50}
inline constexpr CLSID CLSID_OpenKameraVirtualCamera =
    { 0x53b8fdee, 0xdf3b, 0x440d, { 0x9c, 0x66, 0x39, 0x9f, 0x14, 0xbc, 0x6c, 0x50 } };
inline constexpr wchar_t kClsidString[] = L"{53B8FDEE-DF3B-440D-9C66-399F14BC6C50}";

#define RETURN_IF_FAILED(expr) do { HRESULT _hr = (expr); if (FAILED(_hr)) return _hr; } while (0)

void ModuleAddRef();
void ModuleRelease();
