// PC actor observations. Generation010; generation009 remains independently pinned.
// Layouts come from the pinned GameObjects/GameProcess/GameEffects headers. No raw writes.
constexpr size_t MaxActorModifiers=64, MaxActorEffects=32;
std::unordered_map<std::string,Script*> g_actorFunctions;
std::unordered_map<UInt32,std::string> g_actorHitHistory;
std::uint16_t ActorX87ControlWord() {
    std::uint16_t controlWord=0;
    __asm fnstcw controlWord
    return controlWord;
}
std::string ActorHex(const std::uint8_t* data,size_t length) {
    std::ostringstream text;
    for (size_t i=0;i<length;++i) text<<std::hex<<std::setw(2)<<std::setfill('0')<<unsigned(data[i]);
    return text.str();
}
std::string ActorUnavailable(const char* reason) {
    return "{\"status\":\"unavailable\",\"value\":null,\"reason\":"+Quote(reason)+"}";
}
std::string ActorNumber(float value) {
    return std::isfinite(value) ? "{\"status\":\"observed\",\"value\":"+NumberField(value)+"}"
        : ActorUnavailable("nonfinite-field");
}
std::string ActorModifiers(std::uintptr_t head) {
    UInt32 node=static_cast<UInt32>(head); std::unordered_set<UInt32> seen;
    std::string items,status="complete",reason;
    size_t count=0;
    while (node && count<MaxActorModifiers) {
        UInt32 link[2]{};
        if (!seen.insert(node).second) { status="partial";reason="cyclic-list";break; }
        if (!ReadRuntime(node,link)) { status="partial";reason="unreadable-node";break; }
        if (link[0]) {
            std::uint8_t raw[8]{}; float value=0;
            if (!ReadRuntime(link[0],raw)) { status="partial";reason="unreadable-modifier";break; }
            memcpy(&value,raw+4,4);
            if (!items.empty()) items+=",";
            items+="{\"actorValueCode\":"+std::to_string(raw[0])+",\"amount\":"+ActorNumber(value)+
                ",\"address\":"+std::to_string(link[0])+",\"rawHex\":"+Quote(ActorHex(raw,sizeof(raw)))+"}";
        }
        node=link[1]; ++count;
    }
    if (node && reason.empty()) { status="partial";reason="node-limit"; }
    return "{\"status\":"+Quote(status)+",\"evidence\":\"pinned-sdk-process-layout\",\"nodeLimit\":"+
        std::to_string(MaxActorModifiers)+",\"reason\":"+(reason.empty()?"null":Quote(reason))+",\"items\":["+items+"]}";
}
bool ActorRoutine(std::uintptr_t address) {
    MEMORY_BASIC_INFORMATION memory{};
    return address && VirtualQuery(reinterpret_cast<void*>(address),&memory,sizeof(memory))==sizeof(memory) &&
        memory.State==MEM_COMMIT && memory.AllocationBase==GetModuleHandleW(nullptr) &&
        !(memory.Protect&(PAGE_GUARD|PAGE_NOACCESS)) &&
        (memory.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY));
}
#include "NvseRuntimeCalibration.inl"
enum class ActorEffectAvailability { Unavailable, NotResident, Ready };
struct ActorEffectList {
    ActorEffectAvailability availability=ActorEffectAvailability::Unavailable;
    const char* reason="verified-effect-list-getter-unavailable";
    const char* route=nullptr;
    UInt32 actorTable=0,base=0,table=0,getter=0,process=0,tier=0,processTable=0,processGetter=0,head=0;
    std::uint8_t referenceType=0,baseType=0;
    bool identityRead=false,getterRead=false,processRead=false,tierRead=false,processTableRead=false,processGetterRead=false,identityStable=false;
    std::string prefix,accessorHex,processGetterHex;
};
bool ActorEffectRange(std::uint64_t address,size_t length) {
    return address && length && address<=UINT32_MAX && length-1<=UINT32_MAX-address;
}
bool ActorEffectMapped(std::uintptr_t address,size_t length,bool executable) {
    if(!ActorEffectRange(address,length))return false;
    const std::uint64_t end=std::uint64_t(address)+length;
    for(std::uint64_t current=address;current<end;) {
        MEMORY_BASIC_INFORMATION memory{};
        if(VirtualQuery(reinterpret_cast<void*>(static_cast<std::uintptr_t>(current)),&memory,sizeof(memory))!=sizeof(memory) ||
           memory.State!=MEM_COMMIT || memory.AllocationBase!=GetModuleHandleW(nullptr) ||
           (memory.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
        const auto protection=memory.Protect&0xFF;
        if(executable && protection!=PAGE_EXECUTE && protection!=PAGE_EXECUTE_READ &&
           protection!=PAGE_EXECUTE_READWRITE && protection!=PAGE_EXECUTE_WRITECOPY)return false;
        const auto next=std::uint64_t(reinterpret_cast<std::uintptr_t>(memory.BaseAddress))+memory.RegionSize;
        if(next<=current)return false;
        current=next;
    }
    return true;
}
struct ActorModifierSample {
    UInt32 selector=0,code=0,address=0,raw=0,repeatRaw=0;
    bool read=false,repeatRead=false;
    const char* reason="modifier-field-unreadable";
};
struct ActorModifierSelectors {
    const char* reason="modifier-profile-unverified";
    UInt32 actor=0,base=0,actorTable=0,ownerTable=0;
    bool identityStable=false;
    std::string executable,lookupHex;
    std::array<std::string,3> wrapperHex;
    std::vector<ActorModifierSample> samples;
};
bool ActorModifierAddress(std::uintptr_t actor,UInt32 selector,UInt32 code,UInt32& address) {
    if(selector>2 || code>76)return false;
    const auto offset=selector==0?0x244u+4*code:selector==1?0x378u+4*code:code==16?0x4ACu:0x4B0u+4*code;
    const auto candidate=std::uint64_t(actor)+offset;
    if(!actor || !ActorEffectRange(candidate,4))return false;
    address=static_cast<UInt32>(candidate);return true;
}
// PC007 pins the three complete AV-owner wrappers; PC025 pins the complete lookup.
// Raw selector numbers are retained. No damage/temporary interpretation or engine calls.
template<class Reader,class Mapper> ActorModifierSelectors ReadActorModifierSelectors(bool verified,
    const std::string& executable,std::uintptr_t actor,UInt32 expectedId,UInt32 expectedBase,
    const UInt32* codes,size_t count,Reader&& read,Mapper&& mapped) {
    ActorModifierSelectors result;result.executable=executable;
    if(!verified || !VerifiedPcExecutable(executable))return result;
    if(expectedId!=0x14) {result.reason="npc-modifier-selector-route-unavailable";return result;}
    if(!codes || !count || count>16) {result.reason="modifier-sample-bound-exceeded";return result;}
    for(size_t i=0;i<count;++i)if(codes[i]>76) {result.reason="modifier-actor-value-out-of-range";return result;}
    auto bytes=[&](std::uint64_t address,void* destination,size_t length) {
        return ActorEffectRange(address,length) && read(static_cast<std::uintptr_t>(address),destination,length);
    };
    struct Identity {UInt32 actorTable=0,base=0,ownerTable=0;};
    auto identity=[&](Identity& value) {
        std::uint8_t reference[0x24]{},baseHeader[16]{};UInt32 wrappers[3]{};
        if(expectedBase!=7 || !bytes(actor,reference,sizeof(reference)) || reference[4]!=0x3B ||
           ActorRaw<UInt32>(reference,12)!=expectedId)return false;
        value.actorTable=ActorRaw<UInt32>(reference,0);value.base=ActorRaw<UInt32>(reference,0x20);
        if(!bytes(value.base,baseHeader,sizeof(baseHeader)) || baseHeader[4]!=0x2A || ActorRaw<UInt32>(baseHeader,12)!=expectedBase ||
           !bytes(std::uint64_t(actor)+0xA4,&value.ownerTable,4) || value.ownerTable!=0x0108A974 ||
           !mapped(value.actorTable,4,false) || !mapped(value.ownerTable,28,false) ||
           !bytes(std::uint64_t(value.ownerTable)+16,wrappers,sizeof(wrappers)))return false;
        return wrappers[0]==0x94C460 && wrappers[1]==0x94C490 && wrappers[2]==0x94C4C0;
    };
    Identity before;
    if(!identity(before)) {result.reason="modifier-actor-identity-or-owner-unavailable";return result;}
    result.actor=static_cast<UInt32>(actor);result.base=before.base;result.actorTable=before.actorTable;result.ownerTable=before.ownerTable;
    const UInt32 wrapperAddresses[]={0x94C460,0x94C490,0x94C4C0};
    const char* wrapperBodies[]={
        "558bec51894dfc8b4508506a008b4dfc81e9a4000000e855ffffff8be55dc20400",
        "558bec51894dfc8b4508506a028b4dfc81e9a4000000e825ffffff8be55dc20400",
        "558bec51894dfc8b4508506a018b4dfc81e9a4000000e8f5feffff8be55dc20400"};
    const char* lookupBody="558bec83ec10894df8d9eed95dfc8b45088945f4837df400743c837df4017448837df4027402eb508b4d0c894df0837df0107402eb0e8b55f8d982ac040000d95dfceb108b450c8b4df8d98481b0040000d95dfceb228b550c8b45f8d9849044020000d95dfceb108b4d0c8b55f8d9848a78030000d95dfcd945fc8be55dc20800";
    auto routine=[&](UInt32 address,const char* expected,std::string& observed) {
        const size_t length=strlen(expected)/2;std::uint8_t actual[129]{};
        if(length>sizeof(actual) || !mapped(address,length,true) || !bytes(address,actual,length))return false;
        observed=ActorHex(actual,length);return observed==expected;
    };
    auto proof=[&](std::string& lookup,std::array<std::string,3>& wrappers) {
        if(!routine(0x94C3D0,lookupBody,lookup))return false;
        for(size_t i=0;i<3;++i)if(!routine(wrapperAddresses[i],wrapperBodies[i],wrappers[i]))return false;
        return true;
    };
    if(!proof(result.lookupHex,result.wrapperHex)) {result.reason="modifier-routine-unverified";return result;}
    for(UInt32 selector=0;selector<3;++selector)for(size_t i=0;i<count;++i) {
        ActorModifierSample sample;sample.selector=selector;sample.code=codes[i];
        if(!ActorModifierAddress(actor,selector,codes[i],sample.address))sample.reason="modifier-address-out-of-range";
        else sample.read=bytes(sample.address,&sample.raw,4);
        result.samples.push_back(sample);
    }
    for(auto& sample:result.samples) {
        if(sample.address)sample.repeatRead=bytes(sample.address,&sample.repeatRaw,4);
        if(!sample.read || !sample.repeatRead)continue;
        if(sample.raw!=sample.repeatRaw)sample.reason="modifier-value-changed";
        else {
            float value=0;memcpy(&value,&sample.raw,4);
            sample.reason=std::isfinite(value)?nullptr:"modifier-value-nonfinite";
        }
    }
    Identity after;std::string finalLookup;std::array<std::string,3> finalWrappers;
    if(!identity(after) || before.actorTable!=after.actorTable || before.base!=after.base || before.ownerTable!=after.ownerTable)
        result.reason="modifier-identity-changed";
    else if(!proof(finalLookup,finalWrappers))result.reason="modifier-routine-changed";
    else {result.identityStable=true;result.reason=nullptr;}
    if(result.reason)for(auto& sample:result.samples)sample.reason=result.reason;
    return result;
}
std::string ActorModifierSelectorsJson(const ActorModifierSelectors& result) {
    size_t observed=0;std::string samples,wrappers;
    for(const auto& sample:result.samples) {
        if(!samples.empty())samples+=",";
        float value=0;memcpy(&value,&sample.raw,4);
        if(!sample.reason)++observed;
        samples+="{\"selector\":"+std::to_string(sample.selector)+",\"actorValueCode\":"+std::to_string(sample.code)+
            ",\"status\":"+Quote(sample.reason?"unavailable":"observed")+",\"value\":"+(sample.reason?"null":NumberField(value))+
            ",\"reason\":"+(sample.reason?Quote(sample.reason):"null")+",\"address\":"+std::to_string(sample.address)+
            ",\"rawHex\":"+(sample.read?Quote(ActorHex(reinterpret_cast<const std::uint8_t*>(&sample.raw),4)):"null")+
            ",\"repeatRawHex\":"+(sample.repeatRead?Quote(ActorHex(reinterpret_cast<const std::uint8_t*>(&sample.repeatRaw),4)):"null")+"}";
    }
    const UInt32 addresses[]={0x94C460,0x94C490,0x94C4C0},selectors[]={0,2,1};
    for(size_t i=0;i<3;++i) {
        if(i)wrappers+=",";
        wrappers+="{\"slot\":"+std::to_string(i+4)+",\"selector\":"+std::to_string(selectors[i])+
            ",\"address\":"+std::to_string(addresses[i])+",\"observedHex\":"+
            (result.wrapperHex[i].empty()?"null":Quote(result.wrapperHex[i]))+"}";
    }
    const char* status=result.reason || !observed?"unavailable":observed==result.samples.size()?"observed":"partial";
    return "{\"status\":"+Quote(status)+",\"reason\":"+(result.reason?Quote(result.reason):"null")+
        ",\"resolverRoute\":"+(result.ownerTable?Quote("player-direct-loaded-lookup"):"null")+
        ",\"semanticStatus\":\"raw-selectors\",\"readConsistency\":\"repeated-field-and-identity-reads\""+
        ",\"executableSha256\":"+Quote(result.executable)+",\"actorAddress\":"+std::to_string(result.actor)+
        ",\"baseAddress\":"+std::to_string(result.base)+",\"actorValueOwnerAddress\":"+std::to_string(result.actor?result.actor+0xA4:0)+
        ",\"ownerVtable\":"+std::to_string(result.ownerTable)+",\"identityStable\":"+(result.identityStable?"true":"false")+
        ",\"lookupAddress\":"+std::to_string(0x94C3D0)+",\"lookupObservedHex\":"+(result.lookupHex.empty()?"null":Quote(result.lookupHex))+
        ",\"evidence\":\"pc007-player-av-wrappers-and-pc025-loaded-modifier-lookup\",\"wrappers\":["+wrappers+
        "],\"observations\":["+samples+"]}";
}
std::string ActorRawModifierSelectors(std::uintptr_t actor,UInt32 id,UInt32 baseId) {
    // Same sixteen actor values as statistics above; 48 fields, independent of list/effect sizes.
    const UInt32 codes[]={5,6,7,8,9,10,11,14,16,18,34,35,38,41,45,76};
    auto read=[](std::uintptr_t address,void* destination,size_t length) {
        SIZE_T copied=0;return ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),destination,length,&copied) && copied==length;
    };
    return ActorModifierSelectorsJson(ReadActorModifierSelectors(g_pcLayoutVerified,g_executableSha256,actor,id,baseId,
        codes,std::size(codes),read,ActorEffectMapped));
}
// PC023/PC008 pin the complete wrappers; PC024 pins both transitive bodies and the high-process slot.
// These proofs authorize pointer reads only. No engine routine is called.
template<class Reader,class Mapper> ActorEffectList ReadActorEffectList(bool verified,std::uintptr_t actor,
    UInt32 expectedId,UInt32 expectedBase,Reader&& read,Mapper&& mapped) {
    ActorEffectList result;
    if(!verified)return result;
    auto bytes=[&](std::uint64_t address,void* destination,size_t length) {
        return ActorEffectRange(address,length) && read(static_cast<std::uintptr_t>(address),destination,length);
    };
    auto word=[&](std::uint64_t address,UInt32& value) { return bytes(address,&value,sizeof(value)); };
    auto identity=[&](UInt32& actorTable,UInt32& base,UInt32& table,UInt32& getter,UInt32& process,bool publish=false) {
        std::uint8_t reference[0x24]{},baseHeader[16]{};
        if(!expectedId || !expectedBase || !bytes(actor,reference,sizeof(reference)) ||
           ActorRaw<UInt32>(reference,12)!=expectedId)return false;
        actorTable=ActorRaw<UInt32>(reference,0);base=ActorRaw<UInt32>(reference,0x20);
        if(!bytes(base,baseHeader,sizeof(baseHeader)) || ActorRaw<UInt32>(baseHeader,12)!=expectedBase ||
           !ActorReferenceBasePair(reference[4],baseHeader[4],expectedId,expectedBase))return false;
        if(publish) {
            result.referenceType=reference[4];result.baseType=baseHeader[4];result.identityRead=true;
        } else if(reference[4]!=result.referenceType || baseHeader[4]!=result.baseType)return false;
        if(!word(std::uint64_t(actor)+0x94,table) || !ActorEffectRange(std::uint64_t(table)+8,4) ||
           !mapped(static_cast<std::uintptr_t>(std::uint64_t(table)+8),4,false) ||
           !word(std::uint64_t(table)+8,getter))return false;
        if(publish)result.getterRead=true;
        if(!word(std::uint64_t(actor)+0x68,process))return false;
        if(publish)result.processRead=true;
        return true;
    };
    const bool haveIdentity=identity(result.actorTable,result.base,result.table,result.getter,result.process,true);
    std::uint8_t prefix[32]{};
    if(result.getterRead && ActorEffectRange(result.getter,sizeof(prefix)) && mapped(result.getter,sizeof(prefix),true) && bytes(result.getter,prefix,sizeof(prefix)))
        result.prefix=ActorHex(prefix,sizeof(prefix));
    if(!haveIdentity) {result.reason="effect-actor-identity-or-fields-unavailable";return result;}
    auto routine=[&](UInt32 address,const char* hex,std::string& observed) {
        const auto length=strlen(hex)/2;std::uint8_t actual[70]{};
        if(length>sizeof(actual) || !ActorEffectRange(address,length) || !mapped(address,length,true) || !bytes(address,actual,length))return false;
        observed=ActorHex(actual,length);return observed==hex;
    };
    std::string wrapper;
    if(expectedId==0x14) {
        result.route="player-direct";
        if(result.getter!=0x939710 || !routine(result.getter,"558bec51894dfc8b45fc8b807c0100008be55dc3",wrapper)) {
            result.reason="effect-list-getter-prefix-mismatch";return result;
        }
        if(!word(std::uint64_t(actor)+0x210,result.head)) {result.reason="effect-list-pointer-unreadable";return result;}
    } else {
        result.route=result.referenceType==0x3C?"creature-high-process-direct":"npc-high-process-direct";
        // This is the shared Actor wrapper only if the observed slot and every
        // loaded body match; other creature getters/process layouts stay unavailable.
        if(result.getter!=0x8C4090 || !routine(result.getter,
           "558bec83ec08894dfc8b4dfc81e994000000e87944010085c074258b4dfc81e994000000e8674401008945f88b45f88b108b4df88b82b8030000ffd0eb04eb0233c08be55dc3",wrapper)) {
            result.reason="effect-list-getter-prefix-mismatch";return result;
        }
        if(!routine(0x8D8520,"558bec51894dfc8b45fc8b40688be55dc3",result.accessorHex)) {
            result.reason="effect-process-accessor-unverified";return result;
        }
        if(result.process) {
            result.processTableRead=word(result.process,result.processTable);
            result.tierRead=word(std::uint64_t(result.process)+0x28,result.tier);
            if(!result.processTableRead || !result.tierRead) {result.reason="effect-process-fields-unreadable";return result;}
            if(result.tier!=0) {result.reason="effect-process-tier-unsupported";return result;}
            if(result.processTable!=0x01087864) {result.reason="effect-process-vtable-unverified";return result;}
            const auto slot=std::uint64_t(result.processTable)+0x3B8;
            if(!ActorEffectRange(slot,4) || !mapped(static_cast<std::uintptr_t>(slot),4,false) ||
               !(result.processGetterRead=word(slot,result.processGetter)) || result.processGetter!=0x8D8100 ||
               !routine(result.processGetter,"558bec51894dfc8b45fc8b80b80100008be55dc3",result.processGetterHex)) {
                result.reason="effect-process-getter-unverified";return result;
            }
            if(!word(std::uint64_t(result.process)+0x1B8,result.head)) {result.reason="effect-list-pointer-unreadable";return result;}
        }
    }
    UInt32 actorTable=0,base=0,table=0,getter=0,process=0,processTable=0,tier=0,processGetter=0;
    if(!identity(actorTable,base,table,getter,process) || actorTable!=result.actorTable || base!=result.base ||
       table!=result.table || getter!=result.getter || process!=result.process ||
       (result.processTableRead && (!word(process,processTable) || processTable!=result.processTable)) ||
       (result.tierRead && (!word(std::uint64_t(process)+0x28,tier) || tier!=result.tier)) ||
       (result.processGetterRead && (!word(std::uint64_t(processTable)+0x3B8,processGetter) || processGetter!=result.processGetter))) {
        result.reason="effect-list-identity-changed";return result;
    }
    result.identityStable=true;
    result.availability=expectedId!=0x14 && !result.process?ActorEffectAvailability::NotResident:ActorEffectAvailability::Ready;
    result.reason=result.availability==ActorEffectAvailability::NotResident?"actor-process-not-resident":nullptr;
    return result;
}
bool SameActorEffectList(const ActorEffectList& left,const ActorEffectList& right) {
    return left.availability==right.availability && left.identityStable && right.identityStable &&
        left.referenceType==right.referenceType && left.baseType==right.baseType &&
        left.actorTable==right.actorTable && left.base==right.base && left.table==right.table && left.getter==right.getter &&
        left.process==right.process && left.tier==right.tier && left.processTable==right.processTable &&
        left.processGetter==right.processGetter && left.head==right.head;
}
std::string ActorEffectProvenance(const ActorEffectList& value) {
    auto number=[](UInt32 number,bool observed) {return observed?std::to_string(number):"null";};
    return ",\"engineTargetFormType\":"+number(value.referenceType,value.identityRead)+
        ",\"engineTargetBaseFormType\":"+number(value.baseType,value.identityRead)+
        ",\"baseAddress\":"+number(value.base,value.identityRead)+
        ",\"actorVtable\":"+number(value.actorTable,value.identityRead)+
        ",\"getter\":"+number(value.getter,value.getterRead)+",\"getterPrefixHex\":"+(value.prefix.empty()?"null":Quote(value.prefix))+
        ",\"resolverRoute\":"+(value.route?Quote(value.route):"null")+",\"processAddress\":"+number(value.process,value.processRead)+
        ",\"processTier\":"+number(value.tier,value.tierRead)+",\"processVtable\":"+number(value.processTable,value.processTableRead)+
        ",\"processGetterSlot\":"+(value.processGetterRead?std::to_string(std::uint64_t(value.processTable)+0x3B8):"null")+
        ",\"processGetter\":"+number(value.processGetter,value.processGetterRead)+
        ",\"processAccessorHex\":"+(value.accessorHex.empty()?"null":Quote(value.accessorHex))+
        ",\"processGetterHex\":"+(value.processGetterHex.empty()?"null":Quote(value.processGetterHex))+
        ",\"identityStable\":"+(value.identityStable?"true":"false");
}
std::string ActorEffectFailure(const ActorEffectList& value) {
    return "{\"status\":"+Quote(value.availability==ActorEffectAvailability::NotResident?"not-resident":"unavailable")+
        ",\"value\":null,\"reason\":"+Quote(value.reason)+ActorEffectProvenance(value)+"}";
}
std::string ActorEffects(std::uintptr_t actor,UInt32 id=0,UInt32 baseId=0) {
    auto read=[](std::uintptr_t address,void* data,size_t length) {
        SIZE_T copied=0;return ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),data,length,&copied) && copied==length;
    };
    const auto resolved=ReadActorEffectList(g_pcLayoutVerified,actor,id,baseId,read,ActorEffectMapped);
    if(resolved.availability!=ActorEffectAvailability::Ready)return ActorEffectFailure(resolved);
    UInt32 node=resolved.head; std::unordered_set<UInt32> seen;
    std::string items,status="complete",reason; size_t count=0;
    while (node && count<MaxActorEffects) {
        UInt32 link[2]{};
        if (!seen.insert(node).second) { status="partial";reason="cyclic-list";break; }
        if (!ReadRuntime(node,link)) { status="partial";reason="unreadable-node";break; }
        if (link[0]) {
            std::uint8_t raw[0x48]{};
            if (!ReadRuntime(link[0],raw)) { status="partial";reason="unreadable-effect";break; }
            UInt32 owner=0,item=0,setting=0,settingId=0; std::uint8_t settingType=0;
            float elapsed=0,magnitude=0,duration=0;
            memcpy(&owner,raw+0x24,4);memcpy(&item,raw+0xC,4);
            memcpy(&elapsed,raw+4,4);memcpy(&magnitude,raw+0x1C,4);memcpy(&duration,raw+0x20,4);
            const bool haveSetting=item && ReadRuntime(std::uintptr_t(item)+0x14,setting) &&
                ReadFormIdentity(reinterpret_cast<void*>(setting),settingId,settingType) && settingType==0x10;
            if (!items.empty()) items+=",";
            items+="{\"status\":"+Quote(owner==actor+0x94?"observed":"owner-mismatch")+
                ",\"address\":"+std::to_string(link[0])+",\"rawHex\":"+Quote(ActorHex(raw,sizeof(raw)))+
                ",\"applied\":"+(raw[0x10]?"true":"false")+",\"terminated\":"+(raw[0x11]?"true":"false")+
                ",\"elapsed\":"+ActorNumber(elapsed)+",\"magnitude\":"+ActorNumber(magnitude)+",\"duration\":"+ActorNumber(duration)+
                ",\"effectSetting\":"+(haveSetting?"{\"status\":\"observed\",\"formId\":"+std::to_string(settingId)+
                    ",\"formType\":"+std::to_string(settingType)+"}":ActorUnavailable("effect-setting-unavailable"))+"}";
        }
        node=link[1]; ++count;
    }
    if (node && reason.empty()) { status="partial";reason="node-limit"; }
    if(!SameActorEffectList(resolved,ReadActorEffectList(g_pcLayoutVerified,actor,id,baseId,read,ActorEffectMapped))) {
        auto stale=resolved;stale.identityStable=false;
        return "{\"status\":\"unavailable\",\"value\":null,\"reason\":\"effect-list-identity-changed\""+
            ActorEffectProvenance(stale)+",\"items\":["+items+"]}";
    }
    return "{\"status\":"+Quote(status)+",\"evidence\":\"pinned-sdk-virtual-getter-and-layout\""+ActorEffectProvenance(resolved)+
        ",\"nodeLimit\":"+std::to_string(MaxActorEffects)+",\"reason\":"+(reason.empty()?"null":Quote(reason))+",\"items\":["+items+"]}";
}
bool ActorValue(void* actor,const std::string& expression,double& value,std::string& reason) {
    if (!g_script || !g_script->CompileScript || !g_numericEnabled) { reason="sdk-numeric-api-unavailable";return false; }
    auto function=g_actorFunctions.find(expression);
    if (function==g_actorFunctions.end()) {
        if (g_actorFunctions.size()>=64) { reason="actor-function-cache-limit";return false; }
        const auto text="ref target\nbegin function { target }\nSetFunctionValue (target."+expression+")\nend\n";
        auto compiled=g_script->CompileScript(text.c_str());
        if (!compiled) { reason="actor-function-compile-failed";return false; }
        function=g_actorFunctions.emplace(expression,compiled).first;
    }
    NumericElement result{};
    const bool returned=g_script->CallFunction(function->second,nullptr,nullptr,&result,1,actor);
    if (!returned || result.type!=1 || !std::isfinite(result.number)) {
        if (result.type==3) g_numericEnabled=false;
        reason="actor-numeric-return-unavailable";return false;
    }
    value=result.number;return true;
}
bool ParseActorTarget(const std::string& payload,UInt32& formId,std::string& reason) {
    if (payload=="player") { formId=0x14;return true; }
    const auto tab=payload.find('\t');
    if (tab==std::string::npos || payload.find('\t',tab+1)!=std::string::npos) { reason="invalid-actor-target";return false; }
    const auto plugin=payload.substr(0,tab),local=payload.substr(tab+1);
    if (plugin.size()<5 || plugin.size()>255 || plugin.find_first_of("\"\\/:")!=std::string::npos ||
        !std::all_of(plugin.begin(),plugin.end(),[](unsigned char c){return c>=32 && c<127;}) ||
        (_stricmp(plugin.c_str()+plugin.size()-4,".esm") && _stricmp(plugin.c_str()+plugin.size()-4,".esp")) ||
        !std::regex_match(local,std::regex("^[0-9a-fA-F]{1,6}$"))) { reason="invalid-actor-target";return false; }
    const auto id=static_cast<UInt32>(strtoul(local.c_str(),nullptr,16)); UInt32 index=255;
    if (!id || !PluginNumber("GetModIndex \""+plugin+"\"",index) || index>=255) { reason="actor-plugin-not-loaded";return false; }
    formId=(index<<24)|id;return true;
}
std::string ActorHitRecord(void* actor,UInt32 actorId,const char* eventName=nullptr,UInt32 eventOther=0) {
    UInt32 process=0,tier=99,table=0,getter=0,hit=0;
    if (!g_pcLayoutVerified || !ReadRuntime(reinterpret_cast<std::uintptr_t>(actor)+0x68,process) || !process ||
        !ReadRuntime(std::uintptr_t(process)+0x28,tier) || tier>1 || !ReadRuntime(process,table) ||
        !ReadRuntime(std::uintptr_t(table)+0x1DC*4,getter) || !ActorRoutine(getter))
        return ActorUnavailable("verified-high-process-hit-getter-unavailable");
    // Session007 independently measured this getter's process+0x240 load.
    const std::uint8_t expected[]={0x55,0x8B,0xEC,0x51,0x89,0x4D,0xFC,0x8B,0x45,0xFC,0x8B,0x80,0x40,0x02,0x00,0x00,0x8B,0xE5,0x5D,0xC3};
    std::uint8_t actual[sizeof(expected)]{};
    if (!ReadRuntime(getter,actual) || memcmp(actual,expected,sizeof(expected)) ||
        !ReadRuntime(std::uintptr_t(process)+0x240,hit)) return ActorUnavailable("hit-getter-prefix-or-pointer-unavailable");
    if (!hit) {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (g_actorHitHistory.size()<256 || g_actorHitHistory.count(actorId)) g_actorHitHistory[actorId]="not-resident";
        return "{\"status\":\"not-resident\",\"value\":null}";
    }
    std::uint8_t raw[0x64]{};
    if (!ReadRuntime(hit,raw)) return ActorUnavailable("hit-record-unreadable");
    const auto fingerprint=std::to_string(hit)+":"+ActorHex(raw,sizeof(raw));
    bool prior=false,changed=false;
    {std::lock_guard<std::mutex> lock(g_mutex);
        auto found=g_actorHitHistory.find(actorId);prior=found!=g_actorHitHistory.end();changed=prior && found->second!=fingerprint;
        if (g_actorHitHistory.size()<256 || prior) g_actorHitHistory[actorId]=fingerprint;}
    UInt32 source=0,target=0,weapon=0,sourceId=0,targetId=0,weaponId=0,flags=0;std::uint8_t sourceType=0,targetType=0,weaponType=0;
    memcpy(&source,raw,4);memcpy(&target,raw+4,4);memcpy(&weapon,raw+0x30,4);memcpy(&flags,raw+0x58,4);
    const bool haveSource=ReadFormIdentity(reinterpret_cast<void*>(source),sourceId,sourceType) && (sourceType==0x3B || sourceType==0x3C);
    const bool haveTarget=ReadFormIdentity(reinterpret_cast<void*>(target),targetId,targetType) && (targetType==0x3B || targetType==0x3C);
    const bool haveWeapon=ReadFormIdentity(reinterpret_cast<void*>(weapon),weaponId,weaponType) && weaponType==0x28;
    std::string values;
    // ActorHitData +0x18 includes skill and weapon condition. Retain the legacy key.
    for (const auto& field:std::initializer_list<std::pair<const char*,size_t>>{{"healthDamage",0x14},{"weaponBaseDamage",0x18},{"weaponDamage",0x18},{"fatigueDamage",0x1C},{"limbDamage",0x20},{"damageMultiplier",0x5C}}) {
        float number=0;memcpy(&number,raw+field.second,4);if(!values.empty())values+=",";
        values+=Quote(field.first)+":"+ActorNumber(number);
    }
    const bool targetAgrees=haveTarget && targetId==actorId && target==reinterpret_cast<UInt32>(actor);
    const bool isHit=eventName && !strcmp(eventName,"onhit"),isHitWith=eventName && !strcmp(eventName,"onhitwith");
    const bool haveOtherCheck=(isHit && haveSource && eventOther) || (isHitWith && haveWeapon && eventOther);
    const bool otherAgrees=isHit?sourceId==eventOther:weaponId==eventOther;
    return "{\"status\":\"observed-record\",\"evidence\":\"verified-getter-and-pinned-sdk-hit-layout\",\"address\":"+
        std::to_string(hit)+",\"rawHex\":"+Quote(ActorHex(raw,sizeof(raw)))+",\"fields\":{"+values+
        "},\"fieldSemantics\":{\"weaponDamage\":\"skill-and-condition-adjusted\",\"weaponBaseDamage\":\"legacy-alias-of-weaponDamage\"},\"flags\":"+
        std::to_string(flags)+",\"sourceFormId\":"+(haveSource?std::to_string(sourceId):"null")+
        ",\"targetFormId\":"+(haveTarget?std::to_string(targetId):"null")+",\"weaponFormId\":"+(haveWeapon?std::to_string(weaponId):"null")+
        ",\"targetAgrees\":"+(targetAgrees?"true":"false")+",\"eventParticipantAgrees\":"+
        (haveOtherCheck?(otherAgrees?"true":"false"):"null")+",\"priorRecordObserved\":"+(prior?"true":"false")+
        ",\"recordChangedSinceLastObservation\":"+(prior?(changed?"true":"false"):"null")+
        ",\"attributionConfirmed\":false,\"freshness\":\"requires-independent-hit-and-health-control\"}";
}
void ActorHitEvent(void* actor,UInt32 id,const char* eventName,UInt32 other) {
    if (!g_pcLayoutVerified || (strcmp(eventName,"onhit") && strcmp(eventName,"onhitwith"))) return;
    UInt32 base=0,baseId=0;std::uint8_t baseType=0;
    const bool haveBase=ReadRuntime(reinterpret_cast<std::uintptr_t>(actor)+0x20,base) &&
        ReadFormIdentity(reinterpret_cast<void*>(base),baseId,baseType) && (baseType==0x2A || baseType==0x2B);
    Emit("actor-hit-state",0,",\"event\":"+Quote(eventName)+",\"engineTargetFormId\":"+std::to_string(id)+
        ",\"engineTargetBaseFormId\":"+(haveBase?std::to_string(baseId):"null")+",\"targetKind\":\"actor\",\"record\":"+
        ActorHitRecord(actor,id,eventName,other));
}
bool ResolveActor(const std::string& payload,void*& actor,UInt32& id,UInt32& baseId,std::uint8_t& type,std::string& reason) {
    if (!g_capture || !g_loadedGameObserved || !VerifyRuntimeFormMap()) { reason="actor-runtime-profile-unavailable";return false; }
    UInt32 requestedId=0;
    if (!ParseActorTarget(payload,requestedId,reason)) return false;
    actor=LookupRuntimeForm(requestedId); UInt32 base=0;std::uint8_t baseType=0;
    if (!ReadFormIdentity(actor,id,type) || id!=requestedId || (type!=0x3B && type!=0x3C) ||
        !ReadRuntime(reinterpret_cast<std::uintptr_t>(actor)+0x20,base) ||
        !ReadFormIdentity(reinterpret_cast<void*>(base),baseId,baseType) ||
        (type==0x3B?baseType!=0x2A:baseType!=0x2B)) { reason="resident-actor-or-base-unavailable";return false; }
    return true;
}
bool ParseActorSet(const std::string& payload,std::string& target,unsigned& number) {
    const auto last=payload.rfind('\t');
    if(last==std::string::npos || last==0) return false;
    const auto middle=payload.rfind('\t',last-1);
    if(middle==std::string::npos || payload.substr(middle+1,last-middle-1)!="Endurance") return false;
    const auto value=payload.substr(last+1);
    if(!std::regex_match(value,std::regex("^(?:[1-9]|10)$"))) return false;
    number=static_cast<unsigned>(strtoul(value.c_str(),nullptr,10));target=payload.substr(0,middle);return true;
}
void ActorSet(const Request& request) {
    std::string target,reason;unsigned number=0;
    if(!g_capture) { Emit("error",request.id,",\"error\":\"capture-required\"");return; }
    const auto generation=g_captureGeneration.load();
    auto fail=[&](const std::string& error){Emit("error",request.id,",\"error\":"+Quote(error),generation);};
    if(!g_console || !g_console->RunScriptLine) { fail("actor-set-sdk-unavailable");return; }
    if(!ParseActorSet(request.payload,target,number)) { fail("invalid-bounded-actor-set-request");return; }
    void* actor=nullptr;UInt32 id=0,baseId=0;std::uint8_t type=0;
    if(!ResolveActor(target,actor,id,baseId,type,reason)) { fail(reason);return; }
    double before=0;
    if(!ActorValue(actor,"GetBaseAV Endurance",before,reason)) { fail("actor-set-baseline-unavailable");return; }
    const auto command="SetAV Endurance "+std::to_string(number);
    if(!g_capture || !g_connected || generation!=g_captureGeneration.load()) return;
    const bool accepted=g_console->RunScriptLine(command.c_str(),static_cast<TESObjectREFR*>(actor));
    double after=0;const bool haveAfter=ActorValue(actor,"GetBaseAV Endurance",after,reason);
    Emit("action-result",request.id,",\"accepted\":"+std::string(accepted?"true":"false")+
        ",\"engineTargetFormId\":"+std::to_string(id)+",\"engineTargetBaseFormId\":"+std::to_string(baseId)+
        ",\"targetKind\":\"actor\",\"statistic\":\"Endurance\",\"requestedValue\":"+std::to_string(number)+
        ",\"beforeBaseValue\":"+NumberField(before)+",\"afterBaseValue\":"+(haveAfter?NumberField(after):"null")+
        ",\"evidence\":\"sdk-explicit-actor-console-return-and-numeric-readback\",\"requestedValueObserved\":"+
        std::string(haveAfter && after==number?"true":"false")+",\"valueChanged\":"+
        std::string(haveAfter && after!=before?"true":"false"),generation);
}
std::string ActorCreatureHealthScaling(std::uintptr_t actor,UInt32 id,UInt32 baseId);
bool AppendCreatureHealthScaling(std::string& actorState,const std::string& observation);
std::string ActorWeaponCriticalStage(std::uintptr_t actor,UInt32 id,UInt32 baseId);
bool AppendWeaponCriticalStage(std::string& actorState,const std::string& critical);
void ActorSnapshot(const Request& request) {
    const auto generation=g_captureGeneration.load();
    const auto x87ControlWord=ActorX87ControlWord();
    auto fail=[&](const std::string& reason){Emit("error",request.id,",\"error\":"+Quote(reason));};
    void* actor=nullptr;UInt32 id=0,baseId=0;std::uint8_t type=0;std::string reason;
    if(!ResolveActor(request.payload,actor,id,baseId,type,reason)) { fail(reason);return; }
    {std::lock_guard<std::mutex> lock(g_mutex);g_watchedActors.insert(id);}
    const auto identity=",\"engineTargetFormId\":"+std::to_string(id)+",\"engineTargetBaseFormId\":"+std::to_string(baseId)+
        ",\"engineTargetFormType\":"+std::to_string(type)+",\"targetKind\":\"actor\"";
    std::string fields;
    auto value=[&](const char* statistic,const char* component,const std::string& expression) {
        double number=0;std::string missing;
        const bool observed=ActorValue(actor,expression,number,missing);
        if (!fields.empty()) fields+=",";
        fields+="{\"statistic\":"+Quote(statistic)+",\"component\":"+Quote(component)+",\"status\":"+
            Quote(observed?"observed":"unavailable")+",\"value\":"+(observed?NumberField(number):"null")+
            ",\"reason\":"+(observed?"null":Quote(missing))+"}";
        if (observed) Emit("snapshot",request.id,identity+",\"statistic\":"+Quote(statistic)+",\"component\":"+Quote(component)+
            ",\"value\":"+NumberField(number)+",\"evidence\":\"sdk-explicit-actor-return\"");
    };
    value("Level","current","GetLevel");
    for (const auto* statistic:{"Health","Endurance","Strength","Perception","Agility","Intelligence","Charisma","Luck","Guns","EnergyWeapons","MeleeWeapons","Unarmed","Explosives","CritChance"}) {
        value(statistic,"current",std::string("GetAV ")+statistic);
        value(statistic,"base",std::string("GetBaseAV ")+statistic);
        value(statistic,"permanent",std::string("GetPermAV ")+statistic);
    }
    // SDK GameAPI::LookupActorValueByName uses infoName, which can differ from our display label.
    for(const auto& av:std::initializer_list<std::pair<const char*,UInt32>>{{"DamageResistance",18},{"DamageThreshold",76}}) {
        std::string token;
        if(ActorValueToken(av.second,token)) {
            value(av.first,"current","GetAV "+token);
            value(av.first,"base","GetBaseAV "+token);
            value(av.first,"permanent","GetPermAV "+token);
        } else for(const auto* component:{"current","base","permanent"}) {
            if(!fields.empty())fields+=",";
            fields+="{\"statistic\":"+Quote(av.first)+",\"component\":"+Quote(component)+
                ",\"status\":\"unavailable\",\"value\":null,\"reason\":\"actor-value-command-token-unavailable\"}";
        }
    }
    UInt32 process=0,tier=99;std::string damage=ActorUnavailable("process-unavailable"),temporary=damage;
    if (ReadRuntime(reinterpret_cast<std::uintptr_t>(actor)+0x68,process) && process &&
        ReadRuntime(std::uintptr_t(process)+0x28,tier) && tier<=3) {
        damage=ActorModifiers(std::uintptr_t(process)+0x98);
        temporary=tier<=2?ActorModifiers(std::uintptr_t(process)+0xB8):ActorUnavailable("process-tier-has-no-temporary-list");
    }
    std::string actorState=identity+",\"executableSha256\":"+Quote(g_executableSha256)+
        ",\"isPlayer\":"+(id==0x14?"true":"false")+",\"isCreature\":"+(type==0x3C?"true":"false")+
        ",\"baseActorData\":"+ActorBaseData(reinterpret_cast<std::uintptr_t>(actor),baseId,type)+
        ",\"gameSettings\":"+ActorGameSettings()+",\"actorValueInfo\":"+ActorValueInformation()+
        ",\"baseOverride\":"+ActorBaseOverride(reinterpret_cast<std::uintptr_t>(actor),id,baseId)+
        ",\"floatingPoint\":{\"scope\":\"game-thread-before-actor-observation\",\"x87ControlWord\":"+
        std::to_string(x87ControlWord)+",\"evidence\":\"fnstcw\"}"+
        ",\"statistics\":["+fields+"],\"modifiers\":{\"damage\":"+damage+",\"temporary\":"+temporary+
        "},\"modifierSelectors\":"+ActorRawModifierSelectors(reinterpret_cast<std::uintptr_t>(actor),id,baseId)+
        ",\"effects\":"+ActorEffects(reinterpret_cast<std::uintptr_t>(actor),id,baseId)+
        ",\"lastReceivedHit\":"+ActorHitRecord(actor,id)+
        ",\"damageCalculation\":{\"status\":\"unsupported\",\"reason\":\"requires-calibrated-engine-hit\"}";
    if(!AppendWeaponCriticalStage(actorState,ActorWeaponCriticalStage(reinterpret_cast<std::uintptr_t>(actor),id,baseId))) {
        Emit("error",request.id,",\"error\":\"actor-state-payload-limit\"",generation);return;
    }
    if(!AppendCreatureHealthScaling(actorState,ActorCreatureHealthScaling(reinterpret_cast<std::uintptr_t>(actor),id,baseId))) {
        Emit("error",request.id,",\"error\":\"actor-state-payload-limit\"",generation);return;
    }
    Emit("actor-state",request.id,actorState,generation);
}
