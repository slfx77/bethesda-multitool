// Request-scoped, read-only observation around the SDK-published SetPos handler.
// The original argument stream is forwarded once; arguments are never re-extracted.
CommandInfo* g_referenceCommandEntry=nullptr;
CommandExecute g_referenceCommandOriginal=nullptr;
std::atomic<bool> g_referenceCommandHooked{false};
bool g_referenceCommandAttempted=false;
std::string g_referenceCommandPrefix;
CommandExecute g_referencePublishedHandler=nullptr;
std::string g_referencePublishedPrefix;
const char* g_referenceHandlerRoute="published-table";
struct ReferenceCommandSample {
    UInt32 id=0,baseId=0; std::uint8_t type=0;
    bool identity=false;
    std::string json;
};
ReferenceCommandSample SampleReferenceCommand(TESObjectREFR* reference) {
    ReferenceCommandSample sample;
    if(!g_pcLayoutVerified){sample.json=ActorUnavailable("reference-layout-unavailable");return sample;}
    std::uint8_t raw[0x68]{};
    if(!reference || !ReadRuntime(reinterpret_cast<std::uintptr_t>(reference),raw)) {
        sample.json=ActorUnavailable("reference-unreadable");return sample;
    }
    sample.type=raw[4];memcpy(&sample.id,raw+0xC,4);
    const auto hex=ActorHex(raw,sizeof(raw));
    if(!sample.id || sample.type<0x3A || sample.type>0x3C) {
        sample.json="{\"status\":\"unavailable\",\"reason\":\"reference-type-or-identity-mismatch\",\"rawHex\":"+Quote(hex)+"}";
        return sample;
    }
    UInt32 base=0,parent=0,render=0;memcpy(&base,raw+0x20,4);memcpy(&parent,raw+0x40,4);memcpy(&render,raw+0x64,4);
    std::uint8_t baseType=0,parentType=0;UInt32 parentId=0;
    const bool haveBase=ReadFormIdentity(reinterpret_cast<void*>(base),sample.baseId,baseType);
    const bool haveParent=ReadFormIdentity(reinterpret_cast<void*>(parent),parentId,parentType) && parentType==0x39;
    sample.identity=haveBase;
    float rotation[3]{},position[3]{};memcpy(rotation,raw+0x24,sizeof(rotation));memcpy(position,raw+0x30,sizeof(position));
    auto values=[](const float* v){return "["+ActorNumber(v[0])+","+ActorNumber(v[1])+","+ActorNumber(v[2])+"]";};
    sample.json="{\"status\":"+Quote(haveBase?"observed":"partial")+",\"engineTargetFormId\":"+std::to_string(sample.id)+
        ",\"engineTargetFormType\":"+std::to_string(sample.type)+",\"base\":"+
        (haveBase?"{\"status\":\"observed\",\"formId\":"+std::to_string(sample.baseId)+",\"formType\":"+std::to_string(baseType)+"}":ActorUnavailable("base-identity-unreadable"))+
        ",\"parentCell\":"+(haveParent?"{\"status\":\"observed\",\"formId\":"+std::to_string(parentId)+"}":ActorUnavailable("parent-cell-unreadable"))+
        ",\"position\":"+values(position)+",\"rotation\":"+values(rotation)+",\"rotationUnits\":\"radians\",\"renderStateAddress\":"+
        std::to_string(render)+",\"rawHex\":"+Quote(hex)+",\"evidence\":\"pinned-sdk-TESObjectREFR-fields\"}";
    return sample;
}
__declspec(noinline) bool ReferenceCommandExecute(ParamInfo* parameters,void* data,TESObjectREFR* reference,
    TESObjectREFR* containing,Script* script,ScriptEventList* events,double* result,UInt32* offset) {
    const auto incomingError=GetLastError();
    struct RestoreError {DWORD value;~RestoreError(){SetLastError(value);}} restoreError{incomingError};
    CommandFloatingGuard floating;
    const auto request=g_currentRequest,generation=g_captureGeneration.load();
    const bool observe=g_capture && g_connected && request!=0;
    const auto caller=_ReturnAddress();
    ReferenceCommandSample before;UInt32 offsetBefore=0;bool haveOffsetBefore=false,prepared=false;std::string context;
    if(observe)try {
        before=SampleReferenceCommand(reference);
        UInt32 unused=0;bool known=false;context=CallContext(reference,script,caller,unused,known,false);
        haveOffsetBefore=offset && ReadRuntime(reinterpret_cast<std::uintptr_t>(offset),offsetBefore);
        prepared=true;
    } catch(...) {DropCommandObservation(generation);}
    SetLastError(incomingError);
    floating.BeforeOriginal();
    const bool returned=g_referenceCommandOriginal(parameters,data,reference,containing,script,events,result,offset);
    floating.AfterOriginal();
    restoreError.value=GetLastError();
    if(!observe || !prepared || !g_capture || !g_connected || generation!=g_captureGeneration.load())return returned;
    try {
        const auto after=SampleReferenceCommand(reference);
        UInt32 afterOffset=0;
        const bool haveAfterOffset=offset && ReadRuntime(reinterpret_cast<std::uintptr_t>(offset),afterOffset);
        double value=0;const bool haveValue=ReadNumber(result,value);
        if(before.identity)context+=",\"engineTargetBaseFormId\":"+std::to_string(before.baseId)+",\"engineTargetFormType\":"+std::to_string(before.type);
        Emit("reference-command",request,",\"command\":\"SetPos\",\"opcode\":"+std::to_string(g_referenceCommandEntry->opcode)+
            ",\"handlerAddress\":"+std::to_string(reinterpret_cast<UInt32>(g_referenceCommandOriginal))+
            ",\"handlerPrefixHex\":"+Quote(g_referenceCommandPrefix)+
            ",\"publishedHandlerAddress\":"+std::to_string(reinterpret_cast<UInt32>(g_referencePublishedHandler))+
            ",\"publishedHandlerPrefixHex\":"+Quote(g_referencePublishedPrefix)+
            ",\"handlerRoute\":"+Quote(g_referenceHandlerRoute)+",\"handlerReturned\":"+(returned?"true":"false")+context+
            ",\"commandResult\":"+(haveValue?"{\"status\":\"observed\",\"value\":"+NumberField(value)+"}":ActorUnavailable("numeric-return-unavailable"))+
            ",\"opcodeOffsetBefore\":"+(haveOffsetBefore?std::to_string(offsetBefore):"null")+
            ",\"opcodeOffsetAfter\":"+(haveAfterOffset?std::to_string(afterOffset):"null")+
            ",\"before\":"+before.json+",\"after\":"+after.json+
            ",\"sameIdentityAfter\":"+(before.identity && after.identity && before.id==after.id && before.type==after.type && before.baseId==after.baseId?"true":"false")+
            ",\"evidence\":\"actual-command-execute-and-raw-reference-fields\",\"argumentEvaluation\":\"not-repeated\"",generation);
    } catch(...) {DropCommandObservation(generation);}
    return returned;
}
void InstallReferenceCommandHook() {
    if(!g_commands || g_referenceCommandAttempted)return;
    g_referenceCommandAttempted=true;
    g_referenceCommandEntry=const_cast<CommandInfo*>(g_commands->GetByName("SetPos"));
    if(!g_referenceCommandEntry)return;
    g_referenceCommandOriginal=g_referenceCommandEntry->execute;
    if(!ExecutableAddress(reinterpret_cast<void*>(g_referenceCommandOriginal)))return;
    g_referencePublishedHandler=g_referenceCommandOriginal;
    g_referenceHandlerRoute="published-table";
    g_referenceCommandPrefix.clear();g_referencePublishedPrefix.clear();
    for(size_t index=0;index<g_generalInstalled.load();++index) {
        const auto& general=g_generalCommands[index];
        if(general.entry==g_referenceCommandEntry && g_referenceCommandOriginal==g_generalWrappers[index]) {
            g_referencePublishedHandler=general.original;
            g_referenceHandlerRoute="bridge-general-wrapper";
            break;
        }
    }
    std::uint8_t prefix[256]{};
    if(ReadRuntime(reinterpret_cast<std::uintptr_t>(g_referenceCommandOriginal),prefix))g_referenceCommandPrefix=ActorHex(prefix,sizeof(prefix));
    if(ReadRuntime(reinterpret_cast<std::uintptr_t>(g_referencePublishedHandler),prefix))g_referencePublishedPrefix=ActorHex(prefix,sizeof(prefix));
    g_referenceCommandHooked=ReplacePointer(reinterpret_cast<void**>(&g_referenceCommandEntry->execute),
        reinterpret_cast<void*>(g_referenceCommandOriginal),reinterpret_cast<void*>(ReferenceCommandExecute));
}
void RestoreReferenceCommandHook() {
    if(g_referenceCommandHooked && g_referenceCommandEntry && g_referenceCommandOriginal)
        ReplacePointer(reinterpret_cast<void**>(&g_referenceCommandEntry->execute),reinterpret_cast<void*>(ReferenceCommandExecute),reinterpret_cast<void*>(g_referenceCommandOriginal));
    g_referenceCommandHooked=false;
}
