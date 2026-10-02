// Behavioral fixtures for live requests without loading the game.
std::unordered_set<Script*> g_liveFixtureOwned;
size_t g_liveFixtureMaximumOwned=0;
std::string g_liveFixtureLastCall;
std::map<std::string,GmstLookup> g_liveFixtureSettings;
bool g_liveFixtureSettingCompileRejected=false,g_liveFixtureSettingSetterAvailable=true;
int g_liveFixtureSettingCallMode=0;
unsigned g_liveFixtureSettingCalls=0;
std::vector<std::string> g_liveFixtureConsoleCalls;
GmstLookup LiveFixtureSettingLookup(const std::string& name) {
    const auto found=g_liveFixtureSettings.find(LiveSettingKey(name));
    if(found!=g_liveFixtureSettings.end())return found->second;
    GmstLookup missing;missing.status="missing";missing.reason="fixture-missing";return missing;
}
bool LiveFixtureSettingSetter(GmstCommand& command){command={};return g_liveFixtureSettingSetterAvailable;}
Script* LiveFixtureCompile(const char* text) {
    if(std::string(text).find("BROKEN")!=std::string::npos)return nullptr;
    if(g_liveFixtureSettingCompileRejected && std::string(text).find("SetNumericGameSetting")!=std::string::npos)return nullptr;
    auto* script=reinterpret_cast<Script*>(new std::string(text));g_liveFixtureOwned.insert(script);
    g_liveFixtureMaximumOwned=(std::max)(g_liveFixtureMaximumOwned,g_liveFixtureOwned.size());return script;
}
void LiveFixtureDestroy(Script* script) {
    Check(g_liveFixtureOwned.erase(script)==1,"retired unknown or already retired script");
    delete reinterpret_cast<std::string*>(script);
}
bool LiveFixtureCall(Script* script,TESObjectREFR*,TESObjectREFR*,NumericElement* value,std::uint8_t count,...) {
    Check(g_liveFixtureOwned.count(script)==1,"called retired script");
    g_liveFixtureLastCall=*reinterpret_cast<std::string*>(script);if(value){value->type=1;value->number=42;}
    if(g_liveFixtureLastCall.find("SetNumericGameSetting")!=std::string::npos) {
        Check(count==1 && value,"setting setter did not receive one numeric argument");
        va_list args;va_start(args,count);const auto bits=va_arg(args,UInt32);va_end(args);float number=0;memcpy(&number,&bits,4);
        const auto first=g_liveFixtureLastCall.find('"'),last=g_liveFixtureLastCall.find('"',first+1);
        auto& setting=g_liveFixtureSettings.at(LiveSettingKey(g_liveFixtureLastCall.substr(first+1,last-first-1))).candidates[0];
        ++g_liveFixtureSettingCalls;
        if(g_liveFixtureSettingCallMode!=1){float submitted=0;UInt32 raw=0;Check(GmstValue(setting.canonicalName[0],number,submitted,raw),"fixture numeric conversion");setting.raw=raw;setting.value=number;}
        value->number=g_liveFixtureSettingCallMode==0?1:0;return g_liveFixtureSettingCallMode==0;
    }
    return true;
}
void LiveFixtureClear() {}
bool LiveFixtureConsole(const char* command,TESObjectREFR*) {
    if(std::string(command).find("LoadGame")==0)Check(g_liveSettings.empty(),"load dispatched before settings were restored");
    g_liveFixtureConsoleCalls.push_back(command);return true;
}
std::string g_liveFixtureSavePath;
unsigned g_liveFixtureSavePathReads=0;
const char* LiveFixtureSavePath(){++g_liveFixtureSavePathReads;return g_liveFixtureSavePath.c_str();}
void TestLiveSettings() {
    const auto priorLookup=g_liveSettingLookup;const auto priorSetter=g_liveSettingSetter;
    const bool priorLoaded=g_loadedGameObserved;g_loadedGameObserved=true;
    g_liveSettingLookup=LiveFixtureSettingLookup;g_liveSettingSetter=LiveFixtureSettingSetter;
    std::uint64_t requestId=20000;
    const auto request=[&](const std::string& json){LiveRequest({30,++requestId,json});};
    const auto add=[](const std::string& name,double number) {
        float submitted=0;UInt32 raw=0;Check(GmstValue(name[0],number,submitted,raw),"fixture setting original");
        GmstTestMemory memory;memory.Text(0x1700,name);memory.Setting(0x1600,name.c_str(),raw,0x1800);
        g_liveFixtureSettings[LiveSettingKey(name)]=memory.Lookup(name.c_str());
    };
    const auto set=[&](const std::string& name,double number){request("{\"op\":\"setting.set\",\"name\":"+Quote(name)+",\"value\":"+NumberField(number)+"}");};
    for(char type:{'b','i','u','f'}) {
        const std::string name=std::string(1,type)+"Test";const double initial=type=='f'?-1.25:type=='i'?-7:0;
        add(name,initial);const auto original=g_liveFixtureSettings.at(LiveSettingKey(name)).candidates[0].raw;
        set(name,type=='f'?4.5:1);set(name,type=='b'?0:type=='f'?8.25:2);
        Check(g_liveSettings.size()==1 && g_liveSettings.begin()->second.original.candidates[0].raw==original,"repeat writes replaced the original setting");
        request("{\"op\":\"setting.read\",\"name\":"+Quote(name)+"}");
        Check(g_events.back().json.find("\"owned\":true")!=std::string::npos,"setting read omitted ownership");
        // Restore on the reset path before LoadGame. No actual game is loaded.
        g_liveBaseline.ready=true;g_liveBaseline.name="fixture-settings";
        request("{\"op\":\"reset\"}");
        Check(g_liveJob.op=="reset" && g_liveSettings.empty() && g_liveFixtureSettings.at(LiveSettingKey(name)).candidates[0].raw==original,"reset failed to restore numeric original");
        const auto calls=g_liveFixtureSettingCalls;LiveCancel("fixture");request("{\"op\":\"reset\"}");
        Check(g_liveFixtureSettingCalls==calls,"second reset applied restoration twice");LiveCancel("fixture");g_liveFixtureSettings.clear();
    }
    add("fTest",-1.25);
    for(const std::string mode:{"missing","ambiguous","unavailable","compile","setter"}) {
        auto saved=g_liveFixtureSettings.at("ftest");
        if(mode=="compile")g_liveFixtureSettingCompileRejected=true;
        else if(mode=="setter")g_liveFixtureSettingSetterAvailable=false;
        else g_liveFixtureSettings.at("ftest").status=mode;
        const auto calls=g_liveFixtureSettingCalls;set("fTest",4);
        Check(g_liveSettings.empty() && g_liveFixtureSettingCalls==calls && g_events.back().json.find("\"status\":\"failed\"")!=std::string::npos,"rejected setting write acquired ownership or called engine");
        g_liveFixtureSettingCompileRejected=false;g_liveFixtureSettingSetterAvailable=true;g_liveFixtureSettings["ftest"]=saved;
    }
    for(const auto* number:{"1e999","1.5","-1","4294967296"}) {
        request("{\"op\":\"setting.set\",\"name\":\"uTest\",\"value\":"+std::string(number)+"}");
        Check(g_liveSettings.empty() && g_events.back().json.find("\"status\":\"failed\"")!=std::string::npos,"invalid setting value admitted");
    }
    add("iLarge",1);auto& large=g_liveFixtureSettings.at("ilarge").candidates[0];large.raw=16777217;large.value=16777217;
    set("iLarge",2);Check(g_liveSettings.empty() && g_events.back().json.find("setting-original-not-restorable")!=std::string::npos,"unrepresentable original admitted");
    // A failing return after mutation still needs rollback ownership.
    g_liveFixtureSettingCallMode=2;set("fTest",4);
    Check(g_liveSettings.size()==1 && g_liveSettings.at("ftest").original.candidates[0].value==-1.25 &&
        g_liveFixtureSettings.at("ftest").candidates[0].value==4,"failed setter lost possible mutation ownership");
    g_liveFixtureSettingCallMode=1;const auto consoleCalls=g_liveFixtureConsoleCalls.size();request("{\"op\":\"reset\"}");
    Check(!g_liveJob.id && g_liveSettings.size()==1 && g_liveSettingsRestoreStatus=="failed" &&
        g_liveFixtureConsoleCalls.size()==consoleCalls && g_events.back().json.find("settings-restore-failed")!=std::string::npos,
        "failed restoration loaded baseline or discarded ownership");
    request("{\"op\":\"status\"}");Check(g_events.back().json.find("setter-return-unavailable")!=std::string::npos,"restore failure absent from status");
    // A disconnect retries on the game thread even without a connected broker.
    g_liveFixtureSettingCallMode=0;const bool priorConnected=g_connected;g_connected=false;LiveDisconnect();LiveTick();g_connected=priorConnected;
    Check(g_liveSettings.empty() && g_liveFixtureSettings.at("ftest").candidates[0].value==-1.25 && g_liveSettingsRestoreStatus=="completed","disconnect did not restore numeric original");
    set("fTest",3);g_liveFixtureSettingCallMode=1;LiveDisconnect();LiveTick();
    Check(g_liveSettings.size()==1 && g_liveSettingsRestoreStatus=="failed","disconnect discarded failed restoration");
    g_liveFixtureSettingCallMode=0;LiveDisconnect();LiveTick();Check(g_liveSettings.empty(),"disconnect restoration could not be retried");
    // Reads can interleave; writes cannot change a running job.
    Check(LiveStart({30,++requestId,"{}"},"fixture"),"setting active-job fixture");const auto active=g_liveJob.id;const auto called=g_liveFixtureSettingCalls;
    set("fTest",2);Check(g_liveJob.id==active && g_liveFixtureSettingCalls==called && g_events.back().json.find("state-changing-job-active")!=std::string::npos,"setting mutation interleaved with job");
    request("{\"op\":\"setting.read\",\"name\":\"fTest\"}");Check(g_liveJob.id==active && g_events.back().json.find("\"status\":\"completed\"")!=std::string::npos,"setting read blocked by job");LiveCancel("fixture");
    // Case-insensitive ownership, stable identity, and the bounded slot count.
    set("fTest",2);set("ftEST",3);Check(g_liveSettings.size()==1,"case variant acquired another slot");
    ++g_liveFixtureSettings.at("ftest").candidates[0].setting;request("{\"op\":\"reset\"}");
    Check(g_liveSettings.size()==1 && g_events.back().json.find("setting-identity-changed")!=std::string::npos,"changed setting identity overwritten during restore");
    --g_liveFixtureSettings.at("ftest").candidates[0].setting;Check(LiveRestoreSettings(),"restoration after identity recovery");
    g_liveFixtureSettings.clear();
    for(size_t i=0;i<LiveMaxSettings+1;++i){const auto name="fSlot"+std::to_string(i);add(name,0);set(name,1);}
    Check(g_liveSettings.size()==LiveMaxSettings && g_events.back().json.find("setting-ownership-limit")!=std::string::npos,"setting ownership limit failed");
    Check(LiveRestoreSettings() && g_liveFixtureOwned.empty(),"setting restore leaked ownership or temporary scripts");
    g_liveFixtureSettings.clear();g_liveSettingLookup=priorLookup;g_liveSettingSetter=priorSetter;g_loadedGameObserved=priorLoaded;
}
void TestLiveControl() {
    for(const auto* verified:{"3a87f92f011e5dc9179ddf733cf08be2b39ea6e5b7a8a9e3a9a72dafcc1b104d",
        "518c87f58a6c4d9826e9ef8fbb7f4213882fa70822675610d45aea2464502a57"})
        Check(VerifiedPcExecutable(verified),"verified retail engine image rejected");
    for(const auto* unknown:{"","unknown",
        "a376e8741a408edb432869126b659c2a36d503ee43bf8350b5a65951e9b69a33",
        "518c87f58a6c4d9826e9ef8fbb7f4213882fa70822675610d45aea2464502a58"})
        Check(!VerifiedPcExecutable(unknown),"unverified executable admitted");
    for(const auto* json:{"{}","{\"op\":\"console\",\"command\":\"player.GetAV Health\"}",
        "[1,-2,0.5,1e2,true,false,null,\"\\uD83D\\uDE00\"]"}) {
        bmt_live_json::Value value;std::string error;Check(bmt_live_json::Parse(json,value,error),"valid JSON rejected");
    }
    for(const auto* json:{"{\"op\":1,\"op\":2}","[1,]","01","1e999","{\"x\":\"\\uD800\"}","{} garbage","[.2]"}) {
        bmt_live_json::Value value;std::string error;Check(!bmt_live_json::Parse(json,value,error),"invalid JSON accepted");
    }
    std::string error;const std::string source="begin function {}\nSetFunctionValue (42)\nend\n";
    Check(LiveScriptSource(source,error),"ordinary zero-argument function rejected");
    for(const auto* forbidden:{"while 1","CallAfterSeconds 1 SomeFunction","SetEventHandler SomeEvent SomeFunction","GetCurrentScript","begin function {}"})
        Check(!LiveScriptSource("begin function {}\n"+std::string(forbidden)+"\nend\n",error),"unbounded or escaped function admitted");
    NVSEScriptInterface scriptApi{};scriptApi.CompileScript=LiveFixtureCompile;scriptApi.CompileExpression=LiveFixtureCompile;scriptApi.CallFunction=LiveFixtureCall;
    NVSEDataInterface dataApi{2,nullptr,nullptr,nullptr,LiveFixtureClear};
    NVSEConsoleInterface consoleApi{2,LiveFixtureConsole,nullptr};
    NVSESerializationInterface serializationApi{};serializationApi.version=2;serializationApi.GetSavePath=LiveFixtureSavePath;
    auto* priorScript=g_script;auto* priorData=g_liveData;auto* priorConsole=g_console;auto priorDestroy=g_liveDestroyScript;
    const bool priorLayout=g_pcLayoutVerified;
    auto* priorSerialization=g_liveSerialization;g_liveSerialization=&serializationApi;
    g_script=&scriptApi;g_liveData=&dataApi;g_console=&consoleApi;g_liveDestroyScript=LiveFixtureDestroy;
    g_pcLayoutVerified=true;g_connected=true;g_capture=false;++g_connectionGeneration;g_events.clear();
    g_liveScripts.clear();g_liveJob={};g_liveHistory.clear();g_liveBaseline={};g_liveSettings.clear();g_liveStopPending=0;g_liveDisconnectPending=false;
    g_liveCreated=0;g_liveDestroyed=0;g_liveFixtureMaximumOwned=0;
    std::uint64_t requestId=1000;
    auto request=[&](const std::string& json,std::uint16_t kind=30){LiveRequest({kind,++requestId,json});};
    request("{\"op\":\"console\",\"command\":\"player.GetAV Health\"}");
    Check(g_events.back().json.find("\"status\":\"completed\"")!=std::string::npos,"console required a capture");
    bmt_live_input::testing::Reset(1000);bmt_live_input::SetSnapshotCallback(LiveSnapshot);
    request("{\"op\":\"sequence.run\",\"version\":1,\"steps\":[{\"type\":\"wait\",\"durationMs\":1},{\"type\":\"snapshot\",\"fields\":[\"frame\"]}]}");
    Check(g_liveJob.op=="sequence.run","input sequence job not admitted");
    bmt_live_input::testing::TickAt(1001);bmt_live_input::testing::TickAt(1002);LiveTick();
    bmt_live_json::Value inputEvent;
    Check(bmt_live_json::Parse(g_events.back().json,inputEvent,error),"input completion corrupted the JSON envelope");
    const auto* wireSequence=inputEvent.Get("sequence");const auto* inputResult=inputEvent.Get("inputResult");
    Check(wireSequence && wireSequence->type==bmt_live_json::Value::Number && wireSequence->number>0,
        "input completion replaced the numeric wire sequence");
    Check(inputResult && inputResult->type==bmt_live_json::Value::Object &&
        LiveString(*inputResult,"status")=="completed" && inputResult->Get("snapshots") &&
        inputResult->Get("snapshots")->array.size()==1 && !g_liveJob.id,
        "completed input result or snapshot missing from the terminal job event");
    request("{\"op\":\"script.load\",\"name\":\"probe\",\"source\":"+Quote(source)+"}");
    const auto first=g_liveScripts.at("probe").script;
    request("{\"op\":\"script.run\",\"name\":\"probe\",\"intervalMs\":1}");
    Check(g_liveJob.id && !g_liveJob.limit,"repeat job not admitted");LiveTick();
    Check(g_liveJob.calls==1,"repeat call missing");
    const auto repeatingJob=g_liveJob.id,compiledBeforeRejectedEval=g_liveCreated;
    request("{\"op\":\"eval\",\"expression\":\"player.Kill\"}");
    Check(g_liveJob.id==repeatingJob && g_liveCreated==compiledBeforeRejectedEval &&
        g_events.back().json.find("active-job-requires-read-only-expression")!=std::string::npos,
        "mutating evaluation interleaved with an active job");
    request("{\"op\":\"eval\",\"expression\":\"42\"}");
    Check(g_liveJob.id==repeatingJob && g_events.back().json.find("\"status\":\"completed\"")!=std::string::npos,
        "literal evaluation blocked during an active job");
    for(const auto* expression:{"player.GetPos X","player.GetAngle Z","player.GetAV Health","player.GetBaseAV 16", "player.GetPermAV Health", "player.GetDisabled","player.GetDead","MenuMode 1009","GetNumLoadedMods","GetNthModName 0","GetModIndex \"FalloutNV.esm\""})
        Check(LiveReadOnlyExpression(expression),"known simple read rejected");
    for(const auto* expression:{"player.Kill","player.SetAV Health 1","player.GetAV (player.Kill)","42 + player.Kill","GetModIndex \"FalloutNV.esm\" + player.Kill","Call SomeFunction"})
        Check(!LiveReadOnlyExpression(expression),"compound or mutating expression admitted as read-only");
    request("{\"op\":\"script.load\",\"name\":\"probe\",\"source\":"+Quote(source+"BROKEN\n")+"}");
    Check(g_liveScripts.at("probe").script==first && g_liveFixtureOwned.size()==1,"failed compile discarded working revision");
    Check(g_events.back().json.find("Compiler rejected script; source preview:")!=std::string::npos &&
        g_events.back().json.find("line 4: BROKEN")!=std::string::npos,"compiler rejection returned an empty diagnostic");
    Check(LiveCompileDiagnostic("begin function {}\nSetFunctionValue (\nend\n")==
        "Source check: unclosed '(' at line 2: SetFunctionValue (","unclosed expression diagnostic lacks line and snippet");
    Check(LiveCompileDiagnostic("begin function {}\n; (comment\nPrint \"(text)\"\nend\n").find("Compiler rejected script; source preview:")==0,
        "source diagnostic treated quoted or commented delimiters as code");
    g_liveDiagnostic="engine diagnostic";Check(LiveCompileDiagnostic(source)=="engine diagnostic","fallback replaced engine diagnostic");g_liveDiagnostic.clear();
    for(int i=0;i<100;++i) {
        const auto revision=source+"; revision "+std::to_string(i)+"\n";
        request("{\"op\":\"script.load\",\"name\":\"probe\",\"source\":"+Quote(revision)+"}");
        g_liveJob.next=0;LiveTick();
        Check(g_liveFixtureLastCall==revision,"repeat called stale revision");
        Check(g_liveFixtureOwned.size()==1 && g_liveCreated-g_liveDestroyed==1,"replacement leaked an owned script");
    }
    Check(g_liveFixtureMaximumOwned==2,"replacement ownership not bounded to old and new");
    const auto active=g_liveJob.id;
    request("{\"op\":\"job.stop\",\"jobId\":\"999999999\"}",31);
    Check(g_liveJob.id==active,"stale stop cancelled current job");
    Request stop{31,++requestId,"{\"op\":\"job.stop\",\"jobId\":"+Quote(std::to_string(active))+"}"};
    LivePriorityStop(stop);LiveTick();LiveRequest(stop);
    Check(!g_liveJob.id && g_events.back().json.find("\"status\":\"completed\"")!=std::string::npos,"priority stop receipt lost after cancellation");
    const auto calls=g_liveScripts.at("probe").calls;LiveTick();
    Check(g_liveScripts.at("probe").calls==calls,"stopped script ran again");
    request("{\"op\":\"script.run\",\"name\":\"probe\",\"intervalMs\":1}");
    const auto namedJob=g_liveJob.id;
    for(const auto* target:{"another-script","probe"}) {
        Request namedStop{31,++requestId,"{\"op\":\"script.stop\",\"name\":"+Quote(target)+"}"};
        Check(LivePriorityStop(namedStop),"named stop was not admitted with priority");
        Check(!g_liveStopPending && g_liveJob.id==namedJob,"named stop mutated work before name validation");
        LiveRequest(namedStop);
        Check(std::string(target)=="probe"?!g_liveJob.id:g_liveJob.id==namedJob,
            "named stop did not preserve a different script or cancel the matching script");
    }
    request("{\"op\":\"script.run\",\"name\":\"probe\"}");LiveTick();
    Check(!g_liveJob.id && g_liveHistory.back().status=="completed","run-once did not complete");
    request("{\"op\":\"eval\",\"expression\":\"42\"}");
    Check(g_liveFixtureOwned.size()==1 && g_liveCreated-g_liveDestroyed==1,"evaluation leaked its temporary function");
    request("{\"op\":\"eval\",\"expression\":\"player.Kill\"}");
    Check(g_events.back().json.find("\"status\":\"completed\"")!=std::string::npos,"idle expression restricted by active-job allowlist");
    request("{\"op\":\"script.load\",\"name\":\"probe\",\"source\":"+Quote("begin function {}\nplayer.GetAV Health\nend\n")+"}");
    Check(!g_liveScripts.at("probe").returnsValue,"void UDF incorrectly requests an xNVSE return value");
    request("{\"op\":\"script.run\",\"name\":\"probe\"}");LiveTick();
    Check(!g_liveJob.id && g_liveHistory.back().status=="completed","void function did not complete");
    request("{\"op\":\"script.run\",\"name\":\"probe\",\"intervalMs\":1}");
    LiveMessage(6,nullptr);Check(!g_liveJob.id && g_liveHistory.back().status=="cancelled","load did not stop scheduled calls");
    request("{\"op\":\"script.run\",\"name\":\"probe\",\"intervalMs\":1}");
    g_liveBaseline.name="fixture-reset";g_liveBaseline.ready=true;
    request("{\"op\":\"reset\"}");
    Check(g_liveJob.op=="reset" && g_liveHistory.back().status=="cancelled","reset did not cancel scheduled calls first");
    LiveMessage(6,nullptr);LiveMessage(8,nullptr);
    Check(!g_liveJob.id && g_liveHistory.back().status=="failed","failed load incorrectly completed reset");
    // SaveGame delivers a basename before xNVSE publishes the new normalized
    // path. Reading the API inside that callback would select the prior save.
    g_liveBaseline={};g_liveBaseline.name="fixture-baseline";g_liveFixtureSavePath="C:\\old\\other.nvse";g_liveFixtureSavePathReads=0;
    Check(LiveStart({30,++requestId,"{}"},"baseline",g_liveBaseline.name),"baseline fixture not started");
    std::string savedName="fixture-baseline.fos";
    LiveMessage(4,savedName.data(),static_cast<UInt32>(savedName.size()));
    Check(g_liveBaseline.saveNotified && g_liveBaseline.savePath.empty() && g_liveFixtureSavePathReads==0,
        "save callback read the stale serialization path");
    g_liveFixtureSavePath="C:\\bmt-live-fixture-missing\\fixture-baseline.nvse";LiveTick();
    Check(g_liveFixtureSavePathReads==1 && g_liveBaseline.coSavePath==g_liveFixtureSavePath &&
        g_liveBaseline.savePath=="C:\\bmt-live-fixture-missing\\fixture-baseline.fos" && !g_liveBaseline.ready,
        "next tick did not resolve the matching absolute save pair");
    LiveCancel("fixture");
    for(const auto* path:{"fixture-baseline.nvse","C:fixture-baseline.nvse","C:\\saves\\other.nvse","C:\\saves\\fixture-baseline.fos"}) {
        g_liveBaseline={};g_liveBaseline.name="fixture-baseline";g_liveFixtureSavePath=path;
        Check(LiveStart({30,++requestId,"{}"},"baseline",g_liveBaseline.name),"identity fixture not started");
        LiveMessage(4,savedName.data(),static_cast<UInt32>(savedName.size()));LiveTick();
        Check(!g_liveJob.id && g_liveHistory.back().status=="failed" && g_liveBaseline.savePath.empty(),
            "unrelated or relative serialization path accepted");
    }
    std::string resolved;
    Check(LiveResolveSavePair("\\\\server\\share\\FIXTURE-baseline.NVSE","fixture-baseline",resolved) &&
        resolved=="\\\\server\\share\\FIXTURE-baseline.fos","absolute UNC save path not preserved");
    g_liveBaseline={};g_liveBaseline.name="fixture-baseline";g_liveFixtureSavePathReads=0;
    Check(LiveStart({30,++requestId,"{}"},"baseline",g_liveBaseline.name),"unrelated notification fixture not started");
    std::string unrelated="other.fos";LiveMessage(4,unrelated.data(),static_cast<UInt32>(unrelated.size()));LiveTick();
    Check(!g_liveBaseline.saveNotified && !g_liveFixtureSavePathReads,"unrelated save notification accepted");LiveCancel("fixture");
    g_liveBaseline.name="fixture-reset";g_liveBaseline.ready=true;
    request("{\"op\":\"script.run\",\"name\":\"probe\",\"intervalMs\":1}");
    LiveDisconnect();LiveTick();
    Check(!g_liveJob.id && g_liveScripts.empty() && g_liveFixtureOwned.empty(),"disconnect retained scheduled work or revisions");
    Check(g_liveBaseline.ready && g_liveBaseline.name=="fixture-reset","disconnect discarded the pinned reset save");
    Check(g_liveCreated==g_liveDestroyed,"compiled function ownership leaked");
    // Reconnect and send the first request before the old disconnect's next tick.
    request("{\"op\":\"script.load\",\"name\":\"probe\",\"source\":"+Quote(source)+"}");
    LiveDisconnect();++g_connectionGeneration;
    request("{\"op\":\"script.load\",\"name\":\"fresh\",\"source\":"+Quote(source)+"}");
    LiveTick();
    Check(g_liveScripts.size()==1 && g_liveScripts.count("fresh")==1 && g_liveFixtureOwned.size()==1,
        "old disconnect cleanup discarded the reconnected client's revision");
    LiveDisconnect();LiveTick();Check(g_liveCreated==g_liveDestroyed,"reconnect leaked an owned script");
    TestLiveSettings();Check(g_liveCreated==g_liveDestroyed,"setting operation leaked an owned script");
    g_script=priorScript;g_liveData=priorData;g_console=priorConsole;g_liveDestroyScript=priorDestroy;g_pcLayoutVerified=priorLayout;g_liveSerialization=priorSerialization;
    g_events.clear();g_liveHistory.clear();g_liveBaseline={};
}
