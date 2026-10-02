// Bounded opt-in combat cleanup. Engine calls occur only on the game thread.
// Three mutations exactly once; later-frame observations have a two-second
// policy bound. No engine-thread wait or raw writes. Stalled/exited engines
// cannot supply observed cleanup; Hello reports overdue service as unavailable.
constexpr ULONGLONG CombatObservationMilliseconds=2000;
struct CombatCleanupStep {
    std::string json,readbackJson,initialReadbackJson;
    bool observed=false,failed=false,attempted=false,accepted=false,identity=false,haveValue=false,initialIdentity=false;
};
struct CombatPendingStop {Request request{};TypedForm subject;};
struct CombatLeaseRecord {
    bool active=false,cleaning=false,cleanupIssued=false,endRequested=false;
    ULONGLONG cleanupStarted=0,observationDeadline=0;
    std::uint64_t cleanupFrame=0,lastObservationFrame=0,observationCount=0;
    CombatCleanupStep steps[3];
    Request endRequest{};
    CombatPendingStop stops[2];size_t stopCount=0;
    std::uint64_t id=0,requestId=0,captureGeneration=0,connectionGeneration=0,loadEpoch=0;
    ULONGLONG armed=0,deadline=0;
    UInt32 duration=0,attackerBaseAddress=0,targetBaseAddress=0;
    TypedForm attacker,target;
    std::string session,trigger;
};
struct CombatLeaseIo {
    void* (*lookup)(UInt32)=LookupRuntimeForm;
    bool (*mutate)(const std::string&,const TypedForm&,const TypedForm*,float,std::string&)=TypedMutate;
    bool (*number)(const std::string&,const TypedForm&,const TypedForm*,float,double&,std::string&)=TypedNumber;
    bool (*profile)()=VerifyRuntimeFormMap;
};
std::mutex g_combatMutex;
CombatLeaseRecord g_combatLease;
std::string (*g_combatInvocationCoverage)(const CombatLeaseRecord&)=nullptr;
std::uint64_t g_combatLeaseSerial=0;
ULONGLONG g_combatLastService=0;
std::string g_lastCombatCleanup="null";

std::string CombatLeaseFields(const CombatLeaseRecord& lease) {
    return ",\"operation\":\"start-combat-leased\",\"leaseId\":"+std::to_string(lease.id)+
        ",\"originSession\":"+Quote(lease.session)+",\"captureGeneration\":"+std::to_string(lease.captureGeneration)+
        ",\"connectionGeneration\":"+std::to_string(lease.connectionGeneration)+",\"loadEpoch\":"+std::to_string(lease.loadEpoch)+
        ",\"engineTargetFormId\":"+std::to_string(lease.attacker.id)+",\"engineTargetBaseFormId\":"+std::to_string(lease.attacker.baseId)+
        ",\"engineOtherFormId\":"+std::to_string(lease.target.id)+",\"engineOtherBaseFormId\":"+std::to_string(lease.target.baseId)+
        ",\"attackerAddress\":"+std::to_string(reinterpret_cast<std::uintptr_t>(lease.attacker.pointer))+
        ",\"targetAddress\":"+std::to_string(reinterpret_cast<std::uintptr_t>(lease.target.pointer))+
        ",\"attackerBaseAddress\":"+std::to_string(lease.attackerBaseAddress)+",\"targetBaseAddress\":"+std::to_string(lease.targetBaseAddress)+
        ",\"durationMilliseconds\":"+std::to_string(lease.duration)+",\"armedMonotonicMilliseconds\":"+std::to_string(lease.armed)+
        ",\"deadlineMonotonicMilliseconds\":"+std::to_string(lease.deadline);
}
bool CombatLeaseActive() {std::lock_guard<std::mutex> lock(g_combatMutex);return g_combatLease.active;}
void CombatLeaseRequestCleanup(const char* reason,std::uint64_t connection=0) {
    std::lock_guard<std::mutex> lock(g_combatMutex);
    if(g_combatLease.active && (!connection || connection==g_combatLease.connectionGeneration) && g_combatLease.trigger.empty())
        g_combatLease.trigger=reason;
}
bool CombatLeaseSameRequested(const TypedIdentity& a,const TypedIdentity& b) {
    return a.localId==b.localId && !_stricmp(a.plugin.c_str(),b.plugin.c_str());
}
bool CombatLeaseBlocksMutation(const Request& request) {
    CombatLeaseRecord lease;
    {std::lock_guard<std::mutex> lock(g_combatMutex);if(!g_combatLease.active)return false;lease=g_combatLease;}
    if(request.kind==2 || request.kind==3 || request.kind==8 || request.kind==11 || request.kind==14)return true;
    if(request.kind==13)return request.payload!="inspect";
    if(request.kind<16 || request.kind>18)return false;
    TypedOperation op;
    if(!ParseTypedOperation(request,op))return true;
    if(op.op=="read-inventory" || op.op=="read-quest-state")return false;
    return !(op.op=="stop-combat" && (CombatLeaseSameRequested(op.subject,lease.attacker.requested) ||
        CombatLeaseSameRequested(op.subject,lease.target.requested)));
}
bool CombatLeaseResolve(const CombatLeaseRecord& lease,const TypedForm& original,UInt32 baseAddress,
                        TypedForm& current,const CombatLeaseIo& io,std::string& error) {
    if(g_gameLoadEpoch.load()!=lease.loadEpoch || !g_loadedGameObserved){error="load-epoch-changed-or-unavailable";return false;}
    if(!io.profile()){error="runtime-profile-unavailable";return false;}
    UInt32 base=0;
    if(!TypedRefresh(original,current,io.lookup) || current.pointer!=original.pointer || current.baseType!=original.baseType ||
       !ReadRuntime(reinterpret_cast<std::uintptr_t>(current.pointer)+0x20,base) || base!=baseAddress) {
        error="actor-identity-changed-or-unavailable";return false;
    }
    return true;
}
CombatCleanupStep CombatLeaseStep(const CombatLeaseRecord& lease,const char* name,const TypedForm& original,UInt32 base,
                                 const char* statement,const char* statistic,double expected,const CombatLeaseIo& io,bool abandon,
                                 const CombatCleanupStep* previous=nullptr) {
    TypedForm current;std::string error;bool identity=false;
    bool attempted=previous && previous->attempted,accepted=previous && previous->accepted,haveValue=false;
    double value=0;
    if(abandon)error="engine-exiting";
    else if((identity=CombatLeaseResolve(lease,original,base,current,io,error))) {
        if(!previous){attempted=true;accepted=io.mutate(statement,current,nullptr,0,error);}
        TypedForm after;std::string readError;
        if(CombatLeaseResolve(lease,original,base,after,io,readError)) {
            haveValue=io.number(std::string("owner.")+statistic,after,nullptr,0,value,readError) && std::isfinite(value);
            if(!CombatLeaseResolve(lease,original,base,after,io,readError)){identity=false;haveValue=false;}
        } else identity=false;
        if(!haveValue && error.empty())error=readError.empty()?"cleanup-readback-unavailable":readError;
    }
    const bool observed=attempted && accepted && identity && haveValue && value==expected;
    const bool failed=attempted && (!accepted || (haveValue && value!=expected));
    if(!observed && error.empty())error=failed?"cleanup-outcome-mismatch":"cleanup-readback-unavailable";
    const auto readback="{\"statistic\":"+Quote(!strcmp(statistic,"GetDisabled")?"Disabled":"IsInCombat")+
        ",\"status\":"+Quote(haveValue?"observed":"unavailable")+",\"value\":"+(haveValue?NumberField(value):"null")+
        ",\"reason\":"+(haveValue?"null":Quote(error))+"}";
    const auto initial=previous?previous->initialReadbackJson:readback;
    const bool initialIdentity=previous?previous->initialIdentity:identity;
    const auto json="{\"step\":"+Quote(name)+",\"status\":"+Quote(observed?"observed":failed?"failed":"unavailable")+
        ",\"accepted\":"+(attempted?(accepted?"true":"false"):"null")+",\"identityResolved\":"+(identity?"true":"false")+
        ",\"engineTargetFormId\":"+std::to_string(original.id)+",\"engineTargetBaseFormId\":"+std::to_string(original.baseId)+
        ",\"readback\":"+readback+",\"initialReadback\":"+initial+",\"initialIdentityResolved\":"+(initialIdentity?"true":"false")+",\"error\":"+(error.empty()?"null":Quote(error))+"}";
    return {json,readback,initial,observed,failed,attempted,accepted,identity,haveValue,initialIdentity};
}
void CombatLeaseFinishCapture(const Request& request,std::uint64_t generation,std::uint64_t connection) {
    if(!g_connected || g_connectionGeneration.load()!=connection || !g_capture || g_captureGeneration.load()!=generation)return;
    const auto fields=std::string(",\"status\":\"")+(request.kind==5?"cancelled":"completed")+"\""+
        DispatchFingerprintFields("end",generation,connection);
    EndNotificationCapture(request,generation,connection,fields);
}
void ServiceCombatLease(ULONGLONG now=GetTickCount64(),const CombatLeaseIo& io=CombatLeaseIo{},bool abandon=false) {
    CombatLeaseRecord lease;
    {
        std::lock_guard<std::mutex> lock(g_combatMutex);g_combatLastService=now;
        if(!g_combatLease.active || g_combatLease.cleaning)return;
        if(g_combatLease.trigger.empty()) {
            if(abandon)g_combatLease.trigger="engine-exiting";
            else if(g_gameLoadEpoch.load()!=g_combatLease.loadEpoch)g_combatLease.trigger="load-epoch-changed";
            else if(!g_connected || g_connectionGeneration.load()!=g_combatLease.connectionGeneration)g_combatLease.trigger="disconnect";
            else if(!g_capture || g_captureGeneration.load()!=g_combatLease.captureGeneration)g_combatLease.trigger="capture-ended";
            else if(now>=g_combatLease.deadline)g_combatLease.trigger="deadline";
        }
        if(g_combatLease.trigger.empty())return;
        if(g_combatLease.cleanupIssued && g_frame<=g_combatLease.lastObservationFrame &&
           now<g_combatLease.observationDeadline && !abandon && g_gameLoadEpoch.load()==g_combatLease.loadEpoch)return;
        g_combatLease.cleaning=true;lease=g_combatLease;
    }
    const RequestScope cleanupScope(lease.requestId);
    const bool first=!lease.cleanupIssued;
    const bool alreadyExpired=!first && now>=lease.observationDeadline;
    if(first) {
        lease.cleanupIssued=true;lease.cleanupStarted=now;lease.observationDeadline=now+CombatObservationMilliseconds;
        lease.cleanupFrame=g_frame;lease.lastObservationFrame=g_frame;
    }
    // Mutations occur only on the first service. Subsequent frames read all three
    // identities/states together; no eventual-success inference or accumulated flags.
    const bool sample=first || (!alreadyExpired && g_frame>lease.lastObservationFrame) || abandon || g_gameLoadEpoch.load()!=lease.loadEpoch;
    if(sample) {
        lease.steps[0]=CombatLeaseStep(lease,"stop-attacker",lease.attacker,lease.attackerBaseAddress,
            "owner.StopCombat","IsInCombat",0,io,abandon,first?nullptr:&lease.steps[0]);
        lease.steps[1]=CombatLeaseStep(lease,"stop-target",lease.target,lease.targetBaseAddress,
            "owner.StopCombat","IsInCombat",0,io,abandon,first?nullptr:&lease.steps[1]);
        lease.steps[2]=CombatLeaseStep(lease,"disable-attacker",lease.attacker,lease.attackerBaseAddress,
            "owner.Disable","GetDisabled",1,io,abandon,first?nullptr:&lease.steps[2]);
        if(!first && g_frame>lease.lastObservationFrame)++lease.observationCount;
        lease.lastObservationFrame=g_frame;
    }
    const auto completed=std::max(now,GetTickCount64());
    const bool expired=completed>=lease.observationDeadline;
    bool all=true,unsafe=false,failed=false;
    for(const auto& step:lease.steps) {
        all=all && step.observed;failed=failed || step.failed;
        unsafe=unsafe || !step.attempted || !step.accepted || !step.identity || !step.haveValue;
    }
    const bool observed=!first && !expired && !abandon && !unsafe && all && lease.lastObservationFrame>lease.cleanupFrame;
    if(!observed && !unsafe && !expired && !abandon) {
        lease.cleaning=false;
        std::lock_guard<std::mutex> lock(g_combatMutex);
        if(g_combatLease.active && g_combatLease.id==lease.id)g_combatLease=lease;
        return;
    }
    const auto status=observed?"observed":expired || failed?"failed":"unavailable";
    const auto reason=observed?"null":Quote(abandon?"engine-exiting":expired?"cleanup-observation-timeout":"cleanup-command-or-identity-unavailable");
    const auto extra=CombatLeaseFields(lease)+",\"status\":"+Quote(status)+",\"trigger\":"+Quote(lease.trigger)+
        ",\"cleanupStartedMonotonicMilliseconds\":"+std::to_string(lease.cleanupStarted)+
        ",\"completedMonotonicMilliseconds\":"+std::to_string(completed)+
        ",\"observationPolicyMilliseconds\":"+std::to_string(CombatObservationMilliseconds)+
        ",\"observationDeadlineMonotonicMilliseconds\":"+std::to_string(lease.observationDeadline)+
        ",\"cleanupStartedFrame\":"+std::to_string(lease.cleanupFrame)+
        ",\"finalObservationFrame\":"+std::to_string(lease.lastObservationFrame)+
        ",\"observationFrames\":"+std::to_string(lease.observationCount)+
        ",\"observationElapsedMilliseconds\":"+std::to_string(completed-lease.cleanupStarted)+",\"reason\":"+reason+
        ",\"steps\":["+lease.steps[0].json+","+lease.steps[1].json+","+lease.steps[2].json+"]"+
        ",\"evidence\":\"game-thread-fixed-cleanup-and-independent-readback\"";
    {
        std::lock_guard<std::mutex> lock(g_combatMutex);
        g_lastCombatCleanup="{\"kind\":\"combat-cleanup\",\"requestId\":"+std::to_string(lease.requestId)+extra+"}";
        g_combatLease={};
    }
    // Disconnected/retired leases retain history without entering a new capture.
    if(g_connected && g_connectionGeneration.load()==lease.connectionGeneration &&
       g_capture && g_captureGeneration.load()==lease.captureGeneration) {
        Emit("combat-cleanup",lease.requestId,extra,lease.captureGeneration);
        for(size_t i=0;i<lease.stopCount;++i) {
            const auto& stop=lease.stops[i];
            Emit("action-result",stop.request.id,TypedIdentityJson(stop.subject)+
                ",\"operation\":\"stop-combat\",\"accepted\":true,\"cleanupLeaseId\":"+std::to_string(lease.id)+
                ",\"evidence\":\"combat-lease-cleanup-requested\"",lease.captureGeneration);
        }
        if(lease.endRequested)CombatLeaseFinishCapture(lease.endRequest,lease.captureGeneration,lease.connectionGeneration);
    }
}

bool CombatLeaseArm(const Request& request,const TypedOperation& op,const TypedForm& subject,const TypedForm* other,
                    std::uint64_t generation,ULONGLONG now,CombatLeaseRecord& admitted,std::string& error,const CombatLeaseIo& io) {
    if(!g_capture || !g_connected || generation!=g_captureGeneration.load() || !g_loadedGameObserved || !io.profile()) {
        error="combat-lease-runtime-unavailable";return false;
    }
    if(g_session.empty() || g_session.size()>128){error="combat-lease-session-unavailable-or-too-long";return false;}
    if(!TypedIsActor(subject) || subject.id==0x14 || !other || !TypedIsActor(*other) || subject.id==other->id ||
        !subject.pointer || !other->pointer || !subject.baseId || !other->baseId || op.index<1 || op.index>5000) {
        error="invalid-combat-lease-actors-or-duration";return false;
    }
    CombatLeaseRecord lease;lease.attacker=subject;lease.target=*other;lease.captureGeneration=generation;
    lease.connectionGeneration=g_connectionGeneration.load();lease.loadEpoch=g_gameLoadEpoch.load();lease.requestId=request.id;
    lease.duration=op.index;lease.armed=now;lease.deadline=now+op.index;lease.session=g_session;
    if(!ReadRuntime(reinterpret_cast<std::uintptr_t>(subject.pointer)+0x20,lease.attackerBaseAddress) ||
       !ReadRuntime(reinterpret_cast<std::uintptr_t>(other->pointer)+0x20,lease.targetBaseAddress)) {
        error="combat-lease-base-unreadable";return false;
    }
    TypedForm checked;
    if(!CombatLeaseResolve(lease,subject,lease.attackerBaseAddress,checked,io,error) ||
       !CombatLeaseResolve(lease,*other,lease.targetBaseAddress,checked,io,error))return false;
    std::lock_guard<std::mutex> lock(g_combatMutex);
    if(g_combatLease.active){error="combat-lease-already-active";return false;}
    if(!g_capture || !g_connected || generation!=g_captureGeneration.load() ||
       lease.connectionGeneration!=g_connectionGeneration.load() || lease.loadEpoch!=g_gameLoadEpoch.load()) {
        error="combat-lease-admission-changed";return false;
    }
    lease.id=++g_combatLeaseSerial;lease.active=true;g_combatLease=lease;admitted=lease;return true;
}
void CombatLeaseStartCore(const Request& request,const TypedOperation& op,const TypedForm& subject,const TypedForm* other,std::uint64_t generation,
                          ULONGLONG now,const CombatLeaseIo& io) {
    CombatLeaseRecord lease;std::string error;
    if(!CombatLeaseArm(request,op,subject,other,generation,now,lease,error,io)) {
        Emit("error",request.id,",\"error\":"+Quote(error),generation);return;
    }
    const auto before=TypedReferenceState(subject);
    // The lease is already armed if dispatch returns false or changes lifecycle state.
    TypedForm current,currentOther;
    bool accepted=false;
    bool startAllowed=false;
    {std::lock_guard<std::mutex> lock(g_combatMutex);startAllowed=g_combatLease.active && g_combatLease.id==lease.id &&
        g_combatLease.trigger.empty() && GetTickCount64()<lease.deadline;}
    if(startAllowed && CombatLeaseResolve(lease,subject,lease.attackerBaseAddress,current,io,error) &&
       CombatLeaseResolve(lease,*other,lease.targetBaseAddress,currentOther,io,error) &&
       TypedGenerationActive(generation) && g_connectionGeneration.load()==lease.connectionGeneration)
        accepted=io.mutate("owner.StartCombat other",current,&currentOther,0,error);
    if(!accepted)CombatLeaseRequestCleanup("start-rejected",lease.connectionGeneration);
    TypedForm after;std::string afterError;
    const bool resolved=CombatLeaseResolve(lease,subject,lease.attackerBaseAddress,after,io,afterError);
    const auto afterState=resolved?TypedReferenceState(after):ActorUnavailable(afterError.c_str());
    Emit("action-result",request.id,CombatLeaseFields(lease)+",\"accepted\":"+(accepted?"true":"false")+
        ",\"before\":"+before+",\"after\":"+afterState+",\"afterIdentityResolved\":"+(resolved?"true":"false")+
        ",\"error\":"+(accepted?"null":Quote(error.empty()?"combat-lease-start-unavailable":error))+
        ",\"evidence\":\"sdk-explicit-reference-function-and-readback\"",generation);
    ServiceCombatLease(GetTickCount64(),io);
}
void CombatLeaseStart(const Request& request,const TypedOperation& op,const TypedForm& subject,const TypedForm* other,std::uint64_t generation) {
    CombatLeaseStartCore(request,op,subject,other,generation,GetTickCount64(),CombatLeaseIo{});
}
bool CombatLeaseStopCore(const Request& request,const TypedOperation&,const TypedForm& subject,std::uint64_t generation,
                         const CombatLeaseIo& io) {
    bool wrong=false,full=false;
    {
        std::lock_guard<std::mutex> lock(g_combatMutex);
        if(!g_combatLease.active)return false;
        wrong=subject.id!=g_combatLease.attacker.id && subject.id!=g_combatLease.target.id;
        full=g_combatLease.stopCount==2 || generation!=g_combatLease.captureGeneration ||
            g_connectionGeneration.load()!=g_combatLease.connectionGeneration;
        if(!wrong && !full)g_combatLease.stops[g_combatLease.stopCount++]={request,subject};
    }
    if(wrong || full) {
        Emit("error",request.id,",\"error\":\"combat-lease-mutation-blocked\"",generation);return true;
    }
    CombatLeaseRequestCleanup("explicit-stop");ServiceCombatLease(GetTickCount64(),io);
    return true;
}
bool CombatLeaseStop(const Request& request,const TypedOperation& op,const TypedForm& subject,std::uint64_t generation) {
    return CombatLeaseStopCore(request,op,subject,generation,CombatLeaseIo{});
}
void CombatLeaseEndCapture(const Request& request,const CombatLeaseIo& io=CombatLeaseIo{}) {
    const auto generation=g_captureGeneration.load(),connection=g_connectionGeneration.load();
    bool pending=false;
    {
        std::lock_guard<std::mutex> lock(g_combatMutex);
        pending=g_combatLease.active && g_combatLease.captureGeneration==generation && g_combatLease.connectionGeneration==connection;
        if(pending && !g_combatLease.endRequested){g_combatLease.endRequested=true;g_combatLease.endRequest=request;}
    }
    CombatLeaseRequestCleanup(request.kind==5?"cancel":"stop");
    ServiceCombatLease(GetTickCount64(),io);
    if(!pending)CombatLeaseFinishCapture(request,generation,connection);
}
std::string CombatLeaseHello() {
    const auto now=GetTickCount64();std::lock_guard<std::mutex> lock(g_combatMutex);
    const bool overdue=g_combatLease.active && now>=(g_combatLease.cleanupIssued?g_combatLease.observationDeadline:g_combatLease.deadline);
    const auto state=g_combatLease.active?"{\"status\":"+Quote(overdue?"unavailable":g_combatLease.trigger.empty()?"armed":"cleanup-pending")+
        CombatLeaseFields(g_combatLease)+",\"cleanupObserved\":false,\"requiresGameThreadProgress\":true,\"lastServicedMonotonicMilliseconds\":"+
        std::to_string(g_combatLastService)+",\"reason\":"+(overdue?"\"awaiting-game-thread-cleanup\"":"null")+"}":"{\"status\":\"idle\"}";
    return ",\"combatLease\":"+state+",\"lastCombatCleanup\":"+g_lastCombatCleanup;
}
