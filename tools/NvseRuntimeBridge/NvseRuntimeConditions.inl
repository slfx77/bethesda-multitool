// Native32 owner-list pilot DRAFT. Included after Operations; not integrated or built.
// Six exact spans require a separately reviewed installer; OR code remains draft-gated.
// RuntimeConditionPilot.h must be included outside the bridge's anonymous namespace.
#include "RuntimeConditionPilotProfile.h"
namespace condition_pilot = bmt::ctda::pilot;
namespace condition_quest = bmt::ctda::quest;
using ConditionListFunction = bool (__thiscall*)(void*, TESObjectREFR*, TESObjectREFR*, bool*, bool);
using ConditionItemFunction = bool (__thiscall*)(void*, TESObjectREFR*, TESObjectREFR*, bool*);
using ConditionCompareFunction = bool (__cdecl*)(UInt32, float, float);
constexpr auto ConditionListAddress = 0x00680C60u;
constexpr auto ConditionItemAddress = 0x00681600u;
constexpr auto ConditionCompareAddress = 0x00681050u;
constexpr std::array<UInt32, 4> ConditionCallSites{{0x00680D68,0x00680DC9,0x00680E1F,0x006817B4}};
// Intentionally false in this unintegrated draft. Enabling requires reviewed transaction,
// x86 compiled forwarding/FP fixtures, and a new frozen runtime. No runtime payload flips it.
constexpr bool ConditionPilotReviewed = true;
std::atomic<bool> g_conditionCallsInstalled{false};
std::atomic<std::uint64_t> g_conditionInvocationCounter{0};

namespace condition_frame=bmt::ctda::frame;
constexpr std::array<UInt32,2> ConditionFrameSites{{0x00680EC4,0x00680F42}};
constexpr std::array<std::uint8_t,6> ConditionFrameOriginal{{0x0F,0xB6,0x55,0xF7,0x85,0xD2}};
#ifdef BMT_CONDITION_FRAME_TESTS
constexpr bool ConditionFramesReviewed=true; // Fixture process only: never installs game hooks.
UInt32 ConditionFrameResume0=0x00680ECA,ConditionFrameResume1=0x00680F48;
#else
constexpr bool ConditionFramesReviewed=true; // Root-reviewed application only; no runtime switch.
const UInt32 ConditionFrameResume0=0x00680ECA,ConditionFrameResume1=0x00680F48;
#endif
const UInt32 ConditionFrameNeutralMxcsr=0x1F80;

bool ConditionOwnsFrames() noexcept;
std::array<std::uint8_t,6> ConditionOwnedFrame(size_t) noexcept;

struct ConditionOriginalEval {
    UInt32 address = 0, moduleBase = 0;
    std::array<std::uint8_t,64> prefix{};
};

struct ConditionInvocation {
    condition_pilot::Epoch epoch;
    std::uint64_t invocation = 0, droppedBefore = 0;
    UInt32 commandTable = 0;
    ConditionOriginalEval originalEval;
    TypedForm owner, subject, target, playerBase, docBase;
    condition_pilot::SourceBindings bindings;
    bool questV2 = false;
    condition_quest::Binding questBindings;
    bmt::ctda::Snapshot before;
    condition_pilot::Buffer observations;
    condition_frame::Anchor frameAnchor;
    condition_frame::Buffer frames;
    condition_pilot::ItemSample* item = nullptr;
};
thread_local ConditionInvocation* g_conditionInvocation = nullptr;

bool ConditionRead(void*, UInt32 address, void* output, size_t size) noexcept {
    return CommandReadBytes(address, output, size);
}
condition_pilot::Epoch ConditionCurrentEpoch() noexcept {
    return {g_currentRequest, g_captureGeneration.load(), g_connectionGeneration.load(),
        g_gameLoadEpoch.load(), GetCurrentThreadId()};
}
bool ConditionAlive(const ConditionInvocation& invocation) noexcept {
    return g_capture && g_connected && invocation.epoch.capture &&
        invocation.epoch == ConditionCurrentEpoch();
}
bool ConditionSameForm(const TypedForm& prior) {
    TypedForm now;
    return TypedRefresh(prior, now) && now.pointer == prior.pointer && now.baseType == prior.baseType;
}
// Checked map-backed form reads only. The v2 subset admits ACHR/NPC_ identities;
// no CREA, temporary forms, plugin callbacks or arbitrary reference normalization.
bool ConditionQuestResolveUsing(UInt32 address, bool actor, condition_pilot::ActorIdentity& result,
    void* (*lookup)(UInt32)=LookupRuntimeForm) noexcept {
    result={};
    if(address<0x10000 || (address&3) || address>UINT32_MAX-0x24)return false;
    std::array<std::uint8_t,16> before{},after{};
    if(!ConditionRead(nullptr,address,before.data(),before.size()))return false;
    const auto id=bmt::ctda::U32(before.data()+12);
    if(!id || (id>>24)==0xFF || (bmt::ctda::U32(before.data()+8)&0x4020) ||
        before[4]!=(actor?0x3B:0x2A) || lookup(id)!=reinterpret_cast<void*>(address))return false;
    result.form={address,id,before[4]};
    UInt32 base=0,baseAfter=0;
    if(actor) {
        condition_pilot::ActorIdentity baseIdentity;
        if(!ReadRuntime(address+0x20,base) || !ConditionQuestResolveUsing(base,false,baseIdentity,lookup))return false;
        result.base=baseIdentity.form;
    }
    return ConditionRead(nullptr,address,after.data(),after.size()) && before==after &&
        lookup(id)==reinterpret_cast<void*>(address) &&
        (!actor || (ReadRuntime(address+0x20,baseAfter) && baseAfter==base));
}
bool ConditionQuestResolve(void*, UInt32 address, bool actor, condition_pilot::ActorIdentity& result) noexcept {
    return ConditionQuestResolveUsing(address,actor,result);
}
bool ConditionQuestSame(const condition_pilot::ActorIdentity& prior,bool actor) noexcept {
    condition_pilot::ActorIdentity now;
    const auto same=[](const condition_pilot::FormIdentity& a,const condition_pilot::FormIdentity& b) {
        return a.address==b.address && a.id==b.id && a.type==b.type;
    };
    return ConditionQuestResolve(nullptr,prior.form.address,actor,now) && same(prior.form,now.form) &&
        (!actor || same(prior.base,now.base));
}
bool ConditionAllForms(const ConditionInvocation& invocation) {
    if(invocation.questV2) {
        const auto& binding=invocation.questBindings;
        if(binding.rejection!=condition_quest::Rejection::None || !ConditionSameForm(invocation.owner) ||
            !ConditionQuestSame(binding.subject,true) || !ConditionQuestSame(binding.target,true))return false;
        for(size_t i=0;i<binding.count;++i)
            if(!ConditionQuestSame(binding.rows[i].evaluatedActor,true) ||
                !ConditionQuestSame({binding.rows[i].parameter,{}},false))return false;
        return true;
    }
    UInt32 subjectBase = 0, targetBase = 0;
    return ConditionSameForm(invocation.owner) && ConditionSameForm(invocation.subject) &&
        ConditionSameForm(invocation.target) && ConditionSameForm(invocation.playerBase) &&
        ConditionSameForm(invocation.docBase) &&
        ReadRuntime(reinterpret_cast<UInt32>(invocation.subject.pointer)+0x20,subjectBase) && subjectBase == invocation.bindings.playerIdentity.base.address &&
        ReadRuntime(reinterpret_cast<UInt32>(invocation.target.pointer)+0x20,targetBase) && targetBase == invocation.bindings.targetIdentity.base.address;
}
condition_pilot::FormIdentity ConditionFormIdentity(const TypedForm& form) noexcept {
    return {reinterpret_cast<UInt32>(form.pointer), form.id, form.type};
}
bool ConditionParse(const Request& request, TypedIdentity& owner, TypedIdentity& subject, TypedIdentity& target, bool* questV2=nullptr) {
    if(questV2)*questV2=false;
    if (request.kind != 22 || request.payload.empty() || request.payload.size() > 1024 ||
        request.payload.back() == '\t' || !std::all_of(request.payload.begin(), request.payload.end(),
            [](unsigned char c) { return c == '\t' || (c >= 32 && c < 127); })) return false;
    std::vector<std::string> fields; std::istringstream input(request.payload); std::string field;
    while (std::getline(input, field, '\t')) fields.push_back(field);
    if(fields.size()==7 && fields[0]=="quest-conditions-v2") {
        if(!questV2 || !TypedPair(fields[1],fields[2],owner) || owner.plugin=="@player" ||
            !TypedPair(fields[3],fields[4],subject) || !TypedPair(fields[5],fields[6],target))return false;
        *questV2=true;return true;
    }
    return fields.size() == 7 && fields[0] == "quest-conditions-v1" &&
        TypedPair(fields[1], fields[2], owner) && owner.plugin == "BMTConditionControl.esp" && owner.localId == 0x800 &&
        TypedPair(fields[3], fields[4], subject) && subject.plugin == "@player" &&
        TypedPair(fields[5], fields[6], target) &&
        ((target.plugin == "@player" && target.localId == 0x14) ||
         (target.plugin == "FalloutNV.esm" && target.localId == 0x104C0F));
}
bool ConditionReadTable(UInt32& table) {
    // This calls the current SDK interface; PC029's installation-derived table basis
    // remains historical evidence only. Never dereference Start as though it were data.
    if (!g_commands || !g_commands->Start || !g_commands->End || !g_commands->GetByOpcode || !g_commands->GetByName)
        return false;
    auto first = g_commands->Start(); auto last = g_commands->End();
    table = reinterpret_cast<UInt32>(first);
    const auto end = reinterpret_cast<UInt32>(last);
    if (!table || end <= table || (end - table) % sizeof(CommandInfo) ||
        (end - table) / sizeof(CommandInfo) > 4096) return false;
    constexpr UInt32 index = condition_pilot::GetIsIdOpcode - 0x1000;
    if (index >= (end - table) / sizeof(CommandInfo)) return false;
    auto entry = reinterpret_cast<CommandInfo*>(table + index * sizeof(CommandInfo));
    if (g_commands->GetByOpcode(condition_pilot::GetIsIdOpcode) != entry ||
        g_commands->GetByName("GetIsID") != entry || g_commandHooks[10].entry != entry ||
        g_commandHooks[10].opcode != condition_pilot::GetIsIdOpcode ||
        !g_commandHooks[10].eval || !ExecutableAddress(reinterpret_cast<void*>(g_commandHooks[10].eval))) return false;
    CommandInfo metadata{}, after{}; ParamInfo parameter{}; std::string name, type;
    if (!ConditionRead(nullptr, reinterpret_cast<UInt32>(entry), &metadata, sizeof(metadata)) ||
        metadata.opcode != condition_pilot::GetIsIdOpcode || metadata.needsParent != 1 || metadata.numParams != 1 ||
        metadata.eval != &EvalHook<10> ||
        !ReadCommandName(metadata.longName, name) || name != "GetIsID" ||
        !ConditionRead(nullptr, reinterpret_cast<UInt32>(metadata.params), &parameter, sizeof(parameter)) ||
        parameter.typeID != 21 || parameter.isOptional != 0 ||
        !ReadCommandName(parameter.typeStr, type) || type != "ObjectID") return false;
    ParamInfo parameterAfter{};
    return ConditionRead(nullptr, reinterpret_cast<UInt32>(entry), &after, sizeof(after)) &&
        !memcmp(&metadata, &after, sizeof(metadata)) &&
        ConditionRead(nullptr, reinterpret_cast<UInt32>(metadata.params), &parameterAfter, sizeof(parameterAfter)) &&
        !memcmp(&parameter, &parameterAfter, sizeof(parameter)) &&
        g_commands->Start() == first && g_commands->End() == last;
}
bool ConditionOriginalEvalRead(ConditionOriginalEval& result) noexcept {
    result = {};
    const auto original = g_commandHooks[10].eval;
    HMODULE module = nullptr;
    if (!original || !ExecutableAddress(reinterpret_cast<void*>(original)) ||
        !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(original), &module) || module != GetModuleHandleW(nullptr)) return false;
    result.address = reinterpret_cast<UInt32>(original); result.moduleBase = reinterpret_cast<UInt32>(module);
    if (!ConditionRead(nullptr,result.address,result.prefix.data(),result.prefix.size())) return false;
    std::array<std::uint8_t,64> again{};
    return g_commandHooks[10].eval == original && ConditionRead(nullptr,result.address,again.data(),again.size()) && again == result.prefix;
}
bool ConditionSameOriginalEval(const ConditionOriginalEval& before) noexcept {
    ConditionOriginalEval after;
    return ConditionOriginalEvalRead(after) && before.address == after.address &&
        before.moduleBase == after.moduleBase && before.prefix == after.prefix;
}

template<size_t Site> bool __fastcall ConditionItemHook(void*, void*, TESObjectREFR*, TESObjectREFR*, bool*);
bool __cdecl ConditionComparisonHook(UInt32, float, float);
UInt32 ConditionWrapper(size_t index) noexcept {
    const std::array<UInt32, 4> wrappers{{reinterpret_cast<UInt32>(&ConditionItemHook<0>),
        reinterpret_cast<UInt32>(&ConditionItemHook<1>), reinterpret_cast<UInt32>(&ConditionItemHook<2>),
        reinterpret_cast<UInt32>(&ConditionComparisonHook)}};
    return wrappers[index];
}
std::array<std::uint8_t, 5> ConditionOwnedCall(size_t index) noexcept {
    std::array<std::uint8_t, 5> bytes{{0xE8,0,0,0,0}};
    // 32-bit relative displacement wraps modulo 2^32 by x86 CALL semantics.
    const UInt32 displacement = ConditionWrapper(index) - (ConditionCallSites[index] + 5);
    memcpy(bytes.data() + 1, &displacement, 4); return bytes;
}
bool ConditionOwnsCalls() noexcept {
    for (size_t index = 0; index < ConditionCallSites.size(); ++index) {
        std::array<std::uint8_t, 5> bytes{};
        if (!ConditionRead(nullptr, ConditionCallSites[index], bytes.data(), bytes.size()) ||
            bytes != ConditionOwnedCall(index)) return false;
    }
    return ConditionOwnsFrames();
}
bool ConditionCodeMatches(UInt32 table, bool installed) {
    for (const auto& relocation : ConditionRelocations) {
        UInt32 value = 0;
        if (!ReadRuntime(relocation.address, value) || value != table + relocation.fieldOffset) return false;
    }
    for (const auto& region : ConditionCodeRegions) {
        const size_t count = strlen(region.hex) / 2;
        if (!count || count > 1024) return false;
        std::array<std::uint8_t, 1024> expected{}, actual{};
        const auto nibble = [](char c) { return static_cast<std::uint8_t>(c <= '9' ? c-'0' : c-'a'+10); };
        for (size_t index = 0; index < count; ++index)
            expected[index] = (nibble(region.hex[index*2]) << 4) | nibble(region.hex[index*2+1]);
        for (const auto& relocation : ConditionRelocations)
            if (relocation.address >= region.address && relocation.address - region.address + 4 <= count) {
                const UInt32 value = table + relocation.fieldOffset;
                memcpy(expected.data() + relocation.address - region.address, &value, 4);
            }
        if (installed) for (size_t site = 0; site < ConditionCallSites.size(); ++site)
            if (ConditionCallSites[site] >= region.address && ConditionCallSites[site] - region.address + 5 <= count) {
                const auto bytes = ConditionOwnedCall(site);
                memcpy(expected.data() + ConditionCallSites[site] - region.address, bytes.data(), bytes.size());
            }
        if (installed) for(size_t site=0;site<ConditionFrameSites.size();++site)
            if(ConditionFrameSites[site]>=region.address && ConditionFrameSites[site]-region.address+6<=count) {
                const auto bytes=ConditionOwnedFrame(site);
                memcpy(expected.data()+ConditionFrameSites[site]-region.address,bytes.data(),bytes.size());
            }
        if (!ConditionRead(nullptr, region.address, actual.data(), count) ||
            memcmp(actual.data(), expected.data(), count)) return false;
    }
    return !installed || ConditionOwnsCalls();
}
bool ConditionPilotAvailable() noexcept {
    return ConditionPilotReviewed && ConditionFramesReviewed && g_pcLayoutVerified && g_conditionCallsInstalled;
}
// Called only by a separately reviewed game-thread installer after all four writes.
// Failure never claims hook ownership. This function itself cannot install anything.
bool ConditionConfirmInstallation() {
    UInt32 table = 0;
    const bool valid = ConditionPilotReviewed && g_pcLayoutVerified && ConditionReadTable(table) && ConditionCodeMatches(table, true);
    g_conditionCallsInstalled = valid; return valid;
}

UInt32 ConditionSourceRow(const ConditionInvocation& scope, void* item) noexcept {
    UInt32 index = 0;
    for (const auto& node : scope.before.nodes) if (node.hasItem) {
        if (node.itemAddress == reinterpret_cast<UInt32>(item)) return index;
        ++index;
    }
    return UINT32_MAX;
}
struct ConditionLastError {
    DWORD value;
    ~ConditionLastError() { SetLastError(value); }
};
template<size_t Site> __declspec(noinline) bool ConditionForwardItem(ConditionItemFunction original, void* item,
    TESObjectREFR* subject, TESObjectREFR* target, bool* auxiliary) {
    const DWORD incomingError = GetLastError();
    auto* scope = g_conditionInvocation;
    if (!scope || !ConditionAlive(*scope)) {
        SetLastError(incomingError); return original(item, subject, target, auxiliary);
    }
    ConditionLastError error{incomingError}; CommandFloatingGuard floating;
    condition_pilot::ItemSample* sample = nullptr;
    auto* previous = scope->item;
    if (previous) scope->observations.unexpected = true;
    else {
        sample = scope->observations.Begin(ConditionSourceRow(*scope, item));
        if (sample) {
            sample->site = ConditionCallSites[Site]; sample->item = reinterpret_cast<UInt32>(item);
            sample->subject = reinterpret_cast<UInt32>(subject); sample->target = reinterpret_cast<UInt32>(target);
            sample->auxiliary = reinterpret_cast<UInt32>(auxiliary);
            sample->rawBeforeReadable = ConditionRead(nullptr, sample->item, sample->before.data(), sample->before.size());
            sample->auxiliaryBeforeReadable = ConditionRead(nullptr, sample->auxiliary, &sample->auxiliaryBefore, 1);
        }
    }
    scope->item = sample;
    struct RestoreItem { ConditionInvocation* scope; condition_pilot::ItemSample* previous;
        ~RestoreItem() { scope->item = previous; } } restore{scope, previous};
    SetLastError(error.value); floating.BeforeOriginal();
    const bool result = original(item, subject, target, auxiliary); // Exactly once, original this and arguments.
    floating.AfterOriginal(); error.value = GetLastError();
    if (sample && ConditionAlive(*scope)) {
        sample->returned = true; sample->value = result;
        sample->rawAfterReadable = ConditionRead(nullptr, sample->item, sample->after.data(), sample->after.size());
        sample->auxiliaryAfterReadable = ConditionRead(nullptr, sample->auxiliary, &sample->auxiliaryAfter, 1);
    }
    return result;
}
template<size_t Site> __declspec(noinline) bool __fastcall ConditionItemHook(void* item, void*,
    TESObjectREFR* subject, TESObjectREFR* target, bool* auxiliary) {
    return ConditionForwardItem<Site>(reinterpret_cast<ConditionItemFunction>(ConditionItemAddress), item, subject, target, auxiliary);
}
__declspec(noinline) bool ConditionForwardComparison(ConditionCompareFunction original, UInt32 operation, float actual, float comparison) {
    const DWORD incomingError = GetLastError();
    auto* scope = g_conditionInvocation;
    if (!scope || !scope->item || !ConditionAlive(*scope)) {
        SetLastError(incomingError); return original(operation, actual, comparison);
    }
    ConditionLastError error{incomingError}; CommandFloatingGuard floating;
    auto* sample = &scope->item->comparison;
    if (sample->entered) { scope->observations.unexpected = true; sample = nullptr; }
    if (sample) {
        sample->entered = true; sample->operation = operation;
        memcpy(&sample->actualBits, &actual, 4); memcpy(&sample->comparisonBits, &comparison, 4);
    }
    SetLastError(error.value); floating.BeforeOriginal();
    const bool result = original(operation, actual, comparison); // cdecl caller retains stack cleanup.
    floating.AfterOriginal(); error.value = GetLastError();
    if (sample && ConditionAlive(*scope)) { sample->returned = true; sample->value = result; }
    return result;
}
__declspec(noinline) bool __cdecl ConditionComparisonHook(UInt32 operation, float actual, float comparison) {
    return ConditionForwardComparison(reinterpret_cast<ConditionCompareFunction>(ConditionCompareAddress), operation, actual, comparison);
}

// Callback token is captured BEFORE the original eval, then checked on return. The
// containing EvalHook already provides neutral FP observation and preserves LastError.
ConditionEvalToken ConditionEvalEnter(size_t index, TESObjectREFR* subject, void* first, void* second) noexcept {
    auto* scope = g_conditionInvocation;
    if (index != 10 || !scope || !scope->item || !ConditionAlive(*scope)) return {};
    auto& eval = scope->item->eval;
    if (eval.entered) { scope->observations.unexpected = true; return {}; }
    eval.entered = true; eval.subject = reinterpret_cast<UInt32>(subject);
    eval.parameter1 = reinterpret_cast<UInt32>(first); eval.parameter2 = reinterpret_cast<UInt32>(second);
    eval.identityBefore = ReadFormIdentity(subject, eval.subjectId, eval.subjectType) &&
        ReadRuntime(eval.subject + 0x20, eval.subjectBaseAddress) &&
        ReadFormIdentity(reinterpret_cast<void*>(eval.subjectBaseAddress), eval.subjectBaseId, eval.subjectBaseType) &&
        ReadFormIdentity(first, eval.parameter1Id, eval.parameter1Type);
    return {scope, scope->invocation, scope->item->callId};
}
void ConditionEvalReturn(ConditionEvalToken token, bool returned, double* result) noexcept {
    auto* scope = g_conditionInvocation;
    if (!scope || scope != token.scope || scope->invocation != token.invocation || !ConditionAlive(*scope) ||
        !scope->item || scope->item->callId != token.itemCall) return;
    auto& eval = scope->item->eval;
    eval.returned = true; eval.handlerReturned = returned;
    eval.resultReadable = ConditionRead(nullptr, reinterpret_cast<UInt32>(result), &eval.resultBits, sizeof(eval.resultBits));
    UInt32 id = 0, base = 0, baseId = 0, parameterId = 0; std::uint8_t type = 0, baseType = 0, parameterType = 0;
    eval.identityStable = eval.identityBefore &&
        ReadFormIdentity(reinterpret_cast<void*>(eval.subject), id, type) && id == eval.subjectId && type == eval.subjectType &&
        ReadRuntime(eval.subject + 0x20, base) && base == eval.subjectBaseAddress &&
        ReadFormIdentity(reinterpret_cast<void*>(base), baseId, baseType) && baseId == eval.subjectBaseId && baseType == eval.subjectBaseType &&
        ReadFormIdentity(reinterpret_cast<void*>(eval.parameter1), parameterId, parameterType) &&
        parameterId == eval.parameter1Id && parameterType == eval.parameter1Type;
}

#include "NvseRuntimeConditionFrames.inl"

std::string ConditionHexWord(std::uint64_t value, size_t digits) {
    std::ostringstream text; text << std::hex << std::setw(static_cast<int>(digits)) << std::setfill('0') << value; return text.str();
}
std::string ConditionCommon(const ConditionInvocation& scope) {
    const auto source=scope.questV2 ?
        ",\"requestedOwnerPlugin\":"+Quote(scope.owner.requested.plugin)+",\"requestedOwnerLocalId\":"+std::to_string(scope.owner.requested.localId)+
        ",\"requestedSubjectPlugin\":"+Quote(scope.subject.requested.plugin)+",\"requestedSubjectLocalId\":"+std::to_string(scope.subject.requested.localId)+
        ",\"requestedTargetPlugin\":"+Quote(scope.target.requested.plugin)+",\"requestedTargetLocalId\":"+std::to_string(scope.target.requested.localId)+
        ",\"sourceAssociation\":\"runtime-QUST-list-only\",\"physicalSourceAssociation\":\"unavailable-requires-offline-hash-and-row-mapping\","
        "\"ownerLayout\":\"QUST+0x54\",\"ownerListGetterAddress\":6248288," :
        ",\"sourceOwnerPlugin\":\"BMTConditionControl.esp\",\"sourceOwnerLocalId\":2048,\"sourceAssociation\":\"fixed-fixture-resolved-fields\",";
    const auto actorContext=scope.questV2 ?
        ",\"subjectAddress\":"+std::to_string(scope.questBindings.subject.form.address)+
        ",\"subjectBaseFormId\":"+std::to_string(scope.questBindings.subject.base.id)+
        ",\"subjectBaseAddress\":"+std::to_string(scope.questBindings.subject.base.address)+
        ",\"targetAddress\":"+std::to_string(scope.questBindings.target.form.address)+
        ",\"targetBaseFormId\":"+std::to_string(scope.questBindings.target.base.id)+
        ",\"targetBaseAddress\":"+std::to_string(scope.questBindings.target.base.address) : "";
    return ",\"schemaVersion\":"+std::string(scope.questV2?"2":"1")+",\"invocationId\":" + std::to_string(scope.invocation) +
        ",\"parentInvocationId\":null,\"captureGeneration\":" + std::to_string(scope.epoch.capture) +
        ",\"connectionGeneration\":" + std::to_string(scope.epoch.connection) +
        ",\"loadEpoch\":" + std::to_string(scope.epoch.load) + ",\"threadId\":" + std::to_string(scope.epoch.thread) +
        ",\"ownerFormId\":" + std::to_string(scope.owner.id) + ",\"ownerAddress\":" + std::to_string(reinterpret_cast<UInt32>(scope.owner.pointer)) +
        ",\"headAddress\":" + std::to_string(scope.before.headAddress) +
        ",\"subjectFormId\":" + std::to_string(scope.subject.id) + ",\"targetFormId\":" + std::to_string(scope.target.id) +
        actorContext + source + "\"pluginFileHashStatus\":\"requires-offline-profile-binding\",\"commandTableBasis\":\"current-sdk-interface-and-memory\",\"commandTableAddress\":" + std::to_string(scope.commandTable) +
        ",\"originalEval\":{\"status\":\"observed\",\"address\":" + std::to_string(scope.originalEval.address) +
        ",\"moduleBase\":" + std::to_string(scope.originalEval.moduleBase) +
        ",\"moduleIdentity\":\"verified-process-main-executable\",\"moduleFileSha256\":" + Quote(g_executableSha256) +
        ",\"hashBasis\":\"bridge-executable-profile-initialization\",\"prefixHex\":" + Quote(CommandHex(scope.originalEval.prefix.data(),scope.originalEval.prefix.size())) +
        ",\"prefixBytes\":64,\"extent\":\"prefix-only\",\"source\":\"saved-g_commandHooks-GetIsID-eval\"}";
}
void ConditionEmitRows(const ConditionInvocation& scope, const std::string& common) {
    for (size_t index = 0; index < scope.observations.count; ++index) {
        const auto& item = scope.observations.items[index];
        const auto fields = common + ",\"itemCallId\":" + std::to_string(item.callId) + ",\"sourceRow\":" +
            (item.sourceRow == UINT32_MAX ? "null" : std::to_string(item.sourceRow)) +
            ",\"itemAddress\":" + std::to_string(item.item) + ",\"callSite\":" + std::to_string(item.site) +
            ",\"serializationDelayed\":true,\"runtimeItemBeforeHex\":" +
            (item.rawBeforeReadable ? Quote(CommandHex(item.before.data(), item.before.size())) : "null") +
            ",\"runtimeItemAfterHex\":" + (item.rawAfterReadable ? Quote(CommandHex(item.after.data(), item.after.size())) : "null");
        if (item.eval.entered) {
            const auto& eval = item.eval;
            Emit("condition-owner-function", scope.epoch.request, fields + ",\"function\":\"GetIsID\",\"opcode\":4168,\"engineTargetAddress\":" +
                std::to_string(eval.subject) + ",\"engineTargetFormId\":" + std::to_string(eval.subjectId) +
                ",\"engineTargetBaseFormId\":" + std::to_string(eval.subjectBaseId) +
                ",\"engineTargetFormType\":" + std::to_string(eval.subjectType) +
                ",\"engineTargetBaseAddress\":" + std::to_string(eval.subjectBaseAddress) +
                ",\"engineTargetBaseFormType\":" + std::to_string(eval.subjectBaseType) +
                ",\"parameter1Raw\":" + std::to_string(eval.parameter1) + ",\"parameter1FormId\":" + std::to_string(eval.parameter1Id) +
                ",\"parameter1FormType\":" + std::to_string(eval.parameter1Type) +
                ",\"parameter2Raw\":" + std::to_string(eval.parameter2) + ",\"identityStable\":" + (eval.identityStable ? "true" : "false") +
                ",\"handlerReturned\":" + (eval.returned ? (eval.handlerReturned ? "true" : "false") : "null") +
                ",\"resultBits\":" + (eval.resultReadable ? Quote(ConditionHexWord(eval.resultBits,16)) : "null"), scope.epoch.capture);
        }
        if (item.comparison.entered) {
            const auto& comparison = item.comparison;
            Emit("condition-comparison", scope.epoch.request, fields + ",\"operator\":" + std::to_string(comparison.operation) +
                ",\"actualFloat32Bits\":" + Quote(ConditionHexWord(comparison.actualBits,8)) +
                ",\"comparisonFloat32Bits\":" + Quote(ConditionHexWord(comparison.comparisonBits,8)) +
                ",\"nativeResult\":" + (comparison.returned ? (comparison.value ? "true" : "false") : "null"), scope.epoch.capture);
        }
        Emit("condition-item-exit", scope.epoch.request, fields + ",\"nativeResult\":" +
            (item.returned ? (item.value ? "true" : "false") : "null") +
            ",\"auxiliaryBeforeRaw\":" + (item.auxiliaryBeforeReadable ? std::to_string(item.auxiliaryBefore) : "null") +
            ",\"auxiliaryAfterRaw\":" + (item.auxiliaryAfterReadable ? std::to_string(item.auxiliaryAfter) : "null"), scope.epoch.capture);
    }
}
std::string ConditionQuestFormJson(const condition_pilot::FormIdentity& form) {
    return "{\"address\":"+std::to_string(form.address)+",\"formId\":"+std::to_string(form.id)+
        ",\"formType\":"+std::to_string(form.type)+"}";
}
void ConditionEmitSourceRows(const ConditionInvocation& scope,const bmt::ctda::Snapshot& after,
    const std::string& common,bool stable) {
    if(!scope.questV2)return;
    for(size_t index=0;index<scope.questBindings.count;++index) {
        const auto& row=scope.questBindings.rows[index];
        const auto before=std::find_if(scope.before.nodes.begin(),scope.before.nodes.end(),
            [&](const bmt::ctda::Node& node){return node.address==row.node && node.itemAddress==row.item;});
        const auto repeated=std::find_if(after.nodes.begin(),after.nodes.end(),
            [&](const bmt::ctda::Node& node){return node.hasItem && node.address==row.node && node.itemAddress==row.item;});
        Emit("condition-source-row",scope.epoch.request,common+",\"sourceRow\":"+std::to_string(index)+
            ",\"nodeAddress\":"+std::to_string(row.node)+",\"itemAddress\":"+std::to_string(row.item)+
            ",\"nodeBeforeHex\":"+Quote(CommandHex(before->raw.data(),before->raw.size()))+
            ",\"runtimeItemBeforeHex\":"+Quote(CommandHex(row.source.raw.data(),row.source.raw.size()))+
            ",\"runtimeItemAfterHex\":"+(repeated==after.nodes.end()?"null":Quote(CommandHex(repeated->item.raw.data(),repeated->item.raw.size())))+
            ",\"nodeAfterHex\":"+(repeated==after.nodes.end()?"null":Quote(CommandHex(repeated->raw.data(),repeated->raw.size())))+
            ",\"ownerHeaderBeforeHex\":"+Quote(CommandHex(scope.before.ownerHeader.data(),scope.before.ownerHeader.size()))+
            ",\"ownerSourceStable\":"+(stable?"true":"false")+",\"serializedAfterEvaluation\":true"+
            ",\"runOn\":"+std::to_string(row.source.runOn)+",\"resolvedActor\":"+ConditionQuestFormJson(row.evaluatedActor.form)+
            ",\"resolvedActorBase\":"+ConditionQuestFormJson(row.evaluatedActor.base)+
            ",\"resolvedParameter1\":"+ConditionQuestFormJson(row.parameter),scope.epoch.capture);
    }
}
void ConditionOwnerRequest(const Request& request) {
    ConditionLastError error{GetLastError()}; CommandFloatingGuard floating;
    const auto generation = g_captureGeneration.load();
    const auto fail = [&](const char* reason) { Emit("error", request.id, ",\"error\":" + Quote(reason), generation); };
    if (!ConditionPilotAvailable()) { fail("owner-condition-pilot-not-admitted"); return; }
    if (!g_capture || !g_connected || !g_loadedGameObserved || !request.id ||
        request.originConnectionGeneration != g_connectionGeneration.load() ||
        request.captureGenerationAtReceipt != generation || g_conditionInvocation) { fail("owner-condition-lifecycle-unavailable"); return; }
    TypedIdentity owner, subject, target; bool questV2=false;
    if (!ConditionParse(request, owner, subject, target, &questV2)) { fail("owner-condition-payload-unsupported"); return; }
    ConditionInvocation scope; scope.epoch = ConditionCurrentEpoch(); scope.invocation = ++g_conditionInvocationCounter; scope.questV2=questV2;
    std::string reason;
    if(questV2) {
        if(!VerifyRuntimeFormMap() || !TypedResolve(owner,scope.owner,reason) || scope.owner.type!=0x47 ||
            !TypedResolve(subject,scope.subject,reason) || !TypedResolve(target,scope.target,reason)) {
            fail("quest-condition-form-identity-unavailable");return;
        }
        condition_pilot::ActorIdentity subjectIdentity,targetIdentity;
        if(!ConditionQuestResolve(nullptr,reinterpret_cast<UInt32>(scope.subject.pointer),true,subjectIdentity) ||
            !ConditionQuestResolve(nullptr,reinterpret_cast<UInt32>(scope.target.pointer),true,targetIdentity) ||
            subjectIdentity.form.id!=scope.subject.id || subjectIdentity.base.id!=scope.subject.baseId ||
            targetIdentity.form.id!=scope.target.id || targetIdentity.base.id!=scope.target.baseId) {
            fail("quest-condition-actor-layout-unsupported-or-unavailable");return;
        }
        scope.before=bmt::ctda::ReadOwnerList(ConditionRead,nullptr,
            {bmt::ctda::OwnerKind::Quest,reinterpret_cast<UInt32>(scope.owner.pointer),scope.owner.id},{8,4096});
        scope.questBindings=condition_quest::Admit(scope.before,subjectIdentity,targetIdentity,ConditionQuestResolve,nullptr);
        if(scope.questBindings.rejection!=condition_quest::Rejection::None) {
            Emit("error",request.id,",\"error\":\"quest-condition-source-unsupported-or-unavailable\",\"reason\":"+
                Quote(condition_quest::Reason(scope.questBindings.rejection))+",\"sourceRow\":"+
                (scope.questBindings.failedRow==UINT32_MAX?"null":std::to_string(scope.questBindings.failedRow))+
                ",\"engineCallPerformed\":false",generation);return;
        }
        scope.bindings.player=subjectIdentity.form.address;scope.bindings.target=targetIdentity.form.address;
    } else {
    if (!VerifyRuntimeFormMap() || !TypedResolve(owner, scope.owner, reason) || scope.owner.type != 0x47 ||
        !TypedResolve(subject, scope.subject, reason) || scope.subject.id != 0x14 || scope.subject.baseId != 7 ||
        !TypedResolve(target, scope.target, reason) || !TypedIsActor(scope.target) ||
        !TypedResolve({"FalloutNV.esm",7}, scope.playerBase, reason) || scope.playerBase.type != 0x2A ||
        !TypedResolve({"FalloutNV.esm",0x104C0C}, scope.docBase, reason) || scope.docBase.type != 0x2A ||
        (scope.target.id != 0x14 && scope.target.baseId != scope.docBase.id)) { fail("owner-condition-form-identity-unavailable"); return; }
    scope.bindings = {reinterpret_cast<UInt32>(scope.subject.pointer), reinterpret_cast<UInt32>(scope.target.pointer),
        reinterpret_cast<UInt32>(scope.playerBase.pointer), reinterpret_cast<UInt32>(scope.docBase.pointer)};
    scope.bindings.playerBaseIdentity = ConditionFormIdentity(scope.playerBase);
    scope.bindings.docBaseIdentity = ConditionFormIdentity(scope.docBase);
    scope.bindings.playerIdentity = {ConditionFormIdentity(scope.subject),scope.bindings.playerBaseIdentity};
    scope.bindings.targetIdentity = {ConditionFormIdentity(scope.target),
        scope.target.id == scope.subject.id ? scope.bindings.playerBaseIdentity : scope.bindings.docBaseIdentity};
    scope.before = bmt::ctda::ReadOwnerList(ConditionRead, nullptr,
        {bmt::ctda::OwnerKind::Quest, reinterpret_cast<UInt32>(scope.owner.pointer), scope.owner.id}, {8,4096});
    if (condition_pilot::CheckFixture(scope.before, scope.bindings) != condition_pilot::Rejection::None) {
        fail("owner-condition-source-fields-unsupported-or-unstable"); return;
    }
    }
    if (!ConditionReadTable(scope.commandTable) || !ConditionOriginalEvalRead(scope.originalEval) || !ConditionCodeMatches(scope.commandTable,true) ||
        !ConditionAllForms(scope) || !ConditionAlive(scope)) { fail("owner-condition-live-proof-unavailable"); return; }
    // No locks are held across the engine call. The source list and all identities are
    // rechecked afterward; this is not a snapshot lock over arbitrary engine execution.
    const auto common = ConditionCommon(scope);
    { std::lock_guard<std::mutex> lock(g_mutex); scope.droppedBefore = g_dropped; }
    Emit("condition-list-entry", request.id, common + ",\"mode\":false,\"sourceRowCount\":"+
        std::to_string(scope.questV2?scope.questBindings.count:condition_pilot::RowCount)+
        ",\"orGroupStatus\":\"pending-native-frame-observation\"", generation);
    bool auxiliary = false, returned = false;
    scope.frameAnchor.stackLow=__readfsdword(8);scope.frameAnchor.stackHigh=__readfsdword(4);
    scope.frameAnchor.head=scope.before.headAddress;scope.frameAnchor.subject=scope.bindings.player;
    scope.frameAnchor.target=scope.bindings.target;scope.frameAnchor.auxiliary=reinterpret_cast<UInt32>(&auxiliary);
    {
        g_conditionInvocation = &scope;
        struct ResetScope { ~ResetScope() { g_conditionInvocation = nullptr; } } reset;
        SetLastError(error.value); floating.BeforeOriginal();
        returned = ConditionInvokeList(reinterpret_cast<ConditionListFunction>(ConditionListAddress),reinterpret_cast<void*>(scope.before.headAddress),
            static_cast<TESObjectREFR*>(scope.subject.pointer), static_cast<TESObjectREFR*>(scope.target.pointer), &auxiliary, false, &scope.frameAnchor);
        floating.AfterOriginal(); error.value = GetLastError();
    }
    if (!ConditionAlive(scope)) return; // Cannot publish a stale return into another capture.
    const auto after = bmt::ctda::ReadOwnerList(ConditionRead,nullptr,scope.before.owner,{8,4096});
    UInt32 tableAfter = 0;
    const bool stable = condition_pilot::SameSnapshot(scope.before,after) && ConditionAllForms(scope);
    bool itemCoverage = !scope.observations.overflow && !scope.observations.unexpected && scope.observations.count != 0;
    for (size_t index = 0; index < scope.observations.count; ++index) {
        const auto& sample = scope.observations.items[index];
        const auto source = std::find_if(scope.before.nodes.begin(), scope.before.nodes.end(),
            [&](const bmt::ctda::Node& node) { return node.hasItem && node.itemAddress == sample.item; });
        itemCoverage = itemCoverage && (scope.questV2 ? condition_quest::Complete(sample,scope.questBindings) :
            condition_pilot::CompleteObservedItem(sample, scope.bindings)) &&
            source != scope.before.nodes.end() && sample.before == source->item.raw;
    }
    const bool codeStable = ConditionReadTable(tableAfter) && tableAfter == scope.commandTable && ConditionCodeMatches(tableAfter,true) &&
        ConditionSameOriginalEval(scope.originalEval);
    bool losses = false; { std::lock_guard<std::mutex> lock(g_mutex); losses = g_dropped != scope.droppedBefore; }
    const bool frameCoverage=ConditionFrameCoverage(scope,returned);
    ConditionEmitSourceRows(scope,after,common,stable);
    ConditionEmitRows(scope, common);
    ConditionEmitFrames(scope,common,stable && codeStable && itemCoverage && frameCoverage && !losses);
    { std::lock_guard<std::mutex> lock(g_mutex); losses = losses || g_dropped != scope.droppedBefore || g_events.size() >= MaxEvents; }
    // Native boolean survives association/loss failure. The narrow pilot may be observed;
    // broad conditionTrace remains false because other operators/owners/paths are not admitted.
    Emit("condition-list-exit", request.id, common + ",\"status\":" + Quote(stable && codeStable && itemCoverage && frameCoverage && !losses ? "observed" : "unavailable") +
        ",\"nativeResult\":" + (returned ? "true" : "false") + ",\"auxiliaryResult\":" + (auxiliary ? "true" : "false") +
        ",\"ownerSourceStable\":" + (stable ? "true" : "false") + ",\"codeAndTableStable\":" + (codeStable ? "true" : "false") +
        ",\"observedItemCalls\":" + std::to_string(scope.observations.count) + ",\"itemObservationsComplete\":" + (itemCoverage ? "true" : "false") +
        ",\"bufferOverflow\":" + (scope.observations.overflow ? "true" : "false") + ",\"unexpectedNestedObservation\":" + (scope.observations.unexpected ? "true" : "false") +
        ",\"observationLossDetected\":" + (losses ? "true" : "false") +
        ",\"frameObservationsComplete\":"+(frameCoverage?"true":"false")+
        ",\"observedFrameSamples\":"+std::to_string(scope.frames.count)+
        ",\"orGroupStatus\":"+Quote(stable && codeStable && itemCoverage && frameCoverage && !losses?"observed":"unavailable")+
        ",\"fullConditionTrace\":false", generation);
}
