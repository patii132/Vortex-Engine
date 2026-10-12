// The Visual Studio solution links Engine.lib by name; the CMake build links the vortex::engine target (#182).
#ifndef VORTEX_CMAKE_BUILD
#pragma comment(lib, "Engine.lib")
#endif

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <crtdbg.h>
#include <cstdio>
#include <cstdlib>   // _set_invalid_parameter_handler

#if _DEBUG
// A failed Debug-CRT report (standard assert(), _ASSERTE, STL iterator-debug checks, invalid-parameter)
// normally pops the Abort/Retry/Ignore WINDOW or breaks into the debugger. Shown from engine init on the
// WPF UI thread it appears BEHIND the Topmost startup splash (the UI thread then deadlocks in the dialog's
// message loop) or halts F5 in native code — either way the editor "hangs" at the splash under Visual
// Studio. This hook routes every such report to the debugger output + %TEMP%\vortex_crt.log and returns
// 0 = "handled, do NOT break, continue" — matching Release/standalone, which never had this problem.
static int VortexCrtReportHook(int reportType, char* message, int* returnValue)
{
    if (message)
    {
        OutputDebugStringA(message);
        char logPath[MAX_PATH]{};
        DWORD n = GetTempPathA(MAX_PATH, logPath);
        if (n > 0 && n <= MAX_PATH)
        {
            strncat_s(logPath, "vortex_crt.log", _TRUNCATE);
            FILE* f = nullptr;
            if (fopen_s(&f, logPath, "a") == 0 && f) { fputs(message, f); fclose(f); }
        }
    }
    if (returnValue) *returnValue = 0;   // 0 = do not break into the debugger
    return TRUE;                          // TRUE = report fully handled, suppress the default dialog/break
}

// The Debug CRT's invalid-parameter path (e.g. a bad printf spec, an out-of-range STL index) also breaks/
// aborts by default. Swallow it to debug output so it can't freeze startup under F5 either.
static void VortexInvalidParameter(const wchar_t*, const wchar_t*, const wchar_t*, unsigned int, uintptr_t)
{
    OutputDebugStringA("[CRT] invalid parameter (ignored)\n");
}
#endif

// A native crash (an access violation in the engine, a driver fault) used to end the process with nothing but the
// Windows Error Reporting dialog. This filter writes a minidump + a note to %APPDATA%\VortexEngine\logs\ (the folder of the
// editor's editor-errors.log), then lets the previous handler / the OS carry on. dbghelp is loaded on demand — no link
// dependency, nothing happens on a machine without it.
static LPTOP_LEVEL_EXCEPTION_FILTER s_previous_filter = nullptr;
static LONG WINAPI VortexCrashFilter(EXCEPTION_POINTERS* info)
{
    static volatile LONG once = 0;
    if (InterlockedCompareExchange(&once, 1, 0) == 0 && info && info->ExceptionRecord)
    {
        char dir[MAX_PATH]{};
        DWORD n = GetEnvironmentVariableA("APPDATA", dir, MAX_PATH);
        if (n > 0 && n < MAX_PATH)
        {
            strncat_s(dir, "\\VortexEngine", _TRUNCATE); CreateDirectoryA(dir, nullptr);
            strncat_s(dir, "\\logs", _TRUNCATE); CreateDirectoryA(dir, nullptr);
            SYSTEMTIME t{}; GetLocalTime(&t);
            char stamp[32]{};
            snprintf(stamp, sizeof(stamp), "%04d%02d%02d-%02d%02d%02d", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
            char path[MAX_PATH]{};
            // the note: what, where, in which module
            snprintf(path, sizeof(path), "%s\\crash-%s.txt", dir, stamp);
            FILE* f = nullptr;
            if (fopen_s(&f, path, "w") == 0 && f)
            {
                const EXCEPTION_RECORD* r = info->ExceptionRecord;
                HMODULE mod = nullptr; char modName[MAX_PATH] = "?";
                if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)r->ExceptionAddress, &mod) && mod)
                    GetModuleFileNameA(mod, modName, MAX_PATH);
                fprintf(f, "Vortex native crash %s\nexception 0x%08X at %p\nmodule %s (base %p, offset 0x%llX)\n",
                    stamp, (unsigned)r->ExceptionCode, r->ExceptionAddress, modName, (void*)mod,
                    (unsigned long long)((char*)r->ExceptionAddress - (char*)mod));
                if (r->ExceptionCode == EXCEPTION_ACCESS_VIOLATION && r->NumberParameters >= 2)
                    fprintf(f, "access violation: %s address 0x%llX\n", r->ExceptionInformation[0] == 0 ? "reading" : (r->ExceptionInformation[0] == 1 ? "writing" : "executing"), (unsigned long long)r->ExceptionInformation[1]);
                fprintf(f, "minidump: crash-%s.dmp (next to this file, if dbghelp was available)\n", stamp);
                fclose(f);
            }
            // the minidump through dbghelp, loaded on demand
            typedef BOOL (WINAPI *MiniDumpWriteDumpFn)(HANDLE, DWORD, HANDLE, int, void*, void*, void*);
            HMODULE dbghelp = LoadLibraryA("dbghelp.dll");
            MiniDumpWriteDumpFn writeDump = dbghelp ? (MiniDumpWriteDumpFn)GetProcAddress(dbghelp, "MiniDumpWriteDump") : nullptr;
            if (writeDump)
            {
                snprintf(path, sizeof(path), "%s\\crash-%s.dmp", dir, stamp);
                HANDLE h = CreateFileA(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
                if (h != INVALID_HANDLE_VALUE)
                {
                    struct { DWORD ThreadId; EXCEPTION_POINTERS* ExceptionPointers; BOOL ClientPointers; } mei{ GetCurrentThreadId(), info, FALSE };
                    // dbghelp.h: MiniDumpWithDataSegs 0x1 | MiniDumpWithUnloadedModules 0x20 | MiniDumpWithIndirectlyReferencedMemory 0x40 | MiniDumpWithThreadInfo 0x1000
                    writeDump(GetCurrentProcess(), GetCurrentProcessId(), h, 0x00001061, &mei, nullptr, nullptr);
                    CloseHandle(h);
                }
            }
        }
    }
    return s_previous_filter ? s_previous_filter(info) : EXCEPTION_CONTINUE_SEARCH;
}

BOOL APIENTRY DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
                     )
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        s_previous_filter = SetUnhandledExceptionFilter(VortexCrashFilter);   // a minidump + note on a native crash (see above)
#if _DEBUG
        _CrtSetDbgFlag(_CRTDBG_ALLOC_MEM_DF | _CRTDBG_LEAK_CHECK_DF);
        // Send asserts/warnings/errors to our log-and-continue hook instead of a modal window that would
        // freeze the editor behind the startup splash under F5. (Release compiles all of this out.)
        _CrtSetReportHook(VortexCrtReportHook);
        _CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_FILE | _CRTDBG_MODE_DEBUG);
        _CrtSetReportMode(_CRT_ERROR,  _CRTDBG_MODE_FILE | _CRTDBG_MODE_DEBUG);
        _CrtSetReportMode(_CRT_WARN,   _CRTDBG_MODE_FILE | _CRTDBG_MODE_DEBUG);
        _set_invalid_parameter_handler(VortexInvalidParameter);
#endif
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

