// Natural hit observations. No game calls, argument re-evaluation or data writes.
// Root enables this only after the x86 probe fixtures and patch review pass.
namespace damage=bmt::damage;
constexpr bool DamageInvocationReviewed=true;
constexpr UInt32 DamageMaximumEventsPerLease=64;
const UInt32 DamageNeutralMxcsr=0x1F80;
std::atomic<bool> g_damageInstalled{false};
UInt32 g_damageThread=0;
bool g_damageAttempted=false;
std::array<UInt32,damage::SiteCount> g_damageTrampolines{};
std::atomic<std::uint64_t> g_damageSerial{0};
std::uint64_t g_damageLeaseSeen=0;
UInt32 g_damageRetained=0,g_damageOmitted=0;
UInt32 g_damageReportedOmitted=0;
damage::Coverage g_damageCoverage; // Protected by g_combatMutex.
damage::CoverageKey DamageCoverageKey(const CombatLeaseRecord& lease) noexcept {
    return {lease.id,lease.captureGeneration,lease.connectionGeneration,lease.loadEpoch};
}
void DamageCount(const damage::CoverageKey& key,damage::CoverageCounter counter) noexcept {
    std::lock_guard<std::mutex> lock(g_combatMutex);g_damageCoverage.add(key,counter);
}
damage::CoverageKey DamageRouteEntry() noexcept {
    std::lock_guard<std::mutex> lock(g_combatMutex);const auto& lease=g_combatLease;
    if(!lease.active || lease.cleanupIssued || !lease.trigger.empty() || GetTickCount64()>=lease.deadline)return {};
    const auto key=DamageCoverageKey(lease);g_damageCoverage.select(key);
    g_damageCoverage.add(key,damage::RouteEntries);return key;
}
std::string DamageInvocationCoverage(const CombatLeaseRecord& lease) {
    damage::Coverage snapshot;const auto key=DamageCoverageKey(lease);
    {
        std::lock_guard<std::mutex> lock(g_combatMutex);
        if(!(key==DamageCoverageKey(g_combatLease)) || !key.valid())return "{\"status\":\"unavailable\",\"reason\":\"lease-changed\"}";
        g_damageCoverage.select(key);snapshot=g_damageCoverage;
    }
    std::string json="{\"version\":1,\"status\":"+Quote(g_damageInstalled?"observed":"unavailable")+
        ",\"route\":\"009B5170\",\"leaseId\":"+std::to_string(key.lease)+
        ",\"captureGeneration\":"+std::to_string(key.capture)+",\"connectionGeneration\":"+std::to_string(key.connection)+
        ",\"loadEpoch\":"+std::to_string(key.load)+",\"saturated\":"+(snapshot.saturated?"true":"false");
    for(unsigned i=0;i<damage::WrongThread;++i)json+=","+Quote(damage::CoverageNames[i])+":"+std::to_string(snapshot.counts[i]);
    json+=",\"rejected\":{";
    for(unsigned i=damage::WrongThread;i<damage::CoverageCounterCount;++i) {
        if(i!=damage::WrongThread)json+=',';
        json+=Quote(damage::CoverageNames[i])+":"+std::to_string(snapshot.counts[i]);
    }
    return json+"}}";
}
bool DamageCodeMatches(bool installed) noexcept;
using DamageProofNormalizer=bool (*)(UInt32,std::uint8_t*,size_t) noexcept;
DamageProofNormalizer g_damageProofNormalizer=nullptr;
struct DamageSetting {std::array<std::uint8_t,12> raw{};bool valid=false;};
constexpr std::array<UInt32,5> DamageSettingAddresses{{0x011CE2C0,0x011CFCB0,0x011CFAEC,0x011CECE8,0x011CF350}};
constexpr std::array<const char*,5> DamageSettingNames{{"fDamageWeaponMult","fDamageArmConditionBase","fDamageArmConditionMult","fDamageSkillBase","fDamageSkillMult"}};
struct DamageHitScope {
    damage::CoverageKey coverage;
    std::uint64_t invocation=0,request=0,capture=0,connection=0,load=0,frame=0,lease=0;
    UInt32 thread=0,hit=0,attacker=0,target=0,item=0,weapon=0,weaponId=0;
    UInt32 attackerId=0,attackerBaseId=0,targetId=0,targetBaseId=0,attackerBase=0,targetBase=0;
    std::uint8_t attackerType=0,attackerBaseType=0,targetType=0,targetBaseType=0;
    UInt32 entryEsp=0,hitFrame=0,returnAddress=0,stages=0,omittedBefore=0;
    bool selected=false,invalid=false,codeEntry=false,codeReturn=false,hitRepeated=false,weaponBound=false;
    std::array<UInt32,5> hitArguments{};
    std::array<std::uint8_t,0x64> hitAfter{};
    damage::Stage stage;
    std::array<DamageSetting,5> settingsBefore{},settingsAfter{};
};
using DamageHitFinished=void (*)(const DamageHitScope&);
DamageHitFinished g_damageHitFinished=nullptr;
thread_local std::array<DamageHitScope,damage::MaximumDepth> g_damageScopes{};
thread_local unsigned g_damageDepth=0,g_damageOverflow=0;
bool DamageRead(UInt32 address,void* output,size_t size) noexcept {
    return address && size && size-1<=UINT32_MAX-address && CommandReadBytes(address,output,size);
}
bool DamageStack(UInt32 address,size_t size) noexcept {
    const UInt32 low=__readfsdword(8),high=__readfsdword(4);
    return low && low<high && address>=low && address<high && size<=high-address;
}
bool DamageAlive(const DamageHitScope& s) noexcept {
    return s.selected && !s.invalid && g_damageInstalled && g_capture && g_connected && g_loadedGameObserved &&
        s.capture==g_captureGeneration.load() && s.connection==g_connectionGeneration.load() &&
        s.load==g_gameLoadEpoch.load() && s.thread==GetCurrentThreadId() && s.thread==g_damageThread && s.frame==g_frame;
}
DamageHitScope* DamageActiveScope() noexcept {
    if(g_damageOverflow || !g_damageDepth)return nullptr;
    auto& s=g_damageScopes[g_damageDepth-1];return DamageAlive(s)?&s:nullptr;
}
bool DamageForm(UInt32 address,UInt32 expectedId,std::uint8_t expectedType) noexcept {
    std::array<std::uint8_t,16> a{},b{};
    return expectedId && DamageRead(address,a.data(),a.size()) &&
        a[4]==expectedType && damage::U32(a.data()+12)==expectedId && !(damage::U32(a.data()+8)&0x20) &&
        LookupRuntimeForm(expectedId)==reinterpret_cast<void*>(address) && DamageRead(address,b.data(),b.size()) && a==b;
}
bool DamageActor(UInt32 address,UInt32 id,UInt32 base,UInt32 baseId,std::uint8_t expectedType,std::uint8_t expectedBaseType) noexcept {
    UInt32 actualBase=0;std::uint8_t type=0,baseType=0;UInt32 unused=0;
    return ReadFormIdentity(reinterpret_cast<void*>(address),unused,type) && type==expectedType && (type==0x3B || type==0x3C) &&
        DamageForm(address,id,type) && DamageRead(address+0x20,&actualBase,4) && actualBase==base &&
        ReadFormIdentity(reinterpret_cast<void*>(base),unused,baseType) && baseType==expectedBaseType && baseType==(type==0x3B?0x2A:0x2B) &&
        DamageForm(base,baseId,baseType);
}
bool DamageParticipants(const DamageHitScope& s) noexcept {
    return DamageActor(s.attacker,s.attackerId,s.attackerBase,s.attackerBaseId,s.attackerType,s.attackerBaseType) &&
        DamageActor(s.target,s.targetId,s.targetBase,s.targetBaseId,s.targetType,s.targetBaseType);
}
bool DamageSettingRead(unsigned index,DamageSetting& result) noexcept {
    result={};
    const auto address=DamageSettingAddresses[index];
    if(!DamageRead(address,result.raw.data(),result.raw.size()))return false;
    const auto name=damage::U32(result.raw.data()+8);
    char actual[64]{};const auto expected=DamageSettingNames[index];const auto length=strlen(expected)+1;
    std::array<std::uint8_t,12> again{};
    result.valid=length<=sizeof(actual) && DamageRead(name,actual,length) && !memcmp(actual,expected,length) &&
        ActorEffectMapped(address,result.raw.size(),false) && ActorEffectMapped(damage::U32(result.raw.data()),4,false) &&
        damage::Finite(damage::U32(result.raw.data()+4)) && DamageRead(address,again.data(),again.size()) && again==result.raw;
    return result.valid;
}
void DamageHitEnter(const UInt32* saved,const damage::CoverageKey& coverage) noexcept {
    const auto esp=saved[3]+4;
    if(g_damageOverflow){++g_damageOverflow;DamageCount(coverage,damage::Depth);return;}
    // A nonlocal engine unwind cannot make a later invocation inherit a stale frame.
    while(g_damageDepth && esp>=g_damageScopes[g_damageDepth-1].entryEsp) {DamageCount(g_damageScopes[g_damageDepth-1].coverage,damage::InvalidReturn);--g_damageDepth;++g_damageOmitted;}
    if(g_damageDepth==g_damageScopes.size()){g_damageOverflow=1;++g_damageOmitted;DamageCount(coverage,damage::Depth);return;}
    auto& s=g_damageScopes[g_damageDepth++];s={};s.coverage=coverage;s.entryEsp=esp;s.hitFrame=esp-4;
    if(!g_damageInstalled || !g_capture || !g_connected || !g_loadedGameObserved) {DamageCount(coverage,damage::InactiveCapture);return;}
    if(!DamageStack(esp,24) || !DamageRead(esp,&s.returnAddress,4) || !DamageRead(esp+4,s.hitArguments.data(),20)) {
        DamageCount(coverage,damage::StackOrArguments);return;
    }
    s.hit=s.hitArguments[0];s.attacker=s.hitArguments[1];s.target=s.hitArguments[2];s.item=s.hitArguments[3];
    if(!s.hit || !s.attacker || !s.target){DamageCount(coverage,damage::MissingParticipants);return;}
    { // A natural hit is selected only by an active bounded combat lease.
        std::lock_guard<std::mutex> lock(g_combatMutex);const auto& lease=g_combatLease;
        if(!coverage.valid() || !(coverage==DamageCoverageKey(lease)) || !lease.active || lease.cleanupIssued ||
            lease.trigger.size() || GetTickCount64()>=lease.deadline || lease.captureGeneration!=g_captureGeneration.load() ||
            lease.connectionGeneration!=g_connectionGeneration.load() || lease.loadEpoch!=g_gameLoadEpoch.load()) {
            g_damageCoverage.add(coverage,damage::LeaseChanged);return;
        }
        if(reinterpret_cast<UInt32>(lease.attacker.pointer)!=s.attacker || reinterpret_cast<UInt32>(lease.target.pointer)!=s.target) {
            g_damageCoverage.add(coverage,damage::OtherParticipants);return;
        }
        g_damageCoverage.add(coverage,damage::ParticipantsMatched);
        s.lease=lease.id;s.request=lease.requestId;s.capture=lease.captureGeneration;s.connection=lease.connectionGeneration;s.load=lease.loadEpoch;
        s.attackerId=lease.attacker.id;s.attackerBaseId=lease.attacker.baseId;s.targetId=lease.target.id;s.targetBaseId=lease.target.baseId;
        s.attackerBase=lease.attackerBaseAddress;s.targetBase=lease.targetBaseAddress;
        s.attackerType=lease.attacker.type;s.attackerBaseType=lease.attacker.baseType;s.targetType=lease.target.type;s.targetBaseType=lease.target.baseType;
    }
    if(g_damageLeaseSeen!=s.lease){g_damageLeaseSeen=s.lease;g_damageRetained=0;g_damageOmitted=0;g_damageReportedOmitted=0;}
    if(g_damageRetained>=DamageMaximumEventsPerLease){++g_damageOmitted;DamageCount(coverage,damage::Budget);return;}
    ++g_damageRetained;s.omittedBefore=g_damageOmitted;s.frame=g_frame;s.thread=GetCurrentThreadId();
    s.invocation=++g_damageSerial;s.selected=true;DamageCount(coverage,damage::AdmittedInvocations);
    s.codeEntry=DamageCodeMatches(true);
    if(!DamageAlive(s) || !DamageParticipants(s) || !s.codeEntry){s.invalid=true;DamageCount(coverage,damage::InvalidEntry);}
    if(!s.item)s.weaponBound=true; // The retained null-item branch stores hit.weapon=0.
    else {
        // Pinned ExtraContainerChanges::EntryData.type at+08. Capture this
        // independently before the engine creates the hit's weapon field.
        UInt32 candidate=0,id=0;std::uint8_t type=0;
        if(DamageRead(s.item+8,&candidate,4) && ReadFormIdentity(reinterpret_cast<void*>(candidate),id,type) &&
            type==0x28 && DamageForm(candidate,id,type)) {s.weapon=candidate;s.weaponId=id;s.weaponBound=true;}
    }
}
void DamageStageEnter(DamageHitScope& s,const UInt32* saved,const std::uint8_t* fx) noexcept {
    if(++s.stages!=1){s.invalid=true;return;}
    auto& stage=s.stage;stage.entered=true;stage.entryEsp=saved[3]+4;stage.frame=stage.entryEsp-4;
    stage.callerFrame=saved[2];stage.entryControl=damage::U16(fx);
    UInt32 callerItem=0,callerReturn=0,callerParent=0;
    if(!DamageStack(stage.entryEsp,36) || !DamageStack(stage.callerFrame-0x1C,0x30) ||
        !DamageRead(stage.entryEsp,&stage.returnAddress,4) || !DamageRead(stage.entryEsp+4,stage.arguments.data(),32) ||
        stage.returnAddress!=0x004BDF76 || stage.arguments[0]!=s.attacker+0xA4 || stage.arguments[6]!=s.item ||
        !DamageRead(stage.callerFrame-0xC,&callerItem,4) || callerItem!=s.item ||
        !DamageRead(stage.callerFrame,&callerParent,4) || callerParent!=s.hitFrame ||
        !DamageRead(stage.callerFrame+4,&callerReturn,4) || callerReturn!=0x009B5330) {stage.invalid=true;return;}
    if(!s.weaponBound || s.weapon!=stage.arguments[1]){stage.invalid=true;return;}
    if(!DamageRead(s.weapon,stage.weaponBefore.data(),stage.weaponBefore.size())){stage.invalid=true;return;}
    if(s.weaponId!=damage::U32(stage.weaponBefore.data()+12)){stage.invalid=true;return;}
    if(!DamageForm(s.weapon,s.weaponId,0x28) || stage.weaponBefore[0xF4]!=1) {stage.invalid=true;return;}
    for(unsigned i=0;i<s.settingsBefore.size();++i)if(!DamageSettingRead(i,s.settingsBefore[i]))stage.invalid=true;
}
void DamageStageOperand(DamageHitScope& s,unsigned site,const UInt32* saved,const std::uint8_t* fx) noexcept {
    auto& stage=s.stage;
    if(!stage.entered || stage.returned || stage.invalid || saved[2]!=stage.frame ||
        !DamageStack(stage.frame-0x74,0x9C) || damage::U16(fx)!=stage.entryControl){stage.invalid=true;return;}
    if(site==damage::MeleeAv) {
        if(++stage.meleeCalls!=1 || !DamageRead(stage.frame-0x28,&stage.extra,4) || !damage::St0(fx,stage.av17Raw,stage.av17))stage.invalid=true;
    } else if(site==damage::Limb) {
        if(++stage.limbCalls!=1 || !DamageRead(stage.frame-0x50,stage.limbRaw.data(),stage.limbRaw.size()))stage.invalid=true;
    } else if(site==damage::StageReturn) {
        // The probe executes the displaced FSTP/FLD before saving registers. -74
        // is the exact engine binary32 return; the original x87 stack is restored.
        stage.returned=true;stage.returnControl=damage::U16(fx);
        if(!DamageRead(stage.frame-0x74,stage.after.raw.data(),stage.after.raw.size()) ||
            !DamageRead(s.weapon,stage.weaponAfter.data(),stage.weaponAfter.size())){stage.invalid=true;return;}
        stage.weaponStable=DamageForm(s.weapon,s.weaponId,0x28) && stage.weaponAfter==stage.weaponBefore;
        stage.settingsStable=true;
        for(unsigned i=0;i<s.settingsAfter.size();++i)
            if(!DamageSettingRead(i,s.settingsAfter[i]) || s.settingsAfter[i].raw!=s.settingsBefore[i].raw)stage.settingsStable=false;
    }
}
std::string DamageScalar(UInt32 bits,UInt32 address=0) {
    return "{\"status\":"+Quote(damage::Finite(bits)?"observed":"unavailable")+",\"value\":"+
        (damage::Finite(bits)?NumberField(damage::F32(bits)):"null")+",\"rawUInt32\":"+std::to_string(bits)+",\"sourceAddress\":"+std::to_string(address)+"}";
}
std::string DamageUInt(UInt32 value,UInt32 address=0) {
    return "{\"status\":\"observed\",\"value\":"+std::to_string(value)+",\"rawUInt32\":"+std::to_string(value)+",\"sourceAddress\":"+std::to_string(address)+"}";
}
void DamageEmit(const DamageHitScope& s) {
    const auto& stage=s.stage;const auto& f=stage.after;
    const char* reason=!DamageAlive(s)?"capture-or-identity-changed":!s.codeEntry || !s.codeReturn?"code-profile-changed":s.stages!=1?"stage-count-mismatch":damage::Complete(stage);
    const bool complete=!reason;
    std::string inputs="null",output="null";
    if(complete) {
        double limb=0;memcpy(&limb,stage.limbRaw.data(),8);
        inputs="{\"weaponBaseDamage\":"+DamageUInt(f.local(0x3C),s.weapon+0xA0)+
            ",\"governingSkill\":"+DamageUInt(f.local(0x30),s.weapon+0x15C)+
            ",\"skillValue\":"+DamageScalar(f.local(0x20),stage.frame-0x20)+
            ",\"rightArmFactor\":{\"status\":\"observed\",\"value\":"+NumberField(limb)+",\"rawHex\":"+Quote(ActorHex(stage.limbRaw.data(),8))+"}"+
            ",\"meleeDamageActorValue\":{\"status\":\"observed\",\"value\":"+NumberField(damage::F32(stage.av17))+",\"rawUInt32\":"+std::to_string(stage.av17)+",\"rawHex\":"+Quote(ActorHex(stage.av17Raw.data(),10))+",\"actorValueCode\":17}"+
            ",\"extraDataDamage\":"+DamageScalar(stage.extra,stage.frame-0x28)+
            ",\"conditionFraction\":"+DamageScalar(stage.arguments[2],stage.frame+0x10)+
            ",\"attackMultiplier\":"+DamageScalar(stage.arguments[3],stage.frame+0x14)+
            ",\"ammunitionDamage\":"+DamageScalar(f.local(0x24),stage.frame-0x24)+
            ",\"weaponModeMultiplier\":"+DamageScalar(f.local(8),stage.frame-8)+
            ",\"actorModeMultiplier\":"+DamageScalar(f.local(4),stage.frame-4);
        for(unsigned i=0;i<s.settingsBefore.size();++i)inputs+=","+Quote(DamageSettingNames[i])+":"+DamageScalar(damage::U32(s.settingsBefore[i].raw.data()+4),DamageSettingAddresses[i]+4);
        inputs+="}";output=DamageScalar(f.local(0x74),stage.frame-0x74);
    }
    std::string arguments="[";
    for(unsigned i=0;i<stage.arguments.size();++i){if(i)arguments+=',';arguments+=std::to_string(stage.arguments[i]);}arguments+=']';
    DamageCount(s.coverage,damage::StageEventAttempts);
    Emit("damage-stage",s.request,
        ",\"targetKind\":\"actor\",\"engineTargetFormId\":"+std::to_string(s.attackerId)+",\"engineTargetBaseFormId\":"+std::to_string(s.attackerBaseId)+
        ",\"engineTargetFormType\":"+std::to_string(s.attackerType)+",\"engineTargetBaseFormType\":"+std::to_string(s.attackerBaseType)+
        ",\"engineOtherFormId\":"+std::to_string(s.targetId)+",\"engineOtherBaseFormId\":"+std::to_string(s.targetBaseId)+
        ",\"executableSha256\":"+Quote(g_executableSha256)+
        ",\"codeEvidenceImageSha256\":\"e46b43cdaa32d9b79b7816fa45cb076c7ed59e335b1533118dcb6bf1d9da692d\""+
        ",\"hitInvocationId\":"+std::to_string(s.invocation)+",\"stageInvocationId\":"+std::to_string(s.invocation)+
        ",\"captureGeneration\":"+std::to_string(s.capture)+",\"connectionGeneration\":"+std::to_string(s.connection)+
        ",\"loadEpoch\":"+std::to_string(s.load)+",\"threadId\":"+std::to_string(s.thread)+",\"entryFrame\":"+std::to_string(s.frame)+
        ",\"leaseId\":"+std::to_string(s.lease)+",\"hitAddress\":"+std::to_string(s.hit)+",\"attackerAddress\":"+std::to_string(s.attacker)+
        ",\"targetAddress\":"+std::to_string(s.target)+",\"itemAddress\":"+std::to_string(s.item)+",\"weaponAddress\":"+std::to_string(s.weapon)+",\"weaponFormId\":"+std::to_string(s.weaponId)+
        ",\"retainedEventLimit\":"+std::to_string(DamageMaximumEventsPerLease)+",\"omittedBefore\":"+std::to_string(s.omittedBefore)+
        ",\"damageStage\":{\"version\":1,\"status\":"+Quote(complete?"observed":"partial")+",\"reason\":"+(reason?Quote(reason):"null")+
        ",\"route\":\"009B5170/004BDF00/00644CE0\",\"codeProfile\":\"pc-retail-damage-v1\",\"returnAddress\":"+std::to_string(stage.returnAddress)+
        ",\"frameAddress\":"+std::to_string(stage.frame)+",\"callerFrameAddress\":"+std::to_string(stage.callerFrame)+
        ",\"x87ControlWord\":"+std::to_string(stage.entryControl)+",\"x87ReturnControlWord\":"+std::to_string(stage.returnControl)+
        ",\"arguments\":"+arguments+",\"inputs\":"+inputs+",\"output\":"+output+
        ",\"operandFrameRawHex\":"+Quote(ActorHex(f.raw.data(),f.raw.size()))+
        ",\"weaponRawHex\":"+Quote(ActorHex(stage.weaponBefore.data(),stage.weaponBefore.size()))+
        ",\"hitReturnRawHex\":"+Quote(ActorHex(s.hitAfter.data(),s.hitAfter.size()))+
        ",\"hitReturnHealthDamage\":"+(s.hitRepeated?DamageScalar(damage::U32(s.hitAfter.data()+0x14),s.hit+0x14):ActorUnavailable("hit-identity-changed"))+
        ",\"hitReturnFlags\":"+(s.hitRepeated?DamageUInt(damage::U32(s.hitAfter.data()+0x58),s.hit+0x58):ActorUnavailable("hit-identity-changed"))+
        ",\"hitIdentityRepeated\":"+(s.hitRepeated?"true":"false")+
        ",\"codeProfileMatched\":"+(s.codeEntry && s.codeReturn?"true":"false")+
        ",\"constantsRepeated\":"+(s.codeEntry && s.codeReturn?"true":"false")+
        ",\"meleeOperandCalls\":"+std::to_string(stage.meleeCalls)+",\"limbOperandCalls\":"+std::to_string(stage.limbCalls)+
        ",\"inputsRepeated\":"+(stage.settingsStable && stage.weaponStable?"true":"false")+",\"returnIsFinalHealthDamage\":false}",s.capture);
}
void DamageHitExit(const UInt32* saved) {
    if(g_damageOverflow){--g_damageOverflow;return;}
    if(!g_damageDepth)return;
    auto& s=g_damageScopes[g_damageDepth-1];
    if(saved[2]!=s.hitFrame){s.invalid=true;DamageCount(s.coverage,damage::InvalidReturn);--g_damageDepth;++g_damageOmitted;return;}
    if(DamageAlive(s)) {
        std::array<UInt32,5> again{};UInt32 returnAddress=0;
        UInt32 itemType=0;
        const bool same=DamageStack(s.hitFrame,28) && DamageRead(s.hitFrame+4,&returnAddress,4) && returnAddress==s.returnAddress &&
            DamageRead(s.hitFrame+8,again.data(),20) && again==s.hitArguments && DamageParticipants(s) &&
            s.weaponBound && (!s.item || (DamageRead(s.item+8,&itemType,4) && itemType==s.weapon && DamageForm(s.weapon,s.weaponId,0x28))) &&
            DamageRead(s.hit,s.hitAfter.data(),s.hitAfter.size()) && damage::U32(s.hitAfter.data())==s.attacker &&
            damage::U32(s.hitAfter.data()+4)==s.target && damage::U32(s.hitAfter.data()+0x30)==s.weapon;
        s.hitRepeated=same;s.codeReturn=DamageCodeMatches(true);
        if(!same || !s.codeReturn){s.stage.invalid=true;DamageCount(s.coverage,damage::InvalidReturn);}
        DamageEmit(s);
        if(g_damageHitFinished)g_damageHitFinished(s);
    } else if(s.selected)DamageCount(s.coverage,damage::InvalidReturn);
    --g_damageDepth;
}
void __cdecl DamageObserve(UInt32 site,const UInt32* saved,const std::uint8_t* fx) noexcept {
    try {
        if(site==damage::HitEntry) {
            const auto coverage=DamageRouteEntry();
            if(GetCurrentThreadId()!=g_damageThread){DamageCount(coverage,damage::WrongThread);return;}
            DamageHitEnter(saved,coverage);return;
        }
        if(GetCurrentThreadId()!=g_damageThread)return;
        if(site==damage::HitReturn){DamageHitExit(saved);return;}
        auto* scope=DamageActiveScope();if(!scope)return;
        if(site==damage::StageEntry)DamageStageEnter(*scope,saved,fx);
        else DamageStageOperand(*scope,site,saved,fx);
    } catch(...) {if(g_damageDepth){auto& s=g_damageScopes[g_damageDepth-1];s.invalid=true;DamageCount(s.coverage,damage::Exception);}++g_damageOmitted;}
}
// Preserve all GPRs, EFLAGS, x87/MMX/XMM, MXCSR and LastError. Observation runs
// with clear DF and neutral FP state. The caller's state is restored verbatim.
#define DAMAGE_PROBE_BODY(Index) \
    __asm { pushfd } __asm { pushad } __asm { mov ebx,esp } \
    __asm { sub esp,544 } __asm { and esp,0FFFFFFF0h } __asm { fxsave [esp] } \
    __asm { fninit } __asm { ldmxcsr [DamageNeutralMxcsr] } __asm { cld } \
    __asm { call GetLastError } __asm { mov [esp+512],eax } __asm { mov eax,esp } \
    __asm { push eax } __asm { push ebx } __asm { push Index } __asm { call DamageObserve } __asm { add esp,12 } \
    __asm { push dword ptr [esp+512] } __asm { call SetLastError } __asm { fxrstor [esp] } \
    __asm { mov esp,ebx } __asm { popad } __asm { popfd } \
    __asm { jmp dword ptr [g_damageTrampolines+Index*4] }
__declspec(naked) void DamageHook0(){DAMAGE_PROBE_BODY(0)}
__declspec(naked) void DamageHook1(){DAMAGE_PROBE_BODY(1)}
__declspec(naked) void DamageHook2(){DAMAGE_PROBE_BODY(2)}
__declspec(naked) void DamageHook3(){DAMAGE_PROBE_BODY(3)}
__declspec(naked) void DamageHook4(){DAMAGE_PROBE_BODY(4)}
__declspec(naked) void DamageHook5(){__asm {fstp dword ptr [ebp-74h]} __asm {fld dword ptr [ebp-74h]} DAMAGE_PROBE_BODY(5)}
#undef DAMAGE_PROBE_BODY
UInt32 DamageWrapper(unsigned index) noexcept {
    const UInt32 hooks[]={reinterpret_cast<UInt32>(&DamageHook0),reinterpret_cast<UInt32>(&DamageHook1),reinterpret_cast<UInt32>(&DamageHook2),reinterpret_cast<UInt32>(&DamageHook3),reinterpret_cast<UInt32>(&DamageHook4),reinterpret_cast<UInt32>(&DamageHook5)};
    return index<damage::SiteCount?hooks[index]:0;
}
std::array<condition_patch::Span,condition_patch::Count> DamagePlan(bool restoring) noexcept {
    std::array<condition_patch::Span,condition_patch::Count> plan{};
    for(unsigned i=0;i<plan.size();++i) {
        auto& p=plan[i];p.address=damage::Sites[i];p.length=damage::Lengths[i];p.before=damage::Original[i];
        p.after[0]=0xE9;const UInt32 relative=DamageWrapper(i)-p.address-5;memcpy(p.after.data()+1,&relative,4);
        if(p.length==6)p.after[5]=0x90;
        if(restoring)std::swap(p.before,p.after);
    }
    return plan;
}
#include "RuntimeDamageInvocationProfile.inl"
ConditionPatchReport DamageTransact(bool restoring) noexcept {
    ConditionPatchReport report;report.restoring=restoring;const auto plan=DamagePlan(restoring);
    ConditionThreadSet threads;ConditionPages pages;ConditionNativeThreads native;
    if(!ConditionPrepareNativeThreads(native)){report.phase=ConditionPatchPhase::ThreadRejected;return report;}
    if(!ConditionPrepareThreads(threads)){report.phase=ConditionPatchPhase::ThreadRejected;VirtualFree(native.buffer,0,MEM_RELEASE);return report;}
    report.threads=static_cast<UInt32>(threads.count);
    if(!ConditionPreparePages(pages,plan) || !ConditionOpenPages(pages)) {
        report.phase=ConditionPatchPhase::PageRejected;report.protectionRestoreFailures=ConditionClosePages(pages);
        ConditionCloseThreads(threads);VirtualFree(native.buffer,0,MEM_RELEASE);return report;
    }
    const bool admitted=ConditionSuspendThreads(threads,plan) && ConditionVerifyFrozenThreads(native,threads);
    report.suspended=static_cast<UInt32>(threads.suspended);
    if(admitted) {
        report.transaction=condition_patch::Apply(plan,{nullptr,ConditionPatchRead,ConditionPatchWrite,ConditionPatchFlush});
        report.phase=report.transaction.status==condition_patch::Status::Committed?(restoring?ConditionPatchPhase::Restored:ConditionPatchPhase::Installed):ConditionPatchPhase::TransactionFailed;
    } else report.phase=ConditionPatchPhase::ThreadRejected;
    report.protectionRestoreFailures=ConditionClosePages(pages);report.resumeFailures=ConditionResumeThreads(threads);
    ConditionCloseThreads(threads);VirtualFree(native.buffer,0,MEM_RELEASE);return report;
}
ConditionPatchReport g_damagePatchReport; // Game-thread writer only.
std::atomic<bool> g_damageMayBeOwned{false};
std::atomic<UInt32> g_damageReportSequence{0};
std::array<std::atomic<UInt32>,6> g_damageReportWords{};
void DamageSetPatchReport(const ConditionPatchReport& report) noexcept {g_damagePatchReport=report;}
void DamagePublishState() noexcept {
    const std::array<UInt32,6> words{{static_cast<UInt32>(g_damagePatchReport.phase),
        static_cast<UInt32>(g_damagePatchReport.transaction.status),g_damagePatchReport.protectionRestoreFailures,
        g_damagePatchReport.resumeFailures,g_damageMayBeOwned?1u:0u,g_damageInstalled?1u:0u}};
    ++g_damageReportSequence;
    for(size_t i=0;i<words.size();++i)g_damageReportWords[i].store(words[i]);
    ++g_damageReportSequence;
}
struct DamagePublishGuard {~DamagePublishGuard() noexcept {DamagePublishState();}};
std::string DamageCapabilityFields() {
    std::array<UInt32,6> words{};bool stable=false;
    for(unsigned attempt=0;attempt<3 && !stable;++attempt) {
        const auto sequence=g_damageReportSequence.load();if(sequence&1)continue;
        for(size_t i=0;i<words.size();++i)words[i]=g_damageReportWords[i].load();
        stable=g_damageReportSequence.load()==sequence;
    }
    if(!stable)return ",\"damageInvocation\":{\"version\":1,\"status\":\"changing\"}";
    return ",\"damageInvocation\":{\"status\":"+Quote(words[5]?"available":DamageInvocationReviewed?"unavailable":"unreviewed")+
        ",\"version\":1,\"profile\":\"pc-retail-damage-v1\",\"patchPhase\":"+std::to_string(words[0])+
        ",\"patchStatus\":"+std::to_string(words[1])+
        ",\"protectionRestoreFailures\":"+std::to_string(words[2])+
        ",\"resumeFailures\":"+std::to_string(words[3])+",\"retainedOwnedBytesPossible\":"+(words[4]?"true":"false")+
        ",\"eventLimitPerLease\":"+std::to_string(DamageMaximumEventsPerLease)+"}";
}
void ServiceDamageInvocation() {
    if(g_damageOmitted==g_damageReportedOmitted || !g_capture || !g_connected)return;
    g_damageReportedOmitted=g_damageOmitted;
    Emit("damage-coverage",0,",\"status\":\"partial\",\"leaseId\":"+std::to_string(g_damageLeaseSeen)+
        ",\"retained\":"+std::to_string(g_damageRetained)+",\"omitted\":"+std::to_string(g_damageOmitted)+
        ",\"limit\":"+std::to_string(DamageMaximumEventsPerLease)+",\"reason\":\"bounded-observation-omission\"");
}
void InstallDamageInvocation() {
    g_combatInvocationCoverage=DamageInvocationCoverage;
    g_damageThread=GetCurrentThreadId();
    if(!DamageInvocationReviewed || g_damageAttempted || !g_loadedGameObserved || !g_pcLayoutVerified)return;
    g_damageAttempted=true;
    DamagePublishGuard publish;
    ConditionLastError error{GetLastError()};CommandFloatingGuard floating;
    HMODULE pinned=nullptr;
    if(!DamageCodeMatches(false) || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(DamageWrapper(0)),&pinned)) {
        ConditionPatchReport report;report.phase=ConditionPatchPhase::ProfileRejected;DamageSetPatchReport(report);return;
    }
    auto* memory=static_cast<std::uint8_t*>(VirtualAlloc(nullptr,damage::SiteCount*16,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if(!memory)return;
    for(unsigned i=0;i<damage::SiteCount;++i) {
        auto* entry=memory+i*16;const auto length=damage::Lengths[i];
        // StageReturn executed both displaced instructions before observation.
        const auto copied=i==damage::StageReturn?0u:length;
        if(copied)memcpy(entry,damage::Original[i].data(),copied);
        entry[copied]=0xE9;const UInt32 back=damage::Sites[i]+length-reinterpret_cast<UInt32>(entry+copied+5);memcpy(entry+copied+1,&back,4);
        g_damageTrampolines[i]=reinterpret_cast<UInt32>(entry);
    }
    DWORD previous=0;
    if(!VirtualProtect(memory,damage::SiteCount*16,PAGE_EXECUTE_READ,&previous) || !FlushInstructionCache(GetCurrentProcess(),memory,damage::SiteCount*16)) {
        g_damageTrampolines={};VirtualFree(memory,0,MEM_RELEASE);return;
    }
    const auto report=DamageTransact(false);DamageSetPatchReport(report);
    g_damageMayBeOwned=report.transaction.attemptedMask!=0 && !report.transaction.bytesRestored;
    g_damageInstalled=report.phase==ConditionPatchPhase::Installed && !report.resumeFailures &&
        !report.protectionRestoreFailures && DamageCodeMatches(true);
    // Retain trampolines through process exit, including uncertain transactions.
}
void RestoreDamageInvocation() {
    if(!g_damageMayBeOwned || g_damageDepth)return;
    DamagePublishGuard publish;
    ConditionLastError error{GetLastError()};CommandFloatingGuard floating;
    g_damageInstalled=false;const auto report=DamageTransact(true);DamageSetPatchReport(report);
    if(report.phase==ConditionPatchPhase::Restored && !report.resumeFailures && !report.protectionRestoreFailures)g_damageMayBeOwned=false;
}
