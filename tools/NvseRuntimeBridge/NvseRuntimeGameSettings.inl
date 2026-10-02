// PC numeric GMSTs: pinned GameSettings.h / NiTypes.h layouts, read-only map traversal.
// Mutations use the installed SDK command; no calls through map vtables or raw writes.
constexpr UInt32 GmstSingletonSlot=0x011C8048, GmstMapOffset=0x10C;
constexpr size_t GmstBucketLimit=16384, GmstEntryLimit=65536, GmstCandidateLimit=16;
struct GmstSpan { UInt32 address=0; std::vector<std::uint8_t> bytes; };
struct GmstCandidate {
    UInt32 bucket=0,entry=0,key=0,setting=0,vtable=0,name=0,raw=0;
    std::string keyName,canonicalName,type,reason;
    double value=0;
    bool numeric=false,rawObserved=false;
};
struct GmstLookup {
    std::string requestedName,status="unavailable",reason="profile-unverified";
    UInt32 singleton=GmstSingletonSlot,collection=0,map=0,vtable=0,bucketCount=0,buckets=0,itemCount=0;
    size_t visited=0;
    bool complete=false,identityStable=false;
    std::vector<GmstCandidate> candidates;
};
bool GmstName(const std::string& name) {
    return name.size()>=2 && name.size()<=128 && (name[0]=='b'||name[0]=='i'||name[0]=='u'||name[0]=='f') &&
        std::all_of(name.begin()+1,name.end(),[](unsigned char c){return (c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_';});
}
bool GmstEqualName(const std::string& a,const std::string& b) {
    if(a.size()!=b.size())return false;
    for(size_t i=0;i<a.size();++i) {
        const auto lower=[](unsigned char c){return c>='A'&&c<='Z'?c+('a'-'A'):c;};
        if(lower(a[i])!=lower(b[i]))return false;
    }
    return true;
}
bool GmstValue(char type,double requested,float& submitted,UInt32& raw) {
    if(!std::isfinite(requested)||std::abs(requested)>std::numeric_limits<float>::max())return false;
    submitted=static_cast<float>(requested);
    if(type=='f'){memcpy(&raw,&submitted,4);return true;}
    if(requested!=std::trunc(requested)||static_cast<double>(submitted)!=requested)return false;
    if(type=='b'){if(requested!=0&&requested!=1)return false;raw=requested==1?1:0;return true;}
    if(type=='i') {
        if(requested<INT32_MIN||requested>INT32_MAX)return false;
        const auto value=static_cast<std::int32_t>(requested);memcpy(&raw,&value,4);return true;
    }
    if(type=='u'){if(requested<0||requested>UINT32_MAX)return false;raw=static_cast<UInt32>(requested);return true;}
    return false;
}
bool GmstMapped(UInt32 address,size_t length,bool image) {
    if(!ActorEffectRange(address,length))return false;
    const auto end=std::uint64_t(address)+length;
    for(std::uint64_t current=address;current<end;) {
        MEMORY_BASIC_INFORMATION memory{};
        if(VirtualQuery(reinterpret_cast<void*>(static_cast<UInt32>(current)),&memory,sizeof(memory))!=sizeof(memory)||
           memory.State!=MEM_COMMIT||(memory.Protect&(PAGE_GUARD|PAGE_NOACCESS))||
           (image&&memory.AllocationBase!=GetModuleHandleW(nullptr)))return false;
        const auto p=memory.Protect&0xFF;
        if(p!=PAGE_READONLY&&p!=PAGE_READWRITE&&p!=PAGE_WRITECOPY&&p!=PAGE_EXECUTE_READ&&
           p!=PAGE_EXECUTE_READWRITE&&p!=PAGE_EXECUTE_WRITECOPY)return false;
        const auto next=std::uint64_t(reinterpret_cast<std::uintptr_t>(memory.BaseAddress))+memory.RegionSize;
        if(next<=current)return false;
        current=next;
    }
    return true;
}
template<class Read,class Mapped>
GmstLookup ReadGmstLookup(bool profile,const std::string& requested,Read read,Mapped mapped,UInt32 singleton=GmstSingletonSlot) {
    GmstLookup result;result.requestedName=requested;result.singleton=singleton;
    if(!profile)return result;
    if(!GmstName(requested)){result.reason="invalid-setting-name";return result;}
    std::vector<GmstSpan> spans;
    auto bytes=[&](std::uint64_t address,void* destination,size_t length,bool image=false) {
        if(!ActorEffectRange(address,length)||!mapped(static_cast<UInt32>(address),length,image)||
           !read(static_cast<UInt32>(address),destination,length))return false;
        const auto first=static_cast<const std::uint8_t*>(destination);
        spans.push_back({static_cast<UInt32>(address),{first,first+length}});return true;
    };
    auto text=[&](UInt32 address,std::string& value) {
        value.clear();
        for(size_t i=0;i<256;) {
            const auto current=std::uint64_t(address)+i;
            const auto length=std::min<size_t>({32,256-i,4096-static_cast<size_t>(current&4095)});
            char part[32]{};if(!bytes(current,part,length))return false;
            for(size_t j=0;j<length;++j) {
                const auto c=static_cast<unsigned char>(part[j]);
                if(!c)return !value.empty();
                if(c<32||c>=127)return false;
                value+=static_cast<char>(c);
            }
            i+=length;
        }
        return false;
    };
    if(!bytes(singleton,&result.collection,4,true)||!result.collection){result.reason="collection-unavailable";return result;}
    const auto mapAddress=std::uint64_t(result.collection)+GmstMapOffset;
    UInt32 header[4]{};
    if(!bytes(mapAddress,header,sizeof(header))){result.reason="map-unavailable";return result;}
    result.map=static_cast<UInt32>(mapAddress);result.vtable=header[0];result.bucketCount=header[1];result.buckets=header[2];result.itemCount=header[3];
    if(!mapped(header[0],4,true)||!result.bucketCount||result.bucketCount>GmstBucketLimit||
       result.itemCount>GmstEntryLimit||!result.buckets){result.reason="map-bounds-or-identity-unavailable";return result;}
    std::vector<UInt32> buckets(result.bucketCount);
    if(!bytes(result.buckets,buckets.data(),buckets.size()*4)){result.reason="buckets-unavailable";return result;}
    std::unordered_set<UInt32> seen;
    for(UInt32 bucket=0;bucket<result.bucketCount;++bucket) {
        for(auto node=buckets[bucket];node;) {
            if(result.visited>=GmstEntryLimit||!seen.insert(node).second){result.reason="entry-limit-or-cycle";return result;}
            ++result.visited;
            UInt32 entry[3]{};std::string key;
            if(!bytes(node,entry,sizeof(entry))||!text(entry[1],key)){result.reason="entry-or-key-unavailable";return result;}
            if(GmstEqualName(key,requested)) {
                if(result.candidates.size()>=GmstCandidateLimit){result.reason="candidate-limit";return result;}
                GmstCandidate candidate;candidate.bucket=bucket;candidate.entry=node;candidate.key=entry[1];candidate.setting=entry[2];candidate.keyName=key;
                UInt32 setting[3]{};
                if(!bytes(entry[2],setting,sizeof(setting)))candidate.reason="setting-unreadable";
                else {
                    candidate.vtable=setting[0];candidate.raw=setting[1];candidate.name=setting[2];candidate.rawObserved=true;
                    if(!mapped(setting[0],4,true))candidate.reason="setting-vtable-unavailable";
                    else if(!text(setting[2],candidate.canonicalName))candidate.reason="setting-name-unavailable";
                    else if(!GmstEqualName(candidate.canonicalName,key)||!GmstName(candidate.canonicalName)||
                            candidate.canonicalName[0]!=requested[0])candidate.reason="setting-name-or-type-mismatch";
                    else {
                        const auto type=candidate.canonicalName[0];
                        candidate.type=type=='f'?"float":type=='i'?"integer":type=='u'?"unsigned":"bool";
                        if(type=='f'){float value=0;memcpy(&value,&candidate.raw,4);candidate.value=value;}
                        else if(type=='i'||type=='b'){std::int32_t value=0;memcpy(&value,&candidate.raw,4);candidate.value=value;}
                        else candidate.value=candidate.raw;
                        if(!std::isfinite(candidate.value))candidate.reason="nonfinite-setting";
                        else candidate.numeric=true;
                    }
                }
                result.candidates.push_back(std::move(candidate));
            }
            node=entry[0];
        }
    }
    if(result.visited!=result.itemCount){result.reason="item-count-mismatch";return result;}
    // Repeat all observed structure/key/value bytes, including the singleton and bucket array.
    for(const auto& span:spans) {
        std::vector<std::uint8_t> current(span.bytes.size());
        if(!mapped(span.address,current.size(),false)||!read(span.address,current.data(),current.size())||current!=span.bytes) {
            result.reason="lookup-changed-or-unreadable";return result;
        }
    }
    result.complete=true;result.identityStable=true;result.reason.clear();
    if(result.candidates.empty())result.status="missing";
    else if(result.candidates.size()!=1)result.status="ambiguous";
    else if(!result.candidates[0].numeric){result.status="unavailable";result.reason=result.candidates[0].reason;}
    else result.status="observed";
    return result;
}
GmstLookup GmstLookupNow(const std::string& name) { return ReadGmstLookup(g_pcLayoutVerified,name,CommandReadBytes,GmstMapped); }
bool SameGmstIdentity(const GmstLookup& a,const GmstLookup& b,bool includeValue) {
    if(a.status!="observed"||b.status!="observed"||a.collection!=b.collection||a.map!=b.map||a.vtable!=b.vtable||
       a.bucketCount!=b.bucketCount||a.buckets!=b.buckets||a.itemCount!=b.itemCount)return false;
    const auto& x=a.candidates[0];const auto& y=b.candidates[0];
    return x.bucket==y.bucket&&x.entry==y.entry&&x.key==y.key&&x.setting==y.setting&&x.vtable==y.vtable&&
        x.name==y.name&&x.keyName==y.keyName&&x.canonicalName==y.canonicalName&&x.type==y.type&&(!includeValue||x.raw==y.raw);
}
std::string GmstJson(const GmstLookup& value) {
    std::string candidates;
    for(const auto& c:value.candidates) {
        if(!candidates.empty())candidates+=',';
        candidates+="{\"bucket\":"+std::to_string(c.bucket)+",\"entryAddress\":"+std::to_string(c.entry)+
            ",\"keyAddress\":"+std::to_string(c.key)+",\"keyName\":"+Quote(c.keyName)+
            ",\"settingAddress\":"+std::to_string(c.setting)+",\"settingVtable\":"+std::to_string(c.vtable)+
            ",\"nameAddress\":"+std::to_string(c.name)+",\"canonicalName\":"+Quote(c.canonicalName)+
            ",\"type\":"+Quote(c.type)+",\"rawHex\":"+(c.rawObserved?Quote(ActorHex(reinterpret_cast<const std::uint8_t*>(&c.raw),4)):"null")+
            ",\"value\":"+(c.numeric?NumberField(c.value):"null")+",\"reason\":"+(c.reason.empty()?"null":Quote(c.reason))+"}";
    }
    return "{\"status\":"+Quote(value.status)+",\"reason\":"+(value.reason.empty()?"null":Quote(value.reason))+
        ",\"singletonAddress\":"+std::to_string(value.singleton)+",\"collectionAddress\":"+std::to_string(value.collection)+
        ",\"mapAddress\":"+std::to_string(value.map)+",\"mapVtable\":"+std::to_string(value.vtable)+
        ",\"bucketCount\":"+std::to_string(value.bucketCount)+",\"bucketsAddress\":"+std::to_string(value.buckets)+
        ",\"itemCount\":"+std::to_string(value.itemCount)+",\"visitedEntries\":"+std::to_string(value.visited)+
        ",\"complete\":"+(value.complete?"true":"false")+",\"identityStable\":"+(value.identityStable?"true":"false")+
        ",\"candidates\":["+candidates+"],\"evidence\":\"pinned-sdk-and-retained-PC-map-layout;bounded-memory-read\"}";
}
struct GmstCommand { const CommandInfo* entry=nullptr;CommandInfo metadata{};ParamInfo parameters[2]{};UInt32 returnType=UINT32_MAX; };
bool GmstReadSetter(GmstCommand& command) {
    if(!g_commands||!g_commands->GetByName||!g_commands->GetByOpcode||!g_commands->GetReturnType)return false;
    command={};command.entry=g_commands->GetByName("SetNumericGameSetting");std::string name;
    if(!command.entry||!CommandReadBytes(reinterpret_cast<UInt32>(command.entry),&command.metadata,sizeof(command.metadata))||
       !ReadCommandName(command.metadata.longName,name)||name!="SetNumericGameSetting"||!command.metadata.opcode||
       g_commands->GetByOpcode(command.metadata.opcode)!=command.entry||command.metadata.needsParent!=0||
       command.metadata.numParams!=2||!command.metadata.execute||!ExecutableAddress(reinterpret_cast<void*>(command.metadata.execute))||
       !CommandReadBytes(reinterpret_cast<UInt32>(command.metadata.params),command.parameters,sizeof(command.parameters)))return false;
    command.returnType=g_commands->GetReturnType(command.entry);
    return command.returnType==0&&command.parameters[0].typeID==0&&command.parameters[1].typeID==2&&
        command.parameters[0].isOptional==0&&command.parameters[1].isOptional==0;
}
bool SameGmstCommand(const GmstCommand& a,const GmstCommand& b) {
    return a.entry==b.entry&&a.returnType==b.returnType&&!memcmp(&a.metadata,&b.metadata,sizeof(a.metadata))&&
        !memcmp(a.parameters,b.parameters,sizeof(a.parameters));
}
bool ParseGmstRequest(const Request& request,std::string& name,double& requested,float& submitted,UInt32& expected) {
    if(request.payload.size()>256||request.payload.empty()||request.payload.back()=='\t')return false;
    std::vector<std::string> fields;std::istringstream input(request.payload);std::string field;
    while(std::getline(input,field,'\t'))fields.push_back(field);
    if(fields.size()<3||fields[0]!="gmst/1"||!GmstName(fields[2]))return false;
    name=fields[2];
    if(request.kind==7)return fields.size()==3&&fields[1]=="read";
    if(request.kind!=3||fields.size()!=4||fields[1]!="write"||fields[3].empty()||fields[3].size()>32||
       !std::all_of(fields[3].begin(),fields[3].end(),[](char c){return (c>='0'&&c<='9')||c=='+'||c=='-'||c=='.'||c=='e'||c=='E';}))return false;
    char* end=nullptr;requested=strtod(fields[3].c_str(),&end);
    return end&&end!=fields[3].c_str()&&!*end&&GmstValue(name[0],requested,submitted,expected);
}
const char* GmstWriteFailure(const GmstLookup& before,const GmstLookup& after,bool stable,bool returned,const NumericElement& value,UInt32 expected) {
    if(!stable||!SameGmstIdentity(before,after,false))return "setter-identity-or-capture-changed";
    if(!returned||value.type!=1||!std::isfinite(value.number)||value.number!=1)return "setter-return-unavailable";
    if(after.candidates[0].raw!=expected)return "setter-readback-mismatch";
    return nullptr;
}
bool GmstRequest(const Request& request) {
    if(request.payload.rfind("gmst/",0)!=0)return false;
    std::string name;double requested=0;float submitted=0;UInt32 expected=0;
    if(!ParseGmstRequest(request,name,requested,submitted,expected)) {Emit("error",request.id,",\"error\":\"invalid-game-setting-request\"");return true;}
    if(!g_capture||!g_connected||!g_pcLayoutVerified||!g_loadedGameObserved) {Emit("error",request.id,",\"error\":\"game-setting-profile-or-loaded-capture-unavailable\"");return true;}
    const auto capture=g_captureGeneration.load(),connection=g_connectionGeneration.load(),load=g_gameLoadEpoch.load();
    if(request.receivedObserved&&(request.originConnectionGeneration!=connection||request.captureGenerationAtReceipt!=capture)) {
        Emit("error",request.id,",\"error\":\"game-setting-request-generation-changed\"");return true;
    }
    auto stable=[&](){return g_capture&&g_connected&&g_loadedGameObserved&&capture==g_captureGeneration.load()&&connection==g_connectionGeneration.load()&&load==g_gameLoadEpoch.load();};
    auto before=GmstLookupNow(name);auto after=before;
    std::string status=before.status,reason=before.reason;bool accepted=false,called=false,returned=false,commandValueObserved=false;double commandValue=0;
    GmstCommand command;
    if(request.kind==3&&before.status=="observed") {
        if(!g_numericEnabled||!g_script||!g_script->CompileScript||!g_script->CallFunction||!GmstReadSetter(command)) {status="unavailable";reason="setter-command-unavailable";}
        else if(!stable()){status="unavailable";reason="capture-or-load-changed";}
        else {
            const auto& setting=before.candidates[0];
            const auto body="SetFunctionValue (SetNumericGameSetting \""+setting.canonicalName+"\" number)\n; opcode "+
                std::to_string(command.metadata.opcode)+" handler "+std::to_string(reinterpret_cast<UInt32>(command.metadata.execute));
            // Compile before the final identity check. The cached body contains only a validated name.
            auto found=g_operationFunctions.find(body);
            if(found==g_operationFunctions.end()&&g_operationFunctions.size()<MaxOperationFunctions) {
                const auto script=g_script->CompileScript(("ref owner\nref other\nfloat number\nbegin function { owner other number }\n"+body+"\nend\n").c_str());
                if(script)found=g_operationFunctions.emplace(body,script).first;
            }
            GmstCommand admitted;const auto prior=GmstLookupNow(name);
            if(found==g_operationFunctions.end()){status="unavailable";reason="setter-function-unavailable";}
            else if(!stable()||!SameGmstIdentity(before,prior,true)||!GmstReadSetter(admitted)||!SameGmstCommand(command,admitted)) {status="unavailable";reason="setter-admission-changed";}
            else {
                UInt32 bits=0;memcpy(&bits,&submitted,4);NumericElement value{};called=true;
                returned=g_script->CallFunction(found->second,nullptr,nullptr,&value,3,nullptr,nullptr,bits);
                if(value.type==3)g_numericEnabled=false;
                commandValueObserved=value.type==1&&std::isfinite(value.number);
                if(commandValueObserved)commandValue=value.number;
                after=GmstLookupNow(name);GmstCommand current;
                const auto failure=GmstWriteFailure(before,after,stable()&&GmstReadSetter(current)&&SameGmstCommand(command,current),returned,value,expected);
                if(failure){status="unavailable";reason=failure;}
                else {accepted=true;status="observed";reason.clear();}
            }
        }
    }
    const bool sessionStable=stable(),identityStable=sessionStable&&before.identityStable&&after.identityStable;
    if(!sessionStable){status="unavailable";reason="capture-or-load-changed";accepted=false;}
    const bool observed=status=="observed";
    std::string extra=",\"targetKind\":\"game-setting\",\"component\":\"numeric\",\"statistic\":"+Quote(name)+",\"name\":"+Quote(name)+
        ",\"status\":"+Quote(status)+",\"lookupStatus\":"+Quote(before.status)+",\"reason\":"+(reason.empty()?"null":Quote(reason))+
        ",\"value\":"+(observed?NumberField(after.candidates[0].value):"null")+",\"identityStable\":"+(identityStable?"true":"false")+
        ",\"captureGeneration\":"+std::to_string(capture)+",\"connectionGeneration\":"+std::to_string(connection)+",\"loadEpoch\":"+std::to_string(load)+
        ",\"gameSetting\":"+GmstJson(after)+",\"evidence\":\"game-thread-numeric-setting\"";
    if(request.kind==3)extra+=",\"accepted\":"+std::string(accepted?"true":"false")+",\"requestedValue\":"+NumberField(requested)+
        ",\"submittedValue\":"+NumberField(submitted)+",\"oldValue\":"+(before.status=="observed"?NumberField(before.candidates[0].value):"null")+
        ",\"readbackValue\":"+(called&&after.status=="observed"?NumberField(after.candidates[0].value):"null")+
        ",\"before\":"+GmstJson(before)+",\"commandCalled\":"+(called?"true":"false")+",\"functionReturned\":"+(called?(returned?"true":"false"):"null")+
        ",\"commandValue\":"+(commandValueObserved?NumberField(commandValue):"null")+",\"commandOpcode\":"+std::to_string(command.metadata.opcode)+
        ",\"commandAddress\":"+std::to_string(reinterpret_cast<UInt32>(command.entry));
    Emit(request.kind==7?"snapshot":"action-result",request.id,extra);return true;
}
