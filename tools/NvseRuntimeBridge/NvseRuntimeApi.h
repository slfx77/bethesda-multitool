#pragma once
#include <cstdint>
#include <cstddef>

// Minimal ABI declarations from pinned xNVSE PluginAPI.h; see reference/sources.json.
// Executable-specific layout reads and hooks live in the separately gated runtime profile helpers.
using UInt32 = std::uint32_t;
struct TESObjectREFR;
struct Script;
struct ScriptEventList;
struct ParamInfo { const char* typeStr; UInt32 typeID, isOptional; };
using CommandExecute = bool (*)(ParamInfo*, void*, TESObjectREFR*, TESObjectREFR*, Script*, ScriptEventList*, double*, UInt32*);
using CommandEval = bool (*)(TESObjectREFR*, void*, void*, double*);
struct CommandInfo {
    const char* longName; const char* shortName; UInt32 opcode; const char* helpText;
    std::uint16_t needsParent, numParams; ParamInfo* params;
    CommandExecute execute; void* parse; CommandEval eval; UInt32 flags;
};
static_assert(sizeof(CommandInfo) == 0x28 && offsetof(CommandInfo, execute) == 0x18 && offsetof(CommandInfo, eval) == 0x20);
struct PluginInfo { UInt32 infoVersion; const char* name; UInt32 version; };
struct NVSEInterface {
    UInt32 nvseVersion, runtimeVersion, editorVersion, isEditor;
    bool (*RegisterCommand)(CommandInfo*);
    void (*SetOpcodeBase)(UInt32);
    void* (*QueryInterface)(UInt32);
    UInt32 (*GetPluginHandle)();
    void* RegisterTypedCommand;
    const char* (*GetRuntimeDirectory)();
    UInt32 isNogore;
};
struct NVSEConsoleInterface {
    UInt32 version;
    bool (*RunScriptLine)(const char*, TESObjectREFR*);
    bool (*RunScriptLine2)(const char*, TESObjectREFR*, bool);
};
struct NVSEMessagingInterface {
    struct Message { const char* sender; UInt32 type, dataLen; void* data; };
    using Callback = void (*)(Message*);
    UInt32 version;
    bool (*RegisterListener)(UInt32, const char*, Callback);
    bool (*Dispatch)(UInt32, UInt32, void*, UInt32, const char*);
};
// The SDK Element has a double/pointer union followed by a byte type, no virtual table.
// Only numeric expressions are supported. No string/array ownership is taken.
struct alignas(8) NumericElement { union { double number = 0; void* form; }; std::uint8_t type = 0; std::uint8_t padding[7]{}; };
static_assert(sizeof(NumericElement) == 16 && offsetof(NumericElement, type) == 8);
struct NVSEScriptInterface {
    bool (*CallFunction)(Script*, TESObjectREFR*, TESObjectREFR*, NumericElement*, std::uint8_t, ...);
    void* GetFunctionParams;
    void* ExtractArgsEx;
    void* ExtractFormatStringArgs;
    void* CallFunctionAlt;
    Script* (*CompileScript)(const char*);
    Script* (*CompileExpression)(const char*);
};

// Pinned PluginAPI.h NVSEDataInterface v2 prefix. Clear on the game thread before
// destroying an owned temporary script; UDF metadata contains its event list.
struct NVSEDataInterface {
    UInt32 version;
    void* (*GetSingleton)(UInt32);
    void* (*GetFunc)(UInt32);
    void* (*GetData)(UInt32);
    void (*ClearScriptDataCache)();
};
static_assert(offsetof(NVSEDataInterface, ClearScriptDataCache)==0x10);

// Pinned PluginAPI.h NVSESerializationInterface v2 prefix. Serialization.cpp
// updates GetSavePath after dispatching kMessage_SaveGame; read it on a later tick.
struct NVSESerializationInterface {
    UInt32 version;
    void* SetSaveCallback;
    void* SetLoadCallback;
    void* SetNewGameCallback;
    void* WriteRecord;
    void* OpenRecord;
    void* WriteRecordData;
    void* GetNextRecordInfo;
    void* ReadRecordData;
    void* ResolveRefID;
    void* SetPreLoadCallback;
    const char* (*GetSavePath)();
};
static_assert(offsetof(NVSESerializationInterface, GetSavePath)==0x2c);

struct NVSECommandTableInterface {
    UInt32 version;
    const CommandInfo* (*Start)(); const CommandInfo* (*End)();
    const CommandInfo* (*GetByOpcode)(UInt32);
    const CommandInfo* (*GetByName)(const char*);
    UInt32 (*GetReturnType)(const CommandInfo*);
};
static_assert(offsetof(NVSECommandTableInterface, Start)==4 && offsetof(NVSECommandTableInterface, GetReturnType)==0x14);

struct NVSEEventManagerInterface {
    using NativeEventHandler = void (*)(TESObjectREFR*, void*);
    void* RegisterEvent; void* DispatchEvent; void* DispatchEventAlt;
    bool (*SetNativeEventHandler)(const char*, NativeEventHandler);
    bool (*RemoveNativeEventHandler)(const char*, NativeEventHandler);
};
