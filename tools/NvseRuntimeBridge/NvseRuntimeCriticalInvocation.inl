// Included after NvseRuntimeDamageInvocation.inl. All probes observe natural calls.
#include "RuntimeCriticalInvocation.h"
#include "RuntimeCriticalInvocationProfile.h"
namespace critical_invocation=bmt::critical;
struct CriticalSlot {const DamageHitScope* owner=nullptr;critical_invocation::Sample sample;};
thread_local std::array<CriticalSlot,8> g_criticalSlots{};
std::atomic<bool> g_criticalInvocationInstalled{false};
std::atomic<UInt32> g_criticalInvocationAbandoned{0};
std::atomic<UInt32> g_criticalInvocationAbandonedTotal{0};
std::uint64_t g_criticalCaptureSeen=0,g_criticalConnectionSeen=0;
const UInt32 CriticalNeutralMxcsr=0x1F80;
UInt32 CriticalOriginalCaller=0x009B7060,CriticalOriginalRandom=0x00487F50,CriticalOriginalThreshold=0x00EC62C0;
UInt32 CriticalResumeWeapon=0x009B7106,CriticalResumeSource=0x009B7144,CriticalResumeTarget=0x009B7180;
UInt32 CriticalWrapper(size_t index) noexcept;
std::array<std::uint8_t,6> CriticalOwned(size_t index) noexcept;
bool CriticalCodeMatches(bool installed) noexcept;

CriticalSlot* CriticalFind(const DamageHitScope& scope,bool create=false) noexcept {
    if(create && (g_criticalCaptureSeen!=scope.capture || g_criticalConnectionSeen!=scope.connection)) {
        g_criticalInvocationAbandoned=0;g_criticalCaptureSeen=scope.capture;g_criticalConnectionSeen=scope.connection;
    }
    for(auto& slot:g_criticalSlots)if(slot.owner==&scope && slot.sample.invocation==scope.invocation)return &slot;
    if(create)for(auto& slot:g_criticalSlots)if(slot.owner==&scope) {
        ++g_criticalInvocationAbandonedTotal;
        if(slot.sample.capture==scope.capture && slot.sample.connection==scope.connection)++g_criticalInvocationAbandoned;
        slot={};slot.owner=&scope;slot.sample.invocation=scope.invocation;
        slot.sample.capture=scope.capture;slot.sample.connection=scope.connection;slot.sample.load=scope.load;return &slot;
    }
    if(create)for(auto& slot:g_criticalSlots)if(!slot.owner) {
        slot={};slot.owner=&scope;slot.sample.invocation=scope.invocation;
        slot.sample.capture=scope.capture;slot.sample.connection=scope.connection;slot.sample.load=scope.load;return &slot;
    }
    return nullptr;
}
bool CriticalRead(UInt32 address,void* out,size_t length) noexcept {
    return address && address<=UINT32_MAX-length && DamageRead(address,out,length);
}
bool CriticalWord(UInt32 address,UInt32& out) noexcept {return CriticalRead(address,&out,4);}
bool CriticalHit(critical_invocation::Sample& sample,std::array<std::uint8_t,0x64>& out) noexcept {
    std::array<std::uint8_t,0x64> again{};
    if(!CriticalRead(sample.hit,out.data(),out.size()) || !CriticalRead(sample.hit,again.data(),again.size())) {
        critical_invocation::Fail(sample,critical_invocation::Failure::Read);return false;
    }
    if(out!=again){critical_invocation::Fail(sample,critical_invocation::Failure::Changed);return false;}
    return true;
}
bool CriticalStableContext(critical_invocation::Sample& sample) noexcept {
    std::array<std::uint8_t,0x64> hit{};
    UInt32 fallback=0,bonus=0,multiplier=0;
    if(!CriticalHit(sample,hit) || !CriticalWord(0x011CA278,fallback) ||
        !CriticalWord(0x011CE470,bonus) || !CriticalWord(0x011CE85C,multiplier)) {
        critical_invocation::Fail(sample,critical_invocation::Failure::Read);return false;
    }
    if(critical_invocation::Word(hit.data(),0)!=sample.source || critical_invocation::Word(hit.data(),4)!=sample.target ||
        critical_invocation::Word(hit.data(),0x30)!=sample.weapon || critical_invocation::Word(hit.data(),0x54)!=sample.context ||
        (!sample.weapon && fallback!=sample.fallback) || bonus!=sample.vatsBonusBits || multiplier!=sample.sneakMultiplierBits) {
        critical_invocation::Fail(sample,critical_invocation::Failure::Changed);return false;
    }
    if(sample.selectedWeapon) {
        std::array<std::uint8_t,16> header{};
        if(!CriticalRead(sample.selectedWeapon,header.data(),header.size()) || header!=sample.selectedWeaponHeader ||
            LookupRuntimeForm(sample.selectedWeaponId)!=reinterpret_cast<void*>(sample.selectedWeapon)) {
            critical_invocation::Fail(sample,critical_invocation::Failure::Identity);return false;
        }
    }
    return true;
}
// Pushad layout matches ConditionFrameObserve; fx points at the original FXSAVE.
void __cdecl CriticalObserve(UInt32 point,const UInt32* saved,const std::uint8_t* fx,UInt32 returnAddress) noexcept {
    auto* owner=DamageActiveScope();if(!owner)return;
    const auto step=static_cast<critical_invocation::Point>(point);
    auto* slot=CriticalFind(*owner,step==critical_invocation::Point::Begin);
    if(!slot)return; // Eight slots equal the damage scope bound; overflow is rejected upstream.
    auto& sample=slot->sample;
    if(!DamageAlive(*owner)){critical_invocation::Fail(sample,critical_invocation::Failure::Lifetime);return;}
    if(!g_criticalInvocationInstalled || !CriticalCodeMatches(true)) {
        critical_invocation::Fail(sample,critical_invocation::Failure::Code);return;
    }
    if(!critical_invocation::Next(sample,step))return;
    std::uint16_t control=0;memcpy(&control,fx,2);
    const UInt32 esp=saved[3]+4;
    if(step==critical_invocation::Point::Begin) {
        sample.hit=saved[6];sample.frame=esp-8;sample.returnAddress=returnAddress;
        sample.stackLow=__readfsdword(8);sample.stackHigh=__readfsdword(4);sample.controlWord=control;
        if(sample.hit!=owner->hit || !returnAddress || esp<8 ||
            !critical_invocation::Frame(sample,sample.frame,sample.frame-0x3C)) {
            critical_invocation::Fail(sample,critical_invocation::Failure::Bounds);return;
        }
        UInt32 caller=0;
        if(!CriticalWord(esp,caller) || caller!=0x009B561E || !CriticalHit(sample,sample.hitBefore) ||
            !CriticalWord(0x011CA278,sample.fallback) || !CriticalWord(0x011CE470,sample.vatsBonusBits) ||
            !CriticalWord(0x011CE85C,sample.sneakMultiplierBits)) {
            critical_invocation::Fail(sample,critical_invocation::Failure::Read);return;
        }
        sample.source=critical_invocation::Word(sample.hitBefore.data(),0);
        sample.target=critical_invocation::Word(sample.hitBefore.data(),4);
        sample.weapon=critical_invocation::Word(sample.hitBefore.data(),0x30);
        sample.context=critical_invocation::Word(sample.hitBefore.data(),0x54);
        sample.flagsBefore=critical_invocation::Word(sample.hitBefore.data(),0x58);
        sample.selectedWeapon=sample.weapon?sample.weapon:sample.fallback;
        if(sample.source!=owner->attacker || sample.target!=owner->target) {
            critical_invocation::Fail(sample,critical_invocation::Failure::Identity);return;
        }
        if(sample.selectedWeapon) {
            if(!CriticalRead(sample.selectedWeapon,sample.selectedWeaponHeader.data(),sample.selectedWeaponHeader.size()) ||
                sample.selectedWeaponHeader[4]!=0x28) {
                critical_invocation::Fail(sample,critical_invocation::Failure::Identity);return;
            }
            sample.selectedWeaponId=critical_invocation::Word(sample.selectedWeaponHeader.data(),12);
            if(!sample.selectedWeaponId || (critical_invocation::Word(sample.selectedWeaponHeader.data(),8)&0x4020) ||
                LookupRuntimeForm(sample.selectedWeaponId)!=reinterpret_cast<void*>(sample.selectedWeapon)) {
                critical_invocation::Fail(sample,critical_invocation::Failure::Identity);return;
            }
        }
    } else if(step==critical_invocation::Point::End) {
        if(esp!=sample.frame+8 || !CriticalHit(sample,sample.hitAfter)) {
            critical_invocation::Fail(sample,critical_invocation::Failure::Frame);return;
        }
        sample.flagsAfter=critical_invocation::Word(sample.hitAfter.data(),0x58);
    } else {
        UInt32 localHit=0,returned=0,value=0;
        if(!critical_invocation::Frame(sample,saved[2],esp) || !CriticalWord(sample.frame-0x1C,localHit) ||
            !CriticalWord(sample.frame+4,returned) || localHit!=sample.hit || returned!=sample.returnAddress ||
            !CriticalWord(sample.frame-0xC,value)) {
            critical_invocation::Fail(sample,critical_invocation::Failure::Frame);return;
        }
        if(step==critical_invocation::Point::Weapon)sample.weaponBits=value;
        else if(step==critical_invocation::Point::SourceModifiers)sample.sourceBits=value;
        else if(step==critical_invocation::Point::TargetModifiers)sample.targetBits=value;
        else if(step==critical_invocation::Point::BeforeRandom)sample.finalBits=value;
        else if(step==critical_invocation::Point::AfterRandom) {
            sample.random=saved[7];sample.randomObserved=true;
            if(value!=sample.finalBits)critical_invocation::Fail(sample,critical_invocation::Failure::Changed);
        } else if(step==critical_invocation::Point::BeforeThreshold) {
            if(!CriticalWord(sample.frame-4,sample.remainder) || sample.remainder!=sample.random%1000 || value!=sample.finalBits)
                critical_invocation::Fail(sample,critical_invocation::Failure::Changed);
        } else if(step==critical_invocation::Point::AfterThreshold) {
            sample.threshold=saved[7];sample.thresholdObserved=true;
            if(value!=sample.finalBits)critical_invocation::Fail(sample,critical_invocation::Failure::Changed);
        }
    }
    if(control!=0x007F || control!=sample.controlWord)critical_invocation::Fail(sample,critical_invocation::Failure::FloatingPoint);
    CriticalStableContext(sample);
}

// Every observer restores LastError, DF, GPRs, EFLAGS, x87/MMX/XMM and controls.
#define CRITICAL_OBSERVE(Point,Return) \
    __asm { pushfd } __asm { pushad } __asm { mov ebx,esp } \
    __asm { sub esp,544 } __asm { and esp,0FFFFFFF0h } __asm { fxsave [esp] } \
    __asm { fninit } __asm { ldmxcsr [CriticalNeutralMxcsr] } __asm { cld } \
    __asm { call GetLastError } __asm { mov [esp+512],eax } \
    __asm { mov eax,esp } __asm { push Return } __asm { push eax } __asm { push ebx } \
    __asm { push Point } __asm { call CriticalObserve } __asm { add esp,16 } \
    __asm { push dword ptr [esp+512] } __asm { call SetLastError } __asm { fxrstor [esp] } \
    __asm { mov esp,ebx } __asm { popad } __asm { popfd }
__declspec(naked) void CriticalCallerHook() {
    CRITICAL_OBSERVE(0,offset callerReturned)
    __asm { call dword ptr [CriticalOriginalCaller] }
    __asm { callerReturned: }
    CRITICAL_OBSERVE(8,0)
    __asm { ret }
}
__declspec(naked) void CriticalWeaponHook() {
    __asm { add esp,8 } __asm { fstp dword ptr [ebp-0Ch] }
    CRITICAL_OBSERVE(1,0)
    __asm { jmp dword ptr [CriticalResumeWeapon] }
}
__declspec(naked) void CriticalSourceHook() {
    __asm { add esp,14h } __asm { mov ecx,[ebp-1Ch] }
    CRITICAL_OBSERVE(2,0)
    __asm { jmp dword ptr [CriticalResumeSource] }
}
__declspec(naked) void CriticalTargetHook() {
    __asm { add esp,14h } __asm { mov edx,[ebp-1Ch] }
    CRITICAL_OBSERVE(3,0)
    __asm { jmp dword ptr [CriticalResumeTarget] }
}
__declspec(naked) void CriticalRandomHook() {
    CRITICAL_OBSERVE(4,0)
    __asm { call dword ptr [CriticalOriginalRandom] }
    CRITICAL_OBSERVE(5,0)
    __asm { ret }
}
__declspec(naked) void CriticalThresholdHook() {
    CRITICAL_OBSERVE(6,0)
    __asm { call dword ptr [CriticalOriginalThreshold] }
    CRITICAL_OBSERVE(7,0)
    __asm { ret }
}
#undef CRITICAL_OBSERVE
UInt32 CriticalWrapper(size_t index) noexcept {
    const std::array<void(*)(),6> functions{{CriticalCallerHook,CriticalWeaponHook,CriticalSourceHook,
        CriticalTargetHook,CriticalRandomHook,CriticalThresholdHook}};
    return index<functions.size()?reinterpret_cast<UInt32>(functions[index]):0;
}
std::array<std::uint8_t,6> CriticalOwned(size_t index) noexcept {
    std::array<std::uint8_t,6> result{};
    if(index>=critical_invocation::Spans.size())return {};
    if(critical_invocation::Spans[index].length==6)result[5]=0x90;
    result[0]=(index==0 || index>=4)?0xE8:0xE9;
    const UInt32 displacement=CriticalWrapper(index)-(critical_invocation::Spans[index].address+5);
    memcpy(result.data()+1,&displacement,4);return result;
}
bool CriticalOwnsCallerSpan() noexcept {
    std::array<std::uint8_t,6> actual{};const auto expected=CriticalOwned(0);
    return g_criticalInvocationInstalled && CriticalRead(0x009B5619,actual.data(),5) &&
        !memcmp(actual.data(),expected.data(),5);
}
bool CriticalCodeMatches(bool installed) noexcept {
    if(!VerifiedPcExecutable(g_executableSha256))return false;
    if(CriticalOriginalCaller!=0x009B7060 || CriticalOriginalRandom!=0x00487F50 || CriticalOriginalThreshold!=0x00EC62C0 ||
        CriticalResumeWeapon!=0x009B7106 || CriticalResumeSource!=0x009B7144 || CriticalResumeTarget!=0x009B7180)return false;
    std::array<std::uint8_t,critical_invocation::Body.size()> actual{},again{};
    if(!CriticalRead(0x009B7060,actual.data(),actual.size()) || !CriticalRead(0x009B7060,again.data(),again.size()) || actual!=again)return false;
    for(size_t index=0;index<critical_invocation::Spans.size();++index) {
        const auto& span=critical_invocation::Spans[index];const auto expected=installed?CriticalOwned(index):span.bytes;
        std::array<std::uint8_t,6> loaded{};
        if(!CriticalRead(span.address,loaded.data(),span.length) || memcmp(loaded.data(),expected.data(),span.length))return false;
        if(index)memcpy(actual.data()+span.address-0x009B7060,span.bytes.data(),span.length);
    }
    return actual==critical_invocation::Body;
}

// Called from the damage hit-return observer after original009B7060 has returned.
// No calculation consumes the observed threshold or draw as a probability input.
void CriticalFinish(const DamageHitScope& owner) {
    auto* slot=CriticalFind(owner);if(!slot)return;
    const auto sample=slot->sample;*slot={};
    UInt32 observedTarget=0,observedBase=0;std::uint8_t targetType=0,baseType=0;
    const bool identity=ReadFormIdentity(reinterpret_cast<void*>(owner.attacker),observedTarget,targetType) &&
        ReadFormIdentity(reinterpret_cast<void*>(owner.attackerBase),observedBase,baseType) &&
        observedTarget==owner.attackerId && observedBase==owner.attackerBaseId &&
        ((targetType==0x3B && baseType==0x2A) || (targetType==0x3C && baseType==0x2B));
    const bool complete=identity && owner.hitRepeated && owner.codeEntry && owner.codeReturn &&
        DamageAlive(owner) && CriticalCodeMatches(true) && critical_invocation::Complete(sample);
    const bool comparison=sample.thresholdObserved && critical_invocation::Comparison(sample);
    std::int32_t signedThreshold=0;memcpy(&signedThreshold,&sample.threshold,4);
    auto word=[](UInt32 value){return std::to_string(value);};
    Emit("critical-invocation",owner.request,
        ",\"schemaVersion\":1,\"status\":"+Quote(complete?"Observed":"Partial")+
        ",\"profile\":\"pc-retail-critical-invocation-v1\",\"codeVerified\":"+(complete?"true":"false")+
        ",\"executableSha256\":"+Quote(g_executableSha256)+
        ",\"codeEvidenceImageSha256\":\"e46b43cdaa32d9b79b7816fa45cb076c7ed59e335b1533118dcb6bf1d9da692d\""+
        ",\"engineTargetFormId\":"+word(owner.attackerId)+",\"engineTargetBaseFormId\":"+word(owner.attackerBaseId)+",\"targetKind\":\"actor\""+
        ",\"engineTargetFormType\":"+word(targetType)+",\"engineTargetBaseFormType\":"+word(baseType)+
        ",\"hitInvocationId\":"+std::to_string(owner.invocation)+
        ",\"leaseId\":"+std::to_string(owner.lease)+
        ",\"sourceFormId\":"+word(owner.attackerId)+",\"targetFormId\":"+word(owner.targetId)+
        ",\"sourceBaseFormId\":"+word(owner.attackerBaseId)+
        ",\"hitAddress\":"+word(sample.hit)+",\"sourceAddress\":"+word(sample.source)+",\"targetAddress\":"+word(sample.target)+
        ",\"weaponAddress\":"+word(sample.weapon)+",\"fallbackAddress\":"+word(sample.fallback)+
        ",\"selectedWeaponAddress\":"+word(sample.selectedWeapon)+",\"selectedWeaponFormId\":"+word(sample.selectedWeaponId)+
        ",\"selectedWeaponHeaderHex\":"+Quote(ActorHex(sample.selectedWeaponHeader.data(),sample.selectedWeaponHeader.size()))+
        ",\"hitBeforeHex\":"+Quote(ActorHex(sample.hitBefore.data(),sample.hitBefore.size()))+
        ",\"hitAfterHex\":"+Quote(ActorHex(sample.hitAfter.data(),sample.hitAfter.size()))+
        ",\"frameAddress\":"+word(sample.frame)+",\"returnAddress\":"+word(sample.returnAddress)+
        ",\"captureGeneration\":"+std::to_string(owner.capture)+",\"connectionGeneration\":"+std::to_string(owner.connection)+
        ",\"loadEpoch\":"+std::to_string(owner.load)+",\"threadId\":"+word(owner.thread)+
        ",\"x87ControlWord\":"+word(sample.controlWord)+",\"stepCount\":"+word(sample.count)+
        ",\"abandonedInvocations\":"+word(g_criticalInvocationAbandoned.load())+
        ",\"complete\":"+(complete?"true":"false")+",\"earlyExit\":"+(sample.early?"true":"false")+
        ",\"failureCode\":"+word(static_cast<UInt32>(sample.failure))+
        ",\"weaponStageBits\":"+word(sample.weaponBits)+",\"sourceModifierStageBits\":"+word(sample.sourceBits)+
        ",\"targetModifierStageBits\":"+word(sample.targetBits)+",\"finalChanceScaledBits\":"+word(sample.finalBits)+
        ",\"context\":"+word(sample.context)+",\"vatsBonusBits\":"+word(sample.vatsBonusBits)+
        ",\"sneakMultiplierBits\":"+word(sample.sneakMultiplierBits)+
        ",\"randomObserved\":"+(sample.randomObserved?"true":"false")+
        ",\"thresholdObserved\":"+(sample.thresholdObserved?"true":"false")+
        ",\"randomRaw\":"+(sample.randomObserved?word(sample.random):"null")+
        ",\"randomRemainder\":"+(sample.thresholdObserved?word(sample.remainder):"null")+
        ",\"thresholdRaw\":"+(sample.thresholdObserved?word(sample.threshold):"null")+
        ",\"thresholdSigned\":"+(sample.thresholdObserved?std::to_string(signedThreshold):"null")+
        ",\"comparisonAccepted\":"+(sample.thresholdObserved?(comparison?"true":"false"):"null")+
        ",\"criticalFlagBefore\":"+((sample.flagsBefore&4)?"true":"false")+
        ",\"criticalFlagAfter\":"+((sample.flagsAfter&4)?"true":"false"),owner.capture);
}

bool CriticalNormalizeDamageProof(UInt32 address,std::uint8_t* bytes,size_t length) noexcept {
    constexpr UInt32 site=0x009B5619;
    if(address>site || length<=site-address)return true;
    const auto offset=site-address;
    if(length-offset<5)return false;
    const auto expected=g_criticalInvocationInstalled?CriticalOwned(0):critical_invocation::Spans[0].bytes;
    if(memcmp(bytes+offset,expected.data(),5))return false;
    memcpy(bytes+offset,critical_invocation::Spans[0].bytes.data(),5);return true;
}
constexpr bool CriticalInvocationReviewed=true;
bool g_criticalInvocationAttempted=false,g_criticalInvocationMayBeOwned=false;
ConditionPatchReport g_criticalPatchReport;
std::atomic<UInt32> g_criticalReportSequence{0};
std::array<std::atomic<UInt32>,6> g_criticalReportWords{};
void CriticalPublishState() noexcept {
    const std::array<UInt32,6> words{{static_cast<UInt32>(g_criticalPatchReport.phase),
        static_cast<UInt32>(g_criticalPatchReport.transaction.status),g_criticalPatchReport.resumeFailures,
        g_criticalPatchReport.protectionRestoreFailures,g_criticalInvocationMayBeOwned?1u:0u,g_criticalInvocationInstalled?1u:0u}};
    ++g_criticalReportSequence;
    for(size_t i=0;i<words.size();++i)g_criticalReportWords[i].store(words[i]);
    ++g_criticalReportSequence;
}
struct CriticalPublishGuard {~CriticalPublishGuard() noexcept {CriticalPublishState();}};
std::array<condition_patch::Span,condition_patch::Count> CriticalPatchPlan(bool restoring) noexcept {
    static_assert(condition_patch::Count==critical_invocation::Spans.size());
    std::array<condition_patch::Span,condition_patch::Count> result{};
    for(size_t i=0;i<result.size();++i) {
        result[i].address=critical_invocation::Spans[i].address;
        result[i].length=critical_invocation::Spans[i].length;
        result[i].before=critical_invocation::Spans[i].bytes;result[i].after=CriticalOwned(i);
        if(restoring)std::swap(result[i].before,result[i].after);
    }
    return result;
}
ConditionPatchReport CriticalTransact(bool restoring) noexcept {
    ConditionPatchReport report;report.restoring=restoring;const auto spans=CriticalPatchPlan(restoring);
    ConditionThreadSet threads;ConditionPages pages;ConditionNativeThreads native;
    if(!ConditionPrepareNativeThreads(native)) {
        report.phase=ConditionPatchPhase::ThreadRejected;report.nativeThreadCheck=native.check;return report;
    }
    if(!ConditionPrepareThreads(threads)) {
        report.phase=ConditionPatchPhase::ThreadRejected;VirtualFree(native.buffer,0,MEM_RELEASE);return report;
    }
    report.threads=static_cast<UInt32>(threads.count);
    if(!ConditionPreparePages(pages,spans) || !ConditionOpenPages(pages)) {
        report.phase=ConditionPatchPhase::PageRejected;report.protectionRestoreFailures=ConditionClosePages(pages);
        ConditionCloseThreads(threads);VirtualFree(native.buffer,0,MEM_RELEASE);return report;
    }
    const bool admitted=ConditionSuspendThreads(threads,spans) && ConditionVerifyFrozenThreads(native,threads);
    report.suspended=static_cast<UInt32>(threads.suspended);report.nativeThreadCheck=native.check;report.nativeQueryStatus=native.status;
    if(admitted) {
        report.transaction=condition_patch::Apply(spans,{nullptr,ConditionPatchRead,ConditionPatchWrite,ConditionPatchFlush});
        report.phase=report.transaction.status==condition_patch::Status::Committed?
            (restoring?ConditionPatchPhase::Restored:ConditionPatchPhase::Installed):ConditionPatchPhase::TransactionFailed;
    } else report.phase=ConditionPatchPhase::ThreadRejected;
    report.protectionRestoreFailures=ConditionClosePages(pages);report.resumeFailures=ConditionResumeThreads(threads);
    ConditionCloseThreads(threads);VirtualFree(native.buffer,0,MEM_RELEASE);
    if(report.protectionRestoreFailures)report.phase=ConditionPatchPhase::ProtectionRestoreFailed;
    if(report.resumeFailures)report.phase=ConditionPatchPhase::ResumeFailed;
    return report;
}
void InstallCriticalInvocation() {
    if(!CriticalInvocationReviewed || g_criticalInvocationAttempted || !g_damageInstalled || !g_loadedGameObserved ||
        !g_pcLayoutVerified || g_damageDepth)return;
    g_criticalInvocationAttempted=true;
    CriticalPublishGuard publish;
    ConditionLastError error{GetLastError()};CommandFloatingGuard floating;
    if(!CriticalCodeMatches(false) || (g_damageProofNormalizer && g_damageProofNormalizer!=CriticalNormalizeDamageProof)) {
        g_criticalPatchReport.phase=ConditionPatchPhase::ProfileRejected;return;
    }
    HMODULE pinned=nullptr;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(CriticalWrapper(0)),&pinned)) {
        g_criticalPatchReport.phase=ConditionPatchPhase::ProfileRejected;return;
    }
    g_damageHitFinished=CriticalFinish;
    g_damageProofNormalizer=CriticalNormalizeDamageProof;
    g_criticalPatchReport=CriticalTransact(false);
    g_criticalInvocationMayBeOwned=g_criticalPatchReport.transaction.attemptedMask!=0 && !g_criticalPatchReport.transaction.bytesRestored;
    g_criticalInvocationInstalled=g_criticalPatchReport.phase==ConditionPatchPhase::Installed && CriticalCodeMatches(true);
}
void RestoreCriticalInvocation() {
    if(!g_criticalInvocationMayBeOwned || g_damageDepth)return;
    CriticalPublishGuard publish;
    ConditionLastError error{GetLastError()};CommandFloatingGuard floating;
    if(!CriticalCodeMatches(true)) {
        g_criticalPatchReport.phase=ConditionPatchPhase::ProfileRejected;g_criticalInvocationInstalled=false;return;
    }
    g_criticalInvocationInstalled=false;g_criticalPatchReport=CriticalTransact(true);
    if(g_criticalPatchReport.phase==ConditionPatchPhase::Restored)g_criticalInvocationMayBeOwned=false;
}
std::string CriticalCapabilityFields() {
    std::array<UInt32,6> words{};bool stable=false;
    for(unsigned attempt=0;attempt<3 && !stable;++attempt) {
        const auto sequence=g_criticalReportSequence.load();if(sequence&1)continue;
        for(size_t i=0;i<words.size();++i)words[i]=g_criticalReportWords[i].load();
        stable=g_criticalReportSequence.load()==sequence;
    }
    if(!stable)return ",\"criticalInvocation\":{\"version\":1,\"status\":\"changing\"}";
    return ",\"criticalInvocation\":{\"version\":1,\"status\":"+
        Quote(words[5]?"available":CriticalInvocationReviewed?"unavailable":"unreviewed")+
        ",\"patchPhase\":"+std::to_string(words[0])+
        ",\"transactionStatus\":"+std::to_string(words[1])+
        ",\"resumeFailures\":"+std::to_string(words[2])+
        ",\"protectionRestoreFailures\":"+std::to_string(words[3])+
        ",\"retainedOwnedBytesPossible\":"+(words[4]?"true":"false")+
        ",\"abandonedInvocations\":"+std::to_string(g_criticalInvocationAbandoned.load())+
        ",\"abandonedInvocationsTotal\":"+std::to_string(g_criticalInvocationAbandonedTotal.load())+"}";
}
