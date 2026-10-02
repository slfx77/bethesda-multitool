// Script::Execute entry/return wrapper for the single verified retail executable.
// Pinned GameScript.h supplies the thiscall signature. Session003's live capture supplied
// the complete relocated prologue. The xNVSE ScriptRunner hooks are not changed.
using ScriptExecuteFunction=bool (__thiscall*)(Script*,TESObjectREFR*,ScriptEventList*,TESObjectREFR*,bool);
ScriptExecuteFunction g_scriptExecuteOriginal=nullptr;
std::atomic<bool> g_scriptTraceInstalled{false};
bool g_scriptTraceAttempted=false;
std::atomic<std::uint64_t> g_scriptCallCounter{0};
thread_local std::uint64_t g_scriptParentCall=0;
thread_local std::uint64_t g_scriptParentGeneration=0;
thread_local UInt32 g_scriptDepth=0;
constexpr std::uintptr_t ScriptExecuteAddress=0x005AC1E0;
alignas(8) LONG64 g_scriptOriginalPrefix=0,g_scriptReplacementPrefix=0;
std::atomic<UInt32> g_scriptTraceMode{1}; // 0 off, 1 requested temporary scripts, 2 selected + requested, 3 all.
std::mutex g_scriptSelectionMutex;
std::unordered_set<UInt32> g_scriptSelections;

bool TraceSelectedScript(Script* script) {
    const auto mode=g_scriptTraceMode.load();
    if (!mode) return false;
    if (mode==3) return true;
    UInt32 id=0,flags=0;
    if (!ReadFormFields(script,id,flags)) return false;
    if (g_currentRequest && (!id || (flags&0x00004000)!=0)) return true;
    if (mode!=2) return false;
    std::lock_guard<std::mutex> lock(g_scriptSelectionMutex);
    return g_scriptSelections.find(id)!=g_scriptSelections.end();
}
bool ConfigureScriptTraceStart(const std::string& payload, std::string& pluginPayload, std::string& scope, std::string& error) {
    std::istringstream input(payload); std::string line,mode="requested";
    if (!std::getline(input,pluginPayload) || pluginPayload.empty()) { error="invalid-capture-session"; return false; }
    struct Selection { std::string kind,plugin,local; UInt32 localId; };
    std::vector<Selection> selections; bool modeSeen=false;
    while(std::getline(input,line)) {
        if (line.rfind("@bmt-script-scope=",0)==0) {
            if (modeSeen) { error="duplicate-script-scope"; return false; }
            modeSeen=true; mode=line.substr(18);
        } else if (line.rfind("@bmt-script=",0)==0 || line.rfind("@bmt-quest=",0)==0) {
            const bool quest=line.rfind("@bmt-quest=",0)==0;
            std::vector<std::string> parts; UInt32 localId=0; double unused=0;
            if (selections.size()>=64 || !ParseQuestPayload({10,0,line.substr(quest?11:12)+"\tTrace"},parts,localId,unused)) {
                error="invalid-script-trace-target"; return false;
            }
            selections.push_back({quest?"quest":"script",parts[0],parts[1],localId});
        } else if (line.rfind("@bmt-",0)==0) { error="unknown-script-trace-option"; return false; }
        else pluginPayload+="\n"+line;
    }
    const UInt32 modeValue=mode=="off"?0:mode=="requested"?1:mode=="selected"?2:mode=="all"?3:4;
    if (modeValue==4 || (modeValue==2)==selections.empty()) { error="invalid-script-trace-scope"; return false; }
    std::unordered_set<UInt32> resolved;
    std::string entries; bool first=true;
    if (!selections.empty() && (!g_loadedGameObserved || !VerifyRuntimeFormMap())) { error="script-target-runtime-profile-unavailable"; return false; }
    for (const auto& target:selections) {
        UInt32 slot=255;
        if (!PluginNumber("GetModIndex \""+target.plugin+"\"",slot) || slot>=255) { error="script-target-plugin-not-loaded"; return false; }
        const auto requestedId=(slot<<24)|target.localId;
        auto form=LookupRuntimeForm(requestedId); UInt32 id=0; std::uint8_t type=0;
        if (!ReadFormIdentity(form,id,type) || id!=requestedId) { error="script-target-form-unavailable"; return false; }
        UInt32 scriptId=id;
        if (target.kind=="quest") {
            UInt32 script=0;
            if (type!=0x47 || !ReadRuntime(reinterpret_cast<std::uintptr_t>(form)+0x1C,script) ||
                !ReadFormIdentity(reinterpret_cast<void*>(script),scriptId,type) || type!=0x11) { error="quest-script-target-unavailable"; return false; }
        } else if(type!=0x11) { error="script-target-type-mismatch"; return false; }
        resolved.insert(scriptId);
        if (!first) entries+=','; first=false;
        entries+="{\"requestedKind\":"+Quote(target.kind)+",\"plugin\":"+Quote(target.plugin)+",\"localFormId\":"+
            std::to_string(target.localId)+",\"engineTargetFormId\":"+std::to_string(requestedId)+",\"scriptFormId\":"+std::to_string(scriptId)+"}";
    }
    { std::lock_guard<std::mutex> lock(g_scriptSelectionMutex); g_scriptSelections=std::move(resolved); }
    g_scriptTraceMode=modeValue;
    scope=",\"scriptTraceScope\":{\"mode\":"+Quote(mode)+",\"requestedTemporaryScripts\":"+
        std::string(modeValue==1 || modeValue==2?"true":"false")+",\"selection\":["+entries+
        "],\"filterApplied\":"+std::string(modeValue==3?"false":"true")+",\"entryIsBlockExecution\":false}";
    return true;
}

std::string ScriptIdentity(Script* script, TESObjectREFR* reference, ScriptEventList* events, TESObjectREFR* container) {
    UInt32 owner=0; bool ownerKnown=false;
    auto fields=CallContext(reference,script,reinterpret_cast<void*>(ScriptExecuteAddress),owner,ownerKnown,false);
    fields+=",\"eventListAddress\":"+std::to_string(reinterpret_cast<UInt32>(events));
    UInt32 id=0; std::uint8_t type=0;
    if (ReadFormIdentity(container,id,type)) fields+=",\"containerFormId\":"+std::to_string(id);
    UInt32 length=0,quest=0;
    if (ReadRuntime(reinterpret_cast<std::uintptr_t>(script)+0x20,length)) fields+=",\"bytecodeLength\":"+std::to_string(length);
    if (ReadRuntime(reinterpret_cast<std::uintptr_t>(script)+0x40,quest) &&
        ReadFormIdentity(reinterpret_cast<void*>(quest),id,type) && type==0x47) fields+=",\"questFormId\":"+std::to_string(id);
    return fields;
}
bool __fastcall TraceScriptExecute(Script* script, void*, TESObjectREFR* reference, ScriptEventList* events,
    TESObjectREFR* container, bool force) {
    // Start, Stop/Cancel and disconnect each advance this process-monotonic generation.
    // A call admitted in an earlier capture must not publish into a later connection/capture.
    const auto generation=g_captureGeneration.load();
    if (!generation || !g_capture || !g_connected || !TraceSelectedScript(script))
        return g_scriptExecuteOriginal(script,reference,events,container,force);
    const auto requestId=g_currentRequest;
    const auto callId=++g_scriptCallCounter;
    struct RestoreScriptContext {
        std::uint64_t parent,generation;
        UInt32 depth;
        ~RestoreScriptContext() {
            g_scriptParentCall=parent; g_scriptParentGeneration=generation; g_scriptDepth=depth;
        }
    } restore{g_scriptParentCall,g_scriptParentGeneration,g_scriptDepth};
    const auto parent=restore.generation==generation?restore.parent:0;
    const auto depth=restore.generation==generation?restore.depth:0;
    g_scriptParentCall=callId;
    g_scriptParentGeneration=generation;
    g_scriptDepth=depth+1;
    const auto fields=",\"callId\":"+std::to_string(callId)+",\"parentCallId\":"+std::to_string(parent)+
        ",\"depth\":"+std::to_string(depth)+",\"threadId\":"+std::to_string(GetCurrentThreadId())+
        ",\"forced\":"+std::string(force?"true":"false")+",\"evidence\":\"verified-script-execute-wrapper\""+
        ScriptIdentity(script,reference,events,container);
    Emit("script-entry",requestId,fields,generation);
    const bool returned=g_scriptExecuteOriginal(script,reference,events,container,force);
    const auto lastError=GetLastError();
    Emit("script-exit",requestId,fields+",\"returned\":"+std::string(returned?"true":"false"),generation);
    SetLastError(lastError);
    return returned;
}
bool ReplaceScriptPrefix(LONG64 expected, LONG64 replacement) {
    // Prepare every handle before suspension. Never allocate or acquire a C++ mutex with
    // another thread stopped. Decline the patch if a thread is inside the stolen prologue.
    HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
    if (snapshot==INVALID_HANDLE_VALUE) return false;
    HANDLE handles[256]{}; size_t count=0,suspended=0; bool valid=true;
    THREADENTRY32 entry{}; entry.dwSize=sizeof(entry);
    if (Thread32First(snapshot,&entry)) do {
        if (entry.th32OwnerProcessID!=GetCurrentProcessId() || entry.th32ThreadID==GetCurrentThreadId()) continue;
        if (count==std::size(handles)) { valid=false; break; }
        auto handle=OpenThread(THREAD_SUSPEND_RESUME|THREAD_GET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,entry.th32ThreadID);
        if (!handle) { valid=false; break; }
        handles[count++]=handle;
    } while(Thread32Next(snapshot,&entry));
    else valid=false;
    CloseHandle(snapshot);
    DWORD protection=0;
    auto target=reinterpret_cast<volatile LONG64*>(ScriptExecuteAddress);
    if (valid) valid=VirtualProtect(reinterpret_cast<void*>(ScriptExecuteAddress),8,PAGE_EXECUTE_READWRITE,&protection)!=FALSE;
    if (valid) {
        for (;suspended<count;++suspended) {
            if (SuspendThread(handles[suspended])==DWORD(-1)) { valid=false; break; }
            CONTEXT context{}; context.ContextFlags=CONTEXT_CONTROL;
            if (!GetThreadContext(handles[suspended],&context) ||
                (context.Eip>=ScriptExecuteAddress && context.Eip<ScriptExecuteAddress+6)) {
                ++suspended; valid=false; break;
            }
        }
        if (valid) valid=InterlockedCompareExchange64(target,replacement,expected)==expected;
        DWORD ignored=0; VirtualProtect(reinterpret_cast<void*>(ScriptExecuteAddress),8,protection,&ignored);
        FlushInstructionCache(GetCurrentProcess(),reinterpret_cast<void*>(ScriptExecuteAddress),8);
        for (size_t index=0;index<suspended;++index) ResumeThread(handles[index]);
    }
    for (size_t index=0;index<count;++index) CloseHandle(handles[index]);
    return valid;
}
void InstallScriptTrace() {
    if (g_scriptTraceAttempted || !g_pcLayoutVerified) return;
    g_scriptTraceAttempted=true;
    // First six bytes are three whole instructions, with no PC-relative operands.
    constexpr std::uint8_t expected[16]={0x55,0x8B,0xEC,0x83,0xEC,0x08,0x89,0x4D,0xF8,0x0F,0xB6,0x45,0x14,0x85,0xC0,0x75};
    std::uint8_t actual[16]{};
    if (!ReadRuntime(ScriptExecuteAddress,actual) || memcmp(actual,expected,sizeof(actual))) return;
    auto trampoline=static_cast<std::uint8_t*>(VirtualAlloc(nullptr,11,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if (!trampoline) return;
    memcpy(trampoline,actual,6); trampoline[6]=0xE9;
    const auto back=UInt32(ScriptExecuteAddress+6)-UInt32(reinterpret_cast<std::uintptr_t>(trampoline)+11);
    memcpy(trampoline+7,&back,4);
    DWORD ignored=0;
    if (!VirtualProtect(trampoline,11,PAGE_EXECUTE_READ,&ignored)) { VirtualFree(trampoline,0,MEM_RELEASE); return; }
    FlushInstructionCache(GetCurrentProcess(),trampoline,11);
    std::uint8_t replacement[8]{}; memcpy(replacement,actual,8); replacement[0]=0xE9; replacement[5]=0x90;
    const auto jump=UInt32(reinterpret_cast<std::uintptr_t>(TraceScriptExecute))-UInt32(ScriptExecuteAddress+5);
    memcpy(replacement+1,&jump,4);
    memcpy(&g_scriptOriginalPrefix,actual,8); memcpy(&g_scriptReplacementPrefix,replacement,8);
    g_scriptExecuteOriginal=reinterpret_cast<ScriptExecuteFunction>(trampoline);
    if (!ReplaceScriptPrefix(g_scriptOriginalPrefix,g_scriptReplacementPrefix)) {
        g_scriptExecuteOriginal=nullptr; VirtualFree(trampoline,0,MEM_RELEASE); return;
    }
    g_scriptTraceInstalled=true;
}
void RestoreScriptTrace() {
    if (g_scriptTraceInstalled && ReplaceScriptPrefix(g_scriptReplacementPrefix,g_scriptOriginalPrefix)) g_scriptTraceInstalled=false;
    // Keep the trampoline until process exit: an already-entered original call may still return through it.
}
