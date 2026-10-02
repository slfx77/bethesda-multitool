// Included inside the bridge namespace after Emit/ReadFormIdentity.
// ABI and table ownership are pinned in reference/sources.json. No absolute engine addresses.
NVSECommandTableInterface* g_commands = nullptr;
thread_local std::uint64_t g_currentRequest = 0;
struct RequestScope {
    std::uint64_t previous;
    explicit RequestScope(std::uint64_t current) : previous(g_currentRequest) { g_currentRequest=current; }
    ~RequestScope() { g_currentRequest=previous; }
};
struct CommandHook { const char* name; CommandInfo* entry=nullptr; CommandExecute execute=nullptr; CommandEval eval=nullptr; UInt32 returnType=UINT32_MAX,opcode=0; };
CommandHook g_commandHooks[] = {
    {"ShowMessage"}, {"MessageBoxEx"}, {"GetButtonPressed"},
    {"GetActorValue"}, {"GetBaseActorValue"}, {"GetQuestVariable"}, {"GetGlobalValue"},
    {"GetStage"}, {"GetStageDone"}, {"GetDead"}, {"GetIsID"}
};
std::atomic<UInt32> g_messageHookMask{0}, g_conditionHookCount{0};
bool g_commandHooksAttempted=false;
struct ObservedMessage { std::uint64_t instance=0; UInt32 owner=0; bool ownerKnown=false; std::uint64_t generation=0; };
ObservedMessage g_lastMessage;
std::uint64_t g_messageInstance=0;
std::mutex g_messageMutex;

bool ReadFormFields(void* form, UInt32& id, UInt32& flags) {
    std::uint8_t bytes[16]{}; SIZE_T count=0;
    if (!form || !ReadProcessMemory(GetCurrentProcess(),form,bytes,sizeof(bytes),&count) || count!=sizeof(bytes)) return false;
    memcpy(&id,bytes+12,4); memcpy(&flags,bytes+8,4); return true; // Zero is a valid temporary script ID.
}
std::string CallContext(TESObjectREFR* reference, Script* script, void* caller, UInt32& owner, bool& ownerKnown, bool messageContext=true) {
    std::string fields=",\"callerAddress\":"+std::to_string(reinterpret_cast<UInt32>(caller));
    HMODULE module=nullptr;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(caller),&module)) {
        fields+=",\"callerImageBase\":"+std::to_string(reinterpret_cast<UInt32>(module))+
            ",\"callerRva\":"+std::to_string(reinterpret_cast<UInt32>(caller)-reinterpret_cast<UInt32>(module));
    }
    UInt32 id=0, flags=0;
    if (ReadFormFields(script,id,flags)) {
        fields+=",\"scriptFormId\":"+std::to_string(id)+",\"scriptAddress\":"+std::to_string(reinterpret_cast<UInt32>(script));
        fields+=",\"scriptFlags\":"+std::to_string(flags)+",\"temporaryScript\":"+std::string((flags&0x00004000)!=0?"true":"false");
        owner=id; ownerKnown=true;
    }
    if (ReadFormFields(reference,id,flags)) {
        fields+=",\"engineTargetFormId\":"+std::to_string(id);
        if (!(flags & 0x00004000)) { owner=id; ownerKnown=true; }
    }
    if (ownerKnown && messageContext) fields+=",\"messageOwnerFormId\":"+std::to_string(owner);
    return fields;
}
bool ReadNumber(double* result, double& value) {
    SIZE_T count=0;
    return result && ReadProcessMemory(GetCurrentProcess(),result,&value,sizeof(value),&count) && count==sizeof(value) && std::isfinite(value);
}
std::string NumberField(double value) { std::ostringstream text; text<<std::setprecision(17)<<value; return text.str(); }
#include "NvseRuntimeCommandLocation.inl"
extern thread_local std::uint64_t g_scriptParentCall,g_scriptParentGeneration;
bool TraceSelectedScript(Script* script);
// The x86 SDK returns bool and writes its value through double*. Preserve the complete
// x87/SSE state across observation, including exception flags and rounding/precision.
// Noinline calls make volatile-register clobbering explicit to the C++ compiler.
struct alignas(16) CommandFloatingState {
    unsigned char bytes[512];
    __declspec(noinline) void Save() noexcept {_fxsave(bytes);}
    __declspec(noinline) void Restore() const noexcept {_fxrstor(bytes);}
};
__declspec(noinline) void CommandObserverFloatingEnvironment() noexcept {
    // FNINIT is deliberately non-waiting: the saved game environment may contain
    // an unmasked pending x87 exception. Observation starts with an empty stack.
    __asm fninit
    _mm_setcsr(0x1F80); // All SSE exceptions masked, nearest rounding, clear status.
}
struct CommandFloatingGuard {
    CommandFloatingState state;
    CommandFloatingGuard() noexcept {state.Save();CommandObserverFloatingEnvironment();}
    ~CommandFloatingGuard() {state.Restore();}
    void BeforeOriginal() const noexcept {state.Restore();}
    void AfterOriginal() noexcept {state.Save();CommandObserverFloatingEnvironment();}
};
struct CommandAdmission {
    std::uint64_t generation=0,request=0,call=0;
    UInt32 thread=0;
};
CommandAdmission AdmitCommand() {
    const auto generation=g_captureGeneration.load();
    if(!generation || !g_capture || !g_connected)return {};
    return {generation,g_currentRequest,g_scriptParentGeneration==generation?g_scriptParentCall:0,GetCurrentThreadId()};
}
std::string CommandAdmissionFields(const CommandAdmission& admission) {
    return ",\"threadId\":"+std::to_string(admission.thread)+",\"observedScriptCallId\":"+
        (admission.call?std::to_string(admission.call):"null")+",\"scriptCallStatus\":"+Quote(admission.call?"observed-scope":"Unavailable");
}
std::string CommandResultFields(double* result,UInt32 returnType) {
    std::uint8_t bytes[8]{};
    const bool observed=CommandReadBytes(reinterpret_cast<UInt32>(result),bytes,sizeof(bytes));
    std::uint8_t highFirst[8]{};for(size_t i=0;i<8;++i)highFirst[i]=bytes[7-i];
    std::string fields=",\"returnType\":"+(returnType<6?std::to_string(returnType):"null")+
        ",\"returnTypeStatus\":"+Quote(returnType<6?"observed":"unavailable")+
        ",\"resultStatus\":"+Quote(observed?"observed":"unavailable")+
        ",\"resultBits\":"+(observed?Quote(CommandHex(highFirst,sizeof(highFirst))):"null");
    if(observed && returnType==0) {
        double number=0;memcpy(&number,bytes,sizeof(number));
        if(std::isfinite(number))fields+=",\"numericValue\":"+NumberField(number);
    }
    return fields;
}
void DropCommandObservation(std::uint64_t generation) noexcept {
    try {std::lock_guard<std::mutex> lock(g_mutex);if(generation==g_captureGeneration.load() && g_capture && g_connected)++g_dropped;}
    catch(...) {} // Observer bookkeeping cannot prevent the original handler from running.
}

template<size_t Index> __declspec(noinline) bool ExecuteHook(ParamInfo* parameters, void* data, TESObjectREFR* reference,
    TESObjectREFR* containing, Script* script, ScriptEventList* events, double* result, UInt32* offset) {
    const auto original=g_commandHooks[Index].execute;
    const auto caller=_ReturnAddress();
    const auto incomingError=GetLastError();
    struct RestoreError {DWORD value;~RestoreError(){SetLastError(value);}} restoreError{incomingError};
    CommandFloatingGuard floating;
    const auto admission=AdmitCommand();
    const auto generation=admission.generation,request=admission.request;
    const bool admitted=generation!=0;
    CommandLocationSample before;
    if(admitted)try {before=SampleCommandLocation(data,script,offset);}
    catch(...) {DropCommandObservation(generation);}
    // Never re-extract arguments: evaluating an argument twice can change game behavior.
    SetLastError(incomingError);
    floating.BeforeOriginal();
    const bool returned=original(parameters,data,reference,containing,script,events,result,offset);
    floating.AfterOriginal();
    restoreError.value=GetLastError();
    if (!admitted || !g_capture || !g_connected || generation!=g_captureGeneration.load()) return returned;
    try {
        const auto after=SampleCommandLocation(data,script,offset);
        UInt32 owner=0; bool ownerKnown=false;
        auto context=CallContext(reference,script,caller,owner,ownerKnown);
        context+=CommandLocationFields(before,after)+CommandAdmissionFields(admission)+CommandResultFields(result,g_commandHooks[Index].returnType);
        const auto prefix=",\"command\":"+Quote(g_commandHooks[Index].name)+",\"opcode\":"+
            std::to_string(g_commandHooks[Index].opcode)+",\"handlerReturned\":"+(returned?"true":"false");
        if constexpr (Index<2) {
            std::uint64_t instance=0;
            { std::lock_guard<std::mutex> lock(g_messageMutex);
              if(!g_capture || !g_connected || generation!=g_captureGeneration.load())return returned;
              instance=++g_messageInstance; g_lastMessage={instance,owner,ownerKnown,generation}; }
            Emit("message-command",request,prefix+context+",\"messageInstance\":"+std::to_string(instance)+
                ",\"evidence\":\"command-execute-return\",\"displayObserved\":false,\"messageFormIdRecovered\":false",generation);
        } else {
            double value=0;
            if (ReadNumber(result,value)) {
                std::string association;
                if (value>=0) {
                    std::lock_guard<std::mutex> lock(g_messageMutex);
                    if (ownerKnown && g_lastMessage.ownerKnown && g_lastMessage.owner==owner && g_lastMessage.instance && g_lastMessage.generation==generation) {
                        // Script button globals can be overwritten while another dialog is queued.
                        // This is a candidate association, not the selected visible message instance.
                        association=",\"candidateMessageInstance\":"+std::to_string(g_lastMessage.instance)+
                            ",\"instanceAttribution\":\"most-recent-matching-script-owner\",\"attributionConfirmed\":false";
                        g_lastMessage={};
                    }
                }
                Emit("message-choice-read",request,prefix+context+",\"value\":"+NumberField(value)+association+
                    ",\"evidence\":\"get-button-pressed-return\"",generation);
            }
        }
    } catch (...) {DropCommandObservation(generation);}
    return returned;
}

// Requested owner-list pilot only; no allocation, emission or calls from these callbacks.
struct ConditionEvalToken { void* scope=nullptr; std::uint64_t invocation=0; UInt32 itemCall=0; };
ConditionEvalToken ConditionEvalEnter(size_t, TESObjectREFR*, void*, void*) noexcept;
void ConditionEvalReturn(ConditionEvalToken, bool, double*) noexcept;
template<size_t Index> __declspec(noinline) bool EvalHook(TESObjectREFR* reference, void* parameter1, void* parameter2, double* result) {
    const auto caller=_ReturnAddress();
    const auto incomingError=GetLastError();
    struct RestoreError {DWORD value;~RestoreError(){SetLastError(value);}} restoreError{incomingError};
    CommandFloatingGuard floating;
    const auto admission=AdmitCommand();
    bool admitted=admission.generation!=0;
    if(admitted && !admission.request)try {
        UInt32 id=0;std::uint8_t type=0;const bool haveIdentity=ReadFormIdentity(reference,id,type);
        std::lock_guard<std::mutex> lock(g_mutex);
        if(!g_watchedActors.empty() && (!haveIdentity || g_watchedActors.find(id)==g_watchedActors.end()))admitted=false;
    } catch(...) {admitted=false;DropCommandObservation(admission.generation);}
    const auto conditionToken=ConditionEvalEnter(Index,reference,parameter1,parameter2);
    SetLastError(incomingError);
    floating.BeforeOriginal();
    const bool returned=g_commandHooks[Index].eval(reference,parameter1,parameter2,result);
    floating.AfterOriginal();
    restoreError.value=GetLastError();
    ConditionEvalReturn(conditionToken,returned,result);
    if (!admitted || !g_capture || !g_connected || admission.generation!=g_captureGeneration.load()) return returned;
    try {
        UInt32 unusedOwner=0; bool unusedKnown=false;
        auto context=CallContext(reference,nullptr,caller,unusedOwner,unusedKnown,false);
        double value=0;
        std::string fields=",\"function\":"+Quote(g_commandHooks[Index].name)+",\"opcode\":"+
            std::to_string(g_commandHooks[Index].opcode)+",\"handlerReturned\":"+(returned?"true":"false")+
            ",\"parameter1Raw\":"+std::to_string(reinterpret_cast<UInt32>(parameter1))+
            ",\"parameter2Raw\":"+std::to_string(reinterpret_cast<UInt32>(parameter2));
        if (ReadNumber(result,value)) fields+=",\"value\":"+NumberField(value);
        Emit("condition-function",admission.request,fields+context+CommandAdmissionFields(admission)+
            ",\"evidence\":\"command-eval-return\",\"comparisonObserved\":false,\"conditionOwnerRecovered\":false",admission.generation);
    } catch (...) {DropCommandObservation(admission.generation);}
    return returned;
}
CommandExecute g_executeWrappers[]={ExecuteHook<0>,ExecuteHook<1>,ExecuteHook<2>};
CommandEval g_evalWrappers[]={EvalHook<3>,EvalHook<4>,EvalHook<5>,EvalHook<6>,EvalHook<7>,EvalHook<8>,EvalHook<9>,EvalHook<10>};
bool ExecutableAddress(void* address) {
    MEMORY_BASIC_INFORMATION region{};
    if (!address || !VirtualQuery(address,&region,sizeof(region)) || region.State!=MEM_COMMIT || (region.Protect & PAGE_GUARD)) return false;
    return (region.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))!=0;
}
bool ReplacePointer(void** slot, void* expected, void* replacement) {
    DWORD protection=0;
    if (!VirtualProtect(slot,sizeof(void*),PAGE_READWRITE,&protection)) return false;
    const bool changed=InterlockedCompareExchangePointer(slot,replacement,expected)==expected;
    DWORD ignored=0; VirtualProtect(slot,sizeof(void*),protection,&ignored);
    return changed;
}
void InstallCommandHooks() {
    if (!g_commands || g_commandHooksAttempted) return;
    g_commandHooksAttempted=true;
    for (size_t index=0;index<std::size(g_commandHooks);++index) {
        auto& hook=g_commandHooks[index];
        hook.entry=const_cast<CommandInfo*>(g_commands->GetByName(hook.name));
        if (!hook.entry) continue;
        hook.opcode=hook.entry->opcode;
        if(g_commands->GetReturnType)hook.returnType=g_commands->GetReturnType(hook.entry);
        if (index<3) {
            hook.execute=hook.entry->execute;
            if (ExecutableAddress(reinterpret_cast<void*>(hook.execute)) &&
                ReplacePointer(reinterpret_cast<void**>(&hook.entry->execute),reinterpret_cast<void*>(hook.execute),reinterpret_cast<void*>(g_executeWrappers[index])))
                g_messageHookMask.fetch_or(1u<<index);
        } else {
            hook.eval=hook.entry->eval;
            if (ExecutableAddress(reinterpret_cast<void*>(hook.eval)) &&
                ReplacePointer(reinterpret_cast<void**>(&hook.entry->eval),reinterpret_cast<void*>(hook.eval),reinterpret_cast<void*>(g_evalWrappers[index-3])))
                ++g_conditionHookCount;
        }
    }
}
void RestoreCommandHooks() {
    for (size_t index=0;index<std::size(g_commandHooks);++index) {
        auto& hook=g_commandHooks[index]; if (!hook.entry) continue;
        if (index<3 && hook.execute) ReplacePointer(reinterpret_cast<void**>(&hook.entry->execute),reinterpret_cast<void*>(g_executeWrappers[index]),reinterpret_cast<void*>(hook.execute));
        if (index>=3 && hook.eval) ReplacePointer(reinterpret_cast<void**>(&hook.entry->eval),reinterpret_cast<void*>(g_evalWrappers[index-3]),reinterpret_cast<void*>(hook.eval));
    }
    g_messageHookMask=0; g_conditionHookCount=0;
}
