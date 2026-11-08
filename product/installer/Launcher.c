#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <strsafe.h>

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR args, int show)
{
    wchar_t directory[MAX_PATH], program[MAX_PATH];
    DWORD length = GetModuleFileNameW(NULL, directory, ARRAYSIZE(directory));
    if (length && length < ARRAYSIZE(directory)) {
        while (length && directory[length - 1] != L'\\') --length;
        if (length && SUCCEEDED(StringCchCopyW(directory + length, ARRAYSIZE(directory) - length, L"app")) &&
            SUCCEEDED(StringCchPrintfW(program, ARRAYSIZE(program), L"%s\\JX3Artist.exe", directory))) {
            SHELLEXECUTEINFOW launch = { sizeof(launch) };
            launch.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
            launch.lpFile = program;
            launch.lpParameters = args;
            launch.lpDirectory = directory;
            launch.nShow = show;
            if (ShellExecuteExW(&launch) || GetLastError() == ERROR_CANCELLED) return 0;
        }
    }
    MessageBoxW(NULL, L"无法打开艺人助手。请确认安装目录完整，或重新运行安装程序。", L"艺人助手", MB_OK | MB_ICONERROR);
    return 1;
}
