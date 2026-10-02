// Independent public SDK return, bound to a persistent reference and its attached script.
// The optional GetVariable argument is a QUEST. Reference calls use owner.Command instead.
struct ReferenceSdkCommand {
    const CommandInfo* entry=nullptr; CommandInfo metadata{};
    std::array<ParamInfo,2> params{}; UInt32 returnType=UINT32_MAX;
};
bool ReadReferenceSdkCommand(const char* name,ReferenceSdkCommand& result,std::string& reason) {
    const bool script=!strcmp(name,"GetScript");
    if(!script&&strcmp(name,"HasVariable")&&strcmp(name,"GetVariable")){reason="unsupported-sdk-local-command";return false;}
    if(!g_commands||g_commands->version<1||!g_commands->GetByName||!g_commands->GetByOpcode||!g_commands->GetReturnType) {
        reason="sdk-local-command-interface-unavailable";return false;
    }
    result={};result.entry=g_commands->GetByName(name);
    if(!result.entry||!CommandReadBytes(reinterpret_cast<UInt32>(result.entry),&result.metadata,sizeof(result.metadata))) {
        reason="sdk-local-command-metadata-unavailable";return false;
    }
    const auto& c=result.metadata;std::string actual;
    if(!ReadCommandName(c.longName,actual)||actual!=name||!c.opcode||g_commands->GetByOpcode(c.opcode)!=result.entry||
       c.needsParent!=0||c.numParams!=(script?1:2)||!c.params||!c.execute||!ExecutableAddress(reinterpret_cast<void*>(c.execute))||
       !CommandReadBytes(reinterpret_cast<UInt32>(c.params),result.params.data(),c.numParams*sizeof(ParamInfo))) {
        reason="sdk-local-command-metadata-mismatch";return false;
    }
    // Pinned CommandTable.h / Commands_Script.h: string=0, quest=0x0E, any form=0x3D.
    const auto& p=result.params;
    if(script?(p[0].typeID!=0x3D||p[0].isOptional!=1):
       (p[0].typeID!=0||p[0].isOptional!=0||p[1].typeID!=0x0E||p[1].isOptional!=1)) {
        reason="sdk-local-command-parameters-mismatch";return false;
    }
    result.returnType=g_commands->GetReturnType(result.entry);
    if(result.returnType!=(script?1u:0u)){reason="sdk-local-command-return-type-mismatch";return false;}
    return true;
}
bool SameReferenceSdkCommand(const ReferenceSdkCommand& a,const ReferenceSdkCommand& b) {
    return a.entry==b.entry&&a.returnType==b.returnType&&!memcmp(&a.metadata,&b.metadata,sizeof(a.metadata))&&
        !memcmp(a.params.data(),b.params.data(),a.metadata.numParams*sizeof(ParamInfo));
}
std::string ReferenceSdkCommandJson(const ReferenceSdkCommand& c) {
    return "{\"tableEntryAddress\":"+std::to_string(reinterpret_cast<UInt32>(c.entry))+",\"opcode\":"+std::to_string(c.metadata.opcode)+
        ",\"executeAddress\":"+std::to_string(reinterpret_cast<UInt32>(c.metadata.execute))+",\"needsParent\":0,\"numParams\":"+
        std::to_string(c.metadata.numParams)+",\"returnType\":"+std::to_string(c.returnType)+"}";
}
std::string ReferenceSdkBody(const char* command,const std::string& name,const ReferenceSdkCommand& metadata) {
    return std::string("SetFunctionValue (owner.")+command+(!strcmp(command,"GetScript")?"":" \""+name+"\"")+")\n; active PC opcode "+
        std::to_string(metadata.metadata.opcode)+" handler "+std::to_string(reinterpret_cast<UInt32>(metadata.metadata.execute));
}
bool SameReferenceLocalBinding(const ReferenceLocalObservation& a,const ReferenceLocalObservation& b) {
    const auto& x=a.storage;const auto& y=b.storage;
    return a.identityStable&&b.identityStable&&a.owner==b.owner&&a.ownerId==b.ownerId&&a.ownerType==b.ownerType&&
        a.ownerFlags==b.ownerFlags&&a.base==b.base&&a.baseId==b.baseId&&a.baseType==b.baseType&&a.extra==b.extra&&a.extraHead==b.extraHead&&
        x.script==y.script&&x.scriptId==y.scriptId&&x.eventList==y.eventList&&x.metadata==y.metadata&&x.index==y.index&&
        x.metadataType==y.metadataType&&x.local==y.local&&x.listHead==y.listHead&&x.valueAddress==y.valueAddress&&
        x.status==y.status&&x.reason==y.reason&&x.presence==y.presence&&x.complete==y.complete&&x.rawObserved==y.rawObserved&&x.raw==y.raw;
}
struct ReferenceSdkValue { bool returned=false;std::uint8_t type=0;double number=0;UInt32 form=0;std::string reason; };
struct ReferenceSdkObservation {
    ReferenceLocalObservation before,after;std::string status="unavailable",reason="sdk-local-unavailable",presence="unknown";
    ReferenceSdkValue scriptBefore,hasBefore,value,hasAfter,scriptAfter;
    bool afterObserved=false,identityStable=false,commandsStable=false,valueMatchesStorage=false;
};
template<class Observe,class Call,class Alive,class CommandsStable>
ReferenceSdkObservation ReadReferenceSdkLocal(const ReferenceLocalObservation& before,Observe observe,Call call,Alive alive,CommandsStable commandsStable) {
    ReferenceSdkObservation out;out.before=before;
    auto fail=[&](const char* reason){out.status="unavailable";out.reason=reason;return out;};
    const auto& s=before.storage;
    if(!before.identityStable||!s.script||!s.scriptId||!alive())return fail("sdk-local-owner-or-script-unavailable");
    if(s.status!="observed"&&s.status!="absent"&&s.status!="unsupported")return fail("sdk-local-storage-classification-unavailable");
    auto invoke=[&](const char* name,ReferenceSdkValue& value) {
        if(!alive()){value.reason="sdk-local-capture-changed";return false;}
        value=call(name);
        return value.returned&&alive();
    };
    if(!invoke("GetScript",out.scriptBefore))return fail("sdk-local-script-return-unavailable");
    if(out.scriptBefore.type!=2||out.scriptBefore.form!=s.script)return fail("sdk-local-attached-script-mismatch");
    if(!invoke("HasVariable",out.hasBefore)||out.hasBefore.type!=1||!std::isfinite(out.hasBefore.number)||
       (out.hasBefore.number!=0&&out.hasBefore.number!=1))return fail("sdk-local-presence-return-unavailable");
    const bool declared=s.metadata!=0;
    if(bool(out.hasBefore.number)!=declared)return fail("sdk-local-presence-disagrees");
    if(s.status=="observed") {
        if(!invoke("GetVariable",out.value)||out.value.type!=1||!std::isfinite(out.value.number))return fail("sdk-local-number-return-unavailable");
    }
    if(!invoke("HasVariable",out.hasAfter)||out.hasAfter.type!=1||out.hasAfter.number!=out.hasBefore.number)
        return fail("sdk-local-presence-changed");
    if(!invoke("GetScript",out.scriptAfter)||out.scriptAfter.type!=2||out.scriptAfter.form!=s.script)
        return fail("sdk-local-attached-script-changed");
    if(!alive())return fail("sdk-local-capture-changed");
    out.after=observe();out.afterObserved=true;out.identityStable=SameReferenceLocalBinding(before,out.after);
    out.commandsStable=commandsStable();
    if(!out.identityStable)return fail("sdk-local-owner-or-storage-changed");
    if(!out.commandsStable)return fail("sdk-local-command-metadata-changed");
    if(!alive())return fail("sdk-local-capture-changed");
    out.status=s.status;out.presence=s.presence;
    out.reason=s.status=="observed"?"sdk-reference-variable-return":s.reason;
    out.valueMatchesStorage=s.status=="observed"&&out.value.number==s.value;
    return out;
}
std::string ReferenceSdkObservationJson(const ReferenceSdkObservation& o) {
    auto number=[](const ReferenceSdkValue& v){return v.returned&&v.type==1&&std::isfinite(v.number)?NumberField(v.number):"null";};
    return ",\"targetKind\":\"reference\",\"component\":\"variable\",\"statistic\":"+Quote(o.before.storage.name)+
        ",\"status\":"+Quote(o.status)+",\"reason\":"+Quote(o.reason)+",\"presence\":"+Quote(o.presence)+
        ",\"value\":"+(o.status=="observed"?number(o.value):"null")+",\"sdkReturnedValue\":"+number(o.value)+
        ",\"hasVariableBefore\":"+number(o.hasBefore)+",\"hasVariableAfter\":"+number(o.hasAfter)+
        ",\"sdkScriptBeforeAddress\":"+(o.scriptBefore.returned&&o.scriptBefore.type==2?std::to_string(o.scriptBefore.form):"null")+
        ",\"sdkScriptAfterAddress\":"+(o.scriptAfter.returned&&o.scriptAfter.type==2?std::to_string(o.scriptAfter.form):"null")+
        ",\"identityStable\":"+(o.identityStable?"true":"false")+",\"commandsStable\":"+(o.commandsStable?"true":"false")+
        ",\"valueMatchesStorage\":"+(o.status=="observed"?(o.valueMatchesStorage?"true":"false"):"null")+
        ",\"engineTargetFormId\":"+std::to_string(o.before.ownerId)+",\"engineTargetBaseFormId\":"+std::to_string(o.before.baseId)+
        ",\"engineTargetFormType\":"+std::to_string(o.before.ownerType)+",\"scriptFormId\":"+std::to_string(o.before.storage.scriptId)+
        ",\"storageBefore\":{"+ReferenceLocalJson(o.before).substr(1)+"},\"storageAfter\":"+
        (o.afterObserved?"{"+ReferenceLocalJson(o.after).substr(1)+"}":"null")+
        ",\"resolverRoute\":\"sdk-explicit-reference-udf\",\"evidence\":\"sdk-return-with-independent-storage-bracket\"";
}
bool ReferenceLocalSdkRequest(const Request& request) {
    if(request.payload.rfind("reference-local-sdk/",0)!=0)return false;
    auto fail=[&](const char* reason){Emit("error",request.id,",\"error\":"+Quote(reason));return true;};
    std::vector<std::string> parts;std::istringstream input(request.payload);std::string part;
    while(std::getline(input,part,'\t'))parts.push_back(part);
    TypedIdentity identity;
    if(request.kind!=7||request.payload.size()>512||request.payload.back()=='\t'||parts.size()!=4||parts[0]!="reference-local-sdk/1"||
       !TypedPair(parts[1],parts[2],identity)||identity.plugin=="@player"||!ScriptLocalName(parts[3]))return fail("invalid-reference-local-sdk-request");
    const auto capture=g_captureGeneration.load(),connection=g_connectionGeneration.load(),load=g_gameLoadEpoch.load();
    auto alive=[&](){return g_capture&&g_connected&&g_loadedGameObserved&&capture==g_captureGeneration.load()&&
        connection==g_connectionGeneration.load()&&load==g_gameLoadEpoch.load();};
    if(!alive()||!g_pcLayoutVerified||!g_numericEnabled||!g_script||!g_script->CompileScript||!g_script->CallFunction||!request.id||
       request.originConnectionGeneration!=connection||request.captureGenerationAtReceipt!=capture||!VerifyRuntimeFormMap())
        return fail("reference-local-sdk-profile-unavailable");
    TypedForm owner;std::string reason;
    if(!TypedResolve(identity,owner,reason)||!TypedIsReference(owner))return fail("reference-local-sdk-owner-unavailable");
    auto observe=[&](){return ReadReferenceLocal(true,owner.id,parts[3],
        [](UInt32 a,void* d,size_t n){SIZE_T count=0;return ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(a),d,n,&count)&&count==n;},
        GmstMapped,[](UInt32 id){return static_cast<UInt32>(reinterpret_cast<std::uintptr_t>(LookupRuntimeForm(id)));});};
    const auto before=observe();
    if(before.owner!=reinterpret_cast<UInt32>(owner.pointer)||before.baseId!=owner.baseId)return fail("reference-local-sdk-owner-rebound");
    std::map<std::string,ReferenceSdkCommand> commands;
    for(const auto* name:{"GetScript","HasVariable","GetVariable"})
        if(!ReadReferenceSdkCommand(name,commands[name],reason))return fail(reason.c_str());
    auto stable=[&](){for(const auto& pair:commands){ReferenceSdkCommand current;std::string why;
        if(!ReadReferenceSdkCommand(pair.first.c_str(),current,why)||!SameReferenceSdkCommand(pair.second,current))return false;}return true;};
    auto call=[&](const char* name){ReferenceSdkValue value;NumericElement result{};
        // Recheck the pointer map and base before each SDK call, not only after the complete bracket.
        TypedForm current;UInt32 base=0;
        if(!alive()||!stable()||!TypedRefresh(owner,current)||current.pointer!=owner.pointer||current.baseType!=before.baseType||
           !ReadRuntime(reinterpret_cast<UInt32>(current.pointer)+0x20,base)||base!=before.base){value.reason="sdk-local-owner-or-command-changed";return value;}
        value.returned=TypedCall(ReferenceSdkBody(name,parts[3],commands.at(name)),current,nullptr,0,result,value.reason);
        value.type=result.type;
        if(result.type==1)value.number=result.number;
        if(result.type==2)value.form=reinterpret_cast<UInt32>(result.form);
        return value;};
    const auto result=ReadReferenceSdkLocal(before,observe,call,alive,stable);
    if(!alive())return true;
    Emit("snapshot",request.id,ReferenceSdkObservationJson(result)+",\"plugin\":"+Quote(identity.plugin)+",\"localFormId\":"+
        std::to_string(identity.localId)+",\"requestedFormId\":"+std::to_string(owner.id)+",\"connectionGeneration\":"+
        std::to_string(connection)+",\"captureGeneration\":"+std::to_string(capture)+",\"loadEpoch\":"+std::to_string(load)+
        ",\"executableSha256\":"+Quote(g_executableSha256)+",\"sdkCommands\":{\"GetScript\":"+ReferenceSdkCommandJson(commands.at("GetScript"))+
        ",\"HasVariable\":"+ReferenceSdkCommandJson(commands.at("HasVariable"))+",\"GetVariable\":"+ReferenceSdkCommandJson(commands.at("GetVariable"))+"}",capture);
    return true;
}
