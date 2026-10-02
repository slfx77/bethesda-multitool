// Capture-independent live control. Except the three atomic signals below, all
// state is owned by the game's main-loop thread. Include after typed operations.
constexpr size_t LiveMaxScripts=16,LiveMaxHistory=64,LiveMaxSettings=16;
struct LiveScriptRevision {Script* script=nullptr;std::uint64_t revision=0,calls=0;bool returnsValue=false;};
struct LiveJob {
    std::uint64_t id=0,requestId=0,connection=0,calls=0,limit=1;
    ULONGLONG started=0,next=0,deadline=0,ended=0,progress=0;
    UInt32 interval=0;
    std::string op,name,status="running",extra;
    bool loadStarted=false,loadSucceeded=false;
};
struct LiveBaseline {
    std::string name,label,savePath,coSavePath;
    bool ready=false,saveNotified=false;
    double position[3]{},rotation[3]{};
    UInt32 cell=0;
};
NVSEDataInterface* g_liveData=nullptr;
NVSESerializationInterface* g_liveSerialization=nullptr;
std::map<std::string,LiveScriptRevision> g_liveScripts;
std::deque<LiveJob> g_liveHistory;
LiveJob g_liveJob;
LiveBaseline g_liveBaseline;
struct LiveOwnedSetting {GmstLookup original,last;std::string error;};
std::map<std::string,LiveOwnedSetting> g_liveSettings;
std::string g_liveSettingsRestoreStatus="not-run",g_liveSettingsRestoreReport="[]";
GmstLookup (*g_liveSettingLookup)(const std::string&)=GmstLookupNow;
bool (*g_liveSettingSetter)(GmstCommand&)=GmstReadSetter;
std::atomic<bool> g_liveDisconnectPending{false};
std::atomic<bool> g_liveActiveScriptJob{false};
std::atomic<std::uint64_t> g_liveStopPending{0},g_liveActiveJobId{0};
std::uint64_t g_liveNextJob=0,g_liveRevision=0,g_liveCreated=0,g_liveDestroyed=0;
std::string g_liveDiagnostic;
bool g_liveCompiling=false,g_liveExecuting=false;
ULONGLONG g_liveSaveStableSince=0;
std::uint64_t g_liveSaveSize=0,g_liveCoSaveSize=0;

void LiveDestroyEngineScript(Script* script) {
    // Pinned GameScript.cpp Script::Delete -> Utilities.h Delete -> GameAPI.cpp.
    // This path is reachable only for the executable hash admitted at Load.
    g_liveData->ClearScriptDataCache();
    reinterpret_cast<void (__thiscall*)(Script*)>(0x005AA1A0)(script);
    reinterpret_cast<void (__cdecl*)(void*)>(0x00401030)(script);
}
void (*g_liveDestroyScript)(Script*)=LiveDestroyEngineScript;
bool LiveScriptApiAvailable() {
    return g_pcLayoutVerified && g_script && g_script->CompileScript && g_script->CompileExpression &&
        g_script->CallFunction && g_liveData && g_liveData->version>=2 && g_liveData->ClearScriptDataCache;
}
void LiveRetire(Script* script) {if(script){g_liveDestroyScript(script);++g_liveDestroyed;}}
void LiveClearScripts() {for(auto& item:g_liveScripts)LiveRetire(item.second.script);g_liveScripts.clear();}
std::string LiveString(const bmt_live_json::Value& value,const char* key,const std::string& fallback="") {
    const auto* field=value.Get(key);return field?field->StringOr(fallback):fallback;
}
std::string LiveCompileDiagnostic(const std::string& source) {
    if(!g_liveDiagnostic.empty())return g_liveDiagnostic;
    // The SDK publishes runtime errors, not compiler errors. Label this bounded
    // source check explicitly instead of presenting it as compiler output.
    std::vector<std::string> lines;std::istringstream input(source);std::string line;
    while(std::getline(input,line)){if(!line.empty() && line.back()=='\r')line.pop_back();lines.push_back(line);}
    auto context=[&](size_t index){return "line "+std::to_string(index+1)+": "+lines[index].substr(0,180);};
    std::vector<std::pair<char,size_t>> delimiters;
    for(size_t index=0;index<lines.size();++index) {
        bool quoted=false;
        for(size_t column=0;column<lines[index].size();++column) {
            const auto c=lines[index][column];
            if(quoted && c=='\\' && column+1<lines[index].size()){++column;continue;}
            if(c=='"'){quoted=!quoted;continue;}
            if(quoted)continue;if(c==';')break;
            if(c=='(' || c=='[' || c=='{')delimiters.push_back({c,index});
            else if(c==')' || c==']' || c=='}') {
                const auto opening=c==')'?'(':c==']'?'[':'{';
                if(delimiters.empty() || delimiters.back().first!=opening)
                    return "Source check: unmatched '"+std::string(1,c)+"' at "+context(index);
                delimiters.pop_back();
            }
        }
        if(quoted)return "Source check: unclosed string at "+context(index);
    }
    if(!delimiters.empty())return "Source check: unclosed '"+std::string(1,delimiters.back().first)+"' at "+context(delimiters.back().second);
    std::string result="Compiler rejected script; source preview:";
    for(size_t index=0;index<lines.size() && index<5;++index)result+='\n'+context(index);
    if(lines.size()>5)result+="\n...";
    return result;
}
bool LiveInteger(const bmt_live_json::Value& value,const char* key,std::uint64_t fallback,std::uint64_t min,
    std::uint64_t max,std::uint64_t& result) {
    const auto* field=value.Get(key);result=fallback;if(!field)return true;
    if(field->type!=bmt_live_json::Value::Number || field->number<double(min) || field->number>double(max) ||
        std::floor(field->number)!=field->number)return false;
    result=static_cast<std::uint64_t>(field->number);return true;
}
bool LiveName(const std::string& name) {
    return !name.empty() && name.size()<=64 && std::all_of(name.begin(),name.end(),[](unsigned char c){
        return (c>='a' && c<='z') || (c>='A' && c<='Z') || (c>='0' && c<='9') || c=='_' || c=='-';});
}
bool LiveAscii(const std::string& text,size_t limit,bool multiline=false) {
    return !text.empty() && text.size()<=limit && std::all_of(text.begin(),text.end(),[=](unsigned char c){
        return (c>=32 && c<127) || (multiline && (c=='\n' || c=='\r' || c=='\t'));});
}
std::string LiveSettingKey(const std::string& name) {
    std::string key=name;for(auto& c:key)c=char(std::tolower(static_cast<unsigned char>(c)));return key;
}
std::string LiveSettingsJson() {
    std::string result="[";
    for(const auto& entry:g_liveSettings) {
        if(result.size()>1)result+=',';
        const auto& owned=entry.second;const auto& original=owned.original.candidates[0];
        result+="{\"name\":"+Quote(original.canonicalName)+",\"originalValue\":"+NumberField(original.value)+
            ",\"originalRawHex\":"+Quote(ActorHex(reinterpret_cast<const std::uint8_t*>(&original.raw),4))+
            ",\"lastObservation\":"+GmstJson(owned.last)+",\"error\":"+(owned.error.empty()?"null":Quote(owned.error))+"}";
    }
    return result+"]";
}
bool LiveApplySetting(const GmstLookup& before,double requested,UInt32 expected,GmstLookup& after,std::string& error,bool& called) {
    called=false;after=before;float submitted=0;UInt32 raw=0;
    const auto& name=before.candidates[0].canonicalName;
    if(!LiveScriptApiAvailable() || !g_loadedGameObserved){error="setting-api-or-loaded-game-unavailable";return false;}
    if(!GmstValue(name[0],requested,submitted,raw) || raw!=expected){error="setting-value-not-representable";return false;}
    GmstCommand command;if(!g_liveSettingSetter(command)){error="setter-command-unavailable";return false;}
    const auto load=g_gameLoadEpoch.load();
    const auto source="float number\nbegin function { number }\nSetFunctionValue (SetNumericGameSetting \""+name+"\" number)\nend\n";
    g_liveDiagnostic.clear();g_liveCompiling=true;auto* script=g_script->CompileScript(source.c_str());g_liveCompiling=false;
    if(!script){error="setter-compile-failed: "+LiveCompileDiagnostic(source);return false;}++g_liveCreated;
    GmstCommand admitted;const auto prior=g_liveSettingLookup(name);
    if(load!=g_gameLoadEpoch.load() || !SameGmstIdentity(before,prior,true) ||
        !g_liveSettingSetter(admitted) || !SameGmstCommand(command,admitted)) {
        LiveRetire(script);error="setter-admission-changed";return false;
    }
    UInt32 bits=0;memcpy(&bits,&submitted,4);NumericElement value{};called=true;g_liveExecuting=true;
    const bool returned=g_script->CallFunction(script,nullptr,nullptr,&value,1,bits);g_liveExecuting=false;
    after=g_liveSettingLookup(name);GmstCommand current;
    const auto failure=GmstWriteFailure(before,after,g_pcLayoutVerified && load==g_gameLoadEpoch.load() &&
        g_liveSettingSetter(current) && SameGmstCommand(command,current),returned,value,expected);
    LiveRetire(script);
    if(failure){error=failure;return false;}if(!g_liveDiagnostic.empty()){error=g_liveDiagnostic;return false;}
    error.clear();return true;
}
bool LiveRestoreSettings() {
    std::string report="[";
    for(auto it=g_liveSettings.begin();it!=g_liveSettings.end();) {
        auto& owned=it->second;const auto& original=owned.original.candidates[0];
        auto before=g_liveSettingLookup(original.canonicalName),after=before;bool called=false,restored=false;
        if(!SameGmstIdentity(owned.original,before,false))owned.error="setting-identity-changed";
        else if(before.candidates[0].raw==original.raw){owned.error.clear();restored=true;}
        else restored=LiveApplySetting(before,original.value,original.raw,after,owned.error,called);
        owned.last=after;
        if(report.size()>1)report+=',';
        report+="{\"name\":"+Quote(original.canonicalName)+",\"status\":"+Quote(restored?"restored":"failed")+
            ",\"setterCalled\":"+(called?"true":"false")+",\"error\":"+(owned.error.empty()?"null":Quote(owned.error))+
            ",\"readback\":"+GmstJson(after)+"}";
        if(restored)it=g_liveSettings.erase(it);else ++it;
    }
    g_liveSettingsRestoreReport=report+"]";g_liveSettingsRestoreStatus=g_liveSettings.empty()?"completed":"failed";
    return g_liveSettings.empty();
}
void LiveResult(const Request& request,const char* status,const std::string& extra="") {
    Emit("live-result",request.id,",\"status\":"+Quote(status)+extra);
}
void LiveFailure(const Request& request,const std::string& reason,const std::string& extra="") {
    LiveResult(request,"failed",",\"error\":"+Quote(reason)+extra);
}
std::string LiveJobJson(const LiveJob& job) {
    return "{\"jobId\":"+Quote(std::to_string(job.id))+",\"op\":"+Quote(job.op)+",\"name\":"+Quote(job.name)+
        ",\"status\":"+Quote(job.status)+",\"calls\":"+std::to_string(job.calls)+
        ",\"elapsedMs\":"+std::to_string((job.ended?job.ended:GetTickCount64())-job.started)+job.extra+"}";
}
void LiveFinish(const char* status,const std::string& extra="") {
    if(!g_liveJob.id)return;
    g_liveJob.status=status;g_liveJob.extra=extra;g_liveJob.ended=GetTickCount64();
    if(g_liveJob.connection==g_connectionGeneration.load())
        Emit("live-job",g_liveJob.requestId,",\"status\":"+Quote(status)+",\"jobId\":"+Quote(std::to_string(g_liveJob.id))+
            ",\"op\":"+Quote(g_liveJob.op)+",\"elapsedMs\":"+std::to_string(GetTickCount64()-g_liveJob.started)+
            ",\"calls\":"+std::to_string(g_liveJob.calls)+extra);
    if(g_liveHistory.size()==LiveMaxHistory)g_liveHistory.pop_front();
    g_liveHistory.push_back(g_liveJob);g_liveJob={};g_liveActiveJobId=0;g_liveActiveScriptJob=false;
}
void LiveCancel(const char* reason) {
    bmt_live_input::Release();
    LiveFinish("cancelled",",\"reason\":"+Quote(reason));
}
bool LiveStart(const Request& request,const char* op,const std::string& name="") {
    if(g_liveJob.id){LiveFailure(request,"state-changing-job-active",",\"job\":"+LiveJobJson(g_liveJob));return false;}
    g_liveJob={};g_liveJob.id=++g_liveNextJob;g_liveJob.requestId=request.id;
    g_liveJob.connection=g_connectionGeneration.load();g_liveJob.started=GetTickCount64();
    g_liveJob.op=op;g_liveJob.name=name;g_liveActiveScriptJob=g_liveJob.op=="script.run";g_liveActiveJobId=g_liveJob.id;
    LiveResult(request,"running",",\"jobId\":"+Quote(std::to_string(g_liveJob.id)));return true;
}
// Called on the IO thread. Never touches engine objects. A stale targeted stop
// does not release the input owned by a different job.
bool LivePriorityStop(const Request& request) {
    bmt_live_json::Value value;std::string error;
    if(!bmt_live_json::Parse(request.payload,value,error))return false;
    const auto op=LiveString(value,"op");if(op!="job.stop" && op!="script.stop")return false;
    const auto id=g_liveActiveJobId.load();const auto target=LiveString(value,"jobId");
    if(!target.empty() && target!=std::to_string(id))return false;
    if(op=="script.stop" && id && !g_liveActiveScriptJob.load())return false;
    // Admit named stops at the front of the queue, but leave cancellation and
    // input untouched until the game thread validates the script name.
    if(!LiveString(value,"name").empty())return true;
    g_liveStopPending=id;
    // Targeted cancellation is applied at the game-thread boundary so a just-
    // completed old job cannot release a newly admitted sequence's controls.
    if(target.empty())bmt_live_input::Release();return true;
}
void LiveDisconnect() {g_liveDisconnectPending=true;bmt_live_input::Release();}
void LiveServiceDisconnect() {
    if(g_liveDisconnectPending.exchange(false)){LiveCancel("disconnected");LiveRestoreSettings();LiveClearScripts();g_liveHistory.clear();}
}

bool LiveScriptSource(const std::string& source,std::string& reason,bool* returnsValue=nullptr) {
    if(!LiveAscii(source,49152,true)){reason="source-must-be-ASCII-at-most-49152-bytes";return false;}
    // Inspect code tokens, excluding quoted text and semicolon comments. This is
    // an investigation UDF contract, not a sandbox for untrusted game scripts.
    std::string code;bool quoted=false,comment=false;
    for(const char c:source) {
        if(c=='\n'){comment=false;code+='\n';continue;}
        if(comment)continue;
        if(c=='"'){quoted=!quoted;code+=' ';continue;}
        if(!quoted && c==';'){comment=true;continue;}
        code+=quoted?' ':char(std::tolower(static_cast<unsigned char>(c)));
    }
    if(quoted){reason="unterminated-script-string";return false;}
    const std::regex function("\\bbegin\\s+function\\s*\\{\\s*\\}");
    if(std::distance(std::sregex_iterator(code.begin(),code.end(),function),std::sregex_iterator())!=1) {
        reason="expected-one-zero-argument-function-block";return false;
    }
    const std::regex begin("\\bbegin\\b");
    if(std::distance(std::sregex_iterator(code.begin(),code.end(),begin),std::sregex_iterator())!=1) {
        reason="nested-or-additional-blocks-unsupported";return false;
    }
    const auto body=std::regex_replace(code,function,"");
    if(body.find_first_of("{}")!=std::string::npos){reason="lambda-or-additional-function-parameters-unsupported";return false;}
    // No loops, script escape or independently scheduled callbacks: Stop cancels
    // calls between frames, and retired functions must have no external owners.
    const std::regex forbidden("\\b(while|foreach|loop|goto|label|call|call(after|for|while|when)[a-z_0-9]*|seteventhandler[a-z_0-9]*|seton[a-z_0-9]*|getcurrentscript|setscript|runscript|runbatchscript|runconsolecommand|executescript|eval|lambda|returninglambda|load|loadgame|save|savegame|qqq|quitgame|exitgame)\\b");
    if(std::regex_search(code,forbidden)){reason="unsupported-loop-callback-or-script-escape";return false;}
    if(returnsValue)*returnsValue=std::regex_search(code,std::regex("\\bsetfunctionvalue\\b"));
    return true;
}
std::string LiveElement(NumericElement& value) {
    if(value.type==0)return "{\"type\":\"none\"}";
    if(value.type==1)return std::isfinite(value.number)?"{\"type\":\"number\",\"value\":"+NumberField(value.number)+"}":"{\"type\":\"number\",\"status\":\"non-finite\"}";
    if(value.type==2){UInt32 id=0;std::uint8_t type=0;return !value.form?"{\"type\":\"form\",\"value\":null}":
        ReadFormIdentity(value.form,id,type)?"{\"type\":\"form\",\"formId\":"+std::to_string(id)+",\"formType\":"+std::to_string(type)+"}":"{\"type\":\"form\",\"status\":\"unavailable\"}";}
    if(value.type==3) {
        std::string text;bool complete=false;
        for(size_t i=0;value.form && i<4096;++i){char c=0;SIZE_T read=0;
            if(!ReadProcessMemory(GetCurrentProcess(),static_cast<char*>(value.form)+i,&c,1,&read) || read!=1)break;
            if(!c){complete=true;break;}text+=c;}
        // Pinned SDK Element::Reset owns and frees its returned string.
        reinterpret_cast<void (__cdecl*)(void*)>(0x00401030)(value.form);value={};
        return "{\"type\":\"string\",\"value\":"+Quote(text)+",\"complete\":"+(complete?"true":"false")+"}";
    }
    return "{\"type\":\"unsupported\",\"elementType\":"+std::to_string(value.type)+"}";
}
std::string LiveSnapshot(const std::vector<std::string>& requested) {
    const auto fields=requested.empty()?std::vector<std::string>{"player.position","player.rotation","player.cell","player.health"}:requested;
    TypedForm player;std::string reason;
    const bool ready=g_pcLayoutVerified && g_loadedGameObserved && TypedResolve({"@player",0x14},player,reason);
    std::string result="{";bool first=true;
    for(const auto& field:fields) {
        if(!first)result+=',';first=false;result+=Quote(field)+":";
        if(field=="frame"){result+=std::to_string(g_frame);continue;}
        if(field=="message.menu") {
            RuntimeMenuState menu;MenuInspection inspection;
            if(ReadMenuState(menu,inspection))result+="{\"status\":\"visible\""+MenuFields(menu)+"}";
            else result+="{\"status\":\"unavailable\",\"reason\":"+Quote(inspection.failedCheck)+MenuInspectionFields(inspection)+"}";
            continue;
        }
        if(!ready){result+=ActorUnavailable("player-not-ready");continue;}
        if(field=="player.position" || field=="player.rotation") {
            const std::string command=field=="player.position"?"owner.GetPos ":"owner.GetAngle ";
            result+="{\"x\":"+TypedNumberJson(command+"X",player)+",\"y\":"+TypedNumberJson(command+"Y",player)+
                ",\"z\":"+TypedNumberJson(command+"Z",player)+",\"units\":"+Quote(field=="player.position"?"game-units":"degrees")+"}";
        } else if(field=="player.cell")result+=TypedFormJson("owner.GetParentCell",player);
        else if(field=="player.health")result+=TypedNumberJson("owner.GetAV Health",player);
        else if(field=="player.disabled")result+=TypedNumberJson("owner.GetDisabled",player);
        else result+=ActorUnavailable("unsupported-field");
    }
    return result+"}";
}
bool LivePlayerPose(LiveBaseline& pose) {
    TypedForm player;std::string reason;if(!g_loadedGameObserved || !TypedResolve({"@player",0x14},player,reason))return false;
    const char* axes[]={"X","Y","Z"};
    for(size_t i=0;i<3;++i)if(!TypedNumber("owner.GetPos "+std::string(axes[i]),player,nullptr,0,pose.position[i],reason) ||
        !TypedNumber("owner.GetAngle "+std::string(axes[i]),player,nullptr,0,pose.rotation[i],reason))return false;
    void* cell=nullptr;std::uint8_t type=0;
    return TypedFormValue("owner.GetParentCell",player,nullptr,0,cell,pose.cell,type,reason) && cell;
}
bool LiveFileSize(const std::string& path,std::uint64_t& size) {
    WIN32_FILE_ATTRIBUTE_DATA data{};
    if(!GetFileAttributesExA(path.c_str(),GetFileExInfoStandard,&data) || (data.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY))return false;
    size=(std::uint64_t(data.nFileSizeHigh)<<32)|data.nFileSizeLow;return size>0;
}
bool LiveSavePathApiAvailable() {
    return g_liveSerialization && g_liveSerialization->version>=2 && g_liveSerialization->GetSavePath;
}
bool LiveResolveSavePair(const std::string& coSavePath,const std::string& name,std::string& savePath) {
    const auto separator=[](char value){return value=='\\' || value=='/';};
    const bool drive=coSavePath.size()>3 && ((coSavePath[0]>='A' && coSavePath[0]<='Z') ||
        (coSavePath[0]>='a' && coSavePath[0]<='z')) && coSavePath[1]==':' && separator(coSavePath[2]);
    const bool unc=coSavePath.size()>4 && separator(coSavePath[0]) && separator(coSavePath[1]) && !separator(coSavePath[2]);
    if((!drive && !unc) || coSavePath.find('\0')!=std::string::npos)return false;
    const auto last=coSavePath.find_last_of("\\/");
    if(last==std::string::npos || _stricmp(coSavePath.substr(last+1).c_str(),(name+".nvse").c_str()))return false;
    savePath=coSavePath.substr(0,coSavePath.size()-5)+".fos";return true;
}
bool LiveReadOnlyExpression(const std::string& expression) {
    // Only simple, known reads may interleave with a state-changing job. Do not
    // accept compound expressions, user functions, or executable arguments here.
    static const std::regex allowed(
        R"(^\s*(?:[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?|player\.(?:GetPos|GetAngle)\s+[XYZ]|player\.(?:GetAV|GetBaseAV|GetPermAV)\s+(?:[A-Za-z_][A-Za-z0-9_]*|[0-9]+)|player\.(?:GetDisabled|GetDead)|MenuMode(?:\s+[0-9]+)?|GetNumLoadedMods|GetNthModName\s+[0-9]+|GetModIndex\s+"[^"\r\n]+")\s*$)",
        std::regex::ECMAScript|std::regex::icase|std::regex::optimize);
    return std::regex_match(expression,allowed);
}
std::string LiveStatus() {
    std::string scripts="[";bool first=true;
    for(const auto& item:g_liveScripts){if(!first)scripts+=',';first=false;scripts+="{\"name\":"+Quote(item.first)+
        ",\"revision\":"+std::to_string(item.second.revision)+",\"calls\":"+std::to_string(item.second.calls)+"}";}
    return ",\"job\":"+(g_liveJob.id?LiveJobJson(g_liveJob):"null")+",\"scripts\":"+scripts+"]"+
        ",\"ownedScripts\":"+std::to_string(g_liveScripts.size())+",\"compiledScripts\":"+std::to_string(g_liveCreated)+
        ",\"retiredScripts\":"+std::to_string(g_liveDestroyed)+",\"baseline\":{\"name\":"+Quote(g_liveBaseline.name)+",\"label\":"+Quote(g_liveBaseline.label)+
        ",\"ready\":"+(g_liveBaseline.ready?"true":"false")+",\"savePath\":"+Quote(g_liveBaseline.savePath)+
        ",\"coSavePath\":"+Quote(g_liveBaseline.coSavePath)+"},\"input\":"+bmt_live_input::DiagnosticsJson()+
        ",\"ownedSettings\":"+LiveSettingsJson()+",\"settingsRestoreStatus\":"+Quote(g_liveSettingsRestoreStatus)+
        ",\"settingsRestore\":"+g_liveSettingsRestoreReport;
}
void LiveRequest(const Request& request) {
    // A client can reconnect before the next game tick. Retire the old session's
    // functions before accepting the first request from the new connection.
    LiveServiceDisconnect();
    bmt_live_json::Value value;std::string error;
    if(!bmt_live_json::Parse(request.payload,value,error) || value.type!=bmt_live_json::Value::Object){LiveFailure(request,"invalid-json",",\"diagnostic\":"+Quote(error));return;}
    if(request.originConnectionGeneration && request.originConnectionGeneration!=g_connectionGeneration.load())return;
    const auto op=LiveString(value,"op"),name=LiveString(value,"name");
    if(request.kind==31 && op!="job.stop" && op!="script.stop"){LiveFailure(request,"priority-request-must-be-stop");return;}
    if(op=="status" || op=="script.status"){LiveResult(request,"completed",LiveStatus());return;}
    if(op=="job.status") {
        const auto target=LiveString(value,"jobId");
        if(g_liveJob.id && (target.empty() || target==std::to_string(g_liveJob.id))){LiveResult(request,"completed",",\"job\":"+LiveJobJson(g_liveJob));return;}
        for(auto it=g_liveHistory.rbegin();it!=g_liveHistory.rend();++it)if(target==std::to_string(it->id)){LiveResult(request,"completed",",\"job\":"+LiveJobJson(*it));return;}
        LiveFailure(request,"unknown-job");return;
    }
    if(op=="job.stop" || op=="script.stop") {
        const auto target=LiveString(value,"jobId");
        if(op=="script.stop" && g_liveJob.id && g_liveJob.op!="script.run"){LiveFailure(request,"active-job-is-not-a-script");return;}
        if(!target.empty() && target!=std::to_string(g_liveJob.id)) {
            for(auto it=g_liveHistory.rbegin();it!=g_liveHistory.rend();++it)
                if(target==std::to_string(it->id) && it->status=="cancelled") {LiveResult(request,"completed",",\"job\":"+LiveJobJson(*it));return;}
            LiveFailure(request,"job-does-not-match");return;
        }
        if(!name.empty() && (g_liveJob.op!="script.run" || name!=g_liveJob.name)) {
            LiveFailure(request,"job-does-not-match");return;
        }
        LiveCancel("requested");g_liveStopPending=0;LiveResult(request,"completed");return;
    }
    if(op=="snapshot") {
        std::vector<std::string> fields;const auto* list=value.Get("fields");
        if(list){if(list->type!=bmt_live_json::Value::Array || list->array.size()>32){LiveFailure(request,"invalid-fields");return;}
            for(const auto& field:list->array){if(field.type!=bmt_live_json::Value::String){LiveFailure(request,"invalid-field");return;}fields.push_back(field.text);}}
        LiveResult(request,"completed",",\"snapshot\":"+LiveSnapshot(fields));return;
    }
    if(op=="setting.read" || op=="setting.set") {
        const bool write=op=="setting.set";
        if(!GmstName(name)){LiveFailure(request,"invalid-setting-name");return;}
        if(!g_pcLayoutVerified || !g_loadedGameObserved){LiveFailure(request,"setting-profile-or-loaded-game-unavailable");return;}
        if(write && g_liveJob.id){LiveFailure(request,"state-changing-job-active");return;}
        const auto* valueField=value.Get("value");float submitted=0;UInt32 expected=0;
        if(write && (!valueField || valueField->type!=bmt_live_json::Value::Number || !GmstValue(name[0],valueField->number,submitted,expected))) {
            LiveFailure(request,"invalid-setting-value");return;
        }
        if(!write && valueField){LiveFailure(request,"setting-read-does-not-accept-value");return;}
        const auto before=g_liveSettingLookup(name);
        if(before.status!="observed") {LiveFailure(request,"setting-not-observed",",\"gameSetting\":"+GmstJson(before));return;}
        const auto key=LiveSettingKey(before.candidates[0].canonicalName);auto owned=g_liveSettings.find(key);
        if(!write){LiveResult(request,"completed",",\"gameSetting\":"+GmstJson(before)+",\"owned\":"+(owned==g_liveSettings.end()?"false":"true"));return;}
        if(owned==g_liveSettings.end() && g_liveSettings.size()>=LiveMaxSettings){LiveFailure(request,"setting-ownership-limit");return;}
        const auto& original=owned==g_liveSettings.end()?before:owned->second.original;
        float originalSubmitted=0;UInt32 originalRaw=0;
        if(!GmstValue(before.candidates[0].canonicalName[0],original.candidates[0].value,originalSubmitted,originalRaw) || originalRaw!=original.candidates[0].raw) {
            LiveFailure(request,"setting-original-not-restorable");return;
        }
        if(!SameGmstIdentity(original,before,false)) {
            if(owned!=g_liveSettings.end())owned->second.error="setting-identity-changed";
            LiveFailure(request,"setting-identity-changed",",\"ownedSettings\":"+LiveSettingsJson());return;
        }
        GmstLookup after;bool called=false;const bool applied=LiveApplySetting(before,valueField->number,expected,after,error,called);
        // A setter may mutate before reporting failure. Retain the original for
        // every attempted engine call, including an unverified readback.
        if(called) {
            if(owned==g_liveSettings.end())owned=g_liveSettings.emplace(key,LiveOwnedSetting{before,after,error}).first;
            else {owned->second.last=after;owned->second.error=error;}
        }
        LiveResult(request,applied?"completed":"failed",",\"setterCalled\":"+std::string(called?"true":"false")+
            ",\"gameSetting\":"+GmstJson(after)+",\"ownedSettings\":"+LiveSettingsJson()+(applied?"":",\"error\":"+Quote(error)));return;
    }
    if(op=="eval") {
        const auto expression=LiveString(value,"expression");
        if(!LiveScriptApiAvailable()){LiveFailure(request,"owned-script-api-unavailable");return;}
        if(!LiveAscii(expression,4096) || expression.find(';')!=std::string::npos ||
            !LiveScriptSource("begin function {}\nSetFunctionValue ("+expression+")\nend\n",error)){LiveFailure(request,"invalid-expression",",\"diagnostic\":"+Quote(error));return;}
        if(g_liveJob.id && !LiveReadOnlyExpression(expression)){LiveFailure(request,"active-job-requires-read-only-expression");return;}
        g_liveDiagnostic.clear();g_liveCompiling=true;auto* compiled=g_script->CompileExpression(expression.c_str());g_liveCompiling=false;
        if(!compiled){LiveFailure(request,"expression-compile-failed",",\"diagnostic\":"+Quote(LiveCompileDiagnostic(expression)));return;}++g_liveCreated;
        NumericElement result{};g_liveExecuting=true;const bool succeeded=g_script->CallFunction(compiled,nullptr,nullptr,&result,0);g_liveExecuting=false;
        const auto json=LiveElement(result);LiveRetire(compiled);
        LiveResult(request,succeeded?"completed":"failed",",\"result\":"+json+(succeeded?"":",\"error\":\"expression-call-failed\",\"diagnostic\":"+Quote(g_liveDiagnostic)));return;
    }
    if(op=="script.load") {
        if(!LiveScriptApiAvailable()){LiveFailure(request,"owned-script-api-unavailable");return;}
        if(!LiveName(name)){LiveFailure(request,"invalid-script-name");return;}
        if(g_liveJob.id && (g_liveJob.op!="script.run" || g_liveJob.name!=name)){LiveFailure(request,"state-changing-job-active");return;}
        const auto source=LiveString(value,"source");bool returnsValue=false;
        if(!LiveScriptSource(source,error,&returnsValue)){LiveFailure(request,error);return;}
        const auto found=g_liveScripts.find(name);
        if(found==g_liveScripts.end() && g_liveScripts.size()==LiveMaxScripts){LiveFailure(request,"script-slot-limit");return;}
        g_liveDiagnostic.clear();g_liveCompiling=true;Script* compiled=g_script->CompileScript(source.c_str());g_liveCompiling=false;
        if(!compiled){LiveFailure(request,"script-compile-failed",",\"diagnostic\":"+Quote(LiveCompileDiagnostic(source))+",\"previousRevisionRetained\":true");return;}
        ++g_liveCreated;Script* previous=found==g_liveScripts.end()?nullptr:found->second.script;
        const auto revision=++g_liveRevision;g_liveScripts[name]={compiled,revision,0,returnsValue};LiveRetire(previous);
        LiveResult(request,"completed",",\"name\":"+Quote(name)+",\"revision\":"+std::to_string(revision)+",\"ownedScripts\":"+std::to_string(g_liveScripts.size()));return;
    }
    if(op=="reset") {
        if(!g_liveBaseline.ready){LiveFailure(request,"baseline-unavailable");return;}
        LiveCancel("reset");
        if(!LiveStart(request,"reset",g_liveBaseline.name))return;
        g_liveJob.deadline=GetTickCount64()+30000;
        if(!LiveRestoreSettings()){LiveFinish("failed",",\"error\":\"settings-restore-failed\",\"settingsRestore\":"+g_liveSettingsRestoreReport+",\"ownedSettings\":"+LiveSettingsJson());return;}
        g_liveJob.extra=",\"settingsRestore\":"+g_liveSettingsRestoreReport;
        if(!g_console->RunScriptLine(("LoadGame \""+g_liveBaseline.name+"\"").c_str(),nullptr))LiveFinish("failed",",\"error\":\"load-command-rejected\"");
        return;
    }
    if(g_liveJob.id){LiveFailure(request,"state-changing-job-active",",\"job\":"+LiveJobJson(g_liveJob));return;}
    if(op=="console") {
        const auto command=LiveString(value,"command");
        if(!LiveAscii(command,4096)){LiveFailure(request,"invalid-console-command");return;}
        const bool accepted=g_console && g_console->RunScriptLine(command.c_str(),nullptr);
        LiveResult(request,accepted?"completed":"failed",",\"accepted\":"+std::string(accepted?"true":"false")+",\"evidence\":\"console-return\"");return;
    }
    if(op=="script.run") {
        if(g_liveScripts.find(name)==g_liveScripts.end()){LiveFailure(request,"script-not-loaded");return;}
        std::uint64_t interval=0,count=1;
        if(!LiveInteger(value,"intervalMs",0,1,3600000,interval) || !LiveInteger(value,"count",interval?0:1,1,1000000,count)) {
            LiveFailure(request,"invalid-interval-or-count");return;
        }
        if(!interval && count!=1){LiveFailure(request,"repeat-requires-intervalMs");return;}
        if(!LiveStart(request,"script.run",name))return;
        g_liveJob.interval=static_cast<UInt32>(interval);g_liveJob.limit=count;g_liveJob.next=GetTickCount64();return;
    }
    if(op=="baseline") {
        if(!LiveName(name)){LiveFailure(request,"invalid-save-name");return;}
        if(g_liveBaseline.ready){LiveFailure(request,"baseline-already-pinned");return;}
        if(!LiveSavePathApiAvailable()){LiveFailure(request,"save-path-api-unavailable");return;}
        LiveBaseline pose;pose.label=name;
        // A private physical filename preserves earlier named saves, including a
        // baseline left by an earlier game process. Return the actual name.
        unsigned char nonce[16]{};
        if(BCryptGenRandom(nullptr,nonce,sizeof(nonce),BCRYPT_USE_SYSTEM_PREFERRED_RNG)<0){LiveFailure(request,"save-name-generation-failed");return;}
        std::ostringstream suffix;for(const auto byte:nonce)suffix<<std::hex<<std::setw(2)<<std::setfill('0')<<unsigned(byte);
        pose.name=name.substr(0,24)+"_"+suffix.str();
        if(!LivePlayerPose(pose)){LiveFailure(request,"player-not-ready");return;}
        if(!LiveStart(request,"baseline",pose.name))return;g_liveBaseline=pose;g_liveSaveStableSince=0;g_liveSaveSize=0;g_liveCoSaveSize=0;
        g_liveJob.deadline=GetTickCount64()+30000;
        if(!g_console->RunScriptLine(("Save \""+pose.name+"\"").c_str(),nullptr))LiveFinish("failed",",\"error\":\"save-command-rejected\"");
        return;
    }
    if(op=="sequence.run") {
        bmt_live_input::TakeCompletionJson();
        if(!bmt_live_input::StartSequence(request.payload,error)){LiveFailure(request,error);return;}
        LiveStart(request,"sequence.run");return;
    }
    LiveFailure(request,"unsupported-live-operation");
}
void LiveMessage(UInt32 type,void* data,UInt32 length=0) {
    if(type==10 && (g_liveCompiling || g_liveExecuting) && data && length && length<=4096)g_liveDiagnostic.assign(static_cast<const char*>(data),length);
    if(type==4 && g_liveJob.op=="baseline" && data && length && length<32768) {
        std::string path(static_cast<const char*>(data),length);while(!path.empty() && path.back()=='\0')path.pop_back();
        if(path.find('\0')!=std::string::npos)return;
        const auto last=path.find_last_of("\\/");const auto filename=path.substr(last==std::string::npos?0:last+1);
        if(_stricmp(filename.c_str(),(g_liveBaseline.name+".fos").c_str()))return;
        // The pinned serializer dispatches this message before assigning its
        // normalized full .nvse path. Do not resolve the basename against cwd.
        g_liveBaseline.saveNotified=true;
    }
    if(type==6) {
        bmt_live_input::Release();
        if(g_liveJob.op=="reset")g_liveJob.loadStarted=true;
        else LiveCancel("game-loading");
    }
    if(type==8 && g_liveJob.op=="reset") {
        if(!data)LiveFinish("failed",",\"error\":\"game-load-failed\"");else g_liveJob.loadSucceeded=true;
    }
    if(type==2 || type==14){LiveCancel("game-state-replaced");}
    if(type==1 || type==7){LiveCancel("game-exiting");LiveClearScripts();}
}
void LiveTick() {
    LiveServiceDisconnect();
    const auto stopped=g_liveStopPending.exchange(0);if(stopped && stopped==g_liveJob.id)LiveCancel("requested");
    if(!g_liveJob.id)return;
    if(g_liveJob.connection!=g_connectionGeneration.load() || !g_connected){LiveCancel("connection-changed");return;}
    const auto now=GetTickCount64();
    if(g_liveJob.deadline && now>=g_liveJob.deadline){bmt_live_input::Release();LiveFinish("failed",",\"error\":\"deadline-exceeded\"");return;}
    if(g_liveJob.deadline && now-g_liveJob.progress>=1000) {
        g_liveJob.progress=now;
        const auto stage=g_liveJob.op=="baseline"?"waiting-for-save-pair":g_liveJob.loadSucceeded?"verifying-player-state":"waiting-for-load";
        Emit("live-job",g_liveJob.requestId,",\"status\":\"running\",\"jobId\":"+Quote(std::to_string(g_liveJob.id))+
            ",\"stage\":"+Quote(stage)+",\"elapsedMs\":"+std::to_string(now-g_liveJob.started));
    }
    if(g_liveJob.op=="script.run" && now>=g_liveJob.next) {
        const RequestScope scope(g_liveJob.requestId);
        const auto found=g_liveScripts.find(g_liveJob.name);
        if(found==g_liveScripts.end()){LiveFinish("failed",",\"error\":\"script-no-longer-loaded\"");return;}
        NumericElement result{};g_liveDiagnostic.clear();g_liveExecuting=true;
        // xNVSE returns false for a successful void UDF when passed a result
        // pointer. Pass nullptr for functions that do not declare a return.
        const bool called=g_script->CallFunction(found->second.script,nullptr,nullptr,found->second.returnsValue?&result:nullptr,0);g_liveExecuting=false;
        const bool succeeded=called && g_liveDiagnostic.empty();
        ++found->second.calls;++g_liveJob.calls;const auto json=LiveElement(result);
        if(!succeeded){LiveFinish("failed",",\"error\":\"script-call-failed\",\"result\":"+json+",\"diagnostic\":"+Quote(g_liveDiagnostic));return;}
        g_liveJob.extra=",\"lastResult\":"+json+",\"revision\":"+std::to_string(found->second.revision);
        if(g_liveJob.limit && g_liveJob.calls>=g_liveJob.limit){const auto extra=g_liveJob.extra;LiveFinish("completed",extra);return;}
        g_liveJob.next=now+g_liveJob.interval;
    } else if(g_liveJob.op=="sequence.run") {
        const auto completion=bmt_live_input::TakeCompletionJson();if(completion.empty())return;
        bmt_live_json::Value result;std::string error;
        if(!bmt_live_json::Parse(completion,result,error)){LiveFinish("failed",",\"error\":\"invalid-input-completion\"");return;}
        const auto status=LiveString(result,"status","failed");LiveFinish(status.c_str(),",\"inputResult\":"+completion);
    } else if(g_liveJob.op=="baseline" && g_liveBaseline.saveNotified) {
        if(g_liveBaseline.savePath.empty()) {
            if(!LiveSavePathApiAvailable()){LiveFinish("failed",",\"error\":\"save-path-api-unavailable\"");return;}
            const auto* path=g_liveSerialization->GetSavePath();
            const auto length=path?strnlen_s(path,32768):0;
            std::string savePath;
            if(!length || length==32768 || !LiveResolveSavePair(std::string(path,length),g_liveBaseline.name,savePath)) {
                LiveFinish("failed",",\"error\":\"saved-path-identity-mismatch\"");return;
            }
            g_liveBaseline.coSavePath.assign(path,length);g_liveBaseline.savePath=std::move(savePath);
        }
        std::uint64_t saveSize=0,coSaveSize=0;
        if(!LiveFileSize(g_liveBaseline.savePath,saveSize) || !LiveFileSize(g_liveBaseline.coSavePath,coSaveSize)){g_liveSaveStableSince=0;return;}
        if(!g_liveSaveStableSince || saveSize!=g_liveSaveSize || coSaveSize!=g_liveCoSaveSize){g_liveSaveStableSince=now;g_liveSaveSize=saveSize;g_liveCoSaveSize=coSaveSize;return;}
        if(now-g_liveSaveStableSince>=250){g_liveBaseline.ready=true;LiveFinish("completed",",\"name\":"+Quote(g_liveBaseline.name)+",\"label\":"+Quote(g_liveBaseline.label)+",\"savePath\":"+Quote(g_liveBaseline.savePath)+
            ",\"coSavePath\":"+Quote(g_liveBaseline.coSavePath)+",\"saveBytes\":"+std::to_string(saveSize)+",\"coSaveBytes\":"+std::to_string(coSaveSize));}
    } else if(g_liveJob.op=="reset" && g_liveJob.loadStarted && g_liveJob.loadSucceeded) {
        LiveBaseline pose;if(!LivePlayerPose(pose))return;
        bool match=pose.cell==g_liveBaseline.cell;
        for(size_t i=0;i<3;++i)match=match && std::abs(pose.position[i]-g_liveBaseline.position[i])<=1.0 &&
            std::abs(std::remainder(pose.rotation[i]-g_liveBaseline.rotation[i],360.0))<=1.0;
        LiveFinish(match?"completed":"failed",g_liveJob.extra+",\"baselinePoseMatched\":"+std::string(match?"true":"false")+
            ",\"snapshot\":"+LiveSnapshot({"player.position","player.rotation","player.cell"})+(match?"":",\"error\":\"baseline-pose-mismatch\""));
    }
}
void LiveInitialize(const NVSEInterface* nvse) {
    g_liveData=static_cast<NVSEDataInterface*>(nvse->QueryInterface(7));
    g_liveSerialization=static_cast<NVSESerializationInterface*>(nvse->QueryInterface(0));
    bmt_live_input::SetSnapshotCallback(LiveSnapshot);
}
std::string LiveCapabilities() {
    return ",\"liveControl\":true,\"liveProtocolVersion\":1,\"liveOwnedScripts\":"+std::string(LiveScriptApiAvailable()?"true":"false")+
        ",\"liveScriptLimit\":16,\"liveSettingOwnership\":"+(LiveScriptApiAvailable()?"true":"false")+
        ",\"liveSettingLimit\":16,\"liveInput\":"+bmt_live_input::DiagnosticsJson();
}
