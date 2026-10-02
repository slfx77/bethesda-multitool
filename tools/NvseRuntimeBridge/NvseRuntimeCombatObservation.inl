// Game-thread sampling of an already armed lease. These six fixed SDK calls are
// reads; observation does not dispatch, extend, or retry combat.
constexpr ULONGLONG CombatStateIntervalMilliseconds=50;
constexpr UInt32 CombatStateMaximumSamples=101;
constexpr const char* CombatStateCommands[]={"GetAnimAction","GetCurrentAIProcedure","GetCurrentAIPackage",
    "IsWeaponOut","IsInCombat","GetCombatTarget"};
struct CombatStateCursor {
    std::uint64_t lease=0,capture=0,connection=0,load=0,frame=0;
    ULONGLONG sampled=0;
    UInt32 samples=0;
};
CombatStateCursor g_combatStateCursor; // Game-thread only.
bool CombatStateCurrent(const CombatLeaseRecord& lease,ULONGLONG now) {
    if(!g_capture || !g_connected || !g_loadedGameObserved || lease.captureGeneration!=g_captureGeneration.load() ||
       lease.connectionGeneration!=g_connectionGeneration.load() || lease.loadEpoch!=g_gameLoadEpoch.load() ||
       now<lease.armed || now>=lease.deadline)return false;
    std::lock_guard<std::mutex> lock(g_combatMutex);
    return g_combatLease.active && g_combatLease.id==lease.id && !g_combatLease.cleanupIssued &&
        !g_combatLease.cleaning && g_combatLease.trigger.empty();
}
bool CombatStateCommandMetadata(const char* name,TypedSafetyCommand& result,std::string& error) {
    bool supported=false;
    for(const auto* allowed:CombatStateCommands)if(!strcmp(allowed,name))supported=true;
    if(!supported){error="unsupported-combat-state-command";return false;}
    if(!g_commands || g_commands->version<1 || !g_commands->GetByName || !g_commands->GetByOpcode || !g_commands->GetReturnType) {
        error="combat-state-command-interface-unavailable";return false;
    }
    result={};result.entry=g_commands->GetByName(name);
    if(!result.entry || !CommandReadBytes(reinterpret_cast<UInt32>(result.entry),&result.metadata,sizeof(result.metadata))) {
        error="combat-state-command-metadata-unavailable";return false;
    }
    std::string actual;
    if(!ReadCommandName(result.metadata.longName,actual) || actual!=name || !result.metadata.opcode ||
       g_commands->GetByOpcode(result.metadata.opcode)!=result.entry || result.metadata.needsParent!=1 ||
       result.metadata.numParams!=0 || !result.metadata.execute || !ExecutableAddress(reinterpret_cast<void*>(result.metadata.execute))) {
        error="combat-state-command-metadata-mismatch";return false;
    }
    result.returnType=g_commands->GetReturnType(result.entry);
    const UInt32 expected=!strcmp(name,"GetCombatTarget")?1u:0u; // Pinned SDK Form / Default.
    if(result.returnType!=expected){error="combat-state-command-return-type-mismatch";return false;}
    return true;
}
struct CombatStateValue {std::string json;bool available=false;};
CombatStateValue CombatStateRead(const char* name,const CombatLeaseRecord& lease,const CombatLeaseIo& io,ULONGLONG now) {
    auto unavailable=[](const std::string& reason){return CombatStateValue{ActorUnavailable(reason.c_str()),false};};
    if(!CombatStateCurrent(lease,std::max(now,GetTickCount64())))return unavailable("combat-lease-no-longer-active");
    std::string error;TypedForm before;
    if(!CombatLeaseResolve(lease,lease.attacker,lease.attackerBaseAddress,before,io,error))return unavailable(error);
    TypedSafetyCommand command;
    if(!CombatStateCommandMetadata(name,command,error))return unavailable(error);
    NumericElement result{};
    // Name/opcode/handler participate in the existing bounded compiled-function cache.
    const bool returned=TypedCall(std::string("SetFunctionValue (owner.")+name+")\n; active PC opcode "+
        std::to_string(command.metadata.opcode)+" handler "+std::to_string(reinterpret_cast<UInt32>(command.metadata.execute)),
        before,nullptr,0,result,error);
    if(!CombatStateCurrent(lease,std::max(now,GetTickCount64())))return unavailable("combat-lease-changed-during-read");
    TypedForm after;
    if(!CombatLeaseResolve(lease,lease.attacker,lease.attackerBaseAddress,after,io,error))return unavailable(error);
    TypedSafetyCommand current;std::string metadataError;
    if(!CombatStateCommandMetadata(name,current,metadataError) || current.entry!=command.entry ||
       memcmp(&current.metadata,&command.metadata,sizeof(command.metadata)) || current.returnType!=command.returnType)
        return unavailable("combat-state-command-changed-during-read");
    if(!returned)return unavailable(error.empty()?"combat-state-return-unavailable":error);
    const auto metadata=",\"command\":{\"name\":"+Quote(name)+",\"opcode\":"+std::to_string(command.metadata.opcode)+
        ",\"tableEntryAddress\":"+std::to_string(reinterpret_cast<UInt32>(command.entry))+
        ",\"executeAddress\":"+std::to_string(reinterpret_cast<UInt32>(command.metadata.execute))+
        ",\"needsParent\":1,\"numParams\":0,\"returnType\":"+std::to_string(command.returnType)+"}";
    if(command.returnType==1) {
        if(result.type!=2)return unavailable("combat-state-form-return-unavailable");
        if(!result.form)return {"{\"status\":\"empty\",\"formId\":null,\"matchesLeaseTarget\":false"+metadata+"}",true};
        UInt32 id=0;std::uint8_t type=0;TypedForm target,repeated;
        if(!ReadFormIdentity(result.form,id,type) || !TypedValidateForm(result.form,id,target) || !TypedIsActor(target) ||
           !TypedRefresh(target,repeated,io.lookup) || repeated.pointer!=target.pointer || repeated.baseType!=target.baseType)
            return unavailable("combat-state-returned-actor-identity-unavailable");
        const bool matches=target.pointer==lease.target.pointer && target.id==lease.target.id && target.baseId==lease.target.baseId;
        return {"{\"status\":\"observed\",\"formId\":"+std::to_string(id)+",\"baseFormId\":"+std::to_string(target.baseId)+
            ",\"formType\":"+std::to_string(type)+",\"referenceAddress\":"+std::to_string(reinterpret_cast<UInt32>(result.form))+
            ",\"matchesLeaseTarget\":"+(matches?"true":"false")+metadata+"}",true};
    }
    if(result.type!=1 || !std::isfinite(result.number) || std::floor(result.number)!=result.number ||
       result.number<INT32_MIN || result.number>UINT32_MAX)return unavailable("combat-state-integer-return-unavailable");
    if((!strcmp(name,"IsInCombat") || !strcmp(name,"IsWeaponOut")) && result.number!=0 && result.number!=1)
        return unavailable("combat-state-boolean-return-unavailable");
    return {"{\"status\":\"observed\",\"value\":"+NumberField(result.number)+metadata+"}",true};
}
void ServiceCombatObservation(ULONGLONG now=GetTickCount64(),const CombatLeaseIo& io=CombatLeaseIo{}) {
    CombatLeaseRecord lease;
    {std::lock_guard<std::mutex> lock(g_combatMutex);lease=g_combatLease;}
    if(!lease.active || !CombatStateCurrent(lease,now))return;
    auto& cursor=g_combatStateCursor;
    if(cursor.lease!=lease.id || cursor.capture!=lease.captureGeneration || cursor.connection!=lease.connectionGeneration || cursor.load!=lease.loadEpoch)
        cursor={lease.id,lease.captureGeneration,lease.connectionGeneration,lease.loadEpoch,0,0,0};
    if(cursor.samples>=CombatStateMaximumSamples || (cursor.samples && (g_frame<=cursor.frame ||
       now<cursor.sampled || now-cursor.sampled<CombatStateIntervalMilliseconds)))return;
    const auto priorFrame=cursor.frame;const auto priorTime=cursor.sampled;
    cursor.frame=g_frame;cursor.sampled=now;++cursor.samples;
    const RequestScope observationScope(lease.requestId);
    std::string fields;bool complete=true;
    for(const auto* command:CombatStateCommands) {
        const auto value=CombatStateRead(command,lease,io,now);
        if(!fields.empty())fields+=',';
        fields+=Quote(command)+":"+value.json;complete=complete && value.available;
    }
    // The optional route counters are read outside g_combatMutex. SDK hit events
    // remain independent evidence; sampled animation codes do not establish contact.
    const auto completed=std::max(now,GetTickCount64());
    if(!CombatStateCurrent(lease,completed))return;
    std::string error;TypedForm attacker,target;
    const bool identity=CombatLeaseResolve(lease,lease.attacker,lease.attackerBaseAddress,attacker,io,error) &&
        CombatLeaseResolve(lease,lease.target,lease.targetBaseAddress,target,io,error);
    if(!CombatStateCurrent(lease,std::max(completed,GetTickCount64())))return;
    const auto coverage=identity && g_combatInvocationCoverage?g_combatInvocationCoverage(lease):"null";
    if(!CombatStateCurrent(lease,std::max(completed,GetTickCount64())))return;
    Emit("combat-lease-state",lease.requestId,CombatLeaseFields(lease)+",\"status\":"+Quote(!identity?"unavailable":complete?"observed":"partial")+
        ",\"sampleIndex\":"+std::to_string(cursor.samples)+",\"sampleLimit\":"+std::to_string(CombatStateMaximumSamples)+
        ",\"minimumIntervalMilliseconds\":"+std::to_string(CombatStateIntervalMilliseconds)+
        ",\"sampleStartedMonotonicMilliseconds\":"+std::to_string(now)+",\"sampleCompletedMonotonicMilliseconds\":"+std::to_string(completed)+
        ",\"previousSampleFrame\":"+(cursor.samples>1?std::to_string(priorFrame):"null")+
        ",\"elapsedSincePreviousSampleMilliseconds\":"+(cursor.samples>1?std::to_string(now-priorTime):"null")+
        ",\"identityResolved\":"+(identity?"true":"false")+",\"attacker\":"+(identity?"{"+fields+"}":"null")+
        ",\"invocationCoverage\":"+(identity?coverage:"null")+",\"error\":"+(identity?"null":Quote(error))+
        ",\"evidence\":\"sampled-active-pc-command-results\"",lease.captureGeneration);
}
