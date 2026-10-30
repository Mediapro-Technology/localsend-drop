// LocalSendDrop-Setup.exe: one file that installs LocalSend Drop.
//
// The MSIX is signed with our own "CN=Mediapro Technology" certificate, which Windows
// won't accept until the PC trusts it, so a bare .msix can't install itself. This setup
// carries both (as resources), runs elevated (manifest), trusts the certificate,
// installs the package for the current user and starts the app.

#include <windows.h>
#include <commctrl.h>
#include <tlhelp32.h>
#include <wincrypt.h>
#include <string>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "user32.lib")

#define IDR_MSIX 101
#define IDR_CER 102
#define IDI_APP 1

// Package family of MediaproTechnology.LocalSendDrop signed by CN=Mediapro Technology.
static const wchar_t* AppId = L"shell:AppsFolder\\MediaproTechnology.LocalSendDrop_q2bcx0jfstxt0!LocalSendDrop";

static std::wstring g_error;
static bool g_done;

static bool Resource(int id, const BYTE** data, DWORD* size)
{
    HRSRC r = FindResourceW(nullptr, MAKEINTRESOURCEW(id), RT_RCDATA);
    if (!r) return false;
    HGLOBAL h = LoadResource(nullptr, r);
    *data = static_cast<const BYTE*>(LockResource(h));
    *size = SizeofResource(nullptr, r);
    return *data && *size;
}

static bool TrustCertificate()
{
    const BYTE* cer; DWORD size;
    if (!Resource(IDR_CER, &cer, &size)) { g_error = L"The certificate is missing from this setup file."; return false; }
    HCERTSTORE store = CertOpenStore(CERT_STORE_PROV_SYSTEM_W, 0, 0,
        CERT_SYSTEM_STORE_LOCAL_MACHINE | CERT_STORE_OPEN_EXISTING_FLAG, L"TrustedPeople");
    if (!store) { g_error = L"Couldn't open the Trusted People certificate store."; return false; }
    BOOL ok = CertAddEncodedCertificateToStore(store, X509_ASN_ENCODING, cer, size, CERT_STORE_ADD_REPLACE_EXISTING, nullptr);
    CertCloseStore(store, 0);
    if (!ok) g_error = L"Windows didn't accept the Mediapro Technology certificate.";
    return ok;
}

static bool WritePackage(const std::wstring& path)
{
    const BYTE* msix; DWORD size;
    if (!Resource(IDR_MSIX, &msix, &size)) { g_error = L"The app package is missing from this setup file."; return false; }
    HANDLE f = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) { g_error = L"Couldn't write to the temp folder."; return false; }
    DWORD written = 0;
    BOOL ok = WriteFile(f, msix, size, &written, nullptr) && written == size;
    CloseHandle(f);
    if (!ok) g_error = L"Couldn't write to the temp folder.";
    return ok;
}

// Closes any running copy: a 1.x .exe would otherwise keep the network ports.
static void CloseRunningCopies()
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W pe = { sizeof(pe) };
    for (BOOL more = Process32FirstW(snap, &pe); more; more = Process32NextW(snap, &pe))
    {
        if (_wcsicmp(pe.szExeFile, L"LocalSendDrop.exe") != 0) continue;
        if (HANDLE p = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, pe.th32ProcessID))
        {
            TerminateProcess(p, 0);
            WaitForSingleObject(p, 3000);
            CloseHandle(p);
        }
    }
    CloseHandle(snap);
}

static DWORD RunHidden(std::wstring cmd)
{
    STARTUPINFOW si = { sizeof(si) };
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi = {};
    if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
        return GetLastError();
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return code;
}

static std::wstring ReadText(const std::wstring& path)
{
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return L"";
    char buf[2048] = {};
    DWORD n = 0;
    ReadFile(f, buf, sizeof(buf) - 1, &n, nullptr);
    CloseHandle(f);
    int len = MultiByteToWideChar(CP_UTF8, 0, buf, n, nullptr, 0);
    std::wstring s(len, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, buf, n, s.data(), len);
    while (!s.empty() && (s.back() == L'\n' || s.back() == L'\r' || s.back() == L' ')) s.pop_back();
    return s;
}

static DWORD WINAPI Install(LPVOID hwnd)
{
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    std::wstring dir = std::wstring(tmp) + L"LocalSendDrop-setup";
    CreateDirectoryW(dir.c_str(), nullptr);
    std::wstring msix = dir + L"\\LocalSendDrop.msix";
    std::wstring log = dir + L"\\error.txt";
    DeleteFileW(log.c_str());

    if (TrustCertificate() && WritePackage(msix))
    {
        CloseRunningCopies();
        // Add-AppxPackage registers the package for the signed-in user (this setup runs
        // elevated as that same user). A 1.3 "sparse" package of the same name can't be
        // upgraded in place, so it's removed first.
        std::wstring ps =
            L"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \""
            L"$ErrorActionPreference='Stop'; try { "
            L"Get-AppxPackage MediaproTechnology.LocalSendDrop | Where-Object { $_.Version -lt [version]'2.0.0.0' } | Remove-AppxPackage; "
            L"Add-AppxPackage -Path '" + msix + L"' -ForceApplicationShutdown "
            L"} catch { $_.Exception.Message | Out-File -Encoding utf8 '" + log + L"'; exit 1 }\"";
        if (RunHidden(ps) != 0)
        {
            g_error = ReadText(log);
            if (g_error.empty()) g_error = L"Windows couldn't install the app package.";
        }
        DeleteFileW(msix.c_str());
    }

    if (g_error.empty())
    {
        // Explorer starts it as the normal user (not elevated), so drag and drop works.
        ShellExecuteW(nullptr, L"open", L"explorer.exe", AppId, nullptr, SW_SHOWNORMAL);
    }
    g_done = true;
    PostMessageW(static_cast<HWND>(hwnd), TDM_CLICK_BUTTON, IDCANCEL, 0);
    return 0;
}

static HRESULT CALLBACK Progress(HWND hwnd, UINT msg, WPARAM wp, LPARAM, LONG_PTR)
{
    if (msg == TDN_CREATED)
    {
        SendMessageW(hwnd, TDM_SET_PROGRESS_BAR_MARQUEE, TRUE, 30);
        SendMessageW(hwnd, TDM_ENABLE_BUTTON, IDCANCEL, FALSE);
        CloseHandle(CreateThread(nullptr, 0, Install, hwnd, 0, nullptr));
    }
    if (msg == TDN_BUTTON_CLICKED && !g_done) return S_FALSE; // can't stop halfway
    return S_OK;
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR, int)
{
    INITCOMMONCONTROLSEX icc = { sizeof(icc), ICC_PROGRESS_CLASS };
    InitCommonControlsEx(&icc);

    TASKDIALOGCONFIG cfg = { sizeof(cfg) };
    cfg.hInstance = inst;
    cfg.pszWindowTitle = L"LocalSend Drop";
    cfg.pszMainInstruction = L"Installing LocalSend Drop";
    cfg.pszContent = L"Trusting the Mediapro Technology certificate and installing the app. This takes a few seconds.";
    cfg.dwFlags = TDF_SHOW_MARQUEE_PROGRESS_BAR | TDF_CALLBACK_TIMER | TDF_USE_HICON_MAIN | TDF_POSITION_RELATIVE_TO_WINDOW;
    cfg.hMainIcon = LoadIconW(inst, MAKEINTRESOURCEW(IDI_APP));
    cfg.dwCommonButtons = TDCBF_CANCEL_BUTTON;
    cfg.pfCallback = Progress;
    TaskDialogIndirect(&cfg, nullptr, nullptr, nullptr);

    TASKDIALOGCONFIG done = { sizeof(done) };
    done.hInstance = inst;
    done.pszWindowTitle = L"LocalSend Drop";
    done.dwCommonButtons = TDCBF_CLOSE_BUTTON;
    if (g_error.empty())
    {
        done.dwFlags = TDF_USE_HICON_MAIN;
        done.hMainIcon = cfg.hMainIcon;
        done.pszMainInstruction = L"LocalSend Drop is installed";
        done.pszContent = L"It's running now and starts with Windows. Drag files into the top-left corner of your screen to send them, "
                          L"or right-click a file and choose Send with LocalSend Drop.";
    }
    else
    {
        done.pszMainIcon = TD_ERROR_ICON;
        done.pszMainInstruction = L"LocalSend Drop couldn't be installed";
        done.pszContent = g_error.c_str();
    }
    TaskDialogIndirect(&done, nullptr, nullptr, nullptr);
    return g_error.empty() ? 0 : 1;
}
