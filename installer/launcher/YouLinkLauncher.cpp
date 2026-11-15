#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0600
#endif
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <tlhelp32.h>
#include <algorithm>
#include <cwctype>
#include <string>
#include <vector>

namespace
{
    constexpr const wchar_t* kInstanceMutexName = L"Local\\YouLink_AppWindow_Mutex_v1";
    constexpr const wchar_t* kActivateEventName = L"Local\\YouLink_ActivateWindow_Event_v1";
    constexpr const wchar_t* kLauncherGuardName = L"Local\\YouLink_Launcher_Guard_v1";

    bool FileExists(const std::wstring& path)
    {
        DWORD attrs = GetFileAttributesW(path.c_str());
        return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    std::wstring ToLowerCopy(std::wstring value)
    {
        std::transform(value.begin(), value.end(), value.begin(),
            [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
        return value;
    }

    std::wstring GetExecutableDirectory()
    {
        std::vector<wchar_t> buffer(MAX_PATH);
        for (;;)
        {
            DWORD len = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
            if (len == 0)
                return L"";
            if (len < buffer.size() - 1)
            {
                std::wstring fullPath(buffer.data(), len);
                size_t slash = fullPath.find_last_of(L"\\/");
                return slash == std::wstring::npos ? L"" : fullPath.substr(0, slash);
            }
            buffer.resize(buffer.size() * 2);
        }
    }

    bool EqualsExePathOrName(DWORD pid, const std::wstring& expectedExeLower)
    {
        HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (!hProc)
            return false;

        std::vector<wchar_t> pathBuf(MAX_PATH * 2);
        DWORD size = static_cast<DWORD>(pathBuf.size());
        BOOL ok = QueryFullProcessImageNameW(hProc, 0, pathBuf.data(), &size);
        CloseHandle(hProc);
        if (!ok || size == 0)
            return false;

        std::wstring actualLower = ToLowerCopy(std::wstring(pathBuf.data(), size));
        if (actualLower == expectedExeLower)
            return true;
        const std::wstring suffix = L"\\appmaui.blazor.exe";
        return actualLower.size() >= suffix.size() &&
            actualLower.compare(actualLower.size() - suffix.size(), suffix.size(), suffix) == 0;
    }

    bool IsIgnoredOverlayOrSystemClass(const std::wstring& classLower)
    {
        return classLower == L"uac_inputindicatoroverlaywnd" ||
            classLower == L"uac input indicator" ||
            classLower == L"ime" ||
            classLower == L"msctfime ui" ||
            classLower == L"cicloaderwndclass" ||
            classLower == L"cicerouiwndframe" ||
            classLower == L"touch tooltip window" ||
            classLower == L"webview2_temp_parent";
    }

    struct WindowSearchContext
    {
        std::wstring expectedExeLower;
        HWND foundHwnd = nullptr;
        DWORD foundPid = 0;
        HWND fallbackHwnd = nullptr;
        DWORD fallbackPid = 0;
    };

    BOOL CALLBACK EnumTopLevelWindowProc(HWND hwnd, LPARAM lParam)
    {
        auto* ctx = reinterpret_cast<WindowSearchContext*>(lParam);
        if (!IsWindow(hwnd))
            return TRUE;

        if (!IsWindowVisible(hwnd) && !IsIconic(hwnd))
            return TRUE;

        if (GetWindow(hwnd, GW_OWNER) != nullptr)
            return TRUE;

        DWORD pid = 0;
        GetWindowThreadProcessId(hwnd, &pid);
        if (pid == 0 || pid == GetCurrentProcessId())
            return TRUE;

        if (!EqualsExePathOrName(pid, ctx->expectedExeLower))
            return TRUE;

        wchar_t classBuf[128]{};
        GetClassNameW(hwnd, classBuf, static_cast<int>(sizeof(classBuf) / sizeof(classBuf[0])));
        const std::wstring classLower = ToLowerCopy(classBuf);
        if (IsIgnoredOverlayOrSystemClass(classLower))
            return TRUE;

        if (classLower == L"winuidesktopwin32windowclass")
        {
            ctx->foundHwnd = hwnd;
            ctx->foundPid = pid;
            return FALSE;
        }

        if (!ctx->fallbackHwnd && GetWindowTextLengthW(hwnd) > 0)
        {
            ctx->fallbackHwnd = hwnd;
            ctx->fallbackPid = pid;
        }

        return TRUE;
    }

    HWND FindExistingMainWindow(const std::wstring& expectedExeLower, DWORD& outPid)
    {
        WindowSearchContext ctx{ expectedExeLower };
        EnumWindows(EnumTopLevelWindowProc, reinterpret_cast<LPARAM>(&ctx));
        if (ctx.foundHwnd)
        {
            outPid = ctx.foundPid;
            return ctx.foundHwnd;
        }
        if (ctx.fallbackHwnd)
        {
            outPid = ctx.fallbackPid;
            return ctx.fallbackHwnd;
        }
        outPid = 0;
        return nullptr;
    }

    std::vector<DWORD> CollectRunningAppPids(const std::wstring& expectedExeLower)
    {
        std::vector<DWORD> pids;
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE)
            return pids;

        PROCESSENTRY32W pe{};
        pe.dwSize = sizeof(pe);

        if (Process32FirstW(snap, &pe))
        {
            do
            {
                if (_wcsicmp(pe.szExeFile, L"appmaui.blazor.exe") == 0 &&
                    EqualsExePathOrName(pe.th32ProcessID, expectedExeLower))
                {
                    pids.push_back(pe.th32ProcessID);
                }
            } while (Process32NextW(snap, &pe));
        }

        CloseHandle(snap);
        return pids;
    }

    bool HasRunningAppProcess(const std::wstring& expectedExeLower, DWORD& outPid)
    {
        auto pids = CollectRunningAppPids(expectedExeLower);
        if (pids.empty())
        {
            outPid = 0;
            return false;
        }
        outPid = pids[0];
        return true;
    }

    ULONGLONG GetProcessAgeMs(DWORD pid)
    {
        HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (!hProc)
            return 0;

        FILETIME ftCreate{}, ftExit{}, ftKernel{}, ftUser{}, ftNow{};
        ULONGLONG ageMs = 0;
        if (GetProcessTimes(hProc, &ftCreate, &ftExit, &ftKernel, &ftUser))
        {
            GetSystemTimeAsFileTime(&ftNow);
            ULARGE_INTEGER createTime{}, nowTime{};
            createTime.LowPart = ftCreate.dwLowDateTime;
            createTime.HighPart = ftCreate.dwHighDateTime;
            nowTime.LowPart = ftNow.dwLowDateTime;
            nowTime.HighPart = ftNow.dwHighDateTime;
            if (nowTime.QuadPart > createTime.QuadPart)
            {
                ageMs = (nowTime.QuadPart - createTime.QuadPart) / 10000ULL;
            }
        }
        CloseHandle(hProc);
        return ageMs;
    }

    void TerminateOrphanAppProcesses(const std::wstring& expectedExeLower)
    {
        const auto pids = CollectRunningAppPids(expectedExeLower);
        for (DWORD pid : pids)
        {
            HANDLE hProc = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, pid);
            if (!hProc)
                continue;

            TerminateProcess(hProc, 1);
            WaitForSingleObject(hProc, 2500);
            CloseHandle(hProc);
        }
        if (!pids.empty())
        {
            Sleep(150);
        }
    }

    void SignalActivationEvent()
    {
        HANDLE hEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, kActivateEventName);
        if (hEvent)
        {
            SetEvent(hEvent);
            CloseHandle(hEvent);
        }
    }

    bool TryActivateExistingInstance(const std::wstring& targetExe)
    {
        const std::wstring expectedExeLower = ToLowerCopy(targetExe);

        // Verifica tanto o mutex do app quanto processos já em execução
        bool instanceExists = false;
        HANDLE hMutex = OpenMutexW(SYNCHRONIZE, FALSE, kInstanceMutexName);
        if (hMutex)
        {
            DWORD waitRes = WaitForSingleObject(hMutex, 0);
            if (waitRes == WAIT_TIMEOUT)
            {
                instanceExists = true;
            }
            else if (waitRes == WAIT_OBJECT_0 || waitRes == WAIT_ABANDONED)
            {
                ReleaseMutex(hMutex);
            }
            CloseHandle(hMutex);
        }

        auto initialPids = CollectRunningAppPids(expectedExeLower);
        if (!instanceExists && !initialPids.empty())
        {
            instanceExists = true;
        }

        if (!instanceExists)
            return false;

        SignalActivationEvent();

        DWORD matchedPid = 0;
        if (HWND hwndNow = FindExistingMainWindow(expectedExeLower, matchedPid))
        {
            if (matchedPid != 0)
                AllowSetForegroundWindow(matchedPid);

            ShowWindow(hwndNow, IsIconic(hwndNow) ? SW_RESTORE : SW_SHOW);
            SetForegroundWindow(hwndNow);
            SignalActivationEvent();
            return true;
        }

        // Se existe um processo recém-iniciado (< 6s), aguarda o boot da janela WinUI;
        // se todos os processos ativos já estiverem rodando há >= 6s sem janela, trata rapidamente como órfão/travado.
        bool hasFreshBootProcess = false;
        for (DWORD pid : initialPids)
        {
            if (GetProcessAgeMs(pid) < 6000ULL)
            {
                hasFreshBootProcess = true;
                break;
            }
        }

        const int maxAttempts = hasFreshBootProcess ? 40 : 4;
        const DWORD sleepMs = hasFreshBootProcess ? 150 : 120;

        for (int attempt = 0; attempt < maxAttempts; ++attempt)
        {
            Sleep(sleepMs);

            DWORD foundPid = 0;
            if (HWND foundHwnd = FindExistingMainWindow(expectedExeLower, foundPid))
            {
                if (foundPid != 0)
                    AllowSetForegroundWindow(foundPid);

                ShowWindow(foundHwnd, IsIconic(foundHwnd) ? SW_RESTORE : SW_SHOW);
                SetForegroundWindow(foundHwnd);
                SignalActivationEvent();
                return true;
            }

            DWORD probePid = 0;
            if (!HasRunningAppProcess(expectedExeLower, probePid))
            {
                // O processo anterior acabou de fechar (ex.: reinício de atualização) — permite abrir o novo
                return false;
            }
        }

        // Nenhum janela real surgiu (processo antigo preso/zumbi sem janela segurando o mutex):
        // encerra os processos órfãos para que o usuário consiga abrir o YouLink imediatamente.
        TerminateOrphanAppProcesses(expectedExeLower);
        return false;
    }

    bool LaunchTarget(const std::wstring& exePath, const std::wstring& workDir, const wchar_t* extraArgs)
    {
        std::wstring cmdLine = L"\"" + exePath + L"\"";
        if (extraArgs && *extraArgs)
        {
            cmdLine += L" ";
            cmdLine += extraArgs;
        }

        std::vector<wchar_t> mutableCmd(cmdLine.begin(), cmdLine.end());
        mutableCmd.push_back(L'\0');

        STARTUPINFOW si{};
        si.cb = sizeof(si);
        PROCESS_INFORMATION pi{};

        if (!CreateProcessW(
                exePath.c_str(),
                mutableCmd.data(),
                nullptr,
                nullptr,
                FALSE,
                0,
                nullptr,
                workDir.c_str(),
                &si,
                &pi))
        {
            return false;
        }

        WaitForInputIdle(pi.hProcess, 1500);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return true;
    }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR pCmdLine, int)
{
    const std::wstring baseDir = GetExecutableDirectory();
    if (baseDir.empty())
    {
        MessageBoxW(
            nullptr,
            L"Não foi possível localizar a pasta do YouLink.",
            L"YouLink",
            MB_OK | MB_ICONERROR);
        return 1;
    }

    const std::wstring appSubDir = baseDir + L"\\App";
    const std::wstring primaryExe = appSubDir + L"\\appmaui.blazor.exe";
    const std::wstring fallbackExe = baseDir + L"\\appmaui.blazor.exe";

    std::wstring targetExe;
    std::wstring workDir;
    if (FileExists(primaryExe))
    {
        targetExe = primaryExe;
        workDir = appSubDir;
    }
    else if (FileExists(fallbackExe))
    {
        targetExe = fallbackExe;
        workDir = baseDir;
    }
    else
    {
        MessageBoxW(
            nullptr,
            L"Não foi possível encontrar os arquivos internos do YouLink (pasta App).\n\n"
            L"Se você abriu este arquivo direto dentro do .zip, extraia toda a pasta 'YouLink' primeiro "
            L"(botão direito no .zip -> Extrair Tudo) antes de clicar em YouLink.exe,\n"
            L"ou baixe e execute o instalador YouLink-Setup-x64.exe.",
            L"YouLink — Extração necessária",
            MB_OK | MB_ICONWARNING);
        return 1;
    }

    HANDLE hGuard = CreateMutexW(nullptr, FALSE, kLauncherGuardName);
    if (hGuard)
    {
        WaitForSingleObject(hGuard, 3000);
    }

    if (TryActivateExistingInstance(targetExe))
    {
        if (hGuard)
        {
            ReleaseMutex(hGuard);
            CloseHandle(hGuard);
        }
        return 0;
    }

    const bool launched = LaunchTarget(targetExe, workDir, pCmdLine);
    if (hGuard)
    {
        ReleaseMutex(hGuard);
        CloseHandle(hGuard);
    }

    if (!launched)
    {
        MessageBoxW(
            nullptr,
            L"Não foi possível iniciar o YouLink. Tente extrair novamente os arquivos ou usar o instalador YouLink-Setup-x64.exe.",
            L"YouLink",
            MB_OK | MB_ICONERROR);
        return 1;
    }

    return 0;
}
