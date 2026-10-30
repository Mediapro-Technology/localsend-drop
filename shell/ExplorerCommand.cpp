// "Send with LocalSend Drop" in the Windows 11 right-click menu.
//
// Windows 11 only shows packaged IExplorerCommand handlers in its short menu, so this
// DLL is registered by a sparse MSIX package (AppxManifest.xml) and loaded in a
// dllhost surrogate. It lives next to LocalSendDrop.exe and just hands the selected
// paths to it: LocalSendDrop.exe --share "<path>" "<path>" ...

#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <new>
#include <string>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")

// {61D4A592-EAE4-4AD8-958C-A6352D7E6064}
static const CLSID CLSID_SendCommand = { 0x61d4a592, 0xeae4, 0x4ad8, { 0x95, 0x8c, 0xa6, 0x35, 0x2d, 0x7e, 0x60, 0x64 } };

static HMODULE g_module;
static LONG g_objects;

static std::wstring ExePath()
{
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(g_module, path, MAX_PATH);
    PathRemoveFileSpecW(path);
    PathAppendW(path, L"LocalSendDrop.exe");
    return path;
}

class SendCommand : public IExplorerCommand
{
    LONG _refs = 1;
public:
    SendCommand() { InterlockedIncrement(&g_objects); }
    ~SendCommand() { InterlockedDecrement(&g_objects); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        static const QITAB qit[] = { QITABENT(SendCommand, IExplorerCommand), { 0 } };
        return QISearch(this, qit, riid, ppv);
    }
    IFACEMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&_refs); }
    IFACEMETHODIMP_(ULONG) Release()
    {
        LONG r = InterlockedDecrement(&_refs);
        if (r == 0) delete this;
        return r;
    }

    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) { return SHStrDupW(L"Send with LocalSend Drop", name); }

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon)
    {
        std::wstring s = ExePath() + L",0";
        return SHStrDupW(s.c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) { *tip = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetCanonicalName(GUID* guid) { *guid = CLSID_SendCommand; return S_OK; }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state)
    {
        // Hidden if the app has been moved away from this folder.
        *state = PathFileExistsW(ExePath().c_str()) ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*)
    {
        if (!items) return S_OK;
        DWORD count = 0;
        items->GetCount(&count);

        std::wstring cmd = L"\"" + ExePath() + L"\" --share";
        for (DWORD i = 0; i < count; i++)
        {
            IShellItem* item = nullptr;
            if (FAILED(items->GetItemAt(i, &item))) continue;
            PWSTR path = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)))
            {
                cmd += L" \"";
                cmd += path;
                cmd += L"\"";
                CoTaskMemFree(path);
            }
            item->Release();
        }

        STARTUPINFOW si = { sizeof(si) };
        PROCESS_INFORMATION pi = {};
        if (CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi))
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) { *flags = ECF_DEFAULT; return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** e) { *e = nullptr; return E_NOTIMPL; }
};

class Factory : public IClassFactory
{
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        static const QITAB qit[] = { QITABENT(Factory, IClassFactory), { 0 } };
        return QISearch(this, qit, riid, ppv);
    }
    IFACEMETHODIMP_(ULONG) AddRef() { return 2; }   // static instance
    IFACEMETHODIMP_(ULONG) Release() { return 1; }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv)
    {
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* cmd = new (std::nothrow) SendCommand();
        if (!cmd) return E_OUTOFMEMORY;
        HRESULT hr = cmd->QueryInterface(riid, ppv);
        cmd->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock)
    {
        if (lock) InterlockedIncrement(&g_objects); else InterlockedDecrement(&g_objects);
        return S_OK;
    }
};

static Factory g_factory;

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    *ppv = nullptr;
    if (clsid != CLSID_SendCommand) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow() { return g_objects == 0 ? S_OK : S_FALSE; }

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
