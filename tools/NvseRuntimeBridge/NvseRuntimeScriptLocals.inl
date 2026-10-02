// PC 1.4.0.525: pinned Script/VariableInfo/ScriptEventList/ExtraScript layouts.
// A checked storage observation, not an engine GetScriptVariable return. No routine calls.
// Legacy QuestRead10/QuestSet11 keep their existing resolver and acceptance unchanged.
struct ScriptLocalSpan { UInt32 address=0; std::vector<std::uint8_t> bytes; };
template<class Read,class Mapped> struct ScriptLocalReader {
    Read read; Mapped mapped; size_t admitted=0; std::vector<ScriptLocalSpan> spans;
    bool Bytes(std::uint64_t address,void* destination,size_t size) {
        if(!ActorEffectRange(address,size)||size>1024*1024-admitted||
           !mapped(static_cast<UInt32>(address),size,false)||!read(static_cast<UInt32>(address),destination,size))return false;
        admitted+=size;const auto first=static_cast<const std::uint8_t*>(destination);
        spans.push_back({static_cast<UInt32>(address),{first,first+size}});return true;
    }
    bool Stable() {
        for(const auto& span:spans) {
            std::vector<std::uint8_t> current(span.bytes.size());
            if(!mapped(span.address,current.size(),false)||!read(span.address,current.data(),current.size())||current!=span.bytes)return false;
        }
        return true;
    }
};
struct ScriptLocalStorage {
    std::string status="unavailable",reason="storage-unreadable",presence="unknown",name;
    UInt32 script=0,scriptId=0,eventList=0,metadata=0,index=0,local=0,valueAddress=0,listHead=0;
    std::uint8_t metadataType=0; std::array<std::uint8_t,8> raw{};
    double value=0; bool rawObserved=false,complete=false;
};
bool ScriptLocalName(const std::string& name) {
    return !name.empty()&&name.size()<=128&&
        ((name[0]>='A'&&name[0]<='Z')||(name[0]>='a'&&name[0]<='z')||name[0]=='_')&&
        std::all_of(name.begin(),name.end(),[](unsigned char c){return (c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_';});
}
template<class Reader>
ScriptLocalStorage ReadScriptLocalStorage(UInt32 script,UInt32 events,const std::string& name,Reader& memory) {
    ScriptLocalStorage result;result.script=script;result.eventList=events;result.name=name;
    auto fail=[&](const char* reason){result.reason=reason;return result;};
    UInt32 header[4]{},eventScript=0;
    if(!ScriptLocalName(name)||!script||!memory.Bytes(script,header,sizeof(header))||
       (header[1]&255)!=0x11||!header[3]||(header[2]&(0x20|0x4000))||
       !memory.mapped(header[0],4,true))return fail("script-identity-unavailable");
    result.scriptId=header[3];
    if(!events){result.status="absent";result.presence="absent";result.reason="event-list-absent";result.complete=true;return result;}
    if(!memory.Bytes(events,&eventScript,4)||eventScript!=script)return fail("event-script-mismatch-or-unreadable");
    std::uint64_t node=std::uint64_t(script)+0x4C;
    std::unordered_set<UInt32> nodes,indices;std::unordered_set<std::string> names;
    bool found=false,ambiguous=false;size_t count=0;
    while(node) {
        UInt32 link[2]{},meta[8]{};
        if(++count>4096||node>UINT32_MAX||!nodes.insert(static_cast<UInt32>(node)).second||!memory.Bytes(node,link,sizeof(link)))return fail("metadata-list-unreadable-cycle-or-limit");
        if(link[0]) {
            if(!memory.Bytes(link[0],meta,sizeof(meta)))return fail("metadata-unreadable");
            const auto length=meta[7]&0xFFFF,capacity=meta[7]>>16;
            if(!meta[0]||!length||length>128||capacity<length||!meta[6])return fail("metadata-name-or-index-invalid");
            std::string observed(length,'\0');
            if(!memory.Bytes(meta[6],observed.data(),length)||!ScriptLocalName(observed))return fail("metadata-name-unreadable-or-invalid");
            auto folded=observed;for(auto& c:folded)if(c>='A'&&c<='Z')c+=('a'-'A');
            if(!indices.insert(meta[0]).second||!names.insert(folded).second)ambiguous=true;
            if(!_stricmp(observed.c_str(),name.c_str())) {
                if(found)ambiguous=true;
                found=true;result.metadata=link[0];result.index=meta[0];result.metadataType=static_cast<std::uint8_t>(meta[4]);
            }
        }
        node=link[1];
    }
    if(ambiguous){result.status="ambiguous";return fail("duplicate-metadata-name-or-index");}
    if(!found){result.status="absent";result.presence="absent";result.complete=true;return fail("variable-name-absent");}
    // Runtime reference entries (including SCRV) discriminate ref from numeric type zero.
    node=std::uint64_t(script)+0x44;nodes.clear();count=0;bool reference=false;
    while(node) {
        UInt32 link[2]{},index=0;
        if(++count>4096||node>UINT32_MAX||!nodes.insert(static_cast<UInt32>(node)).second||!memory.Bytes(node,link,sizeof(link)))return fail("reference-list-unreadable-cycle-or-limit");
        if(link[0]) {
            if(!memory.Bytes(std::uint64_t(link[0])+0xC,&index,4))return fail("reference-index-unreadable");
            if(index==result.index)reference=true;
        }
        node=link[1];
    }
    if(reference){result.status="unsupported";result.presence="declared";return fail("reference-local");}
    if(result.metadataType>1){result.status="unsupported";result.presence="declared";return fail("non-numeric-metadata-type");}
    if(!memory.Bytes(std::uint64_t(events)+0xC,&result.listHead,4))return fail("local-head-unreadable");
    node=result.listHead;nodes.clear();indices.clear();count=0;found=false;ambiguous=false;
    while(node) {
        UInt32 link[2]{},local[4]{};
        if(++count>4096||node>UINT32_MAX||!nodes.insert(static_cast<UInt32>(node)).second||!memory.Bytes(node,link,sizeof(link)))return fail("local-list-unreadable-cycle-or-limit");
        if(link[0]) {
            if(!memory.Bytes(link[0],local,sizeof(local)))return fail("local-unreadable");
            if(!indices.insert(local[0]).second)ambiguous=true;
            if(local[0]==result.index) {
                if(found)ambiguous=true;
                found=true;result.local=link[0];
                if(std::uint64_t(link[0])+8>UINT32_MAX)return fail("local-address-overflow");
                result.valueAddress=link[0]+8;memcpy(result.raw.data(),local+2,8);result.rawObserved=true;memcpy(&result.value,result.raw.data(),8);
            }
        }
        node=link[1];
    }
    if(ambiguous){result.status="ambiguous";return fail("duplicate-local-index");}
    result.complete=true;
    if(!found){result.status="absent";result.presence="absent";return fail("variable-slot-absent");}
    result.presence="present";
    if(!std::isfinite(result.value))return fail("nonfinite-local-value");
    result.status="observed";result.reason="numeric-local-storage";return result;
}
struct ReferenceLocalObservation {
    ScriptLocalStorage storage;
    UInt32 owner=0,ownerId=0,ownerFlags=0,base=0,baseId=0,extra=0,extraHead=0;
    std::uint8_t ownerType=0,baseType=0;bool identityStable=false;
};
template<class Read,class Mapped,class Lookup>
ReferenceLocalObservation ReadReferenceLocal(bool profile,UInt32 requested,const std::string& name,Read read,Mapped mapped,Lookup lookup) {
    ReferenceLocalObservation result;result.storage.name=name;
    auto fail=[&](const char* reason){result.storage.status="unavailable";result.storage.reason=reason;return result;};
    if(!profile)return fail("reference-local-profile-unavailable");
    if(!requested||!ScriptLocalName(name))return fail("invalid-reference-local-request");
    ScriptLocalReader<Read,Mapped> memory{read,mapped};
    result.owner=lookup(requested);UInt32 form[4]{},base[4]{};
    if(!memory.Bytes(result.owner,form,sizeof(form))||form[3]!=requested)return fail("owner-identity-unavailable");
    result.ownerId=form[3];result.ownerType=static_cast<std::uint8_t>(form[1]);result.ownerFlags=form[2];
    if(result.ownerType<0x3A||result.ownerType>0x3C||!(form[2]&0x400)||(form[2]&(0x20|0x4000))||
       (requested>>24)==255)return fail("owner-not-supported-persistent-reference");
    if(!mapped(form[0],4,true)||!memory.Bytes(std::uint64_t(result.owner)+0x20,&result.base,4)||
       !memory.Bytes(result.base,base,sizeof(base))||!base[3]||(base[2]&(0x20|0x4000))||!mapped(base[0],4,true))return fail("base-identity-unavailable");
    result.baseId=base[3];result.baseType=static_cast<std::uint8_t>(base[1]);
    if(lookup(result.baseId)!=result.base)return fail("base-map-identity-mismatch");
    if((result.ownerType==0x3B&&result.baseType!=0x2A)||(result.ownerType==0x3C&&result.baseType!=0x2B))return fail("actor-base-type-mismatch");
    std::uint8_t presence=0;size_t count=0;std::unordered_set<UInt32> seen;unsigned matches=0;
    if(!memory.Bytes(std::uint64_t(result.owner)+0x48,&result.extraHead,4)||
       !memory.Bytes(std::uint64_t(result.owner)+0x4D,&presence,1))return fail("extra-list-unreadable");
    auto node=result.extraHead;
    while(node) {
        UInt32 extra[3]{};
        if(++count>256||!seen.insert(node).second||!memory.Bytes(node,extra,sizeof(extra)))return fail("extra-list-unreadable-cycle-or-limit");
        const auto type=extra[1]&255;
        if(type==0x1C)return fail("container-reference-route-unsupported");
        if(type==0x0D){++matches;result.extra=node;}
        node=extra[2];
    }
    if(matches>1){result.storage.status="ambiguous";result.storage.reason="duplicate-extra-script";}
    else if(bool(presence&0x20)!=(matches==1))return fail("extra-script-presence-mismatch");
    else if(!matches){result.storage.status="absent";result.storage.presence="absent";result.storage.reason="extra-script-absent";result.storage.complete=true;}
    else {
        UInt32 identity[2]{};
        if(!memory.Bytes(std::uint64_t(result.extra)+0xC,identity,sizeof(identity)))return fail("extra-script-unreadable");
        result.storage=ReadScriptLocalStorage(identity[0],identity[1],name,memory);
        if(result.storage.scriptId&&lookup(result.storage.scriptId)!=result.storage.script)return fail("script-map-identity-mismatch");
    }
    result.identityStable=memory.Stable()&&lookup(requested)==result.owner&&lookup(result.baseId)==result.base&&
        (!result.storage.scriptId||lookup(result.storage.scriptId)==result.storage.script);
    if(!result.identityStable){result.storage.status="unavailable";result.storage.reason="identity-or-storage-changed";}
    return result;
}
std::string ReferenceLocalJson(const ReferenceLocalObservation& observed) {
    const auto& s=observed.storage;std::ostringstream raw;
    for(auto byte:s.raw)raw<<std::hex<<std::setw(2)<<std::setfill('0')<<unsigned(byte);
    return ",\"targetKind\":\"reference\",\"component\":\"variable\",\"statistic\":"+Quote(s.name)+
        ",\"status\":"+Quote(s.status)+",\"reason\":"+Quote(s.reason)+",\"presence\":"+Quote(s.presence)+
        ",\"value\":"+(s.status=="observed"&&observed.identityStable?NumberField(s.value):"null")+
        ",\"rawValueHex\":"+(s.rawObserved?Quote(raw.str()):"null")+",\"byteOrder\":\"little\",\"storageType\":\"float64\""+
        ",\"identityStable\":"+(observed.identityStable?"true":"false")+",\"storageScanComplete\":"+(s.complete?"true":"false")+
        ",\"engineTargetFormId\":"+std::to_string(observed.ownerId)+",\"engineTargetFormType\":"+std::to_string(observed.ownerType)+
        ",\"engineTargetBaseFormId\":"+std::to_string(observed.baseId)+",\"engineTargetBaseFormType\":"+std::to_string(observed.baseType)+
        ",\"ownerAddress\":"+std::to_string(observed.owner)+",\"ownerFlags\":"+std::to_string(observed.ownerFlags)+
        ",\"baseAddress\":"+std::to_string(observed.base)+",\"extraHeadAddress\":"+std::to_string(observed.extraHead)+
        ",\"extraScriptAddress\":"+std::to_string(observed.extra)+",\"scriptAddress\":"+std::to_string(s.script)+
        ",\"scriptFormId\":"+std::to_string(s.scriptId)+",\"eventListAddress\":"+std::to_string(s.eventList)+
        ",\"metadataAddress\":"+std::to_string(s.metadata)+",\"variableIndex\":"+std::to_string(s.index)+
        ",\"metadataType\":"+std::to_string(s.metadataType)+",\"localHeadAddress\":"+std::to_string(s.listHead)+
        ",\"localAddress\":"+std::to_string(s.local)+",\"valueAddress\":"+std::to_string(s.valueAddress)+
        ",\"resolverRoute\":\"direct-reference-extra-script\",\"evidence\":\"checked-runtime-storage\"";
}
bool ReferenceLocalRequest(const Request& request) {
    if(request.payload.rfind("reference-local/",0)!=0)return false;
    auto fail=[&](const char* reason){Emit("error",request.id,",\"error\":"+Quote(reason));return true;};
    std::vector<std::string> parts;std::istringstream input(request.payload);std::string part;
    while(std::getline(input,part,'\t'))parts.push_back(part);
    TypedIdentity identity;
    if(request.kind!=7||request.payload.size()>512||request.payload.back()=='\t'||parts.size()!=4||parts[0]!="reference-local/1"||
       !TypedPair(parts[1],parts[2],identity)||identity.plugin=="@player"||!ScriptLocalName(parts[3]))return fail("invalid-reference-local-request");
    const auto capture=g_captureGeneration.load(),connection=g_connectionGeneration.load(),load=g_gameLoadEpoch.load();
    if(!g_capture||!g_connected||!g_loadedGameObserved||!g_pcLayoutVerified||!g_numericEnabled||
       (!request.id)||request.originConnectionGeneration!=connection||request.captureGenerationAtReceipt!=capture||!VerifyRuntimeFormMap())return fail("reference-local-profile-unavailable");
    UInt32 slot=255;
    if(!PluginNumber("GetModIndex \""+identity.plugin+"\"",slot)||slot>=255)return fail("reference-local-plugin-not-loaded");
    const UInt32 requested=(slot<<24)|identity.localId;
    const auto observed=ReadReferenceLocal(true,requested,parts[3],
        [](UInt32 address,void* destination,size_t length){SIZE_T count=0;return ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),destination,length,&count)&&count==length;},
        GmstMapped,[](UInt32 id){return static_cast<UInt32>(reinterpret_cast<std::uintptr_t>(LookupRuntimeForm(id)));});
    if(!g_capture||!g_connected||!g_loadedGameObserved||capture!=g_captureGeneration.load()||
       connection!=g_connectionGeneration.load()||load!=g_gameLoadEpoch.load())return true;
    Emit("snapshot",request.id,ReferenceLocalJson(observed)+",\"plugin\":"+Quote(identity.plugin)+",\"localFormId\":"+std::to_string(identity.localId)+
        ",\"requestedFormId\":"+std::to_string(requested)+",\"connectionGeneration\":"+std::to_string(connection)+
        ",\"captureGeneration\":"+std::to_string(capture)+",\"loadEpoch\":"+std::to_string(load)+",\"executableSha256\":"+Quote(g_executableSha256),capture);
    return true;
}
