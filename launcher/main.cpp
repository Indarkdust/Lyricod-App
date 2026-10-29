#include <windows.h>
#include <cwchar>
#include <string>
#include <vector>

// Starts app\Lyricod.exe, the real Lyricod, from the portable folder.
//
// With "--apply-update <pid>" it finishes an update instead. Lyricod has put the new version beside
// the old one (app.new, Lyricod.exe.new, README.txt.new), started this new launcher as
// Lyricod.exe.new and is quitting. Once it has gone, this moves the old app and launcher aside
// under names no earlier update can have taken (app.old-<n>, Lyricod.exe.old-<n>), moves the new
// ones in, starts the new Lyricod and removes the old files. Whatever can't be removed yet (say a
// file another program still has open) is tried again at Lyricod's next start and at the next
// update, and never blocks one. If the swap fails, the old Lyricod is put back and started.
//
// The old copy stays until the new Lyricod says it has started properly (it sets the event named in
// LYRICOD_UPDATE_SIGNAL once its windows, tray icon and media tracking are up). If it exits or hangs
// instead, the old copy is put back and started. Nothing in that run of Lyricod deletes the old copy.

static std::wstring SystemMessage(DWORD error)
{
    std::wstring text;
    wchar_t* detail = nullptr;
    if (FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                       FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, error, 0,
                       reinterpret_cast<wchar_t*>(&detail), 0, nullptr))
    {
        text = detail;
        LocalFree(detail);
    }
    return text + L" (" + std::to_wstring(error) + L")";
}

static int ShowError(DWORD error)
{
    std::wstring message =
        L"Could not start Lyricod. Extract the whole zip and keep the app folder next to this Lyricod.exe.\n\n"
        L"无法启动 Lyricod。请解压整个 zip 文件，并将 app 文件夹保留在此 Lyricod.exe 旁边。\n\n";
    message += SystemMessage(error);
    MessageBoxW(nullptr, message.c_str(), L"Lyricod", MB_OK | MB_ICONERROR);
    return 1;
}

/// <summary>Said after the old Lyricod is running again, so it's back without waiting for OK.</summary>
static void ShowUpdateError(DWORD error)
{
    std::wstring message =
        L"Lyricod couldn't finish updating, so it was left as it was. You can update it from the release page instead.\n\n"
        L"Lyricod 未能完成更新，已保持原样。你也可以从发布页面下载新版本。\n\n";
    message += SystemMessage(error);
    MessageBoxW(nullptr, message.c_str(), L"Lyricod", MB_OK | MB_ICONWARNING);
}

static const wchar_t* Arguments()
{
    // Only skip argv[0]. Keep the remaining command line, including its quoting, verbatim.
    const wchar_t* cursor = GetCommandLineW();
    bool quoted = false;
    while (*cursor)
    {
        if (*cursor == L'"') quoted = !quoted;
        else if (!quoted && (*cursor == L' ' || *cursor == L'\t')) break;
        ++cursor;
    }
    return cursor;
}

/// <summary>The process id after "--apply-update", or 0 for a normal start.</summary>
static DWORD UpdateRequest(const wchar_t* arguments)
{
    const wchar_t* cursor = arguments;
    while (*cursor == L' ' || *cursor == L'\t') ++cursor;
    static const wchar_t flag[] = L"--apply-update";
    const size_t length = (sizeof(flag) / sizeof(flag[0])) - 1;
    if (wcsncmp(cursor, flag, length) != 0) return 0;
    cursor += length;
    if (*cursor != L' ' && *cursor != L'\t') return 0;
    while (*cursor == L' ' || *cursor == L'\t') ++cursor;
    DWORD id = 0;
    bool digits = false;
    for (; *cursor >= L'0' && *cursor <= L'9'; ++cursor)
    {
        if (id > (MAXDWORD - 9) / 10) return 0;
        id = id * 10 + static_cast<DWORD>(*cursor - L'0');
        digits = true;
    }
    while (*cursor == L' ' || *cursor == L'\t') ++cursor;
    return digits && *cursor == 0 ? id : 0;
}

static const wchar_t SignalVariable[] = L"LYRICOD_UPDATE_SIGNAL";

/// <summary>Starts app\Lyricod.exe; with <paramref name="processOut"/>, hands back its process handle.</summary>
static DWORD StartApp(const std::wstring& root, const wchar_t* arguments, HANDLE* processOut = nullptr)
{
    const std::wstring directory = root + L"app";
    const std::wstring executable = directory + L"\\Lyricod.exe";
    std::wstring command = L"\"" + executable + L"\"" + arguments;

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0,
                        nullptr, directory.c_str(), &startup, &process))
        return GetLastError();

    // Let Lyricod bring its own windows to the front, as it could if started directly.
    AllowSetForegroundWindow(process.dwProcessId);
    CloseHandle(process.hThread);
    if (processOut) *processOut = process.hProcess;
    else CloseHandle(process.hProcess);
    return ERROR_SUCCESS;
}

static bool Exists(const std::wstring& path)
{
    return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

/// <summary>Renames, retrying for a few seconds: a virus scanner or the old Lyricod's last handles
/// can hold the files briefly after it has quit.</summary>
static DWORD Move(const std::wstring& from, const std::wstring& to, DWORD flags = 0)
{
    DWORD error = ERROR_SUCCESS;
    for (int attempt = 0; attempt < 40; ++attempt)
    {
        if (MoveFileExW(from.c_str(), to.c_str(), flags)) return ERROR_SUCCESS;
        error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) break;
        Sleep(250);
    }
    return error;
}

/// <summary>Best effort: whatever can't be removed now is tried again by Lyricod at its next start.</summary>
static void DeleteTree(const std::wstring& path)
{
    const DWORD attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) return;
    if (attributes & FILE_ATTRIBUTE_READONLY)
        SetFileAttributesW(path.c_str(), attributes & ~static_cast<DWORD>(FILE_ATTRIBUTE_READONLY));
    if (!(attributes & FILE_ATTRIBUTE_DIRECTORY) || (attributes & FILE_ATTRIBUTE_REPARSE_POINT))
    {
        // A link is removed itself, never followed.
        if (attributes & FILE_ATTRIBUTE_DIRECTORY) RemoveDirectoryW(path.c_str());
        else DeleteFileW(path.c_str());
        return;
    }
    WIN32_FIND_DATAW entry;
    HANDLE find = FindFirstFileW((path + L"\\*").c_str(), &entry);
    if (find != INVALID_HANDLE_VALUE)
    {
        do
        {
            const std::wstring name = entry.cFileName;
            if (name != L"." && name != L"..") DeleteTree(path + L"\\" + name);
        } while (FindNextFileW(find, &entry));
        FindClose(find);
    }
    RemoveDirectoryW(path.c_str());
}

/// <summary>Removes what earlier updates left behind: app.old…, Lyricod.exe.old….</summary>
static void DeleteLeftovers(const std::wstring& root)
{
    for (const wchar_t* prefix : { L"app.old", L"Lyricod.exe.old" })
    {
        std::vector<std::wstring> names;
        WIN32_FIND_DATAW entry;
        HANDLE find = FindFirstFileW((root + prefix + L"*").c_str(), &entry);
        if (find == INVALID_HANDLE_VALUE) continue;
        do
        {
            // The pattern can also match short (8.3) names, so check the real one.
            if (_wcsnicmp(entry.cFileName, prefix, wcslen(prefix)) == 0) names.push_back(entry.cFileName);
        } while (FindNextFileW(find, &entry));
        FindClose(find);
        for (const auto& name : names) DeleteTree(root + name);
    }
}

static int ApplyUpdate(const std::wstring& root, const std::wstring& self, DWORD oldProcess)
{
    // Wait for the old Lyricod to quit; it was told to right after starting this.
    if (HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, oldProcess))
    {
        const DWORD waited = WaitForSingleObject(process, 30000);
        CloseHandle(process);
        if (waited != WAIT_OBJECT_0)
        {
            ShowUpdateError(ERROR_TIMEOUT);
            return 1;
        }
    }

    const std::wstring stamp = std::to_wstring(GetTickCount64());
    const std::wstring app = root + L"app", fresh = root + L"app.new", old = root + L"app.old-" + stamp;
    const std::wstring launcher = root + L"Lyricod.exe", oldLauncher = root + L"Lyricod.exe.old-" + stamp;
    if (!Exists(fresh + L"\\Lyricod.exe"))
    {
        StartApp(root, L"");
        ShowUpdateError(ERROR_FILE_NOT_FOUND);
        return 1;
    }

    DeleteLeftovers(root);

    // The new Lyricod says it has fully started by setting this event. Without it there'd be no telling
    // whether it did, so the update doesn't go ahead (nothing has been touched yet).
    const std::wstring signalName = L"Local\\Lyricod-UpdateStarted-" + std::to_wstring(GetCurrentProcessId()) + L"-" + stamp;
    HANDLE started = CreateEventW(nullptr, TRUE, FALSE, signalName.c_str());
    if (!started)
    {
        const DWORD error = GetLastError();
        StartApp(root, L"");
        ShowUpdateError(error);
        return 1;
    }

    if (DWORD error = Move(app, old))
    {
        CloseHandle(started);
        StartApp(root, L"");
        ShowUpdateError(error);
        return 1;
    }
    if (DWORD error = Move(fresh, app))
    {
        CloseHandle(started);
        Move(old, app);
        StartApp(root, L"");
        ShowUpdateError(error);
        return 1;
    }

    // Windows lets a running program be renamed, so this new launcher can take the old one's place.
    if (Move(launcher, oldLauncher) == ERROR_SUCCESS && Move(self, launcher) != ERROR_SUCCESS)
        Move(oldLauncher, launcher);
    Move(root + L"README.txt.new", root + L"README.txt", MOVEFILE_REPLACE_EXISTING);

    // Start the new Lyricod and wait for it to say it's up; the old copy stays until then.
    SetEnvironmentVariableW(SignalVariable, signalName.c_str());
    HANDLE process = nullptr;
    DWORD error = StartApp(root, L"", &process);
    SetEnvironmentVariableW(SignalVariable, nullptr);
    if (error == ERROR_SUCCESS)
    {
        HANDLE waits[] = { started, process };
        const DWORD waited = WaitForMultipleObjects(2, waits, FALSE, 90000);
        if (waited != WAIT_OBJECT_0)
        {
            // It quit, crashed or hung before it was up.
            if (waited == WAIT_TIMEOUT) TerminateProcess(process, 1);
            WaitForSingleObject(process, 10000);
            error = waited == WAIT_TIMEOUT ? ERROR_TIMEOUT : ERROR_PROCESS_ABORTED;
        }
    }
    if (process) CloseHandle(process);
    CloseHandle(started);

    if (error != ERROR_SUCCESS)
    {
        // Put the old app and launcher back and start them, rather than leave a Lyricod that can't run.
        if (Move(app, fresh) == ERROR_SUCCESS && Move(old, app) == ERROR_SUCCESS)
        {
            if (Exists(oldLauncher) && Move(launcher, self) == ERROR_SUCCESS) Move(oldLauncher, launcher);
            StartApp(root, L"");
            ShowUpdateError(error);
        }
        else ShowError(error);
        return 1;
    }
    DeleteTree(old);
    DeleteTree(oldLauncher);
    return 0;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    // Grow the buffer for paths longer than MAX_PATH.
    std::vector<wchar_t> path(512);
    DWORD length;
    for (;;)
    {
        length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
        if (length == 0) return ShowError(GetLastError());
        if (length < path.size()) break;
        if (path.size() >= 32768) return ShowError(ERROR_FILENAME_EXCED_RANGE);
        path.resize(path.size() * 2);
    }

    const std::wstring module(path.data(), length);
    const auto separator = module.find_last_of(L"\\/");
    if (separator == std::wstring::npos) return ShowError(ERROR_BAD_PATHNAME);
    const std::wstring root = module.substr(0, separator + 1);

    const wchar_t* arguments = Arguments();
    if (DWORD oldProcess = UpdateRequest(arguments)) return ApplyUpdate(root, module, oldProcess);

    if (DWORD error = StartApp(root, arguments)) return ShowError(error);
    return 0;
}
