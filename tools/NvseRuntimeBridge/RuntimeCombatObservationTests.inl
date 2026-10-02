// Synthetic active command table and explicit-owner calls; no game execution.
struct CombatObservationFixture {
    std::array<CommandInfo,6> commands{};
    std::string fault;
    unsigned calls=0,coverageCalls=0;
};
CombatObservationFixture* g_combatObservationFixture=nullptr;
std::vector<std::string> g_combatObservationCases;
const CommandInfo* CombatObservationByName(const char* name) {
    for(const auto& command:g_combatObservationFixture->commands)if(!strcmp(command.longName,name))return &command;
    return nullptr;
}
const CommandInfo* CombatObservationByOpcode(UInt32 opcode) {
    for(const auto& command:g_combatObservationFixture->commands)if(command.opcode==opcode)return &command;
    return nullptr;
}
UInt32 CombatObservationReturnType(const CommandInfo* command) {
    if(g_combatObservationFixture->fault=="form-metadata" && !strcmp(command->longName,"GetCombatTarget"))return 0;
    return !strcmp(command->longName,"GetCombatTarget")?1u:0u;
}
bool CombatObservationCall(Script* script,TESObjectREFR* calling,TESObjectREFR*,NumericElement* result,std::uint8_t count,...) {
    auto& f=*g_combatObservationFixture;auto& lease=*g_leaseFixture;
    va_list args;va_start(args,count);auto owner=va_arg(args,void*);auto other=va_arg(args,void*);auto number=va_arg(args,UInt32);va_end(args);
    Check(count==3 && calling==lease.attacker.pointer && owner==lease.attacker.pointer && !other && !number,
        "combat observer lost its fixed explicit owner/arguments");
    const auto& body=*reinterpret_cast<std::string*>(script);
    const char* name=nullptr;
    for(const auto* candidate:CombatStateCommands)if(body.find(std::string("SetFunctionValue (owner.")+candidate+")")!=std::string::npos)name=candidate;
    Check(name!=nullptr,"combat observer dispatched an unapproved command");
    ++f.calls;result->type=1;result->number=!strcmp(name,"GetAnimAction")?2:!strcmp(name,"GetCurrentAIProcedure")?16:
        !strcmp(name,"GetCurrentAIPackage")?5:1;
    if(!strcmp(name,"GetCombatTarget")) {
        result->type=2;result->form=f.fault=="empty-target"?nullptr:f.fault=="other-target"?lease.attacker.pointer:lease.target.pointer;
        if(f.fault=="numeric-form-result"){result->type=1;result->number=0;}
        if(f.fault=="changed-target")++lease.targetBytes[3];
    }
    if(!strcmp(name,"GetAnimAction")) {
        if(f.fault=="no-result")result->type=0;
        if(f.fault=="nonfinite")result->number=std::numeric_limits<double>::quiet_NaN();
        if(f.fault=="fraction")result->number=2.5;
        if(f.fault=="idle-code")result->number=-1;
        if(f.fault=="unknown-code")result->number=4294967295.0;
        if(f.fault=="changed-command")f.commands[0].execute=OtherExecute;
        if(f.fault=="changed-actor")++lease.attackerBytes[3];
        if(f.fault=="disconnect")g_connected=false;
        if(f.fault=="restart")++g_captureGeneration;
        if(f.fault=="load")++g_gameLoadEpoch;
        if(f.fault=="stop")CombatLeaseRequestCleanup("stop");
    }
    if(!strcmp(name,"IsWeaponOut") && f.fault=="nonboolean")result->number=2;
    return true;
}
std::string CombatObservationCoverage(const CombatLeaseRecord& lease) {
    ++g_combatObservationFixture->coverageCalls;
    // This acquires the lease mutex: the production caller must release it first.
    Check(CombatLeaseActive() && lease.id==g_combatLease.id,"coverage lost lease identity");
    return "{\"status\":\"observed\",\"leaseId\":"+std::to_string(lease.id)+",\"routeEntries\":0}";
}
void SaveCombatObservationCase(const char* name) {
    std::string row="{\"case\":"+Quote(name)+",\"events\":[";
    for(size_t i=0;i<g_events.size();++i){if(i)row+=',';row+=g_events[i].json;}
    g_combatObservationCases.push_back(row+"]}");
}
void TestCombatObservation() {
    const auto savedScript=g_script;const auto savedCommands=g_commands;const auto savedCoverage=g_combatInvocationCoverage;
    const bool savedNumeric=g_numericEnabled.load();
    NVSEScriptInterface sdk{};sdk.CompileScript=CompileTestExpression;sdk.CallFunction=CombatObservationCall;
    NVSECommandTableInterface commands{};commands.version=1;commands.GetByName=CombatObservationByName;
    commands.GetByOpcode=CombatObservationByOpcode;commands.GetReturnType=CombatObservationReturnType;
    auto setup=[&](LeaseFixture& lease,CombatObservationFixture& f) {
        ResetLease(lease);g_combatObservationFixture=&f;g_combatStateCursor={};
        g_script=&sdk;g_commands=&commands;g_numericEnabled=true;g_operationFunctions.clear();
        g_combatInvocationCoverage=CombatObservationCoverage;
        for(size_t i=0;i<f.commands.size();++i) {
            f.commands[i].longName=CombatStateCommands[i];f.commands[i].opcode=static_cast<UInt32>(0x1000+i);
            f.commands[i].needsParent=1;f.commands[i].execute=TestExecute;
        }
        std::string error;Check(ArmLease(lease,error),"observation fixture lease admission failed");
    };
    for(const std::string fault:{"none","empty-target","other-target","idle-code","unknown-code","parameters","form-metadata","numeric-form-result",
        "no-result","nonfinite","fraction","nonboolean","changed-command","changed-actor","changed-target","disconnect","restart","load","stop"}) {
        LeaseFixture lease;CombatObservationFixture f;setup(lease,f);f.fault=fault;
        if(fault=="parameters")f.commands[0].numParams=1;
        ServiceCombatObservation(lease.now,LeaseIo());
        const bool retired=fault=="disconnect" || fault=="restart" || fault=="load" || fault=="stop";
        Check(lease.calls.empty(),"observation mutated or retried combat");
        Check(retired?g_events.empty():g_events.size()==1,"combat observation crossed lifecycle or lost event");
        if(retired)Check(f.calls==1,"retired observation continued calling engine");
        else {
            const auto& row=g_events.back().json;
            const bool identity=fault!="changed-actor" && fault!="changed-target";
            const bool complete=fault=="none" || fault=="empty-target" || fault=="other-target" || fault=="idle-code" || fault=="unknown-code";
            Check(row.find(std::string("\"status\":\"")+(identity?(complete?"observed":"partial"):"unavailable")+"\"")!=std::string::npos,
                "combat observation promoted a failed field or identity");
            Check(row.find("\"requestId\":401")!=std::string::npos && row.find("\"leaseId\":"+std::to_string(lease.admitted.id))!=std::string::npos,
                "combat sample lost request/lease binding");
            if(!identity)Check(row.find("\"attacker\":null,\"invocationCoverage\":null")!=std::string::npos,"identity failure retained attributed values");
            if(fault=="empty-target")Check(row.find("\"status\":\"empty\",\"formId\":null")!=std::string::npos,"empty target became unavailable/zero identity");
            if(fault=="other-target")Check(row.find("\"matchesLeaseTarget\":false")!=std::string::npos,"different combat target matched lease target");
            if(fault=="unknown-code")Check(row.find("\"value\":4294967295")!=std::string::npos,"unknown raw animation code was discarded");
            if(fault=="idle-code")Check(row.find("\"value\":-1")!=std::string::npos,"idle animation code became unavailable");
            if(fault=="none")Check(f.calls==6 && row.find("\"matchesLeaseTarget\":true")!=std::string::npos &&
                row.find("\"invocationCoverage\":{\"status\":\"observed\"")!=std::string::npos,"observation fields or route evidence missing");
            if(fault=="parameters" || fault=="form-metadata")Check(f.calls==5,"invalid command metadata reached SDK dispatch");
            if(fault=="changed-actor")Check(f.calls==1,"changed actor received further SDK calls");
        }
        SaveCombatObservationCase(fault.c_str());
    }
    for(const std::string gate:{"inactive","cleanup","trigger","expired","same-frame","interval","limit"}) {
        LeaseFixture lease;CombatObservationFixture f;setup(lease,f);
        auto now=lease.now;
        if(gate=="inactive")g_combatLease.active=false;
        if(gate=="cleanup")g_combatLease.cleanupIssued=true;
        if(gate=="trigger")g_combatLease.trigger="stop";
        if(gate=="expired")now=lease.admitted.deadline;
        if(gate=="same-frame" || gate=="interval" || gate=="limit") {
            ServiceCombatObservation(now,LeaseIo());Check(f.calls==6,"first sample missing");g_events.clear();f.calls=0;
            if(gate=="same-frame")now+=50;
            if(gate=="interval"){++g_frame;now+=49;}
            if(gate=="limit"){++g_frame;now+=50;g_combatStateCursor.samples=CombatStateMaximumSamples;}
        }
        ServiceCombatObservation(now,LeaseIo());
        Check(f.calls==0 && g_events.empty() && lease.calls.empty(),"closed or bounded sampler performed work");
        SaveCombatObservationCase(gate.c_str());
    }
    {
        LeaseFixture lease;CombatObservationFixture f;setup(lease,f);
        ServiceCombatObservation(lease.now,LeaseIo());++g_frame;
        ServiceCombatObservation(lease.now+50,LeaseIo());++g_frame;
        ServiceCombatObservation(lease.now+200,LeaseIo());
        Check(f.calls==18 && g_events.size()==3 && g_events.back().json.find("\"elapsedSincePreviousSampleMilliseconds\":150")!=std::string::npos,
            "sampler hid a gap or performed catch-up calls");
        f.commands[0].opcode+=0x1000;++g_frame;g_combatInvocationCoverage=nullptr;
        ServiceCombatObservation(lease.now+250,LeaseIo());
        Check(f.calls==24 && g_events.back().json.find("\"opcode\":8192")!=std::string::npos &&
            g_events.back().json.find("\"invocationCoverage\":null")!=std::string::npos,"warm-cache remap or absent coverage failed");
        SaveCombatObservationCase("cadence-gap-remap-and-no-coverage");
    }
    g_script=savedScript;g_commands=savedCommands;g_combatInvocationCoverage=savedCoverage;g_numericEnabled=savedNumeric;
    g_combatLease={};g_events.clear();g_operationFunctions.clear();g_combatObservationFixture=nullptr;g_leaseFixture=nullptr;
}
