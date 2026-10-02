// Synthetic command-table, storage and x86 UDF ABI controls. No game process.
std::vector<std::string> g_scriptLocalSdkRows;
struct ScriptLocalSdkFixture {
    ParamInfo variableParams[2]={{"variable name",0,0},{"quest",0x0E,1}},scriptParam{"form",0x3D,1};
    CommandInfo commands[3]{};std::string fault;unsigned calls=0,hasCalls=0,scriptCalls=0;
    bool active=true;UInt32 returnType=0;double numeric=3.25;void* owner=reinterpret_cast<void*>(0x1000);
    ScriptLocalSdkFixture() {
        for(size_t i=0;i<3;++i){auto& c=commands[i];c.longName=i==0?"GetScript":i==1?"HasVariable":"GetVariable";
            c.opcode=0x1700+static_cast<UInt32>(i);c.params=i?variableParams:&scriptParam;c.numParams=i?2:1;c.execute=TestExecute;}
    }
};
ScriptLocalSdkFixture* g_scriptLocalSdkFixture=nullptr;
const CommandInfo* ScriptLocalSdkByName(const char* name) {
    auto& f=*g_scriptLocalSdkFixture;
    if(f.fault=="no-command")return nullptr;
    for(const auto& c:f.commands)if(!strcmp(name,c.longName))return &c;
    return nullptr;
}
const CommandInfo* ScriptLocalSdkByOpcode(UInt32 opcode) {
    auto& f=*g_scriptLocalSdkFixture;
    if(f.fault=="opcode-mismatch")return nullptr;
    for(const auto& c:f.commands)if(opcode==c.opcode)return &c;
    return nullptr;
}
UInt32 ScriptLocalSdkReturnType(const CommandInfo* c) {
    return g_scriptLocalSdkFixture->fault=="return-metadata"?2:c==&g_scriptLocalSdkFixture->commands[0]?1:0;
}
bool ScriptLocalSdkCall(Script* script,TESObjectREFR* calling,TESObjectREFR* container,NumericElement* result,std::uint8_t count,...) {
    auto& f=*g_scriptLocalSdkFixture;Check(count==3,"SDK local UDF argument count changed");
    va_list args;va_start(args,count);auto owner=va_arg(args,void*);auto other=va_arg(args,void*);auto bits=va_arg(args,UInt32);va_end(args);
    Check(owner==f.owner&&calling==f.owner&&!container&&!other&&bits==0,"SDK local lost explicit calling reference or ABI arguments");
    const auto& text=*reinterpret_cast<std::string*>(script);
    Check(text.rfind("ref owner\nref other\nfloat number\nbegin function { owner other number }\n",0)==0,
        "SDK local UDF signature changed");
    Check(text.find("DocMitchell")==std::string::npos&&text.find("GetVariableFloat")==std::string::npos,
        "SDK local used EditorID or nonexistent numeric command");
    ++f.calls;result->type=1;result->number=0;
    if(text.find("owner.GetScript)")!=std::string::npos) {
        ++f.scriptCalls;result->type=2;result->form=reinterpret_cast<void*>(
            f.fault=="wrong-script"||(f.fault=="script-changed"&&f.scriptCalls==2)?0x1404:0x1400);
    } else if(text.find("owner.HasVariable \"counter\")")!=std::string::npos) {
        ++f.hasCalls;result->number=f.fault=="missing"?0:1;
        if(f.fault=="presence-fraction")result->number=0.5;
        if(f.fault=="presence-changed"&&f.hasCalls==2)result->number=0;
    } else {
        Check(text.find("owner.GetVariable \"counter\")")!=std::string::npos,"SDK local optional quest argument was used for a reference");
        result->number=f.numeric;
        if(f.fault=="call-failed")return false;
        if(f.fault=="form-result")result->type=2;
        if(f.fault=="nonfinite")result->number=std::numeric_limits<double>::infinity();
        if(f.fault=="capture-changed")f.active=false;
    }
    return true;
}
void TestScriptLocalSdk() {
    g_scriptLocalSdkRows.clear();const auto savedScript=g_script;const auto savedCommands=g_commands;const bool savedNumeric=g_numericEnabled;
    NVSEScriptInterface sdk{};sdk.CompileScript=CompileTestExpression;sdk.CallFunction=ScriptLocalSdkCall;
    NVSECommandTableInterface table{};table.version=1;table.GetByName=ScriptLocalSdkByName;
    table.GetByOpcode=ScriptLocalSdkByOpcode;table.GetReturnType=ScriptLocalSdkReturnType;
    g_script=&sdk;g_commands=&table;g_numericEnabled=true;
    const std::pair<const char*,const char*> cases[]={
        {"numeric","observed"},{"zero","observed"},{"negative","observed"},{"disagreement","observed"},
        {"missing","absent"},{"missing-slot","absent"},{"reference","unsupported"},{"non-numeric","unsupported"},
        {"wrong-script","unavailable"},{"script-changed","unavailable"},{"presence-fraction","unavailable"},
        {"presence-changed","unavailable"},{"call-failed","unavailable"},{"form-result","unavailable"},{"nonfinite","unavailable"},
        {"capture-changed","unavailable"},{"binding-changed","unavailable"},{"storage-changed","unavailable"},
        {"commands-changed","unavailable"},{"unstable-before","unavailable"},{"ambiguous","unavailable"}};
    for(const auto& test:cases) {
        ScriptLocalSdkFixture f;g_scriptLocalSdkFixture=&f;f.fault=test.first;g_operationFunctions.clear();
        ScriptLocalTestMemory memory;
        if(f.fault=="zero"){memory.Value(0);f.numeric=0;}
        if(f.fault=="negative"){memory.Value(-17.5);f.numeric=-17.5;}
        if(f.fault=="disagreement")f.numeric=9;
        if(f.fault=="missing")memory.Meta(0x1500,7,0x1600,"other");
        if(f.fault=="missing-slot")memory.Word(0x2000,8);
        if(f.fault=="reference"){memory.Word(0x1444,0x2200);memory.Word(0x220C,7);}
        if(f.fault=="non-numeric")memory.Word(0x1510,2);
        auto before=memory.Observe();
        if(f.fault=="unstable-before")before.identityStable=false;
        if(f.fault=="ambiguous")before.storage.status="ambiguous";
        auto after=before;
        if(f.fault=="binding-changed")after.base+=4;
        if(f.fault=="storage-changed")after.storage.raw[0]^=1;
        TypedForm owner;owner.pointer=f.owner;owner.type=0x3A;
        auto call=[&](const char* name){ReferenceSdkCommand command;std::string reason;
            Check(ReadReferenceSdkCommand(name,command,reason),"valid SDK fixture metadata refused");
            NumericElement raw{};ReferenceSdkValue value;
            value.returned=TypedCall(ReferenceSdkBody(name,"counter",command),owner,nullptr,0,raw,value.reason);value.type=raw.type;
            if(raw.type==1)value.number=raw.number;if(raw.type==2)value.form=reinterpret_cast<UInt32>(raw.form);return value;};
        const auto out=ReadReferenceSdkLocal(before,[&](){return after;},call,[&](){return f.active;},[&](){return f.fault!="commands-changed";});
        Check(out.status==test.second,("SDK local case "+f.fault).c_str());
        if(out.status=="observed") {
            Check(f.calls==5&&out.value.number==f.numeric&&out.identityStable&&out.commandsStable,"SDK numeric result lost independent provenance");
            Check(out.valueMatchesStorage==(f.fault!="disagreement"),"SDK value was replaced with direct storage value");
        }
        if(out.status=="absent"||out.status=="unsupported")Check(f.calls==4&&!out.value.returned,"absent/ref local invoked numeric getter");
        if(f.fault=="unstable-before"||f.fault=="ambiguous")Check(f.calls==0,"unadmitted storage reached SDK");
        const auto json="{"+ReferenceSdkObservationJson(out).substr(1)+"}";
        if(out.status!="observed")Check(json.find("\"value\":null")!=std::string::npos,"unavailable SDK local became zero");
        g_scriptLocalSdkRows.push_back("{\"case\":"+Quote(f.fault)+",\"observation\":"+json+"}");
    }
    const char* metadataCases[]={"valid","no-command","opcode-mismatch","parent","parameter-type","parameter-optional","return-metadata","no-execute"};
    for(const auto* test:metadataCases) {
        ScriptLocalSdkFixture f;g_scriptLocalSdkFixture=&f;f.fault=test;
        if(f.fault=="parent")f.commands[2].needsParent=1;
        if(f.fault=="parameter-type")f.variableParams[1].typeID=0x04;
        if(f.fault=="parameter-optional")f.variableParams[1].isOptional=0;
        if(f.fault=="no-execute")f.commands[2].execute=nullptr;
        ReferenceSdkCommand command;std::string reason;const bool result=ReadReferenceSdkCommand("GetVariable",command,reason);
        Check(result==(f.fault=="valid"),"SDK command admission ignored active signature");
        if(result) {
            auto changed=command;changed.params[1].typeID=4;
            Check(!SameReferenceSdkCommand(command,changed),"SDK parameter mutation was not checked");
            changed=command;changed.metadata.opcode++;
            Check(ReferenceSdkBody("GetVariable","counter",command)!=ReferenceSdkBody("GetVariable","counter",changed),"SDK cache key ignored opcode");
        }
        g_scriptLocalSdkRows.push_back("{\"case\":"+Quote("metadata-"+f.fault)+",\"accepted\":"+(result?"true":"false")+"}");
    }
    for(const std::string payload:{"reference-local-sdk/2\tA.esp\t000123\tx","reference-local-sdk/1\t@player\t000014\tx",
        "reference-local-sdk/1\tA.esp\t000123\tx\t","reference-local-sdk/1\tA.esp\t000123\tx.y"}) {
        Request request{};request.kind=7;request.id=91;request.payload=payload;g_events.clear();
        const bool savedCapture=g_capture,savedConnected=g_connected;g_capture=true;g_connected=true;
        Check(ReferenceLocalSdkRequest(request)&&g_events.size()==1&&g_events[0].json.find("invalid-reference-local-sdk-request")!=std::string::npos,
            "invalid SDK local read escaped typed rejection");g_capture=savedCapture;g_connected=savedConnected;
        g_scriptLocalSdkRows.push_back("{\"case\":\"invalid-payload\",\"event\":"+g_events[0].json+"}");
    }
    g_script=savedScript;g_commands=savedCommands;g_numericEnabled=savedNumeric;g_operationFunctions.clear();g_scriptLocalSdkFixture=nullptr;
}
