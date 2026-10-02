#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>

// Scratch-only check of the pinned usvfs release. This program has no game-launch mode.
namespace fs = std::filesystem;
namespace {
void Require(bool value, const char* reason) { if (!value) throw std::runtime_error(reason); }
std::string Read(const fs::path& path) {
    std::ifstream stream(path, std::ios::binary); Require(bool(stream), "read failed");
    return {std::istreambuf_iterator<char>(stream), {}};
}
void Write(const fs::path& path, const std::string& value) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    Require(file != INVALID_HANDLE_VALUE, "CreateFile failed");
    DWORD written = 0;
    const bool ok = WriteFile(file, value.data(), static_cast<DWORD>(value.size()), &written, nullptr) && written == value.size();
    CloseHandle(file); Require(ok, "WriteFile failed");
}
std::string Sha256(const fs::path& path) {
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0, "SHA256 provider unavailable");
    Require(BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0, "SHA256 init failed");
    std::ifstream stream(path, std::ios::binary); Require(bool(stream), "dependency missing");
    char block[65536];
    while (stream.read(block, sizeof(block)) || stream.gcount())
        Require(BCryptHashData(hash, reinterpret_cast<PUCHAR>(block), static_cast<ULONG>(stream.gcount()), 0) >= 0, "SHA256 update failed");
    unsigned char bytes[32]{}; Require(BCryptFinishHash(hash, bytes, sizeof(bytes), 0) >= 0, "SHA256 finish failed");
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
    const char digits[] = "0123456789abcdef"; std::string result;
    for (auto b : bytes) { result += digits[b >> 4]; result += digits[b & 15]; }
    return result;
}
std::map<std::wstring, std::string> Inventory(const fs::path& root) {
    std::map<std::wstring, std::string> result;
    for (const auto& item : fs::recursive_directory_iterator(root))
        if (item.is_regular_file()) result.emplace(fs::relative(item.path(), root).wstring(), Read(item.path()));
    return result;
}
std::wstring Self() { wchar_t buffer[32768]{}; Require(GetModuleFileNameW(nullptr, buffer, 32768) != 0, "self path failed"); return buffer; }
std::wstring Quote(const std::wstring& value) {
    Require(value.find(L'"') == std::wstring::npos && (value.empty() || value.back() != L'\\'), "invalid command path");
    return L"\"" + value + L"\"";
}
void WaitForProbe(PROCESS_INFORMATION& process) {
    const auto wait = WaitForSingleObject(process.hProcess, 30000);
    if (wait != WAIT_OBJECT_0) TerminateProcess(process.hProcess, 124); // Only our scratch probe process.
    DWORD status = 1; GetExitCodeProcess(process.hProcess, &status);
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
    Require(wait == WAIT_OBJECT_0 && status == 0, "hooked probe failed or exceeded 30 seconds");
}
void Probe(const fs::path& logical, bool grandchild) {
    Require(Read(logical / L"seed.txt") == "profile-copy", "virtual read did not resolve to the copy");
    if (grandchild) { Write(logical / L"child.txt", "inherited-hook"); return; }
    Write(logical / L"seed.txt", "modified-copy");
    // Restore before the inherited child verifies that it sees the same virtual namespace.
    Write(logical / L"seed.txt", "profile-copy");
    Write(logical / L"created.txt", "created-copy");
    Require(MoveFileW((logical / L"created.txt").c_str(), (logical / L"renamed.txt").c_str()) != 0, "virtual rename failed");
    Write(logical / L"deleted.txt", "temporary");
    Require(DeleteFileW((logical / L"deleted.txt").c_str()) != 0, "virtual delete failed");
    Require(CreateDirectoryW((logical / L"Saves").c_str(), nullptr) != 0 || GetLastError() == ERROR_ALREADY_EXISTS, "virtual directory creation failed");
    Write(logical / L"Saves" / L"probe.fos", "synthetic-save");
    const auto ini = logical / L"probe.ini";
    Require(WritePrivateProfileStringW(L"General", L"test", L"copy-write", ini.c_str()) != 0, "INI write failed");
    WritePrivateProfileStringW(nullptr, nullptr, nullptr, ini.c_str());
    wchar_t value[80]{};
    GetPrivateProfileStringW(L"General", L"test", L"", value, 80, ini.c_str());
    Require(std::wstring(value) == L"copy-write", "INI read did not observe copied write");
    auto command = Quote(Self()) + L" --grandchild " + Quote(logical.wstring());
    STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION process{};
    Require(CreateProcessW(Self().c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process) != 0,
        "inherited child creation failed");
    WaitForProbe(process);
}
void SaveProbe(const fs::path& logical) {
    wchar_t relative[160]{};
    GetPrivateProfileStringW(L"General", L"SLocalSavePath", L"", relative, 160, (logical / L"probe.ini").c_str());
    Require(std::wstring(relative) == L"BMT_Live_fixture\\", "private save route missing from INI");
    const auto saves = logical / relative;
    Require(Read(saves / L"quicksave.fos") == "private-existing-fos", "existing private quicksave not selected");
    Require(Read(saves / L"quicksave.nvse") == "private-existing-nvse", "existing private co-save not selected");
    Require(GetFileAttributesW((saves / L"autosave.fos").c_str()) == INVALID_FILE_ATTRIBUTES,
        "uncopied original autosave leaked into private save route");
    for (const auto* prefix : {L"quicksave", L"autosave"}) {
        const auto save = saves / (std::wstring(prefix) + L".fos");
        const auto coSave = saves / (std::wstring(prefix) + L".nvse");
        const auto backup = saves / (std::wstring(prefix) + L".fos.bak");
        const auto saveTemporary = saves / (std::wstring(prefix) + L".fos.tmp");
        const auto coSaveTemporary = saves / (std::wstring(prefix) + L".nvse.tmp");
        Write(save, "private-overwritten-fos");
        Write(coSave, "private-overwritten-nvse");
        Write(backup, "private-overwritten-backup");
        Require(CopyFileW(save.c_str(), backup.c_str(), FALSE) != 0, "save backup replacement failed");
        Write(saveTemporary, "private-rolled-fos");
        Write(coSaveTemporary, "private-rolled-nvse");
        Require(MoveFileExW(saveTemporary.c_str(), save.c_str(), MOVEFILE_REPLACE_EXISTING) != 0,
            "save rename replacement failed");
        Require(MoveFileExW(coSaveTemporary.c_str(), coSave.c_str(), MOVEFILE_REPLACE_EXISTING) != 0,
            "co-save rename replacement failed");
    }
    Require(Read(saves / L"baseline.fos") == "private-baseline-fos" &&
        Read(saves / L"baseline.nvse") == "private-baseline-nvse", "named baseline changed during rollover");
}
template<class T> T Function(HMODULE module, const char* name) {
    auto value = GetProcAddress(module, name); Require(value != nullptr, "pinned usvfs export missing"); return reinterpret_cast<T>(value);
}
void Verify(const fs::path& binaryRoot, const fs::path& workspace) {
    Require(binaryRoot.is_absolute() && workspace.is_absolute(), "absolute dependency and scratch paths required");
    Require(!fs::exists(workspace), "scratch destination must be new");
    const std::pair<const wchar_t*, const char*> binaries[] = {
        {L"usvfs_x86.dll", "de23207b87aa99a1c15ec29185e3cf1e50dce4b1473eeb894c02abdaa23313fc"},
        {L"usvfs_x64.dll", "7ee7758433ab76713900e661056be8074b9c567971fde38fd0e514c76895e274"},
        {L"usvfs_proxy_x86.exe", "e37a485fcebbde9583005913a34bfdd2bd49443438c30b6dc75bee83bb261b04"},
        {L"usvfs_proxy_x64.exe", "491d4d7e3fce9876e904f8cccb648f43def428a2a527d41f0221d9c0ce1d408e"}
    };
    for (const auto& item : binaries) Require(Sha256(binaryRoot / item.first) == item.second, "usvfs dependency hash mismatch");
    fs::create_directories(workspace / L"original"); fs::create_directories(workspace / L"profile");
    const auto logical = workspace / L"original", physical = workspace / L"profile";
    const auto saveRoute = logical / L"BMT_Live_fixture";
    Require(!fs::exists(saveRoute), "unique logical save directory already exists physically");
    fs::create_directories(logical / L"Saves"); fs::create_directories(physical / L"Saves");
    for (const auto* prefix : {L"quicksave", L"autosave"})
        for (const auto* extension : {L".fos", L".nvse", L".fos.bak"})
            Write(logical / L"Saves" / (std::wstring(prefix) + extension), "untouched-original-save");
    Write(physical / L"Saves" / L"quicksave.fos", "private-existing-fos");
    Write(physical / L"Saves" / L"quicksave.nvse", "private-existing-nvse");
    Write(physical / L"Saves" / L"quicksave.fos.bak", "private-existing-backup");
    Write(physical / L"Saves" / L"baseline.fos", "private-baseline-fos");
    Write(physical / L"Saves" / L"baseline.nvse", "private-baseline-nvse");
    Write(logical / L"seed.txt", "untouched-original");
    Write(logical / L"probe.ini", "[General]\r\ntest=untouched-original\r\n");
    Write(physical / L"seed.txt", "profile-copy");
    Write(physical / L"probe.ini", "[General]\r\ntest=profile-copy\r\nSLocalSavePath=BMT_Live_fixture\\\r\n");
    const auto before = Inventory(logical);
    auto library = LoadLibraryExW((binaryRoot / L"usvfs_x86.dll").c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    Require(library != nullptr, "usvfs DLL load failed");
    const auto createParameters = Function<void* (__cdecl*)()>(library, "usvfsCreateParameters");
    const auto freeParameters = Function<void (__cdecl*)(void*)>(library, "usvfsFreeParameters");
    const auto setName = Function<void (__cdecl*)(void*, const char*)>(library, "usvfsSetInstanceName");
    const auto create = Function<BOOL (WINAPI*)(const void*)>(library, "_usvfsCreateVFS@4");
    const auto link = Function<BOOL (WINAPI*)(LPCWSTR, LPCWSTR, unsigned)>(library, "_usvfsVirtualLinkDirectoryStatic@12");
    const auto spawn = Function<decltype(&CreateProcessW)>(library, "_usvfsCreateProcessHooked@40");
    const auto disconnect = Function<void (WINAPI*)()>(library, "_usvfsDisconnectVFS@0");
    const auto dump = Function<BOOL (WINAPI*)(LPSTR, size_t*)>(library, "_usvfsCreateVFSDump@8");
    void* parameters = createParameters(); Require(parameters != nullptr, "usvfs parameters failed");
    const auto name = "bmt-preflight-" + std::to_string(GetCurrentProcessId()); setName(parameters, name.c_str());
    const bool created = create(parameters) != 0; freeParameters(parameters); Require(created, "usvfs create failed");
    try {
        Require(link(physical.c_str(), logical.c_str(), 4u | 8u) != 0, "usvfs directory mapping failed");
        Require(link((physical / L"Saves").c_str(), saveRoute.c_str(), 4u | 8u) != 0,
            "usvfs private save mapping failed");
        size_t length = 0; dump(nullptr, &length); Require(length < 4 * 1024 * 1024, "unexpected mapping dump size");
        std::vector<char> mapping(length + 1, 0); Require(dump(mapping.data(), &length) != 0, "usvfs mapping dump failed");
        Write(workspace / L"mapping.txt", std::string(mapping.data(), length));
        auto command = Quote(Self()) + L" --probe " + Quote(logical.wstring());
        STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION process{};
        Require(spawn(Self().c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, workspace.c_str(), &startup, &process) != 0,
            "hooked process creation failed");
        WaitForProbe(process);
        command = Quote(Self()) + L" --save-probe " + Quote(logical.wstring());
        process = {};
        Require(spawn(Self().c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, workspace.c_str(), &startup, &process) != 0,
            "hooked save probe creation failed");
        WaitForProbe(process);
        Require(Inventory(logical) == before, "physical originals changed");
        Require(!fs::exists(saveRoute), "unique logical save directory was created physically");
        for (const auto* prefix : {L"quicksave", L"autosave"}) {
            Require(Read(physical / L"Saves" / (std::wstring(prefix) + L".fos")) == "private-rolled-fos" &&
                Read(physical / L"Saves" / (std::wstring(prefix) + L".nvse")) == "private-rolled-nvse" &&
                Read(physical / L"Saves" / (std::wstring(prefix) + L".fos.bak")) == "private-overwritten-fos",
                "private save overwrite or rollover result mismatch");
            Require(!fs::exists(physical / L"Saves" / (std::wstring(prefix) + L".fos.tmp")) &&
                !fs::exists(physical / L"Saves" / (std::wstring(prefix) + L".nvse.tmp")),
                "private save rollover retained temporary files");
        }
        Require(Read(physical / L"Saves" / L"baseline.fos") == "private-baseline-fos" &&
            Read(physical / L"Saves" / L"baseline.nvse") == "private-baseline-nvse", "physical baseline changed");
        Require(Read(physical / L"renamed.txt") == "created-copy" && !fs::exists(physical / L"created.txt") && !fs::exists(physical / L"deleted.txt"), "create/rename/delete result mismatch");
        Require(Read(physical / L"Saves" / L"probe.fos") == "synthetic-save", "save creation not redirected");
        Require(Read(physical / L"child.txt") == "inherited-hook", "child did not inherit virtual writes");
        Require(Read(physical / L"probe.ini").find("copy-write") != std::string::npos, "INI write not redirected");
    } catch (...) { disconnect(); throw; }
    disconnect();
    std::cout << "{\"schema\":\"bmt/usvfs-preflight\",\"version\":1,\"status\":\"passed\",\"usvfs\":\"0.5.7.2\",\"originalsUnchanged\":true,\"fileWrites\":true,\"iniWrites\":true,\"childInheritance\":true,\"uniqueSavePath\":true,\"saveCollisionIsolation\":true,\"saveRollover\":true,\"gameLaunched\":false}\n";
}
}
int wmain(int argc, wchar_t** argv) {
    try {
        if (argc == 3 && std::wstring(argv[1]) == L"--save-probe") { SaveProbe(argv[2]); return 0; }
        if (argc == 3 && (std::wstring(argv[1]) == L"--probe" || std::wstring(argv[1]) == L"--grandchild")) {
            Probe(argv[2], std::wstring(argv[1]) == L"--grandchild"); return 0;
        }
        Require(argc == 4 && std::wstring(argv[1]) == L"--verify", "usage: RuntimeIsolationPreflight --verify <absolute usvfs bin> <new absolute scratch directory>");
        Verify(argv[2], argv[3]); return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << " (win32=" << GetLastError() << ")\n"; return 1; }
}
