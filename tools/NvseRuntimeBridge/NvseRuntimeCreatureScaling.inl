// Read-only, repeated raw inputs for retained PC creature Health scaling. No engine call.
struct CreatureScaleCode {UInt32 address;const char* hex;};
constexpr CreatureScaleCode CreatureScaleCodes[]={
    {0x461560,"558bec51894dfc68800000008b4dfce80c0000008be55dc3"},
    {0x461580,"558bec51894dfc8b45fc8b4804234d080f95c08be55dc20400"},
    {0x47D370,"558bec51894dfc8b45fc668b400c8be55dc3"},
    {0x47D390,"558bec51894dfc8b45fc668b400e8be55dc3"},
    {0x47D3B0,"558bec51894dfc8b45fc668b40108be55dc3"},
    {0x47DED0,"558bec83ec2c56894dec8b4dece88ef4ffff668945fc8b4dece87236feff0fb6c085c00f84de0000000fb74dfc894de8db45e8dc35707b0101d95df8c745f4000000008b0d3cea1d01e8121533008945f0837df000740b8b55f083c2308955e4eb07c745e4000000008b45e48945f4837df40074348b4df4e823f4ffff0fb7c8894de0db45e0d84df8d97dde0fb745de0d000c00008945d8d96dd8db5dd4d96dde668b55d4668955fc8b4dece80ff4ffff0fb7c085c07e210fb775fc8b4dece8fcf3ffff0fb7c83bf17d0e8b4dece8edf3ffff668945fceb2e8b4dece8fff3ffff0fb7d085d27e1f0fb775fc8b4dece8ecf3ffff0fb7c03bf07e0c8b4dece8ddf3ffff668945fc668b45fc5e8be55dc3"},
    {0x5F8E90,"558bec83ec0c56894df433c0668945fc8b4df481e980000000e8b286e6ff0fb6c885c974398b4df481e980000000e80d50e8ff668945f80fbf55f883fa017d09b801000000668945f80fbf75f88b4df4e88bd112000faff0668975fceb0c8b4df4e87ad11200668945fc0fb745fc5e8be55dc3"},
    {0x726070,"558bec51894dfc8b45fc8b40048be55dc3"},
    {0x7AF430,"558bec51894dfc8b45fc8b40208be55dc3"},
};
constexpr UInt32 CreatureScalePlayerSlot=0x11DEA3C;
constexpr UInt32 CreatureScaleDivisorAddress=0x1017B70;
constexpr const char* CreatureScaleDivisorHex="0000000000408f40";
struct CreatureScaleFields {
    UInt32 actorTable=0,base=0,baseOwnerTable=0,healthGetter=0;
    UInt32 player=0,playerBase=0,playerTable=0;
    std::uint8_t baseRaw[0xC4]{},playerRaw[0xC4]{};
};
struct CreatureScaleObservation {
    bool observed=false;
    const char* reason="creature-scaling-profile-unavailable";
    CreatureScaleFields before,after;
};
template<class Reader,class Mapper> bool CreatureScaleProof(Reader& read,Mapper& mapped) {
    std::uint8_t bytes[272]{};
    for(const auto& code:CreatureScaleCodes) {
        const size_t length=strlen(code.hex)/2;
        if(length>sizeof(bytes) || !mapped(code.address,length,true) || !read(code.address,bytes,length) ||
           ActorHex(bytes,length)!=code.hex)return false;
    }
    return mapped(CreatureScaleDivisorAddress,8,false) && read(CreatureScaleDivisorAddress,bytes,8) &&
        ActorHex(bytes,8)==CreatureScaleDivisorHex;
}
template<class Reader,class Mapper,class Lookup>
bool ReadCreatureScaleFields(UInt32 actor,UInt32 id,UInt32 baseId,Reader& read,Mapper& mapped,Lookup& lookup,CreatureScaleFields& out) {
    std::uint8_t reference[0x24]{},playerReference[0x24]{};
    if(!ActorEffectRange(actor,sizeof(reference)) || !read(actor,reference,sizeof(reference)) ||
       reference[4]!=0x3C || ActorRaw<UInt32>(reference,12)!=id || !lookup(id,actor))return false;
    out.actorTable=ActorRaw<UInt32>(reference,0);out.base=ActorRaw<UInt32>(reference,0x20);
    if(out.actorTable!=0x10870AC || !mapped(out.actorTable,4,false) || !ActorEffectRange(out.base,0x104) ||
       !read(out.base,out.baseRaw,sizeof(out.baseRaw)) || out.baseRaw[4]!=0x2B ||
       ActorRaw<UInt32>(out.baseRaw,12)!=baseId || !lookup(baseId,out.base) ||
       ActorRaw<UInt32>(out.baseRaw,0)!=0x1048F5C || !mapped(0x1048F5C,4,false) ||
       ActorRaw<UInt32>(out.baseRaw,0xB0)!=0x1048E6C || !mapped(0x1048E6C,0x14,false) ||
       !read(out.base+0x100,&out.baseOwnerTable,4) || out.baseOwnerTable!=0x1048DC8 ||
       !mapped(out.baseOwnerTable,4,false) || !read(0x1048E7C,&out.healthGetter,4) || out.healthGetter!=0x5F8E90)return false;
    if(!mapped(CreatureScalePlayerSlot,4,false) || !read(CreatureScalePlayerSlot,&out.player,4) ||
       !ActorEffectRange(out.player,sizeof(playerReference)) || !read(out.player,playerReference,sizeof(playerReference)) ||
       playerReference[4]!=0x3B || ActorRaw<UInt32>(playerReference,12)!=0x14 || !lookup(0x14,out.player))return false;
    out.playerTable=ActorRaw<UInt32>(playerReference,0);out.playerBase=ActorRaw<UInt32>(playerReference,0x20);
    return out.playerTable && mapped(out.playerTable,4,false) && ActorEffectRange(out.playerBase,sizeof(out.playerRaw)) &&
        read(out.playerBase,out.playerRaw,sizeof(out.playerRaw)) && out.playerRaw[4]==0x2A &&
        ActorRaw<UInt32>(out.playerRaw,12)==7 && lookup(7,out.playerBase) &&
        ActorRaw<UInt32>(out.playerRaw,0) && mapped(ActorRaw<UInt32>(out.playerRaw,0),4,false);
}
bool SameCreatureScaleFields(const CreatureScaleFields& a,const CreatureScaleFields& b) {
    return a.actorTable==b.actorTable && a.base==b.base && a.baseOwnerTable==b.baseOwnerTable && a.healthGetter==b.healthGetter &&
        a.player==b.player && a.playerBase==b.playerBase && a.playerTable==b.playerTable &&
        !memcmp(a.baseRaw,b.baseRaw,sizeof(a.baseRaw)) && !memcmp(a.playerRaw,b.playerRaw,sizeof(a.playerRaw));
}
template<class Reader,class Mapper,class Lookup,class Context>
CreatureScaleObservation ReadCreatureHealthScaling(bool verified,UInt32 actor,UInt32 id,UInt32 baseId,
    Reader&& read,Mapper&& mapped,Lookup&& lookup,Context&& context) {
    CreatureScaleObservation out;
    if(!verified || !context())return out;
    if(!id || !baseId || id==0x14 || id==UINT32_MAX || baseId==UINT32_MAX){out.reason="creature-scaling-role-unavailable";return out;}
    if(!ReadCreatureScaleFields(actor,id,baseId,read,mapped,lookup,out.before)){
        out.reason="creature-scaling-owner-or-player-unavailable";return out;
    }
    if(!CreatureScaleProof(read,mapped)){out.reason="creature-scaling-code-or-divisor-mismatch";return out;}
    if(!context() || !ReadCreatureScaleFields(actor,id,baseId,read,mapped,lookup,out.after) ||
       !SameCreatureScaleFields(out.before,out.after) || !CreatureScaleProof(read,mapped) || !context()){
        out.reason="creature-scaling-context-or-fields-changed";return out;
    }
    out.observed=true;out.reason=nullptr;return out;
}
std::string CreatureScaleJson(const CreatureScaleObservation& out,UInt32 actor,UInt32 id,UInt32 baseId,
    const std::string& executable,std::uint64_t capture,std::uint64_t connection,std::uint64_t epoch) {
    std::string row="{\"schemaVersion\":1,\"status\":"+Quote(out.observed?"observed":"unavailable")+
        ",\"scope\":\"creature-health-scaled-inputs\",\"reason\":"+(out.reason?Quote(out.reason):"null")+
        ",\"executableSha256\":"+Quote(executable)+",\"captureGeneration\":"+std::to_string(capture)+
        ",\"connectionGeneration\":"+std::to_string(connection)+",\"loadEpoch\":"+std::to_string(epoch)+
        ",\"noEngineCall\":true,\"inheritanceStatus\":\"not-evaluated\",\"identityStable\":"+(out.observed?"true":"false");
    if(!out.observed)return row+",\"readConsistency\":null,\"playerPresent\":null}";
    const auto& b=out.before;
    auto field=[&](const char* key,UInt32 value){row+=","+Quote(key)+":"+std::to_string(value);};
    row+=",\"readConsistency\":\"repeated-owner-fields\",\"playerPresent\":true";
    field("actorAddress",actor);field("engineTargetFormId",id);field("baseAddress",b.base);field("baseFormId",baseId);
    field("actorFormType",0x3C);field("baseFormType",0x2B);field("actorVtable",b.actorTable);
    field("baseVtable",ActorRaw<UInt32>(b.baseRaw,0));field("baseActorValueOwnerAddress",b.base+0x100);
    field("baseActorValueOwnerVtable",b.baseOwnerTable);field("healthComponentAddress",b.base+0xB0);
    field("healthComponentVtable",ActorRaw<UInt32>(b.baseRaw,0xB0));field("healthGetterSlot",0x1048E7C);field("healthGetter",b.healthGetter);
    field("playerSingletonAddress",CreatureScalePlayerSlot);field("playerAddress",b.player);field("playerFormId",0x14);
    field("playerFormType",0x3B);field("playerBaseAddress",b.playerBase);field("playerBaseFormId",7);field("playerBaseFormType",0x2A);
    field("playerStoredLevel",ActorRaw<std::uint16_t>(b.playerRaw,0x3C));
    field("flags",ActorRaw<UInt32>(b.baseRaw,0x34));field("levelEncodedUnsigned",ActorRaw<std::uint16_t>(b.baseRaw,0x3C));
    field("minimumLevel",ActorRaw<std::uint16_t>(b.baseRaw,0x3E));field("maximumLevel",ActorRaw<std::uint16_t>(b.baseRaw,0x40));
    field("templateFlags",ActorRaw<std::uint16_t>(b.baseRaw,0x4A));field("templatePointer",ActorRaw<UInt32>(b.baseRaw,0x54));
    field("storedHealth",ActorRaw<UInt32>(b.baseRaw,0xB4));
    row+=",\"baseRawBeforeHex\":"+Quote(ActorHex(b.baseRaw,sizeof(b.baseRaw)))+
        ",\"baseRawAfterHex\":"+Quote(ActorHex(out.after.baseRaw,sizeof(out.after.baseRaw)))+
        ",\"playerBaseRawBeforeHex\":"+Quote(ActorHex(b.playerRaw,sizeof(b.playerRaw)))+
        ",\"playerBaseRawAfterHex\":"+Quote(ActorHex(out.after.playerRaw,sizeof(out.after.playerRaw)))+
        ",\"levelDivisorAddress\":"+std::to_string(CreatureScaleDivisorAddress)+",\"levelDivisorHex\":"+Quote(CreatureScaleDivisorHex)+
        ",\"codeEvidence\":[";
    bool first=true;for(const auto& code:CreatureScaleCodes){if(!first)row+=",";first=false;
        row+="{\"address\":"+std::to_string(code.address)+",\"hex\":"+Quote(code.hex)+"}";}
    return row+"]}";
}
std::string ActorCreatureHealthScaling(std::uintptr_t actor,UInt32 id,UInt32 baseId) {
    const auto error=GetLastError();struct ErrorGuard{DWORD value;~ErrorGuard(){SetLastError(value);}} guard{error};
    const auto capture=g_captureGeneration.load(),connection=g_connectionGeneration.load(),epoch=g_gameLoadEpoch.load();
    auto context=[&](){return g_capture && g_connected && g_loadedGameObserved && capture==g_captureGeneration.load() &&
        connection==g_connectionGeneration.load() && epoch==g_gameLoadEpoch.load();};
    const auto out=ReadCreatureHealthScaling(g_pcLayoutVerified,static_cast<UInt32>(actor),id,baseId,
        [](std::uintptr_t address,void* bytes,size_t length){SIZE_T copied=0;return ActorEffectRange(address,length) &&
            ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),bytes,length,&copied) && copied==length;},
        ActorEffectMapped,[](UInt32 formId,UInt32 pointer){return LookupRuntimeForm(formId)==reinterpret_cast<void*>(pointer);},context);
    return CreatureScaleJson(out,static_cast<UInt32>(actor),id,baseId,g_executableSha256,capture,connection,epoch);
}
bool AppendCreatureHealthScaling(std::string& actorState,const std::string& observation) {
    constexpr size_t envelopeReserve=512;
    const std::string key=",\"creatureHealthScaling\":";
    const std::string unavailable="{\"schemaVersion\":1,\"status\":\"unavailable\",\"scope\":\"creature-health-scaled-inputs\",\"reason\":\"actor-state-payload-limit\"}";
    const auto& chosen=actorState.size()+key.size()+observation.size()<=MaxPayload-envelopeReserve?observation:unavailable;
    if(actorState.size()+key.size()+chosen.size()>MaxPayload-envelopeReserve)return false;
    actorState+=key+chosen;return true;
}
