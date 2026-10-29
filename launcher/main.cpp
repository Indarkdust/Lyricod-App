#include <windows.h>
#include <string>
#include <vector>

static int ShowError(DWORD error)
{
    std::wstring message =
        L"Could not start Lyricod. Extract the whole zip and keep the app folder next to this Lyricod.exe.\n\n"
        L"无法启动 Lyricod。请解压整个 zip 文件，并将 app 文件夹保留在此 Lyricod.exe 旁边。\n\n";

    wchar_t* detail = nullptr;
    if (FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                       FORMAT_MESSAGE_IGNORE_INSERTS, nullptr, error, 0,
                       reinterpret_cast<wchar_t*>(&detail), 0, nullptr))
    {
        message += detail;
        LocalFree(detail);
    }
    message += L" (" + std::to_wstring(error) + L")";
    MessageBoxW(nullptr, message.c_str(), L"Lyricod", MB_OK | MB_ICONERROR);
    return 1;
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
    const std::wstring directory = module.substr(0, separator + 1) + L"app";
    const std::wstring executable = directory + L"\\Lyricod.exe";
    std::wstring command = L"\"" + executable + L"\"" + Arguments();

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0,
                        nullptr, directory.c_str(), &startup, &process))
        return ShowError(GetLastError());

    // Let Lyricod bring its own windows to the front, as it could if started directly.
    AllowSetForegroundWindow(process.dwProcessId);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 0;
}
