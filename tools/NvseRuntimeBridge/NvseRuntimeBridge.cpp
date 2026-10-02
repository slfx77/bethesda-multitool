#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <sddl.h>
#include <winver.h>
#include <bcrypt.h>
#include <psapi.h>
#include <TlHelp32.h>
#include <winternl.h>
#pragma comment(lib,"Bcrypt.lib")
#include <intrin.h>
#include <immintrin.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cctype>
#include <cstdint>
#include <deque>
#include <iomanip>
#include <initializer_list>
#include <limits>
#include <map>
#include <mutex>
#include <regex>
#include <sstream>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>
#include "NvseRuntimeApi.h"
#include "RuntimeConditionPilot.h"
#include "RuntimeQuestCondition.h"
#include "RuntimeConditionPatch.h"
#include "RuntimeConditionFrame.h"
#include "RuntimeDamageInvocation.h"
#include "RuntimeCriticalInvocation.h"
#include "RuntimeCriticalInvocationProfile.h"
#include "RuntimeLiveInput.h"
#include "RuntimeLiveJson.h"

namespace {
constexpr UInt32 RuntimeVersion = 0x040020D0, Magic = 0x31544D42;
constexpr std::uint16_t Protocol = 1, Event = 256;
constexpr UInt32 MaxPayload = 65536, MainGameLoop = 20;
constexpr size_t MaxQueue = 128, MaxEvents = 4096, MaxExpressions = 128;
bool VerifiedPcExecutable(const std::string& sha256) noexcept {
    // The second image changes only PE headers and the Steam/xNVSE bootstrap in
    // .bind. Every engine section and layout is byte-identical to retail.
    // Evidence: artifacts/runtime-live/laa-only/user-executable-comparison.json.
    return sha256=="3a87f92f011e5dc9179ddf733cf08be2b39ea6e5b7a8a9e3a9a72dafcc1b104d" ||
        sha256=="518c87f58a6c4d9826e9ef8fbb7f4213882fa70822675610d45aea2464502a57";
}
struct Request {
    std::uint16_t kind; std::uint64_t id; std::string payload;
    // Receipt metadata survives queue moves and later connection/capture changes.
    std::uint64_t originConnectionGeneration=0,captureGenerationAtReceipt=0;
    ULONGLONG receivedMilliseconds=0,queuedMilliseconds=0;
    bool receivedObserved=false,queuedObserved=false;
};
struct EventRecord { std::uint64_t id; std::string json; };
NVSEConsoleInterface* g_console = nullptr;
NVSEScriptInterface* g_script = nullptr;
bool g_getBaseObject = false;
NVSEEventManagerInterface* g_eventManager = nullptr;
std::atomic<UInt32> g_actorEventMask{0};
bool g_actorEventsAttempted = false;
std::unordered_set<UInt32> g_watchedActors;
std::mutex g_mutex;
std::deque<Request> g_requests;
std::deque<EventRecord> g_events;
std::atomic<bool> g_running{true}, g_capture{false}, g_connected{false}, g_numericEnabled{false};
std::atomic<std::uint64_t> g_captureGeneration{0};
std::atomic<std::uint64_t> g_connectionGeneration{0},g_gameLoadEpoch{0};
std::uint64_t g_sequence = 0, g_dropped = 0, g_frame = 0;
UInt32 g_nvseVersion = 0;
std::unordered_map<std::string, Script*> g_expressions;
std::string g_session;
HANDLE g_pipe = INVALID_HANDLE_VALUE;

std::string Quote(const std::string& text) {
    std::ostringstream out; out << '"';
    for (unsigned char c : text) {
        switch(c) {
        case '"': out << "\\\""; break; case '\\': out << "\\\\"; break;
        case '\n': out << "\\n"; break; case '\r': out << "\\r"; break; case '\t': out << "\\t"; break;
        default:
            if (c < 32 || c >= 128) out << "\\u00" << std::hex << std::setw(2) << std::setfill('0') << int(c) << std::dec;
            else out << c;
        }
    }
    out << '"'; return out.str();
}
struct DispatchRequestSample {
    bool observed=false,queuedObserved=false;
    std::uint16_t kind=0;
    std::uint64_t id=0,originConnection=0,captureAtReceipt=0,observedConnection=0,observedCapture=0,frame=0;
    ULONGLONG received=0,queued=0,observedAt=0;
    DWORD thread=0;
};
struct DispatchLoopSample {
    bool observed=false;
    ULONGLONG at=0;
    LONGLONG qpc=0;
    std::uint64_t frame=0,connection=0,capture=0;
    DWORD thread=0;
};
struct DispatchClearSample {
    bool observed=false,retainedStart=false;
    std::string reason;
    ULONGLONG at=0;
    std::uint64_t connection=0,capture=0,retainedId=0,retainedConnection=0;
    size_t removed=0;
    DispatchRequestSample frontRemoved;
};
// All diagnostic state is protected by g_mutex. Samples contain no request payload.
DispatchRequestSample g_lastReceived,g_lastNonDiagnosticReceived,g_lastQueued,g_lastDispatched;
DispatchLoopSample g_lastMainGameLoop;
DispatchClearSample g_lastQueueClear,g_lastNonemptyQueueClear;
DispatchRequestSample DispatchSampleLocked(const Request& request,ULONGLONG now) {
    return {request.receivedObserved,request.queuedObserved,request.kind,request.id,
        request.originConnectionGeneration,request.captureGenerationAtReceipt,
        g_connectionGeneration.load(),g_captureGeneration.load(),g_frame,
        request.receivedMilliseconds,request.queuedMilliseconds,now,GetCurrentThreadId()};
}
void RecordReceivedRequest(Request& request,ULONGLONG now=0) {
    std::lock_guard<std::mutex> lock(g_mutex);
    request.originConnectionGeneration=g_connectionGeneration.load();
    request.captureGenerationAtReceipt=g_captureGeneration.load();
    request.receivedMilliseconds=now?now:GetTickCount64();request.receivedObserved=true;
    g_lastReceived=DispatchSampleLocked(request,request.receivedMilliseconds);
    if(request.kind!=1 && request.kind!=6)g_lastNonDiagnosticReceived=g_lastReceived;
}
void ClearRequestsLocked(const char* reason,ULONGLONG now,std::uint64_t connection,const Request* retainedStart=nullptr) {
    DispatchRequestSample frontRemoved;bool skippedRetained=false;
    for(const auto& request:g_requests) {
        if(retainedStart && !skippedRetained && request.kind==2 && request.id==retainedStart->id &&
            request.originConnectionGeneration==retainedStart->originConnectionGeneration) {skippedRetained=true;continue;}
        frontRemoved=DispatchSampleLocked(request,now);break;
    }
    g_lastQueueClear={true,retainedStart!=nullptr,reason,now,connection,g_captureGeneration.load(),
        retainedStart?retainedStart->id:0,retainedStart?retainedStart->originConnectionGeneration:0,
        g_requests.size()-(retainedStart?1u:0u),frontRemoved};
    if(g_lastQueueClear.removed)g_lastNonemptyQueueClear=g_lastQueueClear;
    g_requests.clear();
}
bool QueueRequest(Request request,bool priority,ULONGLONG now=0) {
    std::lock_guard<std::mutex> lock(g_mutex);
    const auto at=now?now:GetTickCount64();
    if(priority) {
        // Preserve the existing cancellation rule: a pending Start precedes cancellation.
        Request start{};bool haveStart=false;
        if(!g_capture) {
            const auto found=std::find_if(g_requests.begin(),g_requests.end(),[](const Request& item){return item.kind==2;});
            if(found!=g_requests.end()){start=std::move(*found);haveStart=true;}
        }
        ClearRequestsLocked(request.kind==5?"cancel-priority":"lease-stop-priority",at,
            request.originConnectionGeneration,haveStart?&start:nullptr);
        if(haveStart)g_requests.push_back(std::move(start));
    } else if(g_requests.size()>=MaxQueue)return false;
    request.queuedMilliseconds=at;request.queuedObserved=true;
    g_lastQueued=DispatchSampleLocked(request,at);g_requests.push_back(std::move(request));return true;
}
void RecordMainGameLoop(ULONGLONG now=0) {
    std::lock_guard<std::mutex> lock(g_mutex);
    LARGE_INTEGER qpc{};QueryPerformanceCounter(&qpc);
    g_lastMainGameLoop={true,now?now:GetTickCount64(),qpc.QuadPart,++g_frame,
        g_connectionGeneration.load(),g_captureGeneration.load(),GetCurrentThreadId()};
}
bool TakeDispatchRequest(Request& request,ULONGLONG now=0) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if(g_requests.empty())return false;
    request=std::move(g_requests.front());g_requests.pop_front();
    g_lastDispatched=DispatchSampleLocked(request,now?now:GetTickCount64());return true;
}
std::string DispatchRequestJson(const DispatchRequestSample& sample) {
    if(!sample.observed)return "null";
    return "{\"requestKind\":"+std::to_string(sample.kind)+",\"requestId\":"+std::to_string(sample.id)+
        ",\"originConnectionGeneration\":"+std::to_string(sample.originConnection)+
        ",\"captureGenerationAtReceipt\":"+std::to_string(sample.captureAtReceipt)+
        ",\"receivedMonotonicMilliseconds\":"+std::to_string(sample.received)+
        ",\"queuedMonotonicMilliseconds\":"+(sample.queuedObserved?std::to_string(sample.queued):"null")+
        ",\"observedMonotonicMilliseconds\":"+std::to_string(sample.observedAt)+
        ",\"observedConnectionGeneration\":"+std::to_string(sample.observedConnection)+
        ",\"observedCaptureGeneration\":"+std::to_string(sample.observedCapture)+
        ",\"frame\":"+std::to_string(sample.frame)+",\"threadId\":"+std::to_string(sample.thread)+"}";
}
std::string DispatchClearJson(const DispatchClearSample& clear) {
    if(!clear.observed)return "null";
    return "{\"reason\":"+Quote(clear.reason)+
        ",\"monotonicMilliseconds\":"+std::to_string(clear.at)+",\"connectionGeneration\":"+std::to_string(clear.connection)+
        ",\"captureGeneration\":"+std::to_string(clear.capture)+",\"removedCount\":"+std::to_string(clear.removed)+
        ",\"retainedStartRequestId\":"+(clear.retainedStart?std::to_string(clear.retainedId):"null")+
        ",\"retainedStartConnectionGeneration\":"+(clear.retainedStart?std::to_string(clear.retainedConnection):"null")+
        ",\"frontRemoved\":"+DispatchRequestJson(clear.frontRemoved)+"}";
}
std::string DispatchDiagnostics() {
    std::lock_guard<std::mutex> lock(g_mutex);
    LARGE_INTEGER qpc{};QueryPerformanceCounter(&qpc);
    const auto now=GetTickCount64();const auto& loop=g_lastMainGameLoop;
    const auto loopJson=!loop.observed?"null":"{\"monotonicMilliseconds\":"+std::to_string(loop.at)+
        ",\"qpc\":"+std::to_string(loop.qpc)+",\"frame\":"+std::to_string(loop.frame)+
        ",\"threadId\":"+std::to_string(loop.thread)+",\"connectionGeneration\":"+std::to_string(loop.connection)+
        ",\"captureGeneration\":"+std::to_string(loop.capture)+"}";
    return ",\"dispatchDiagnostics\":{\"schemaVersion\":1,\"evidence\":\"bridge-dispatch-observation\",\"monotonicMilliseconds\":"+
        std::to_string(now)+",\"qpc\":"+std::to_string(qpc.QuadPart)+",\"connectionGeneration\":"+std::to_string(g_connectionGeneration.load())+
        ",\"captureGeneration\":"+std::to_string(g_captureGeneration.load())+
        ",\"connected\":"+(g_connected?"true":"false")+",\"captureActive\":"+(g_capture?"true":"false")+
        ",\"lastMainGameLoop\":"+loopJson+",\"queue\":{\"count\":"+std::to_string(g_requests.size())+
        ",\"front\":"+(g_requests.empty()?"null":DispatchRequestJson(DispatchSampleLocked(g_requests.front(),now)))+"}"+
        ",\"lastReceived\":"+DispatchRequestJson(g_lastReceived)+
        ",\"lastNonDiagnosticReceived\":"+DispatchRequestJson(g_lastNonDiagnosticReceived)+
        ",\"lastQueued\":"+DispatchRequestJson(g_lastQueued)+",\"lastDispatched\":"+DispatchRequestJson(g_lastDispatched)+
        ",\"lastQueueClear\":"+DispatchClearJson(g_lastQueueClear)+
        ",\"lastNonemptyQueueClear\":"+DispatchClearJson(g_lastNonemptyQueueClear)+"}";
}
// Caller holds g_mutex. Shared by ordinary events and atomic capture boundaries.
void EmitLocked(const char* kind,std::uint64_t id,const std::string& extra,std::uint64_t captureGeneration,LONGLONG qpc) {
    if (captureGeneration && (!g_connected || !g_capture || captureGeneration!=g_captureGeneration.load())) return;
    if (g_events.size() >= MaxEvents) { g_events.pop_front(); ++g_dropped; }
    std::ostringstream json;
    json << "{\"protocol\":1,\"kind\":" << Quote(kind) << ",\"sequence\":" << ++g_sequence
         << ",\"requestId\":" << id << ",\"frame\":" << g_frame << ",\"qpc\":" << qpc
         << ",\"dropped\":" << g_dropped << extra << "}";
    g_events.push_back({id,json.str()});
}
void Emit(const char* kind, std::uint64_t id, const std::string& extra = "", std::uint64_t captureGeneration = 0) {
    if (!g_connected) return;
    LARGE_INTEGER time{}; QueryPerformanceCounter(&time);
    std::lock_guard<std::mutex> lock(g_mutex);
    EmitLocked(kind,id,extra,captureGeneration,time.QuadPart);
}
#include "NvseRuntimeNotifications.inl"
bool ReadFormIdentity(void* form, UInt32& id, std::uint8_t& type) {
    std::uint8_t bytes[16]{}; SIZE_T read=0;
    if (!form || !ReadProcessMemory(GetCurrentProcess(),form,bytes,16,&read) || read != 16) return false;
    type=bytes[4]; memcpy(&id,bytes+12,4); return id != 0;
}
#include "NvseRuntimeCommandHooks.inl"
#include "NvseRuntimeGeneralCommands.inl"
#include "NvseRuntimePluginIdentity.inl"
#include "NvseRuntimeQuest.inl"
#include "NvseRuntimeActor.inl"
#include "NvseRuntimeOperations.inl"
#include "NvseRuntimeWeaponCritical.inl"
#include "NvseRuntimeCreatureScaling.inl"
#include "NvseRuntimeGameSettings.inl"
#include "NvseRuntimeScriptLocals.inl"
#include "NvseRuntimeScriptLocalSdk.inl"
#include "NvseRuntimeConditions.inl"
#include "NvseRuntimeConditionPatch.inl"
#include "NvseRuntimeDispatchFingerprint.inl"
#include "NvseRuntimeCombatLease.inl"
#include "NvseRuntimeCombatObservation.inl"
#include "NvseRuntimeDamageInvocation.inl"
#include "NvseRuntimeCriticalInvocation.inl"
#include "NvseRuntimeMembership.inl"
#include "NvseRuntimeReferenceHooks.inl"
#include "NvseRuntimeMessageProbe.inl"
#include "NvseRuntimeMenu.inl"
#include "NvseRuntimeScriptTrace.inl"
#include "NvseRuntimeLive.inl"
void ActorEvent(const char* eventName, const char* relation, void* parameters) {
    if (!g_capture || !parameters) return;
    void* args[2]{}; SIZE_T read=0;
    if (!ReadProcessMemory(GetCurrentProcess(),parameters,args,sizeof(args),&read) || read != sizeof(args)) return;
    UInt32 source=0, other=0; std::uint8_t sourceType=0, otherType=0;
    if (!ReadFormIdentity(args[0],source,sourceType)) return;
    const bool haveOther=ReadFormIdentity(args[1],other,otherType);
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (g_watchedActors.find(source)==g_watchedActors.end() &&
            (!haveOther || g_watchedActors.find(other)==g_watchedActors.end())) return;
    }
    std::string extra=",\"event\":"+Quote(eventName)+",\"engineTargetFormId\":"+std::to_string(source)+
        ",\"engineTargetFormType\":"+std::to_string(sourceType)+",\"evidence\":\"sdk-native-event\"";
    if (haveOther) extra += ",\"secondaryFormId\":"+std::to_string(other)+
        ",\"secondaryFormType\":"+std::to_string(otherType)+",\"secondaryRelation\":"+Quote(relation);
    Emit("actor-event",0,extra);
    if (sourceType==0x3B || sourceType==0x3C) ActorHitEvent(args[0],source,eventName,haveOther?other:0);
}
void OnHit(TESObjectREFR*, void* args) { ActorEvent("onhit","attacker",args); }
void OnHitWith(TESObjectREFR*, void* args) { ActorEvent("onhitwith","weapon",args); }
void OnDeath(TESObjectREFR*, void* args) { ActorEvent("ondeath","event-object",args); }
void RegisterActorEvents() {
    if (!g_eventManager || g_actorEventsAttempted) return;
    g_actorEventsAttempted=true;
    UInt32 mask=0;
    if (g_eventManager->SetNativeEventHandler("onhit",OnHit)) mask|=1;
    if (g_eventManager->SetNativeEventHandler("onhitwith",OnHitWith)) mask|=2;
    if (g_eventManager->SetNativeEventHandler("ondeath",OnDeath)) mask|=4;
    g_actorEventMask=mask;
}
bool NewEnoughScriptApi() {
    HMODULE module = GetModuleHandleW(L"nvse_1_4.dll");
    wchar_t path[32768]{};
    if (!module || !GetModuleFileNameW(module,path,32768)) return false;
    DWORD unused = 0, length = GetFileVersionInfoSizeW(path,&unused);
    std::vector<std::uint8_t> bytes(length);
    if (!length || !GetFileVersionInfoW(path,0,length,bytes.data())) return false;
    VS_FIXEDFILEINFO* info = nullptr; UINT size = 0;
    if (!VerQueryValueW(bytes.data(),L"\\",reinterpret_cast<void**>(&info),&size) || size < sizeof(*info)) return false;
    // Pinned SDK release declares file version 0,6,4,9.
    return info->dwFileVersionMS > 6 || (info->dwFileVersionMS == 6 && info->dwFileVersionLS >= 0x00040009);
}
bool ValidExpression(const std::string& expression) {
    static const std::regex pattern("^[A-Za-z_][A-Za-z_0-9]{0,127}(\\.[A-Za-z_][A-Za-z_0-9]{0,127}|\\.(GetAV|GetBaseAV|GetPermAV) [A-Za-z_][A-Za-z_0-9]{0,127})?$");
    return expression.size() <= 300 && std::regex_match(expression,pattern);
}
void ConditionProbe(const Request& request) {
    if (!g_capture || !g_script || !g_numericEnabled || !g_commands || request.payload!="player-get-dead") {
        Emit("error",request.id,",\"error\":\"condition-probe-api-unavailable-or-invalid\""); return;
    }
    const auto command=g_commands->GetByName("GetDead");
    if (!command || !command->eval || command->numParams!=0) {
        Emit("error",request.id,",\"error\":\"condition-probe-handler-unavailable\""); return;
    }
    auto owner=g_expressions.find("player");
    if (owner==g_expressions.end() && g_expressions.size()<MaxExpressions) {
        if (const auto compiled=g_script->CompileExpression("player")) owner=g_expressions.emplace("player",compiled).first;
    }
    NumericElement player{};
    if (owner==g_expressions.end() || !g_script->CallFunction(owner->second,nullptr,nullptr,&player,0) || player.type!=2 || !player.form) {
        Emit("error",request.id,",\"error\":\"condition-probe-player-unavailable\""); return;
    }
    UInt32 id=0; std::uint8_t type=0;
    if (!ReadFormIdentity(player.form,id,type) || type!=0x3B) {
        Emit("error",request.id,",\"error\":\"condition-probe-actor-type-mismatch\""); return;
    }
    double value=0;
    const bool returned=command->eval(static_cast<TESObjectREFR*>(player.form),nullptr,nullptr,&value);
    if (!std::isfinite(value)) { Emit("error",request.id,",\"error\":\"condition-probe-nonfinite-result\""); return; }
    Emit("condition-probe-result",request.id,",\"function\":\"GetDead\",\"engineTargetFormId\":"+std::to_string(id)+
        ",\"value\":"+NumberField(value)+",\"handlerReturned\":"+(returned?"true":"false")+
        ",\"evidence\":\"requested-engine-eval-handler\",\"comparisonObserved\":false");
}
void Execute(const Request& request) {
    const RequestScope requestScope(request.id);
    // Sequence boundary for passive observations: emitted on the game thread before
    // validation/dispatch, so synchronous callbacks cannot precede their request.
    if (g_capture && (request.kind==3 || (request.kind>=7 && request.kind<=19) || request.kind==22))
        Emit("action-begin",request.id,",\"requestKind\":"+std::to_string(request.kind)+
            ",\"evidence\":\"game-thread-dispatch\"",g_captureGeneration.load());
    if(CombatLeaseBlocksMutation(request)) {
        Emit("error",request.id,",\"error\":\"combat-lease-mutation-blocked\"");return;
    }
    switch (request.kind) {
    case 30: case 31: LiveRequest(request); break;
    case 2:
        if (g_capture) { Emit("error",request.id,",\"error\":\"capture-already-active\""); break; }
        {
        std::string pluginPayload,scriptScope,scopeError;
        if (!ConfigureScriptTraceStart(request.payload,pluginPayload,scriptScope,scopeError)) {
            Emit("error",request.id,",\"error\":"+Quote(scopeError)); break;
        }
        const auto pluginNamespace=CapturePluginNamespace(pluginPayload);
        ++g_captureGeneration;
        { std::lock_guard<std::mutex> lock(g_mutex); g_watchedActors.clear(); g_actorHitHistory.clear(); }
        { std::lock_guard<std::mutex> lock(g_messageMutex); g_lastMessage={}; g_messageInstance=0; }
        const auto generation=g_captureGeneration.load(),connection=g_connectionGeneration.load();
        const auto dispatchWindow=DispatchFingerprintFields("start",generation,connection);
        if(BeginNotificationCapture(request,generation,connection,",\"session\":"+Quote(g_session)+pluginNamespace+scriptScope+GeneralCommandCoverage()+dispatchWindow))
            EmitGeneralCommandCoverage(request.id,generation);
        break;
        }
    case 4: case 5:
        CombatLeaseEndCapture(request); // Terminal cleanup receipt precedes capture-end.
        break;
    case 8: MessageProbe(request); break;
    case 9: ConditionProbe(request); break;
    case 10: case 11: QuestVariable(request); break;
    case 12: ActorSnapshot(request); break;
    case 13: MenuAction(request); break;
    case 14: ActorSet(request); break;
    case 15: case 16: case 17: case 18: TypedOperationRequest(request); break;
    case 19: MembershipRequest(request); break;
    case 22: ConditionOwnerRequest(request); break;
    case 3:
        if(GmstRequest(request))break;
        if (!g_capture) { Emit("error",request.id,",\"error\":\"capture-required\""); break; }
        if (request.payload.empty() || request.payload.size() > 4096 ||
            !std::all_of(request.payload.begin(),request.payload.end(),[](unsigned char c) { return c >= 32 && c < 127; })) {
            Emit("error",request.id,",\"error\":\"invalid-command\""); break;
        }
        {
            const bool accepted = g_console->RunScriptLine(request.payload.c_str(),nullptr);
            Emit("action-result",request.id,",\"accepted\":"+std::string(accepted?"true":"false")+
                ",\"evidence\":\"console-return\",\"command\":"+Quote(request.payload));
        }
        break;
    case 7:
        if(GmstRequest(request))break;
        if(ReferenceLocalRequest(request))break;
        if(ReferenceLocalSdkRequest(request))break;
        if (!g_capture) { Emit("error",request.id,",\"error\":\"capture-required\""); break; }
        if (!g_script || !g_numericEnabled) { Emit("error",request.id,",\"error\":\"numeric-state-api-unavailable\""); break; }
        if (!ValidExpression(request.payload)) { Emit("error",request.id,",\"error\":\"invalid-numeric-expression\""); break; }
        {
            auto found = g_expressions.find(request.payload);
            if (found == g_expressions.end()) {
                if (g_expressions.size() >= MaxExpressions) {
                    Emit("error",request.id,",\"error\":\"expression-cache-limit\""); break;
                }
                auto script = g_script->CompileExpression(request.payload.c_str());
                if (!script) { Emit("error",request.id,",\"error\":\"expression-compile-failed\""); break; }
                found = g_expressions.emplace(request.payload,script).first;
            }
            NumericElement value{};
            const bool succeeded = g_script->CallFunction(found->second,nullptr,nullptr,&value,0);
            if (!succeeded || value.type != 1 || !std::isfinite(value.number)) {
                // The public SDK has no general Element disposal API. Unexpected string results
                // disable this capability for the process, bounding any retained allocation to one.
                if (value.type == 3) g_numericEnabled = false;
                Emit("error",request.id,",\"error\":\"numeric-result-unavailable\",\"resultType\":"+std::to_string(value.type)); break;
            }
            std::ostringstream result; result << std::setprecision(17) << value.number;
            std::string identity;
            const auto dot = request.payload.find('.');
            if (dot != std::string::npos) {
                const auto target = request.payload.substr(0,dot);
                auto owner = g_expressions.find(target);
                if (owner == g_expressions.end() && g_expressions.size() < MaxExpressions) {
                    auto compiled = g_script->CompileExpression(target.c_str());
                    if (compiled) owner = g_expressions.emplace(target,compiled).first;
                }
                if (owner != g_expressions.end()) {
                    NumericElement form{};
                    if (g_script->CallFunction(owner->second,nullptr,nullptr,&form,0) && form.type == 2 && form.form) {
                        // TESForm layout from pinned GameForms.h: typeID +4, refID +0xC.
                        std::uint8_t bytes[16]{}; SIZE_T read = 0;
                        if (ReadProcessMemory(GetCurrentProcess(),form.form,bytes,16,&read) && read == 16) {
                            UInt32 formId=0; memcpy(&formId,bytes+12,4);
                            if (request.payload.find(' ') != std::string::npos &&
                                (bytes[4]==0x3A || bytes[4]==0x3B || bytes[4]==0x3C)) {
                                std::lock_guard<std::mutex> lock(g_mutex);
                                g_watchedActors.insert(formId);
                            }
                            identity = ",\"engineTargetFormId\":"+std::to_string(formId)+",\"engineTargetFormType\":"+std::to_string(bytes[4]);
                            if (g_getBaseObject && (bytes[4] == 0x3A || bytes[4] == 0x3B || bytes[4] == 0x3C)) {
                                const auto expression = target + ".GetBaseObject";
                                auto base = g_expressions.find(expression);
                                if (base == g_expressions.end() && g_expressions.size() < MaxExpressions) {
                                    auto compiled = g_script->CompileExpression(expression.c_str());
                                    if (compiled) base = g_expressions.emplace(expression,compiled).first;
                                }
                                NumericElement baseForm{};
                                if (base != g_expressions.end() &&
                                    g_script->CallFunction(base->second,nullptr,nullptr,&baseForm,0) &&
                                    baseForm.type == 2 && baseForm.form &&
                                    ReadProcessMemory(GetCurrentProcess(),baseForm.form,bytes,16,&read) && read == 16) {
                                    memcpy(&formId,bytes+12,4);
                                    identity += ",\"engineTargetBaseFormId\":"+std::to_string(formId);
                                }
                                if (baseForm.type == 3) g_numericEnabled = false;
                            }
                        }
                    }
                    if (form.type == 3) g_numericEnabled = false;
                }
            }
            const char* component = request.payload.find(".GetBaseAV ") != std::string::npos ? "base"
                : request.payload.find(".GetPermAV ") != std::string::npos ? "permanent"
                : request.payload.find(".GetAV ") != std::string::npos ? "current" : "variable";
            const auto space = request.payload.find(' ');
            const auto statistic = space != std::string::npos ? request.payload.substr(space+1)
                : dot != std::string::npos ? request.payload.substr(dot+1) : request.payload;
            const char* targetKind = space != std::string::npos ? "actor" : dot != std::string::npos ? "quest" : "global";
            Emit("snapshot",request.id,",\"expression\":"+Quote(request.payload)+",\"value\":"+result.str()+
                ",\"component\":"+Quote(component)+",\"statistic\":"+Quote(statistic)+",\"targetKind\":"+Quote(targetKind)+
                identity+",\"evidence\":\"engine-expression-return\"");
        }
        break;
    default: Emit("error",request.id,",\"error\":\"unsupported-request\""); break;
    }
}
void OnMessage(NVSEMessagingInterface::Message* message) {
    if (!message) return;
    LiveMessage(message->type,message->data,message->dataLen);
    if(message->type==2 || message->type==6 || message->type==8 || message->type==14) {
        ++g_gameLoadEpoch;CombatLeaseRequestCleanup("load-epoch-changed");
    }
    if (message->type==2 || message->type==6) g_loadedGameObserved=false;
    if (message->type==14) g_loadedGameObserved=true;
    if (message->type==8) g_loadedGameObserved=message->data!=nullptr;
    if (message->type == 0 || message->type == 9) RegisterActorEvents();
    if (message->type == MainGameLoop) {
        RecordMainGameLoop(); // Callback entry, distinct from eventual queued-command dispatch.
        InstallCommandHooks(); // After command registration/table publication, on the game thread.
        InstallGeneralCommandHooks();
        InstallReferenceCommandHook();
        InstallScriptTrace();
        InstallConditionPilotCalls();
        InstallDamageInvocation();
        InstallCriticalInvocation();
        ServiceDamageInvocation();
        ServiceCombatLease(); // Independent of capture/connection and queued actions.
        ServiceCombatObservation(); // Read only while the same bounded lease remains armed.
        Request request{};
        if (TakeDispatchRequest(request)) Execute(request); // exactly one action per game frame
        bmt_live_input::Tick();
        LiveTick();
        VerifyMessageProbeApi();
        if(!g_menuApiVerified) VerifyMenuApi();
        if (g_capture && g_frame % 60 == 0) Emit("heartbeat",0);
    } else {
        CaptureRuntimeNotification(*message);
    }
    if (message->type == 1 || message->type == 7) {
        LiveDisconnect();
        ServiceCombatLease(GetTickCount64(),CombatLeaseIo{},true);
        RestoreCriticalInvocation();
        RestoreDamageInvocation();
        RestoreConditionPilotCalls();
        RestoreReferenceCommandHook(); RestoreGeneralCommandHooks(); RestoreCommandHooks(); RestoreScriptTrace(); g_running = false;
    }
}
std::string Capabilities() {
    LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
    return ",\"processId\":"+std::to_string(GetCurrentProcessId())+",\"runtimeVersion\":\"0x040020D0\",\"nvseVersion\":"+
        std::to_string(g_nvseVersion)+",\"qpcFrequency\":"+std::to_string(frequency.QuadPart)+
        ",\"backend\":\"xnvse\",\"capabilities\":{\"console\":true,\"actionBoundaries\":true,\"numericState\":"+(g_numericEnabled?std::string("true"):std::string("false"))+
        ",\"actorHitEvents\":"+std::string((g_actorEventMask&3)==3?"true":"false")+
        ",\"actorDeathEvents\":"+std::string((g_actorEventMask&4)!=0?"true":"false")+
        ",\"actorState\":"+std::string(g_numericEnabled && g_pcLayoutVerified?"true":"false")+
        ",\"creatureHealthScaling\":"+std::string(g_numericEnabled && g_pcLayoutVerified?"true":"false")+
        ",\"actorWeaponCriticalStage\":"+std::string(g_numericEnabled && g_pcLayoutVerified && g_script && g_script->CompileScript && g_script->CallFunction?"true":"false")+
        ",\"gameSettingRead\":"+std::string(g_pcLayoutVerified?"true":"false")+
        ",\"gameSettingWrite\":"+std::string(g_pcLayoutVerified && g_numericEnabled && g_script && g_script->CompileScript && g_script->CallFunction?"true":"false")+
        ",\"actorHitRecords\":"+std::string(g_pcLayoutVerified && (g_actorEventMask&3)==3?"true":"false")+
        ",\"messageCalls\":"+std::string((g_messageHookMask&3)!=0?"true":"false")+
        ",\"scriptMessageChoice\":"+std::string((g_messageHookMask&4)!=0?"true":"false")+
        ",\"rawCommandLocations\":"+std::string(g_pcLayoutVerified && (g_messageHookMask!=0 || g_generalInstalled>0)?"true":"false")+
        ",\"commandExecuteTrace\":"+std::string(g_generalInstalled>0?"true":"false")+
        ",\"conditionFunctionTrace\":"+std::string(g_conditionHookCount>0?"true":"false")+
        ",\"controlledMessageProbe\":"+std::string(g_messageApiVerified?"true":"false")+
        ",\"typedSingleButtonChoice\":"+std::string(g_menuApiVerified?"true":"false")+
        ",\"messageStateAvailability\":"+std::string(g_menuApiVerified?"true":"false")+
        ",\"explicitActorEnduranceSet\":"+std::string(g_numericEnabled && g_pcLayoutVerified?"true":"false")+
        ",\"referenceState\":"+std::string(g_numericEnabled && g_pcLayoutVerified?"true":"false")+
        ",\"referenceActions\":"+std::string(g_numericEnabled && g_pcLayoutVerified?"true":"false")+
        ",\"combatCleanupLease\":"+std::string(g_numericEnabled && g_pcLayoutVerified && g_script && g_script->CompileScript && g_script->CallFunction?"true":"false")+
        ",\"referenceCommandTrace\":"+std::string(g_referenceCommandHooked?"true":"false")+
        ",\"inventoryOperations\":"+std::string(g_numericEnabled && g_pcLayoutVerified?"true":"false")+
        ",\"questStateOperations\":"+std::string(g_numericEnabled && g_pcLayoutVerified?"true":"false")+
        ",\"recordMembership\":"+std::string(g_numericEnabled && g_pcLayoutVerified?"true":"false")+
        ",\"controlledConditionProbe\":"+std::string(g_numericEnabled && g_commands?"true":"false")+
        ",\"ownerConditionListProbe\":"+std::string(ConditionPilotAvailable()?"true":"false")+
        ",\"questConditionListRead\":"+std::string(ConditionPilotAvailable()?"true":"false")+
        ",\"activePluginQueries\":"+std::string(g_numericEnabled?"true":"false")+
        ",\"explicitQuestVariables\":"+std::string(g_numericEnabled && g_pcLayoutVerified?"true":"false")+
        ",\"referenceScriptLocals\":"+std::string(g_numericEnabled && g_pcLayoutVerified?"true":"false")+
        ",\"referenceScriptLocalsSdk\":"+std::string(g_numericEnabled && g_pcLayoutVerified && g_script && g_script->CompileScript && g_script->CallFunction && g_commands?"true":"false")+
        ",\"scriptEntryExit\":"+std::string(g_scriptTraceInstalled?"true":"false")+
        ",\"scriptTraceFilters\":"+std::string(g_scriptTraceInstalled?"true":"false")+
        LiveCapabilities()+",\"runtimeNotificationAggregation\":true,\"lifecycle\":true,\"scriptErrors\":true,\"temporaryActorValue\":false,\"damageActorValue\":false,\"opcodeTrace\":false,\"conditionTrace\":false,\"messageChoice\":false,\"combatAttribution\":false}"
        ",\"limitations\":[\"console return is acceptance, not outcome\",\"numeric state requires xNVSE 0.6.4.9 or newer\",\"full CTDA comparisons and UI-only message choices are not captured\",\"hit participants do not measure damage\"],"
        "\"sdkCommit\":\"0ccd23ad885ddae533c1790a3fc56cd073e38de3\""+GeneralCommandCoverage()+CombatLeaseHello()+DispatchDiagnostics()+ConditionPatchDiagnostics()+DamageCapabilityFields()+CriticalCapabilityFields()+
        ",\"runtimeNotificationPolicy\":"+NotificationPolicyJson();
}
PSECURITY_DESCRIPTOR UserOnlySecurity() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token)) return nullptr;
    DWORD length = 0; GetTokenInformation(token,TokenUser,nullptr,0,&length);
    std::vector<std::uint8_t> storage(length);
    if (!GetTokenInformation(token,TokenUser,storage.data(),length,&length)) { CloseHandle(token); return nullptr; }
    CloseHandle(token);
    wchar_t* sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(storage.data())->User.Sid,&sid)) return nullptr;
    std::wstring sddl = L"D:P(A;;GA;;;"; sddl += sid; sddl += L")"; LocalFree(sid);
    PSECURITY_DESCRIPTOR result = nullptr;
    ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&result,nullptr);
    return result;
}
bool WriteEvent(HANDLE pipe, const EventRecord& event) {
    if (event.json.size() > MaxPayload) return false;
    std::vector<std::uint8_t> bytes(24+event.json.size(),0);
    const std::uint16_t version = Protocol, type = Event;
    const UInt32 length = static_cast<UInt32>(event.json.size());
    memcpy(bytes.data(),&Magic,4); memcpy(bytes.data()+4,&version,2); memcpy(bytes.data()+6,&type,2);
    memcpy(bytes.data()+8,&event.id,8); memcpy(bytes.data()+16,&length,4);
    memcpy(bytes.data()+24,event.json.data(),length);
    DWORD written = 0;
    return WriteFile(pipe,bytes.data(),static_cast<DWORD>(bytes.size()),&written,nullptr) && written == bytes.size();
}
DWORD WINAPI Server(void*) {
    auto security = UserOnlySecurity();
    if (!security) return 1; // Never fall back to a broader pipe ACL.
    SECURITY_ATTRIBUTES attrs{sizeof(SECURITY_ATTRIBUTES),security,FALSE};
    const auto name = L"\\\\.\\pipe\\BMT.Runtime."+std::to_wstring(GetCurrentProcessId());
    while (g_running) {
        g_pipe = CreateNamedPipeW(name.c_str(),PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,1,131072,131072,1000,&attrs);
        if (g_pipe == INVALID_HANDLE_VALUE) break;
        if (!ConnectNamedPipe(g_pipe,nullptr) && GetLastError() != ERROR_PIPE_CONNECTED) { CloseHandle(g_pipe); continue; }
        { std::lock_guard<std::mutex> lock(g_mutex);
          g_events.clear();
          // An empty reconnect must not erase why the previous connection's queue was cleared.
          if(!g_requests.empty())ClearRequestsLocked("connection-open",GetTickCount64(),g_connectionGeneration.load());
          g_sequence=0; g_dropped=0;g_notifications={};
          ++g_connectionGeneration;g_connected=true; }
        bool handshaken=false;
        while (g_running) {
            DWORD available=0; std::uint8_t header[24]{}; DWORD peeked=0;
            if (!PeekNamedPipe(g_pipe,header,24,&peeked,&available,nullptr)) break;
            if (available >= 24) {
                UInt32 magic=0,length=0,reserved=0; std::uint16_t version=0,kind=0; std::uint64_t id=0;
                memcpy(&magic,header,4); memcpy(&version,header+4,2); memcpy(&kind,header+6,2);
                memcpy(&id,header+8,8); memcpy(&length,header+16,4); memcpy(&reserved,header+20,4);
                if (magic!=Magic || version!=Protocol || reserved || length>MaxPayload || id==0) break;
                if (available >= 24+length) {
                    std::vector<char> packet(24+length); DWORD read=0;
                    if (!ReadFile(g_pipe,packet.data(),static_cast<DWORD>(packet.size()),&read,nullptr) || read!=packet.size()) break;
                    Request request{kind,id,std::string(packet.data()+24,length)};
                    RecordReceivedRequest(request);
                    if (kind==1) { handshaken=true; Emit("hello",id,Capabilities()); }
                    else if (!handshaken) { Emit("error",id,",\"error\":\"handshake-required\""); }
                    else if (kind==6) Emit("pong",id,DispatchDiagnostics());
                    else {
                        if(kind==31 && LivePriorityStop(request)) {
                            bmt_live_json::Value stop;std::string stopError;
                            const bool clearQueued=bmt_live_json::Parse(request.payload,stop,stopError) &&
                                LiveString(stop,"jobId").empty() && LiveString(stop,"name").empty();
                            std::vector<std::uint64_t> cancelled;
                            {
                                std::lock_guard<std::mutex> lock(g_mutex);
                                for(auto it=g_requests.begin();it!=g_requests.end();) {
                                    if(clearQueued && it->kind==30) {cancelled.push_back(it->id);it=g_requests.erase(it);}
                                    else ++it;
                                }
                                request.queuedMilliseconds=GetTickCount64();request.queuedObserved=true;
                                g_lastQueued=DispatchSampleLocked(request,request.queuedMilliseconds);
                                g_requests.push_front(std::move(request));
                            }
                            for(const auto cancelledId:cancelled)
                                Emit("live-result",cancelledId,",\"status\":\"cancelled\",\"error\":\"cancelled-before-dispatch\"");
                        } else {
                        if(kind==4 || kind==5)CombatLeaseRequestCleanup(kind==5?"cancel":"stop",g_connectionGeneration.load());
                        const bool leaseStop=kind==4 && CombatLeaseActive();
                        const bool full=!QueueRequest(std::move(request),kind==5 || leaseStop);
                        if (full) {CombatLeaseRequestCleanup("queue-full",g_connectionGeneration.load());Emit("error",id,",\"error\":\"action-queue-full\"");}
                        }
                    }
                }
            }
            EventRecord event{}; bool present=false;
            { std::lock_guard<std::mutex> lock(g_mutex);
              if (!g_events.empty()) { event=std::move(g_events.front()); g_events.pop_front(); present=true; } }
            if (present && !WriteEvent(g_pipe,event)) break;
            if (!present) Sleep(5);
        }
        const auto disconnectedGeneration=g_connectionGeneration.load();
        LiveDisconnect();
        CombatLeaseRequestCleanup("disconnect",disconnectedGeneration);
        { std::lock_guard<std::mutex> lock(g_mutex);
          g_connected=false;g_capture=false;++g_captureGeneration;++g_connectionGeneration;g_notifications.initialized=false;
          ClearRequestsLocked("disconnect",GetTickCount64(),disconnectedGeneration);g_events.clear(); }
        DisconnectNamedPipe(g_pipe); CloseHandle(g_pipe); g_pipe=INVALID_HANDLE_VALUE;
    }
    LocalFree(security); return 0;
}
}
extern "C" {
__declspec(dllexport) bool NVSEPlugin_Query(const NVSEInterface* nvse, PluginInfo* info) {
    if (info) { info->infoVersion=1; info->name="BethesdaRuntimeBridge"; info->version=1; }
    return nvse && !nvse->isEditor && !nvse->isNogore && nvse->runtimeVersion==RuntimeVersion && nvse->nvseVersion>=6;
}
__declspec(dllexport) bool NVSEPlugin_Load(const NVSEInterface* nvse) {
    if (!nvse || nvse->runtimeVersion!=RuntimeVersion) return false;
    g_nvseVersion=nvse->nvseVersion;
    g_executableSha256=ExecutableHash();
    g_pcLayoutVerified=VerifiedPcExecutable(g_executableSha256);
    g_console=static_cast<NVSEConsoleInterface*>(nvse->QueryInterface(1));
    auto messaging=static_cast<NVSEMessagingInterface*>(nvse->QueryInterface(2));
    auto commands=static_cast<NVSECommandTableInterface*>(nvse->QueryInterface(3));
    if (commands && commands->version>=1 && commands->GetByName) g_commands=commands;
    g_getBaseObject=commands && commands->GetByName && commands->GetByName("GetBaseObject");
    if (!g_console || g_console->version<2 || !messaging || messaging->version<4) return false;
    if (NewEnoughScriptApi()) {
        g_eventManager=static_cast<NVSEEventManagerInterface*>(nvse->QueryInterface(8));
        auto script=static_cast<NVSEScriptInterface*>(nvse->QueryInterface(6));
        if (script && script->CompileExpression && script->CallFunction) { g_script=script; g_numericEnabled=true; }
    }
    if (!messaging->RegisterListener(nvse->GetPluginHandle(),"NVSE",OnMessage)) return false;
    LiveInitialize(nvse);
    bmt_live_input::Install();
    HANDLE thread=CreateThread(nullptr,0,Server,nullptr,0,nullptr);
    if (!thread) return false;
    CloseHandle(thread); return true;
}
}
