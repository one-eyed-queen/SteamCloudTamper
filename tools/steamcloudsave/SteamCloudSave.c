// SteamCloudSave.c - steam_api64 shim + RemoteStorage shadow lane for SCT.
//
// One DLL, works across the whole mod ecosystem. Mostly drops in and just works:
//   1) SHIM : rename this DLL to steam_api64.dll in a game folder (back up the real one).
//   2) GBE  : drop into <game>/steam_settings/load_dlls/SteamCloudSave.dll
//             (gbe_fork / SLS fork loads every DLL there via LoadLibraryW automatically).
//   3) OST  : load via OpenSteamTool's [inject] (library_x64/library_x86) into the game process.
//
// AUTODETECT: if you set no config at all, the DLL figures out:
//   - steam path   : from the Windows registry (HKCU/HKLM Valve\Steam), like SteamLocator.cs
//   - appid        : from <own folder>\steam_appid.txt, else ignored
//   - shadowRoot   : defaults to %LOCALAPPDATA%\SCT\shadow\<appid>
//   - loader        : detected for logging + status (load_dlls / OST toml / SHIM / GreenLuma)
//   So for the common case (drop the DLL next to the game or into load_dlls) there is
//   NO config file needed. A config file still overrides everything:
//     appid, shadowRoot, steamPath, autoPark, minShadowBytes
//
// Config file steamcloudsave.cfg (or env SCT_SCS_CONFIG / SCT_HOOK_CONFIG):
//   steamPath=D:\Steam      - where the REAL steam_api64.dll lives (passthrough); auto if unset
//   shadowRoot=D:\sct_shadow- full override of the default shadow dir
//   appid=91330             - the game being redirected; auto if unset
//   autoPark=D:\SCT\SteamCloudTamper.exe  - OPT-IN: spawn SCT CLI to park after a shadow write
//   minShadowBytes=0        - only autoPark when the shadow file grew past this (avoid spam)
//
// When appid + shadowRoot are set, ISteamRemoteStorage calls for that game read/write
// only "<shadowRoot>\<appid>\<file>" and never touch Steam's cloud or userdata.
// Otherwise everything forwards to the real steam_api64.dll next to steamPath.
//
// Status: after every shadow write, a small JSON file is written to
//   %LOCALAPPDATA%\SCT\shadow-status.json  (gameAppId, redirected, files, shadowRoot, loader)
// so the SCT CLI/TUI know which games are actively shadowed without touching the real
// registry.json (which the CLI owns and may be writing concurrently).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <stdarg.h>

#ifdef __cplusplus
extern "C" {
#endif

static char        g_steamPath[MAX_PATH] = {0};
static char        g_shadowRoot[MAX_PATH] = {0};
static char        g_registryPath[MAX_PATH] = {0};
static char        g_autoPark[MAX_PATH] = {0};
static uint32_t    g_targetAppId = 0;
static uint32_t    g_minShadowBytes = 0;
static int         g_resolveMode = 0; // 0=auto 1=path 2=inproc
static HMODULE     g_real = NULL;
static int         g_inited = 0;
static int         g_loaderKind = 0; // 0=unknown 1=shim 2=load_dlls(gbe/sls) 3=ost-inject 4=greenluma
static char        g_dllPath[MAX_PATH] = {0};
static char        g_processDir[MAX_PATH] = {0};
static DWORD       g_lastParkTick = 0;

static void sctLog(const char* fmt, ...)
{
    FILE* f = fopen("steamcloudsave.log", "a");
    if (!f) return;
    va_list ap; va_start(ap, fmt);
    vfprintf(f, fmt, ap); va_end(ap);
    fputs("\n", f);
    fclose(f);
}

static int endsWithI(const char* s, const char* suf)
{
    size_t ls = strlen(s), lu = strlen(suf);
    if (lu > ls) return 0;
    return _strnicmp(s + ls - lu, suf, lu) == 0;
}

static int fileExists(const char* p)
{
    return GetFileAttributesA(p) != INVALID_FILE_ATTRIBUTES;
}

// ---------------------------------------------------------------------------
// Autodetect: steam path (registry), appid (steam_appid.txt), loader context
// ---------------------------------------------------------------------------
static void detectSteamPath(char* out, size_t n)
{
    HKEY keys[3] = {0};
    REGSAM acc = KEY_WOW64_64KEY | KEY_READ;
    const char* names[3] = { "SteamPath", "InstallPath", "InstallPath" };
    DWORD accs[3] = { KEY_READ, acc, KEY_READ };
    const char* roots[3] = { "Software\\Valve\\Steam",
                             "SOFTWARE\\WOW6432Node\\Valve\\Steam",
                             "SOFTWARE\\Valve\\Steam" };
    for (int i = 0; i < 3; i++)
    {
        HKEY h = NULL;
        DWORD a = accs[i];
        if (RegOpenKeyExA(i == 0 ? HKEY_CURRENT_USER : HKEY_LOCAL_MACHINE,
                          roots[i], 0, a, &h) == ERROR_SUCCESS)
        {
            char buf[MAX_PATH]; DWORD len = sizeof(buf); DWORD type = 0;
            if (RegQueryValueExA(h, names[i], NULL, &type, (LPBYTE)buf, &len) == ERROR_SUCCESS
                && type == REG_SZ && buf[0])
            {
                strncpy(out, buf, n - 1);
                RegCloseKey(h);
                return;
            }
            RegCloseKey(h);
        }
    }
    // fallback guesses
    static const char* guesses[] = {
        "C:\\Program Files (x86)\\Steam",
        "C:\\Program Files\\Steam",
        "D:\\Steam",
        "E:\\Steam"
    };
    for (int i = 0; i < 4; i++)
        if (fileExists(guesses[i])) { strncpy(out, guesses[i], n - 1); return; }
}

// Read a numeric appid from <dir>\steam_appid.txt
static uint32_t detectAppIdFromDir(const char* dir)
{
    char p[MAX_PATH];
    snprintf(p, sizeof(p), "%s\\steam_appid.txt", dir);
    FILE* f = fopen(p, "r");
    if (!f) return 0;
    char line[64] = {0};
    if (fgets(line, sizeof(line), f))
    {
        strtok(line, "\r\n ");
        fclose(f);
        return (uint32_t)strtoul(line, NULL, 10);
    }
    fclose(f);
    return 0;
}

// Guess the loader context from where this DLL lives and from the process folder.
static int detectLoaderContext(void)
{
    // g_dllPath holds the full path of this DLL (set in DllMain).
    // A DLL living in a load_dlls or overrides folder = gbe_fork / SLS fork auto-load.
    if (endsWithI(g_dllPath, "\\load_dlls\\SteamCloudSave.dll")
        || endsWithI(g_dllPath, "\\overrides\\SteamCloudSave.dll"))
        return 2; // gbe_fork / SLS fork
    // SHIM: this DLL IS steam_api64.dll next to the game, and steam_appid.txt is a sibling.
    if (endsWithI(g_dllPath, "\\steam_api64.dll"))
        return 1;
    return 0;
}

// ---------------------------------------------------------------------------
// Config loading (no config needed for the common case)
// ---------------------------------------------------------------------------
static void setDefaultShadowRoot(char* out, size_t n)
{
    char local[MAX_PATH] = {0};
    if (SHGetFolderPathA(NULL, CSIDL_LOCAL_APPDATA, NULL, SHGFP_TYPE_CURRENT, local) != S_OK)
        strcpy(local, "C:\\Users\\Public");
    snprintf(out, n, "%s\\SCT\\shadow\\%u", local, g_targetAppId);
}

static void loadConfigFrom(const char* cfgPath)
{
    FILE* f = fopen(cfgPath, "r");
    if (!f) { sctLog("sct: no config %s", cfgPath); return; }

    char line[1024];
    while (fgets(line, sizeof(line), f))
    {
        char key[256], val[768];
        if (sscanf(line, " %255[^=]=%767[^\r\n]", key, val) != 2) continue;
        if (!_stricmp(key, "steamPath"))      strncpy(g_steamPath, val, sizeof(g_steamPath) - 1);
        else if (!_stricmp(key, "shadowRoot")) strncpy(g_shadowRoot, val, sizeof(g_shadowRoot) - 1);
        else if (!_stricmp(key, "appid"))      g_targetAppId = (uint32_t)strtoul(val, NULL, 10);
        else if (!_stricmp(key, "registryPath")) strncpy(g_registryPath, val, sizeof(g_registryPath) - 1);
        else if (!_stricmp(key, "autoPark"))     strncpy(g_autoPark, val, sizeof(g_autoPark) - 1);
        else if (!_stricmp(key, "minShadowBytes")) g_minShadowBytes = (uint32_t)strtoul(val, NULL, 10);
        else if (!_stricmp(key, "resolveMode"))
        {
            if (!_stricmp(val, "path")) g_resolveMode = 1;
            else if (!_stricmp(val, "inproc")) g_resolveMode = 2;
            else g_resolveMode = 0;
        }
    }
    fclose(f);
    sctLog("sct: config %s steam=%s appid=%u shadow=%s registry=%s autopark=%s resolve=%d",
           cfgPath, g_steamPath, g_targetAppId, g_shadowRoot, g_registryPath,
           g_autoPark[0] ? g_autoPark : "(off)", g_resolveMode);
}

static void loadConfig(void)
{
    if (g_inited) return;
    g_inited = 1;

    GetModuleFileNameA(NULL, g_processDir, sizeof(g_processDir));
    char* slash0 = strrchr(g_processDir, '\\');
    if (slash0) *slash0 = 0; // g_processDir = game/process folder

    if (g_dllPath[0] && !g_loaderKind)
        g_loaderKind = detectLoaderContext();

    // appid autodetect from the process folder's steam_appid.txt (covers load_dlls too)
    if (g_targetAppId == 0) g_targetAppId = detectAppIdFromDir(g_processDir);

    const char* cfgPath = getenv("SCT_SCS_CONFIG");
    if (cfgPath && cfgPath[0]) { loadConfigFrom(cfgPath); }
    else
    {
        const char* cfgEnv = getenv("SCT_HOOK_CONFIG");
        if (cfgEnv && cfgEnv[0]) loadConfigFrom(cfgEnv);
        else
        {
            char local[MAX_PATH] = {0};
            if (SHGetFolderPathA(NULL, CSIDL_LOCAL_APPDATA, NULL, SHGFP_TYPE_CURRENT, local) == S_OK)
            {
                char p[MAX_PATH];
                snprintf(p, sizeof(p), "%s\\SCT\\steamcloudsave.cfg", local);
                loadConfigFrom(p);
            }
            loadConfigFrom("steamcloudsave.cfg");
        }
    }

    // Autodetect whatever is still unset.
    if (!g_steamPath[0]) detectSteamPath(g_steamPath, sizeof(g_steamPath));
    if (!g_shadowRoot[0] && g_targetAppId != 0)
        setDefaultShadowRoot(g_shadowRoot, sizeof(g_shadowRoot));

    sctLog("sct: effective steam=%s appid=%u shadow=%s loader=%d resolve=%d",
           g_steamPath, g_targetAppId, g_shadowRoot[0] ? g_shadowRoot : "(none)", g_loaderKind, g_resolveMode);
}

static void mkdirs(const char* path)
{
    char tmp[MAX_PATH];
    strncpy(tmp, path, sizeof(tmp) - 1);
    for (char* p = tmp + 3; *p; p++)
    {
        if (*p == '\\') { *p = 0; CreateDirectoryA(tmp, NULL); *p = '\\'; }
    }
    CreateDirectoryA(tmp, NULL);
}

// shadow layout: <shadowRoot>\<appid>\<file>
static void shadowPathFor(const char* file, char* out, size_t outLen)
{
    snprintf(out, outLen, "%s\\%u\\%s", g_shadowRoot, g_targetAppId, file);
}

static HMODULE realModule(void)
{
    if (g_real) return g_real;

    // In gbe_fork / SLS sidecar mode (load_dlls), the emulator's steam_api64.dll
    // (and usually steamclient64.dll) is ALREADY loaded in this process, and the
    // game talks to it. Loading the real Valve DLL from steamPath would create a
    // second, conflicting API surface. So resolve passthrough to the already-loaded
    // module first (cheap, correct), only falling back to steamPath for the shim/case
    // where the game is running against the real Steam client.
    if (g_resolveMode != 1) // 1 = forced 'path'; 0=auto, 2=inproc => prefer in-proc
    {
        HMODULE inProc = GetModuleHandleA("steam_api64.dll");
        if (!inProc) inProc = GetModuleHandleA("steamclient64.dll");
        if (inProc)
        {
            g_real = inProc;
            sctLog("sct: passthrough -> already-loaded %s",
                   GetModuleHandleA("steam_api64.dll") ? "steam_api64.dll" : "steamclient64.dll");
            return g_real;
        }
    }

    if (g_resolveMode == 2) // 'inproc' forced: do NOT load from steamPath
    {
        sctLog("sct: no in-process steam api; resolveMode=inproc, not loading from steamPath");
        return NULL;
    }

    if (!g_steamPath[0]) { sctLog("sct: no steamPath - not forwarding"); return NULL; }
    char dllPath[MAX_PATH * 2];
    snprintf(dllPath, sizeof(dllPath), "%s\\steam_api64.dll", g_steamPath);
    g_real = LoadLibraryA(dllPath);
    if (!g_real) sctLog("sct: load real %s failed %lu", dllPath, GetLastError());
    return g_real;
}

static FARPROC realFn(const char* name)
{
    HMODULE h = realModule();
    return h ? GetProcAddress(h, name) : NULL;
}

static int ShadowMode(void) { return g_targetAppId != 0 && g_shadowRoot[0] != 0; }

// ---------------------------------------------------------------------------
// Status + optional background auto-park
// ---------------------------------------------------------------------------
static void writeStatusJson(uint32_t files, uint64_t bytes)
{
    char local[MAX_PATH] = {0};
    if (SHGetFolderPathA(NULL, CSIDL_LOCAL_APPDATA, NULL, SHGFP_TYPE_CURRENT, local) != S_OK)
        return;
    char dir[MAX_PATH];
    snprintf(dir, sizeof(dir), "%s\\SCT", local);
    mkdirs(dir);
    char p[MAX_PATH];
    snprintf(p, sizeof(p), "%s\\shadow-status.json", dir);

    FILE* f = fopen(p, "w");
    if (!f) return;
    fprintf(f, "{\"gameAppId\":%u,\"redirected\":true,\"files\":%u,\"bytes\":%llu,"
               "\"shadowRoot\":\"%s\",\"loader\":%d}\n",
            g_targetAppId, files, (unsigned long long)bytes, g_shadowRoot, g_loaderKind);
    fclose(f);
}

// Opt-in: after a shadow write, spawn the SCT CLI to park <appid> in the background.
static void maybeAutoPark(uint32_t bytesWritten)
{
    if (!g_autoPark[0]) return;
    if (!fileExists(g_autoPark)) { sctLog("sct: autopark %s missing", g_autoPark); return; }
    if (bytesWritten < g_minShadowBytes) return;
    // rate-limit: at most one spawn per 30s (signed diff survives GetTickCount wrap)
    DWORD now = GetTickCount();
    if ((int32_t)(now - g_lastParkTick) < 30000) return;
    g_lastParkTick = now;

    char cmd[MAX_PATH * 2 + 32];
    snprintf(cmd, sizeof(cmd), "\"%s\" park %u --lane rpc --stealth",
             g_autoPark, g_targetAppId);

    STARTUPINFOA si; PROCESS_INFORMATION pi;
    memset(&si, 0, sizeof(si)); memset(&pi, 0, sizeof(pi));
    si.cb = sizeof(si);
    // CreateProcessA takes a command line, NOT a shell line: no shell redirection
    // here. DETACHED_PROCESS + CREATE_NO_WINDOW hide the console so the game never
    // waits on it and no window flashes.
    if (CreateProcessA(NULL, cmd, NULL, NULL, FALSE,
                       DETACHED_PROCESS | CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP,
                       NULL, NULL, &si, &pi))
    {
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        sctLog("sct: autopark spawned for appid %u", g_targetAppId);
    }
    else
    {
        sctLog("sct: autopark spawn failed %lu", GetLastError());
    }
}

// ---------------------------------------------------------------------------
// ISteamRemoteStorage bridges (the exports a game's IAT references)
// ---------------------------------------------------------------------------
static int32_t WINAPI Hook_FileWrite(void* c, const char* file, const void* data, int32_t cb)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH * 2];
        shadowPathFor(file, shadow, sizeof(shadow));
        mkdirs(shadow);
        FILE* fp = fopen(shadow, "wb");
        if (!fp) { sctLog("sct: shadow write open fail %s", shadow); return 0; }
        fwrite(data, 1, cb, fp); fclose(fp);
        sctLog("sct: shadow write %s (%d)", shadow, cb);
        writeStatusJson(1, (uint64_t)cb);
        maybeAutoPark((uint32_t)cb);
        return cb;
    }
    typedef int32_t (*F)(void*, const char*, const void*, int32_t);
    F f = (F)realFn("SteamAPI_ISteamRemoteStorage_FileWrite");
    return f ? f(c, file, data, cb) : 0;
}

static int32_t WINAPI SctFileRead(void* c, const char* file, void* data, int32_t toRead)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH * 2];
        shadowPathFor(file, shadow, sizeof(shadow));
        FILE* fp = fopen(shadow, "rb");
        if (fp) { size_t got = fread(data, 1, toRead, fp); fclose(fp); return (int32_t)got; }
        sctLog("sct: shadow read miss %s", file);
        return 0;
    }
    typedef int32_t (*FN)(void*, const char*, void*, int32_t);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileRead");
    return f ? f(c, file, data, toRead) : 0;
}

static bool WINAPI SctFileDelete(void* c, const char* file)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH * 2];
        shadowPathFor(file, shadow, sizeof(shadow));
        DeleteFileA(shadow);
        return true;
    }
    typedef bool (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileDelete");
    return f ? f(c, file) : false;
}

static bool WINAPI SctFileForget(void* c, const char* file)
{
    if (ShadowMode()) return true;
    typedef bool (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileForget");
    return f ? f(c, file) : false;
}

static bool WINAPI SctFileExists(void* c, const char* file)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH];
        shadowPathFor(file, shadow, sizeof(shadow));
        return GetFileAttributesA(shadow) != INVALID_FILE_ATTRIBUTES;
    }
    typedef bool (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileExists");
    return f ? f(c, file) : false;
}

static int64_t WINAPI SctFileSize(void* c, const char* file)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH];
        shadowPathFor(file, shadow, sizeof(shadow));
        HANDLE h = CreateFileA(shadow, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        if (h == INVALID_HANDLE_VALUE) return 0;
        LARGE_INTEGER sz;
        GetFileSizeEx(h, &sz);
        CloseHandle(h);
        return sz.QuadPart;
    }
    typedef int64_t (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileSize");
    return f ? f(c, file) : 0;
}

static bool WINAPI SctFilePersisted(void* c, const char* file)
{
    if (ShadowMode()) return true;
    typedef bool (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FilePersisted");
    return f ? f(c, file) : false;
}

static int64_t WINAPI SctFileTimestamp(void* c, const char* file)
{
    if (ShadowMode())
    {
        char shadow[MAX_PATH];
        shadowPathFor(file, shadow, sizeof(shadow));
        HANDLE h = CreateFileA(shadow, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        if (h == INVALID_HANDLE_VALUE) return 0;
        FILETIME ft;
        GetFileTime(h, NULL, NULL, &ft);
        CloseHandle(h);
        uint64_t t = ((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
        return (int64_t)(t / 10000000ULL - 11644473600ULL);
    }
    typedef int64_t (*FN)(void*, const char*);
    FN f = (FN) realFn("SteamAPI_ISteamRemoteStorage_FileGetTimestamp");
    return f ? f(c, file) : 0;
}

// Generic 1:1 forward (arbitrary signature) used by exports not shadow-managed.
#define PASSTHRU(ret, name, expsig, call) \
    __declspec(dllexport) ret WINAPI name expsig \
    { \
        typedef ret (*FN) expsig; \
        FN _fn = (FN) realFn(#name); \
        return _fn ? _fn call : (ret)0; \
    }

PASSTHRU(int32_t, SteamAPI_ISteamRemoteStorage_GetFileCount, (void* c), (c))
PASSTHRU(bool, SteamAPI_ISteamRemoteStorage_IsCloudEnabledForApp, (void* c), (c))
PASSTHRU(bool, SteamAPI_ISteamRemoteStorage_IsCloudEnabledForAccount, (void* c), (c))
PASSTHRU(void, SteamAPI_ISteamRemoteStorage_SetCloudEnabledForApp, (void* c, bool b), (c, b))
PASSTHRU(int32_t, SteamAPI_ISteamRemoteStorage_FileReadAsync, (void* c, const char* f, uint32_t o, uint32_t n), (c, f, o, n))
PASSTHRU(bool, SteamAPI_ISteamRemoteStorage_FileReadAsyncComplete, (void* c, uint64_t call, void* b, uint32_t n), (c, call, b, n))

// Exports for the shadow-managed set:
#define SHADOW_EXPORT(ret, name, expsig, impl) \
    __declspec(dllexport) ret WINAPI name expsig { return impl; }

SHADOW_EXPORT(int32_t, SteamRemoteStorage_FileWrite, (void* c, const char* f, const void* d, int32_t n), Hook_FileWrite(c, f, d, n))
SHADOW_EXPORT(int32_t, SteamRemoteStorage_FileRead, (void* c, const char* f, void* d, int32_t n), SctFileRead(c, f, d, n))
SHADOW_EXPORT(bool, SteamRemoteStorage_FileDelete, (void* c, const char* f), SctFileDelete(c, f))
SHADOW_EXPORT(bool, SteamRemoteStorage_FileForget, (void* c, const char* f), SctFileForget(c, f))
SHADOW_EXPORT(bool, SteamRemoteStorage_FileExists, (void* c, const char* f), SctFileExists(c, f))
SHADOW_EXPORT(int64_t, SteamRemoteStorage_FileSize, (void* c, const char* f), SctFileSize(c, f))
SHADOW_EXPORT(bool, SteamRemoteStorage_FilePersisted, (void* c, const char* f), SctFilePersisted(c, f))
SHADOW_EXPORT(int64_t, SteamRemoteStorage_FileGetTimestamp, (void* c, const char* f), SctFileTimestamp(c, f))

// The real steam_api64 export table uses "SteamAPI_ISteamRemoteStorage_*" names;
// expose the official names too so games linking by them reach us first.
__declspec(dllexport) int32_t WINAPI SteamAPI_ISteamRemoteStorage_FileWrite(void* c, const char* f, const void* d, int32_t n)
{ return Hook_FileWrite(c, f, d, n); }
__declspec(dllexport) int32_t WINAPI SteamAPI_ISteamRemoteStorage_FileRead(void* c, const char* f, void* d, int32_t n)
{ return SctFileRead(c, f, d, n); }
__declspec(dllexport) bool WINAPI SteamAPI_ISteamRemoteStorage_FileDelete(void* c, const char* f)
{ return SctFileDelete(c, f); }
__declspec(dllexport) bool WINAPI SteamAPI_ISteamRemoteStorage_FileForget(void* c, const char* f)
{ return SctFileForget(c, f); }
__declspec(dllexport) bool WINAPI SteamAPI_ISteamRemoteStorage_FileExists(void* c, const char* f)
{ return SctFileExists(c, f); }
__declspec(dllexport) int64_t WINAPI SteamAPI_ISteamRemoteStorage_FileSize(void* c, const char* f)
{ return SctFileSize(c, f); }
__declspec(dllexport) bool WINAPI SteamAPI_ISteamRemoteStorage_FilePersisted(void* c, const char* f)
{ return SctFilePersisted(c, f); }
__declspec(dllexport) int64_t WINAPI SteamAPI_ISteamRemoteStorage_FileGetTimestamp(void* c, const char* f)
{ return SctFileTimestamp(c, f); }

// Steam API core entrypoints a game's import table may need:
typedef bool (*InitFn)();
__declspec(dllexport) bool WINAPI SteamAPI_Init(void)
{
    HMODULE h = realModule();
    if (!h) return false;
    InitFn fn = (InitFn)GetProcAddress(h, "SteamAPI_Init");
    return fn ? fn() : false;
}
__declspec(dllexport) void WINAPI SteamAPI_Shutdown(void)
{
    HMODULE h = realModule();
    if (!h) return;
    typedef void (*FN)();
    FN fn = (FN)GetProcAddress(h, "SteamAPI_Shutdown");
    if (fn) fn();
}
__declspec(dllexport) void WINAPI SteamAPI_RunCallbacks(void)
{
    HMODULE h = realModule();
    if (!h) return;
    typedef void (*FN)();
    FN fn = (FN)GetProcAddress(h, "SteamAPI_RunCallbacks");
    if (fn) fn();
}
__declspec(dllexport) uint64_t WINAPI SteamAPI_GetHSteamUser(void)
{
    HMODULE h = realModule();
    if (!h) return 0;
    typedef uint64_t (*FN)();
    FN fn = (FN)GetProcAddress(h, "SteamAPI_GetHSteamUser");
    return fn ? fn() : 0;
}
__declspec(dllexport) uint64_t WINAPI SteamAPI_GetHSteamPipe(void)
{
    HMODULE h = realModule();
    if (!h) return 0;
    typedef uint64_t (*FN)();
    FN fn = (FN)GetProcAddress(h, "SteamAPI_GetHSteamPipe");
    return fn ? fn() : 0;
}

// ---------------------------------------------------------------------------
// Public SteamCloudSave API (the "one brain" entry points for other tools)
// ---------------------------------------------------------------------------
__declspec(dllexport) int WINAPI SteamCloudSave_Init(const char* configPath)
{
    loadConfig();
    if (configPath && configPath[0]) loadConfigFrom(configPath);
    if (g_registryPath[0])
    {
        DWORD attrs = GetFileAttributesA(g_registryPath);
        sctLog("sct: registry %s %s", g_registryPath,
               attrs != INVALID_FILE_ATTRIBUTES ? "present" : "missing");
    }
    return 1;
}

__declspec(dllexport) void WINAPI SteamCloudSave_Shutdown(void)
{
    if (g_real) { FreeLibrary(g_real); g_real = NULL; }
    sctLog("sct: shutdown");
}

__declspec(dllexport) const char* WINAPI SteamCloudSave_State(void)
{
    return ShadowMode() ? "redirecting" : "off";
}

__declspec(dllexport) uint32_t WINAPI SteamCloudSave_App(void)
{
    return g_targetAppId;
}

__declspec(dllexport) const char* WINAPI SteamCloudSave_ShadowRoot(void)
{
    return g_shadowRoot;
}

BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        GetModuleFileNameA((HMODULE)hInst, g_dllPath, sizeof(g_dllPath));
        loadConfig();
    }
    return TRUE;
}

#ifdef __cplusplus
}
#endif
