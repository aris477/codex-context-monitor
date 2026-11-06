#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include <winhttp.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <fstream>
#include <limits>
#include "CompanionState.h"
#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "shell32.lib")

// No CLR, screenshot capture, account data, or chat data in this listener.
class Handle {
    HANDLE value;
public:
    explicit Handle(HANDLE value = nullptr) : value(value) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    operator HANDLE() const { return value; }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
class HttpHandle {
    HINTERNET value;
public:
    explicit HttpHandle(HINTERNET value) : value(value) {}
    ~HttpHandle() { if (value) WinHttpCloseHandle(value); }
    operator HINTERNET() const { return value; }
};

std::wstring imagePath(HANDLE process) {
    wchar_t text[32768]; DWORD size = 32768;
    return QueryFullProcessImageNameW(process, 0, text, &size) ? std::wstring(text, size) : L"";
}
bool officialPath(std::wstring path) {
    for (auto& c : path) c = static_cast<wchar_t>(towlower(c));
    auto pos = path.find_last_of(L"\\/");
    auto name = path.substr(pos == std::wstring::npos ? 0 : pos + 1);
    return path.find(L"\\windowsapps\\openai.codex_") != std::wstring::npos
        && (name == L"chatgpt.exe" || name == L"codex.exe");
}

// Strict, bounded parser for the flat, ASCII monitor identity object.
// Unknown primitive fields are allowed; duplicate keys and malformed values fail.
struct Identity { DWORD pid = 0; std::string instance, app; };
bool parseIdentity(const std::string& json, Identity& identity) {
    size_t i = 0; std::vector<std::string> keys;
    auto ws = [&]() { while (i < json.size() && (json[i]==' ' || json[i]=='\r' || json[i]=='\n' || json[i]=='\t')) ++i; };
    auto quoted = [&](std::string& value) {
        if (i >= json.size() || json[i++] != '"') return false;
        value.clear();
        while (i < json.size()) {
            unsigned char c = json[i++];
            if (c == '"') return true;
            if (c < 32 || c >= 127 || c == '\\') return false;
            value += static_cast<char>(c);
        }
        return false;
    };
    ws(); if (i >= json.size() || json[i++] != '{') return false;
    for (;;) {
        ws(); std::string key, value; bool stringValue;
        if (!quoted(key)) return false;
        for (const auto& old : keys) if (key == old) return false;
        keys.push_back(key); ws(); if (i >= json.size() || json[i++] != ':') return false; ws();
        stringValue = i < json.size() && json[i] == '"';
        if (stringValue) { if (!quoted(value)) return false; }
        else {
            auto start = i;
            while (i < json.size() && json[i] >= '0' && json[i] <= '9') ++i;
            value = json.substr(start, i - start);
            if (value.empty() || (value.size() > 1 && value[0] == '0')) return false;
        }
        if (key == "pid") {
            if (stringValue || value.size() > 10) return false;
            unsigned long long pid = 0;
            for (char c : value) pid = pid * 10 + c - '0';
            if (pid == 0 || pid > MAXDWORD) return false;
            identity.pid = static_cast<DWORD>(pid);
        } else if (key == "app") { if (!stringValue) return false; identity.app = value; }
        else if (key == "instance") {
            if (!stringValue || value.size() != 36) return false;
            for (size_t p = 0; p < value.size(); ++p) {
                if (p == 8 || p == 13 || p == 18 || p == 23) { if (value[p] != '-') return false; }
                else if (!((value[p]>='0' && value[p]<='9') || (value[p]>='a' && value[p]<='f') || (value[p]>='A' && value[p]<='F'))) return false;
            }
            identity.instance = value;
        }
        ws(); if (i >= json.size()) return false;
        char end = json[i++];
        if (end == '}') break;
        if (end != ',') return false;
    }
    ws();
    return i == json.size() && identity.app == "codex-context-monitor" && identity.pid != 0 && !identity.instance.empty();
}
std::string identityKey(const Identity& identity) { return std::to_string(identity.pid) + ":" + identity.instance; }
std::string readSmallFile(const std::wstring& path) {
    Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr));
    if (file == INVALID_HANDLE_VALUE) return {};
    LARGE_INTEGER size; if (!GetFileSizeEx(file, &size) || size.QuadPart < 1 || size.QuadPart > 8192) return {};
    std::string text(static_cast<size_t>(size.QuadPart), '\0'); DWORD got = 0;
    if (!ReadFile(file, &text[0], static_cast<DWORD>(text.size()), &got, nullptr) || got != text.size()) return {};
    return text;
}
bool exists(const std::wstring& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; }

class WindowsHost : public CompanionHost {
    std::wstring root, node, data;
    INTERNET_PORT port;
public:
    WindowsHost(std::wstring root, std::wstring node, INTERNET_PORT port) : root(root), node(node), data(root+L"\\.codex-usage"), port(port) { CreateDirectoryW(data.c_str(), nullptr); }
    bool disabled() const { return exists(data + L"\\companion-disabled"); }
    void log(const std::string& message) {
        auto name = data + L"\\companion.log";
        WIN32_FILE_ATTRIBUTE_DATA info;
        bool truncate = GetFileAttributesExW(name.c_str(), GetFileExInfoStandard, &info) && (info.nFileSizeHigh || info.nFileSizeLow > 65536);
        Handle file(CreateFileW(name.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, truncate ? CREATE_ALWAYS : OPEN_ALWAYS, 0, nullptr));
        if (file == INVALID_HANDLE_VALUE) return;
        SYSTEMTIME t; GetLocalTime(&t); char time[40];
        sprintf_s(time, "%04d-%02d-%02dT%02d:%02d:%02d ", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
        auto line = std::string(time) + message + "\r\n"; DWORD written;
        WriteFile(file, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    }
    bool codexRunning() override {
        Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
        if (snapshot == INVALID_HANDLE_VALUE) return false;
        PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
        if (!Process32FirstW(snapshot, &entry)) return false;
        do {
            if (_wcsicmp(entry.szExeFile, L"ChatGPT.exe") && _wcsicmp(entry.szExeFile, L"Codex.exe")) continue;
            Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID));
            if (process && officialPath(imagePath(process))) return true;
        } while (Process32NextW(snapshot, &entry));
        return false;
    }
    bool paused() override { return exists(data + L"\\companion-paused"); }
    void clearPause() override { DeleteFileW((data + L"\\companion-paused").c_str()); }
    std::string health() override {
        Identity disk;
        if (!parseIdentity(readSmallFile(data + L"\\server.json"), disk)) return {};
        HttpHandle session(WinHttpOpen(L"CodexContextCompanion/1", WINHTTP_ACCESS_TYPE_NO_PROXY, nullptr, nullptr, 0));
        if (!session) return {};
        WinHttpSetTimeouts(session, 1500, 1500, 1500, 1500);
        HttpHandle connection(WinHttpConnect(session, L"127.0.0.1", port, 0));
        if (!connection) return {};
        HttpHandle request(WinHttpOpenRequest(connection, L"GET", L"/api/monitor-health", nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, 0));
        if (!request || !WinHttpSendRequest(request, nullptr, 0, nullptr, 0, 0, 0) || !WinHttpReceiveResponse(request, nullptr)) return {};
        DWORD code = 0, size = sizeof(code);
        if (!WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, nullptr, &code, &size, nullptr) || code != 200) return {};
        std::string body; char buffer[1024]; DWORD got;
        for (;;) {
            if (!WinHttpReadData(request, buffer, sizeof(buffer), &got)) return {};
            if (!got) break;
            body.append(buffer, got); if (body.size() > 8192) return {};
        }
        Identity live;
        if (!parseIdentity(body, live) || identityKey(live) != identityKey(disk)) return {};
        return identityKey(live);
    }
    void start() override {
        auto command = L"\"" + node + L"\" \"" + root + L"\\start-local.js\"";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup); startup.dwFlags = STARTF_USESHOWWINDOW; startup.wShowWindow = SW_HIDE;
        PROCESS_INFORMATION process{};
        if (CreateProcessW(node.c_str(), &command[0], nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, root.c_str(), &startup, &process)) {
            CloseHandle(process.hThread); CloseHandle(process.hProcess); log("Requested dashboard start (native companion)");
        } else log("Start failed, Windows error " + std::to_string(GetLastError()));
    }
    void stop(const std::string& identity) override {
        auto pid = static_cast<DWORD>(std::stoul(identity.substr(0, identity.find(':'))));
        // Keep the process handle across health verification to prevent PID reuse.
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE, FALSE, pid));
        if (!process || _wcsicmp(imagePath(process).c_str(), node.c_str()) || health() != identity) return;
        if (TerminateProcess(process, 0)) log("Stopped dashboard after Codex exited");
    }
};

#ifndef COMPANION_TEST
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int count = 0; auto argv = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!argv || count != 3) { if (argv) LocalFree(argv); return 1; }
    std::wstring root = argv[1], node = argv[2]; LocalFree(argv);
    if (!exists(root+L"\\local-server.js") || !exists(root+L"\\start-local.js") || !exists(node)) return 1;
    // Same root always maps to one mutex, regardless of executable hash/version.
    auto mutexName = root;
    for (auto& c : mutexName) { c = static_cast<wchar_t>(towlower(c)); if (c == '\\' || c == '/') c = '|'; }
    mutexName = L"Local\\CodexContextNativeCompanion-" + mutexName;
    Handle mutex(CreateMutexW(nullptr, TRUE, mutexName.c_str()));
    if (!mutex || GetLastError() == ERROR_ALREADY_EXISTS) return 0;
    wchar_t portText[16]; unsigned long port = 3765;
    if (GetEnvironmentVariableW(L"CODEX_CONTEXT_PORT", portText, 16)) {
        wchar_t* end; port = wcstoul(portText, &end, 10);
        if (*end || port < 1 || port > 65535) return 1;
    }
    WindowsHost host(root, node, static_cast<INTERNET_PORT>(port)); CompanionState state(host);
    host.log("Native companion started; checking Codex every 15 seconds");
    while (!host.disabled()) {
        try { state.tick(GetTickCount64()); } catch (...) { host.log("Tick failed; retrying"); }
        Sleep(15000);
    }
    ReleaseMutex(mutex); return 0;
}
#endif
