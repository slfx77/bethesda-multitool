// Equipped-weapon pre-modifier CritChance observation. No mutations or hit-caller fallback.
// Whole retained bodies are compared with the current main-module mappings before invocation.
struct CriticalCode { UInt32 address; const char* hex; bool executable; };
constexpr CriticalCode CriticalStageCode={0x646D80,"558bec83ec1c837d0c00740d8b4d0ce8fcf5dfff8945e8eb07c745e8ffffffff8b45e88945ecd9e8d95df4c645ff00837d0c00741a8b4d0ce883ddedff0fb6c885c9740b8b4d0ce874a81d00d95df4d945f4dc1d60200101dfe0f6c4447a05d9e8d95df4837d080074146a0e8b55088b028b4d088b500cffd2d95de4eb05d9eed95de4d945e4d95df0d945f0d875f4d95df8837d0c0074238b4d0ce890b02800dc1d60200101dfe0f6c401750e8b4d0ce87bb02800d84df8d95df8d945f88be55dc3",true};
constexpr CriticalCode CriticalCurrentCode={0x93ACB0,"558bec83ec20894dfc8b4508508b4dfc8b55fc8b028b5004ffd28b4508506a008b4dfc81e9a4000000dd5df4e8ef160100dc45f48b4d08516a018b4dfc81e9a4000000dd5dece8d5160100dc45ec8b5508526a028b4dfc81e9a4000000dd5de4e8bb160100dc45e4d95de0d945e08be55dc20400",true};
constexpr CriticalCode CriticalHelpers[]={
    {0x446390,"558bec51894dfc8b45fc0fbe80f40000008be55dc3",true},
    {0x524B40,"558bec51894dfc8b45fc0fb6880001000083e1020f95c08be55dc3",true},
    {0x821640,"558bec51894dfc8b45fcd980340100008be55dc3",true},
    {0x8D1EB0,"558bec51894dfc8b45fcd980c40100008be55dc3",true},
    {0x1012060,"0000000000000000",false},
};
struct CriticalIdentity {
    UInt32 actor=0,actorTable=0,base=0,baseTable=0,owner=0,ownerTable=0,getter=0;
    std::uint8_t actorType=0,baseType=0;
};
struct CriticalWeapon {
    UInt32 address=0,id=0,table=0;
    std::uint8_t type=0,raw[10]{};
};
struct CriticalObservation {
    bool observed=false,identityRead=false,weaponRead=false,proofRead=false,currentRead=false,afterRead=false,stageRead=false;
    bool identityStable=false,cwBeforeRead=false,cwAfterRead=false,weaponAfterRead=false;
    const char* reason="critical-profile-unavailable";
    CriticalIdentity identity;
    CriticalWeapon before,after;
    float current=0,currentAfter=0,stage=0;
    std::uint16_t cwBefore=0,cwAfter=0;
};
UInt32 CriticalFloatBits(float value) {UInt32 bits=0;memcpy(&bits,&value,4);return bits;}
bool SameCriticalIdentity(const CriticalIdentity& a,const CriticalIdentity& b) {
    return a.actor==b.actor && a.actorTable==b.actorTable && a.base==b.base && a.baseTable==b.baseTable &&
        a.owner==b.owner && a.ownerTable==b.ownerTable && a.getter==b.getter && a.actorType==b.actorType && a.baseType==b.baseType;
}
bool SameCriticalWeapon(const CriticalWeapon& a,const CriticalWeapon& b) {
    return a.address==b.address && a.id==b.id && a.table==b.table && a.type==b.type && !memcmp(a.raw,b.raw,sizeof(a.raw));
}
template<class Reader,class Mapper> bool CriticalMatchCode(const CriticalCode& code,Reader& read,Mapper& mapped) {
    const size_t length=strlen(code.hex)/2;std::uint8_t bytes[194]{};
    return length<=sizeof(bytes) && mapped(code.address,length,code.executable) && read(code.address,bytes,length) &&
        ActorHex(bytes,length)==code.hex;
}
template<class Reader,class Mapper> bool CriticalProof(Reader& read,Mapper& mapped) {
    if(!CriticalMatchCode(CriticalStageCode,read,mapped) || !CriticalMatchCode(CriticalCurrentCode,read,mapped))return false;
    for(const auto& code:CriticalHelpers)if(!CriticalMatchCode(code,read,mapped))return false;
    return true;
}
template<class Reader,class Mapper> bool ReadCriticalIdentity(UInt32 actor,Reader& read,Mapper& mapped,CriticalIdentity& out) {
    std::uint8_t reference[0x24]{},base[16]{};UInt32 ownerTable=0,getter=0,baseGetter=0;
    if(!ActorEffectRange(actor,0xA8) || !read(actor,reference,sizeof(reference)) || reference[4]!=0x3B ||
       ActorRaw<UInt32>(reference,12)!=0x14)return false;
    const UInt32 baseAddress=ActorRaw<UInt32>(reference,0x20),actorTable=ActorRaw<UInt32>(reference,0);
    if(!ActorEffectRange(baseAddress,sizeof(base)) || !read(baseAddress,base,sizeof(base)) || base[4]!=0x2A ||
       ActorRaw<UInt32>(base,12)!=7 || !actorTable || !ActorRaw<UInt32>(base,0) ||
       !read(actor+0xA4,&ownerTable,4) || ownerTable!=0x108A974 || !mapped(ownerTable,0x10,false) ||
       !read(ownerTable+4,&baseGetter,4) || baseGetter!=0x8803A0 ||
       !read(ownerTable+0xC,&getter,4) || getter!=CriticalCurrentCode.address)return false;
    out={actor,actorTable,baseAddress,ActorRaw<UInt32>(base,0),actor+0xA4,ownerTable,getter,reference[4],base[4]};return true;
}
template<class Reader,class Equipped> bool ReadCriticalWeapon(Reader& read,Equipped& equipped,CriticalWeapon& out) {
    UInt32 pointer=0,id=0;std::uint8_t type=0;
    if(!equipped(pointer,id,type))return false;
    out={};if(!pointer)return id==0 && type==0;
    std::uint8_t header[16]{};
    if(!ActorEffectRange(pointer,0x1C8) || !read(pointer,header,sizeof(header)) || !id || type!=0x28 ||
       header[4]!=type || ActorRaw<UInt32>(header,12)!=id || !ActorRaw<UInt32>(header,0) ||
       !read(pointer+0xF4,out.raw,1) || !read(pointer+0x100,out.raw+1,1) ||
       !read(pointer+0x134,out.raw+2,4) || !read(pointer+0x1C4,out.raw+6,4))return false;
    out.address=pointer;out.id=id;out.type=type;out.table=ActorRaw<UInt32>(header,0);
    const float rate=ActorRaw<float>(out.raw,2),multiplier=ActorRaw<float>(out.raw,6);
    return std::isfinite(multiplier) && (!(out.raw[1]&2) || std::isfinite(rate));
}
template<class Reader,class Mapper,class Equipped,class Current,class Stage,class Context,class ControlWord>
CriticalObservation ReadWeaponCriticalStage(bool verified,UInt32 actor,UInt32 id,UInt32 baseId,
    Reader&& read,Mapper&& mapped,Equipped&& equipped,Current&& current,Stage&& stage,Context&& context,ControlWord&& controlWord) {
    CriticalObservation out;
    if(!verified || !context())return out;
    if(id!=0x14 || baseId!=7){out.reason="critical-current-getter-role-unavailable";return out;}
    if(!ReadCriticalIdentity(actor,read,mapped,out.identity)){out.reason="critical-owner-identity-unavailable";return out;}
    out.identityRead=true;
    if(!CriticalProof(read,mapped)){out.reason="critical-code-or-constant-mismatch";return out;}
    out.proofRead=true;
    if(!ReadCriticalWeapon(read,equipped,out.before)){out.reason="critical-equipped-weapon-unavailable";return out;}
    out.weaponRead=true;out.cwBefore=controlWord();out.cwBeforeRead=true;
    if(out.cwBefore!=0x7F){out.reason="critical-floating-profile-unavailable";return out;}
    CriticalIdentity repeated;CriticalWeapon weaponRepeated;
    // SDK lookup can execute callbacks. Recheck owner, weapon and loaded code before any direct call.
    if(!context() || !ReadCriticalIdentity(actor,read,mapped,repeated) || !SameCriticalIdentity(out.identity,repeated) ||
       !CriticalProof(read,mapped)){out.reason="critical-admission-changed";return out;}
    out.current=current(out.identity.getter,out.identity.owner,14);out.currentRead=true;
    if(!std::isfinite(out.current)){out.reason="critical-current-nonfinite";return out;}
    if(!context() || !ReadCriticalIdentity(actor,read,mapped,repeated) || !SameCriticalIdentity(out.identity,repeated) ||
       !ReadCriticalWeapon(read,equipped,weaponRepeated) || !SameCriticalWeapon(out.before,weaponRepeated) ||
       !CriticalProof(read,mapped) || !context() || !ReadCriticalIdentity(actor,read,mapped,repeated) ||
       !SameCriticalIdentity(out.identity,repeated) || controlWord()!=out.cwBefore){out.reason="critical-before-stage-changed";return out;}
    out.stage=stage(CriticalStageCode.address,out.identity.owner,out.before.address);out.stageRead=true;
    if(!context() || !ReadCriticalIdentity(actor,read,mapped,repeated) || !SameCriticalIdentity(out.identity,repeated) ||
       !CriticalProof(read,mapped) || controlWord()!=out.cwBefore){out.reason="critical-after-stage-changed";return out;}
    out.currentAfter=current(out.identity.getter,out.identity.owner,14);out.afterRead=true;
    if(!ReadCriticalWeapon(read,equipped,out.after)){out.reason="critical-equipped-repeat-unavailable";return out;}
    out.weaponAfterRead=true;out.cwAfter=controlWord();out.cwAfterRead=true;
    if(!context() || !ReadCriticalIdentity(actor,read,mapped,repeated) || !SameCriticalIdentity(out.identity,repeated) ||
       !SameCriticalWeapon(out.before,out.after) || !CriticalProof(read,mapped) || out.cwAfter!=out.cwBefore){
        out.reason="critical-final-identity-or-input-changed";return out;
    }
    out.identityStable=true;
    if(!std::isfinite(out.currentAfter) || CriticalFloatBits(out.current)!=CriticalFloatBits(out.currentAfter)){
        out.reason="critical-current-bracket-changed";return out;
    }
    if(!std::isfinite(out.stage)){out.reason="critical-stage-nonfinite";return out;}
    out.observed=true;out.reason=nullptr;return out;
}
std::string CriticalObservationJson(const CriticalObservation& out,UInt32 actor,UInt32 id,UInt32 baseId,
    const std::string& executable,std::uint64_t capture,std::uint64_t connection,std::uint64_t epoch) {
    auto integer=[](bool present,UInt32 value){return present?std::to_string(value):"null";};
    auto number=[](bool present,float value){return present && std::isfinite(value)?NumberField(value):"null";};
    auto hex=[](bool present,const std::uint8_t* bytes,size_t length){return present?Quote(ActorHex(bytes,length)):"null";};
    const bool weapon=out.weaponRead && out.before.address!=0;
    std::string helpers;
    if(out.proofRead)for(const auto& code:CriticalHelpers) {
        if(!helpers.empty())helpers+=",";
        helpers+="{\"address\":"+std::to_string(code.address)+",\"hex\":"+Quote(code.hex)+"}";
    }
    return "{\"schemaVersion\":1,\"status\":"+Quote(out.observed?"observed":"unavailable")+
        ",\"scope\":\"equipped-weapon-pre-modifier\",\"reason\":"+(out.reason?Quote(out.reason):"null")+
        ",\"executableSha256\":"+Quote(executable)+",\"actorAddress\":"+std::to_string(actor)+
        ",\"baseAddress\":"+integer(out.identityRead,out.identity.base)+",\"engineTargetFormId\":"+std::to_string(id)+
        ",\"baseFormId\":"+std::to_string(baseId)+",\"actorFormType\":"+integer(out.identityRead,out.identity.actorType)+
        ",\"baseFormType\":"+integer(out.identityRead,out.identity.baseType)+
        ",\"actorValueOwnerAddress\":"+integer(out.identityRead,out.identity.owner)+
        ",\"actorValueOwnerVtable\":"+integer(out.identityRead,out.identity.ownerTable)+
        ",\"currentGetterSlot\":"+integer(out.identityRead,out.identity.ownerTable+0xC)+
        ",\"currentGetter\":"+integer(out.identityRead,out.identity.getter)+
        ",\"currentGetterHex\":"+(out.proofRead?Quote(CriticalCurrentCode.hex):"null")+
        ",\"currentCriticalChance\":"+number(out.currentRead,out.current)+
        ",\"currentCriticalChanceBeforeBits\":"+integer(out.currentRead,CriticalFloatBits(out.current))+
        ",\"currentCriticalChanceAfter\":"+number(out.afterRead,out.currentAfter)+
        ",\"currentCriticalChanceAfterBits\":"+integer(out.afterRead,CriticalFloatBits(out.currentAfter))+
        ",\"weaponPresent\":"+(out.weaponRead?(weapon?"true":"false"):"null")+
        ",\"weaponAddress\":"+integer(weapon,out.before.address)+",\"weaponFormId\":"+integer(weapon,out.before.id)+
        ",\"weaponFormType\":"+integer(weapon,out.before.type)+",\"weaponFlags\":"+integer(weapon,out.before.raw[1])+
        ",\"weaponFlagsHex\":"+hex(weapon,out.before.raw+1,1)+
        ",\"fireRate\":"+number(weapon,ActorRaw<float>(out.before.raw,2))+",\"fireRateHex\":"+hex(weapon,out.before.raw+2,4)+
        ",\"criticalMultiplier\":"+number(weapon,ActorRaw<float>(out.before.raw,6))+
        ",\"criticalMultiplierHex\":"+hex(weapon,out.before.raw+6,4)+
        ",\"weaponFieldsBeforeHex\":"+hex(weapon,out.before.raw,sizeof(out.before.raw))+
        ",\"weaponFieldsAfterHex\":"+hex(out.weaponAfterRead && out.after.address!=0,out.after.raw,sizeof(out.after.raw))+
        ",\"x87ControlWordBefore\":"+integer(out.cwBeforeRead,out.cwBefore)+
        ",\"x87ControlWordAfter\":"+integer(out.cwAfterRead,out.cwAfter)+
        ",\"routineAddress\":"+std::to_string(CriticalStageCode.address)+
        ",\"routineHex\":"+(out.proofRead?Quote(CriticalStageCode.hex):"null")+",\"helperEvidence\":["+helpers+"]"+
        ",\"stageValue\":"+number(out.stageRead,out.stage)+",\"stageValueBits\":"+integer(out.stageRead,CriticalFloatBits(out.stage))+
        ",\"identityStable\":"+(out.identityStable?"true":"false")+
        ",\"readConsistency\":"+Quote(out.observed?"bracketed-equal":"unavailable")+
        ",\"equippedWeaponRoute\":\"sdk-explicit-owner-GetEquippedObject-slot5\""+
        ",\"captureGeneration\":"+std::to_string(capture)+",\"connectionGeneration\":"+std::to_string(connection)+
        ",\"loadEpoch\":"+std::to_string(epoch)+"}";
}
// Save without replacing CW: the observed stage runs under the actual game-thread profile.
struct CriticalEnvironmentGuard {
    DWORD lastError;unsigned char reserved[12]{};CommandFloatingState floating;
    CriticalEnvironmentGuard() noexcept:lastError(GetLastError()){floating.Save();}
    ~CriticalEnvironmentGuard(){floating.Restore();SetLastError(lastError);}
};
static_assert(offsetof(CriticalEnvironmentGuard,floating)==16 && alignof(CriticalEnvironmentGuard)==16,
    "Critical FXSAVE storage must remain 16-byte aligned");
float InvokeCriticalCurrent(UInt32 address,UInt32 owner,UInt32 code) {
    using Getter=float(__thiscall*)(void*,UInt32);
    return reinterpret_cast<Getter>(address)(reinterpret_cast<void*>(owner),code);
}
float InvokeCriticalStage(UInt32 address,UInt32 owner,UInt32 weapon) {
    using Stage=float(__cdecl*)(void*,void*);
    return reinterpret_cast<Stage>(address)(reinterpret_cast<void*>(owner),reinterpret_cast<void*>(weapon));
}
std::string ActorWeaponCriticalStage(std::uintptr_t actor,UInt32 id,UInt32 baseId) {
    CriticalEnvironmentGuard environment;
    const auto capture=g_captureGeneration.load(),connection=g_connectionGeneration.load(),epoch=g_gameLoadEpoch.load();
    auto context=[&](){return g_capture && g_connected && g_loadedGameObserved && capture==g_captureGeneration.load() &&
        connection==g_connectionGeneration.load() && epoch==g_gameLoadEpoch.load() && LookupRuntimeForm(id)==reinterpret_cast<void*>(actor);};
    TypedForm subject;subject.pointer=reinterpret_cast<void*>(actor);subject.id=id;subject.baseId=baseId;subject.type=0x3B;subject.baseType=0x2A;
    const auto out=ReadWeaponCriticalStage(g_pcLayoutVerified,static_cast<UInt32>(actor),id,baseId,
        [](std::uintptr_t address,void* bytes,size_t length){SIZE_T copied=0;return ActorEffectRange(address,length) &&
            ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),bytes,length,&copied) && copied==length;},
        ActorEffectMapped,[&](UInt32& address,UInt32& formId,std::uint8_t& formType){void* form=nullptr;std::string reason;
            const bool ok=TypedFormValue("owner.GetEquippedObject number",subject,nullptr,5,form,formId,formType,reason);
            address=reinterpret_cast<UInt32>(form);return ok;},
        InvokeCriticalCurrent,InvokeCriticalStage,
        context,ActorX87ControlWord);
    return CriticalObservationJson(out,static_cast<UInt32>(actor),id,baseId,g_executableSha256,capture,connection,epoch);
}
bool AppendWeaponCriticalStage(std::string& actorState,const std::string& critical) {
    constexpr size_t envelopeReserve=512;
    const std::string key=",\"weaponCriticalStage\":";
    const std::string unavailable="{\"schemaVersion\":1,\"status\":\"unavailable\",\"scope\":\"equipped-weapon-pre-modifier\",\"reason\":\"actor-state-payload-limit\"}";
    const auto& chosen=actorState.size()+key.size()+critical.size()<=MaxPayload-envelopeReserve?critical:unavailable;
    if(actorState.size()+key.size()+chosen.size()>MaxPayload-envelopeReserve)return false;
    actorState+=key+chosen;return true;
}
