// PC012 loaded getter bodies; call ABI is independently retained at 008803A0.
// Evidence: actor-calculation/base-override-review-001 and pc/session-012/override-memory.json.
struct ActorOverrideRoutine { std::uintptr_t address; const char* hex; };
constexpr ActorOverrideRoutine ActorOverrideRoutines[]={
    {0x94C640,"558bec51894dfc8b450c508b4d08518b4dfce80940f3ff8be55dc20800"},
    {0x880660,"558bec83ec08894df88b450c500fb64d08518b4df881c1e0000000e860700b00d95dfcd945fc8be55dc20800"},
    {0x9376E0,"558bec83ec0c894df80fb64508508b4df8e86a0000008945fc837dfc000f95c18b550c880a837dfc00740d8b4dfce81d1ad8ffd95df4eb05d9eed95df4d945f48be55dc20800"},
    {0x937760,"558bec83ec10894df0c745fc000000008b45f083780c0074290fbe4d08833c8d18061e014d7d1b8b55f08b420c0fbe4d088b148d18061e018b008b0c90894dfceb588b55f08955f8837df800744c8b4df8e80a9ed4ff833800743f837dfc0075398b4df8e8f79dd4ff8b008945f4837df40074198b4df4e814fcffff0fbec80fbe55083bca75068b45f48945fc8b4df8e87be8deff8945f8ebae8b45fc8be55dc20400"}
};
// Both concrete reference/base pairs share the Actor ABI. The actual loaded
// virtual target and complete retained bodies are still mandatory below.
bool ActorReferenceBasePair(std::uint8_t referenceType,std::uint8_t baseType,UInt32 id,UInt32 baseId) {
    return id && baseId && ((referenceType==0x3B && baseType==0x2A) ||
        (referenceType==0x3C && baseType==0x2B)) &&
        (id!=0x14 || (referenceType==0x3B && baseType==0x2A && baseId==7));
}
struct ActorOverrideObservation {
    const char* reason="profile-unavailable";
    bool observed=false,present=false;
    float value=0;
    UInt32 base=0,table=0,getter=0;
    std::uint8_t referenceType=0,baseType=0;
    bool identityRead=false,getterRead=false;
};
template<class Reader,class Invoker> ActorOverrideObservation ReadActorBaseOverride(
    bool verified,std::uintptr_t actor,UInt32 expectedId,UInt32 expectedBase,Reader&& read,Invoker&& invoke) {
    ActorOverrideObservation result;
    if(!verified)return result;
    if(!expectedId || !expectedBase){result.reason="override-identity-missing";return result;}
    auto identity=[&](UInt32& base,UInt32& table,UInt32& getter,bool publish=false) {
        std::uint8_t reference[0x24]{},baseHeader[16]{};
        if(!actor || actor>UINT32_MAX-sizeof(reference) || !read(actor,reference,sizeof(reference)) ||
           ActorRaw<UInt32>(reference,12)!=expectedId)return false;
        base=ActorRaw<UInt32>(reference,0x20);table=ActorRaw<UInt32>(reference,0);
        if(!base || base>UINT32_MAX-sizeof(baseHeader) || !table || table>UINT32_MAX-0x490 ||
           !read(base,baseHeader,sizeof(baseHeader)) || ActorRaw<UInt32>(baseHeader,12)!=expectedBase ||
           !ActorReferenceBasePair(reference[4],baseHeader[4],expectedId,expectedBase))return false;
        if(publish) {
            result.referenceType=reference[4];result.baseType=baseHeader[4];result.identityRead=true;
        } else if(reference[4]!=result.referenceType || baseHeader[4]!=result.baseType)return false;
        if(!read(std::uintptr_t(table)+0x48C,&getter,sizeof(getter)))return false;
        if(publish)result.getterRead=true;
        return getter==(expectedId==0x14?0x94C640u:0x880660u);
    };
    if(!identity(result.base,result.table,result.getter,true)){result.reason="override-identity-or-getter-mismatch";return result;}
    for(const auto& routine:ActorOverrideRoutines) {
        if(routine.address==0x94C640 && expectedId!=0x14)continue;
        const auto length=strlen(routine.hex)/2;
        std::uint8_t bytes[163]{};
        if(length>sizeof(bytes) || !read(routine.address,bytes,length)){
            result.reason="override-routine-unreadable";return result;
        }
        auto digit=[](char value){return value<='9'?value-'0':value-'a'+10;};
        for(size_t index=0;index<length;++index)
            if(bytes[index]!=((digit(routine.hex[index*2])<<4)|digit(routine.hex[index*2+1]))){
                result.reason="override-routine-bytes-mismatch";return result;
            }
    }
    result.value=invoke(result.getter,actor,16,result.present);
    UInt32 base=0,table=0,getter=0;
    if(!identity(base,table,getter) || base!=result.base || table!=result.table || getter!=result.getter){
        result.reason="override-identity-changed";return result;
    }
    if(!std::isfinite(result.value)){result.reason="override-nonfinite-result";return result;}
    result.observed=true;result.reason=nullptr;return result;
}
std::string ActorBaseOverride(std::uintptr_t actor,UInt32 id,UInt32 baseId) {
    const auto observation=ReadActorBaseOverride(g_pcLayoutVerified,actor,id,baseId,
        [](std::uintptr_t address,void* data,size_t length){
            for(const auto& routine:ActorOverrideRoutines)
                if(address==routine.address && !ActorRoutine(address))return false;
            SIZE_T copied=0;return address && ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),
                data,length,&copied) && copied==length;
        },[](std::uintptr_t address,std::uintptr_t reference,UInt32 code,bool& present){
            using Getter=float(__thiscall*)(void*,UInt32,bool*);
            return reinterpret_cast<Getter>(address)(reinterpret_cast<void*>(reference),code,&present);
        });
    const auto identityFields=",\"engineTargetFormId\":"+std::to_string(id)+
        ",\"engineTargetBaseFormId\":"+std::to_string(baseId)+
        ",\"engineTargetFormType\":"+(observation.identityRead?std::to_string(observation.referenceType):"null")+
        ",\"engineTargetBaseFormType\":"+(observation.identityRead?std::to_string(observation.baseType):"null")+
        ",\"actorAddress\":"+std::to_string(actor)+
        ",\"baseAddress\":"+(observation.identityRead?std::to_string(observation.base):"null")+
        ",\"actorVtable\":"+(observation.identityRead?std::to_string(observation.table):"null")+
        ",\"getterAddress\":"+(observation.getterRead?std::to_string(observation.getter):"null")+
        ",\"vtableOffset\":1164";
    if(!observation.observed)return "{\"status\":\"unavailable\",\"value\":null,\"reason\":"+
        Quote(observation.reason)+identityFields+"}";
    std::uint8_t raw[4]{};memcpy(raw,&observation.value,sizeof(raw));
    return "{\"status\":\"observed\",\"statistic\":\"Health\""+identityFields+
        ",\"hasOverride\":"+(observation.present?"true":"false")+
        ",\"value\":"+NumberField(observation.value)+",\"rawValueHex\":"+Quote(ActorHex(raw,sizeof(raw)))+
        ",\"evidence\":\"pc012-loaded-getter;game-thread-call;identity-rechecked\"}";
}
