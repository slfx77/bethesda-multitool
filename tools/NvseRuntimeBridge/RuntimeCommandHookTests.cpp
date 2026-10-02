// Synthetic SDK dispatch table: verifies transparent forwarding and reversible pointer replacement.
// This executable does not load or start Fallout: New Vegas.
#include "NvseRuntimeBridge.cpp"
#include <iostream>
#include <stdexcept>
#include <memory>
#include <cstdarg>
#include <fstream>
#include <thread>

namespace {
CommandInfo g_testCommands[11]{};
int g_executeCalls=0, g_evalCalls=0;
bool TestExecute(ParamInfo*, void*, TESObjectREFR*, TESObjectREFR*, Script*, ScriptEventList*, double* result, UInt32* offset) {
    ++g_executeCalls; *result=0.0; ++*offset; return false;
}
bool OtherExecute(ParamInfo*, void*, TESObjectREFR*, TESObjectREFR*, Script*, ScriptEventList*, double*, UInt32*) { return true; }
bool TestEval(TESObjectREFR*, void*, void*, double* result) { ++g_evalCalls; *result=0.0; return false; }
const CommandInfo* GetTestCommand(const char* name) {
    for (auto& command : g_testCommands) if (!_stricmp(command.longName,name)) return &command;
    return nullptr;
}
void Check(bool value, const char* reason) { if (!value) throw std::runtime_error(reason); }
std::vector<std::unique_ptr<std::string>> g_testExpressions;
Script* CompileTestExpression(const char* expression) {
    g_testExpressions.push_back(std::make_unique<std::string>(expression));
    return reinterpret_cast<Script*>(g_testExpressions.back().get());
}
bool CallTestExpression(Script* script, TESObjectREFR*, TESObjectREFR*, NumericElement* result, std::uint8_t, ...) {
    const auto& expression=*reinterpret_cast<std::string*>(script);
    result->type=1;
    result->number=expression=="GetNumLoadedMods" ? 2 : expression=="GetModIndex \"FalloutNV.esm\"" ? 0
        : expression=="GetModIndex \"Addon.esm\"" ? 1 : 255;
    return true;
}
int g_scriptTestCalls=0;
bool __fastcall TestScriptExecute(Script* script, void*, TESObjectREFR* reference, ScriptEventList* events, TESObjectREFR* container, bool force) {
    ++g_scriptTestCalls;
    if (force) Check(!TraceScriptExecute(script,nullptr,reference,events,container,false),"nested original result changed");
    return force;
}
enum class ScriptCaptureChange { None, Stop, Disconnect, Restart, Reconnect, NestedRestart, NestedReconnect, RequestChange };
ScriptCaptureChange g_scriptCaptureChange=ScriptCaptureChange::None;
int g_scriptLifecycleCalls=0;
Script* g_expectedLifecycleScript=nullptr;
TESObjectREFR* g_expectedLifecycleReference=nullptr;
ScriptEventList* g_expectedLifecycleEvents=nullptr;
TESObjectREFR* g_expectedLifecycleContainer=nullptr;
bool __fastcall TestScriptCaptureChange(Script* script,void*,TESObjectREFR* reference,ScriptEventList* events,
    TESObjectREFR* container,bool force) {
    ++g_scriptLifecycleCalls;
    Check(script==g_expectedLifecycleScript && reference==g_expectedLifecycleReference && events==g_expectedLifecycleEvents &&
        container==g_expectedLifecycleContainer,"script lifecycle wrapper changed original arguments");
    if(force) {
        const auto change=g_scriptCaptureChange;
        if(change==ScriptCaptureChange::RequestChange)g_currentRequest=902;
        else if(change!=ScriptCaptureChange::None) {
            g_capture=false; ++g_captureGeneration;
            if(change==ScriptCaptureChange::Disconnect || change==ScriptCaptureChange::Reconnect || change==ScriptCaptureChange::NestedReconnect)
                g_connected=false;
            if(change==ScriptCaptureChange::Restart || change==ScriptCaptureChange::Reconnect ||
                change==ScriptCaptureChange::NestedRestart || change==ScriptCaptureChange::NestedReconnect) {
                g_connected=true; ++g_captureGeneration; g_capture=true; g_currentRequest=902;
            }
            if(change==ScriptCaptureChange::NestedRestart || change==ScriptCaptureChange::NestedReconnect)
                Check(!TraceScriptExecute(script,nullptr,reference,events,container,false),"new-capture nested return changed");
        }
    }
    SetLastError(0x5137+(force?1:0));
    return force;
}
void TestScriptCaptureLifecycle() {
    UInt32 script[21]{},reference[4]{},events[5]{},container[4]{};
    script[1]=0x11;script[3]=0xE7B6D;reference[3]=0x14;container[3]=0x104C0F;
    g_expectedLifecycleScript=reinterpret_cast<Script*>(script);
    g_expectedLifecycleReference=reinterpret_cast<TESObjectREFR*>(reference);
    g_expectedLifecycleEvents=reinterpret_cast<ScriptEventList*>(events);
    g_expectedLifecycleContainer=reinterpret_cast<TESObjectREFR*>(container);
    const auto savedOriginal=g_scriptExecuteOriginal;
    const auto savedMode=g_scriptTraceMode.load();
    const auto savedRequest=g_currentRequest;
    g_scriptExecuteOriginal=reinterpret_cast<ScriptExecuteFunction>(TestScriptCaptureChange);
    g_scriptTraceMode=3;
    for(const auto change:{ScriptCaptureChange::None,ScriptCaptureChange::Stop,ScriptCaptureChange::Disconnect,
        ScriptCaptureChange::Restart,ScriptCaptureChange::Reconnect,ScriptCaptureChange::NestedRestart,
        ScriptCaptureChange::NestedReconnect,ScriptCaptureChange::RequestChange}) {
        g_scriptCaptureChange=change;g_scriptLifecycleCalls=0;g_events.clear();
        g_captureGeneration=100;g_connected=true;g_capture=true;g_currentRequest=901;
        Check(TraceScriptExecute(g_expectedLifecycleScript,nullptr,g_expectedLifecycleReference,g_expectedLifecycleEvents,
            g_expectedLifecycleContainer,true),"capture transition changed original return");
        Check(GetLastError()==0x5138,"capture transition changed original last error");
        const bool nested=change==ScriptCaptureChange::NestedRestart || change==ScriptCaptureChange::NestedReconnect;
        const bool sameCapture=change==ScriptCaptureChange::None || change==ScriptCaptureChange::RequestChange;
        Check(g_scriptLifecycleCalls==(nested?2:1),"capture transition repeated/skipped the original handler");
        Check(g_events.size()==(nested?3u:sameCapture?2u:1u),"old script return escaped its admitted capture");
        Check(g_events[0].json.find("\"requestId\":901")!=std::string::npos &&
            g_events[0].json.find("\"kind\":\"script-entry\"")!=std::string::npos,"original script entry identity changed");
        if(sameCapture) Check(g_events[1].json.find("\"requestId\":901")!=std::string::npos &&
            g_events[1].json.find("\"kind\":\"script-exit\"")!=std::string::npos,"paired return adopted another request");
        if(nested) for(size_t index=1;index<3;++index) Check(
            g_events[index].json.find("\"parentCallId\":0")!=std::string::npos &&
            g_events[index].json.find("\"depth\":0")!=std::string::npos &&
            g_events[index].json.find("\"requestId\":902")!=std::string::npos,"new capture inherited stale nesting context");
        Check(g_scriptParentCall==0 && g_scriptParentGeneration==0 && g_scriptDepth==0,"capture transition lost prior nesting context");
    }
    g_scriptExecuteOriginal=savedOriginal;g_scriptTraceMode=savedMode;g_currentRequest=savedRequest;
    g_capture=true;g_connected=true;g_events.clear();
}

enum class CommandLocationFault { None, OutsideData, BadOffset, NullScript, BadScript, WrongType, BadData,
    LengthLimit, AddressOverflow, ChangedData, ChangedIdentity, Stop, Disconnect, Restart, RequestChange, Inactive, Unverified };
CommandLocationFault g_locationFault=CommandLocationFault::None;
ParamInfo* g_locationParameters=nullptr;void* g_locationData=nullptr;
TESObjectREFR* g_locationReference=nullptr;TESObjectREFR* g_locationContaining=nullptr;
Script* g_locationScript=nullptr;ScriptEventList* g_locationEvents=nullptr;
double* g_locationResult=nullptr;UInt32* g_locationOffset=nullptr;
std::uint8_t* g_locationCode=nullptr;UInt32* g_locationHeader=nullptr;int g_locationCalls=0;
bool LocationTestExecute(ParamInfo* parameters,void* data,TESObjectREFR* reference,TESObjectREFR* containing,
    Script* script,ScriptEventList* events,double* result,UInt32* offset) {
    ++g_locationCalls;
    Check(GetLastError()==0x6371,"location observer changed the incoming LastError");
    Check(parameters==g_locationParameters && data==g_locationData && reference==g_locationReference && containing==g_locationContaining &&
        script==g_locationScript && events==g_locationEvents && result==g_locationResult && offset==g_locationOffset,
        "location wrapper changed original argument identity");
    *result=0;
    if(g_locationFault!=CommandLocationFault::BadOffset)*offset+=3;
    switch(g_locationFault) {
    case CommandLocationFault::ChangedData:g_locationCode[1]^=0x80;break;
    case CommandLocationFault::ChangedIdentity:++g_locationHeader[3];break;
    case CommandLocationFault::Stop:g_capture=false;++g_captureGeneration;break;
    case CommandLocationFault::Disconnect:g_connected=false;g_capture=false;++g_captureGeneration;break;
    case CommandLocationFault::Restart:g_capture=false;++g_captureGeneration;g_currentRequest=902;g_capture=true;break;
    case CommandLocationFault::RequestChange:g_currentRequest=902;break;
    default:break;
    }
    SetLastError(0x6372);return false;
}
void TestCommandLocationObservation() {
    const auto savedProfile=g_pcLayoutVerified,savedCapture=g_capture.load(),savedConnected=g_connected.load();
    const auto savedGeneration=g_captureGeneration.load(),savedRequest=g_currentRequest;
    const auto savedMessage=g_lastMessage;
    const CommandLocationFault faults[]={CommandLocationFault::None,CommandLocationFault::None,CommandLocationFault::None,
        CommandLocationFault::OutsideData,CommandLocationFault::BadOffset,CommandLocationFault::NullScript,CommandLocationFault::BadScript,
        CommandLocationFault::WrongType,CommandLocationFault::BadData,CommandLocationFault::LengthLimit,CommandLocationFault::AddressOverflow,
        CommandLocationFault::ChangedData,CommandLocationFault::ChangedIdentity,CommandLocationFault::Stop,CommandLocationFault::Disconnect,
        CommandLocationFault::Restart,CommandLocationFault::RequestChange,CommandLocationFault::Inactive,CommandLocationFault::Unverified};
    for(size_t index=0;index<std::size(faults);++index) {
        const auto wrapper=index%3;const auto fault=faults[index];g_locationFault=fault;
        std::uint8_t code[]={0x1D,0x00,0x02,0x00,0x01,0x00,0xFF,0x00},outside[8]{};
        UInt32 script[0x34/4]{},reference[4]{},container[4]{},events[4]{},offset=2;double result=-1;
        ParamInfo parameters{};script[1]=0x11;script[3]=0x123456;script[8]=sizeof(code);script[12]=reinterpret_cast<UInt32>(code);
        reference[3]=0x14;container[3]=0x99;
        g_locationCode=code;g_locationHeader=script;g_locationParameters=&parameters;
        g_locationData=fault==CommandLocationFault::OutsideData?outside:code;
        g_locationScript=fault==CommandLocationFault::NullScript?nullptr:fault==CommandLocationFault::BadScript?reinterpret_cast<Script*>(1):reinterpret_cast<Script*>(script);
        g_locationReference=reinterpret_cast<TESObjectREFR*>(reference);g_locationContaining=reinterpret_cast<TESObjectREFR*>(container);
        g_locationEvents=reinterpret_cast<ScriptEventList*>(events);g_locationResult=&result;
        g_locationOffset=fault==CommandLocationFault::BadOffset?reinterpret_cast<UInt32*>(1):&offset;
        if(fault==CommandLocationFault::WrongType)script[1]=0x2A;
        if(fault==CommandLocationFault::BadData)script[12]=1;
        if(fault==CommandLocationFault::LengthLimit)script[8]=CommandBytecodeLimit+1;
        if(fault==CommandLocationFault::AddressOverflow){script[12]=0xFFFFFFFE;script[8]=8;}
        const auto original=g_commandHooks[wrapper].execute;
        g_commandHooks[wrapper].execute=LocationTestExecute;g_events.clear();g_lastMessage={};g_locationCalls=0;
        g_captureGeneration=321;g_currentRequest=901;g_connected=true;g_capture=fault!=CommandLocationFault::Inactive;
        g_pcLayoutVerified=fault!=CommandLocationFault::Unverified;
        SetLastError(0x6371);
        const bool returned=g_executeWrappers[wrapper](&parameters,g_locationData,g_locationReference,g_locationContaining,g_locationScript,g_locationEvents,&result,g_locationOffset);
        Check(GetLastError()==0x6372,"location observer changed handler LastError");
        Check(!returned && result==0 && g_locationCalls==1 && offset==(fault==CommandLocationFault::BadOffset?2u:5u),
            "location wrapper repeated handler or changed its return/result/offset");
        const bool suppressed=fault==CommandLocationFault::Stop || fault==CommandLocationFault::Disconnect || fault==CommandLocationFault::Restart || fault==CommandLocationFault::Inactive;
        Check(g_events.size()==(suppressed?0u:1u),"location event escaped capture admission/generation boundary");
        if(!suppressed) {
            const auto& json=g_events[0].json;
            Check(g_events[0].id==901 && json.find("\"normalizedOffsetStatus\":\"unverified\"")!=std::string::npos,
                "location request changed or raw offset was normalized");
            if(fault==CommandLocationFault::None || fault==CommandLocationFault::OutsideData || fault==CommandLocationFault::BadOffset || fault==CommandLocationFault::RequestChange)
                Check(json.find("\"bytecodeStableAcrossCall\":true")!=std::string::npos,"complete stable bytecode fingerprint unavailable");
            else Check(json.find("\"bytecodeStableAcrossCall\":false")!=std::string::npos,"invalid or changed code promoted to stable fingerprint");
            if(fault==CommandLocationFault::None) {
                const std::string expected="6970a52f9efe4cb99df39dbf73ef0556488ceb0fcc97380441c368b5eee7a0ba";
                Check(json.find("\"sha256\":"+Quote(expected))!=std::string::npos &&
                    json.find("\"opcodeOffset\":2")!=std::string::npos && json.find("\"opcodeOffset\":5")!=std::string::npos,
                    "raw before/after offset or full-byte hash missing");
            }
            const char* expected=fault==CommandLocationFault::OutsideData?"outside-data":fault==CommandLocationFault::BadOffset?"unreadable":
                fault==CommandLocationFault::NullScript?"null":fault==CommandLocationFault::BadScript?"unreadable":
                fault==CommandLocationFault::WrongType?"type-mismatch":fault==CommandLocationFault::BadData?"unreadable":
                fault==CommandLocationFault::LengthLimit?"length-limit":fault==CommandLocationFault::AddressOverflow?"address-overflow":
                fault==CommandLocationFault::ChangedData || fault==CommandLocationFault::ChangedIdentity?"changed":
                fault==CommandLocationFault::Unverified?"layout-unverified":nullptr;
            if(expected)Check(json.find(Quote(expected))!=std::string::npos,"location missing explicit fault status");
        }
        g_commandHooks[wrapper].execute=original;
    }
    g_pcLayoutVerified=savedProfile;g_capture=savedCapture;g_connected=savedConnected;
    g_captureGeneration=savedGeneration;g_currentRequest=savedRequest;g_lastMessage=savedMessage;
}
std::array<CommandInfo,10> g_generalTestTable{};
std::uint64_t g_generalResultBits=0x3FF0000000000000ULL;
std::string g_generalFixtureCoverage;
std::vector<std::string> g_generalFixtureEvents;
const CommandInfo* GeneralTestStart(){return g_generalTestTable.data();}
const CommandInfo* GeneralTestEnd(){return g_generalTestTable.data()+g_generalTestTable.size();}
const CommandInfo* GeneralTestOpcode(UInt32 opcode) {
    if(opcode==0x1007)return nullptr;
    for(auto& entry:g_generalTestTable)if(entry.opcode==opcode) {
        if(opcode==0x1009)entry.execute=OtherExecute;
        return &entry;
    }
    return nullptr;
}
UInt32 GeneralTestReturnType(const CommandInfo* entry){return entry->opcode==0x1001?1:0;}
bool GeneralTestExecute(ParamInfo* parameters,void* data,TESObjectREFR* reference,TESObjectREFR* containing,
    Script* script,ScriptEventList* events,double* result,UInt32* offset) {
    const auto returned=LocationTestExecute(parameters,data,reference,containing,script,events,result,offset);
    memcpy(result,&g_generalResultBits,sizeof(*result));return returned;
}
bool __fastcall GeneralTestScriptExecute(Script* script,void*,TESObjectREFR* reference,ScriptEventList* events,
    TESObjectREFR* container,bool) {
    Check(script==g_locationScript && reference==g_locationReference && events==g_locationEvents &&
        container==g_locationContaining,"fixture script scope changed original arguments");
    SetLastError(0x6371);
    return g_generalTestTable[0].execute(g_locationParameters,g_locationData,reference,container,script,events,g_locationResult,g_locationOffset);
}
void TestGeneralCommandObservation() {
    const auto savedCommands=g_commands;const auto savedSpecial=g_commandHooks[0];
    const auto savedMask=g_messageHookMask.load(),savedMode=g_scriptTraceMode.load();
    const auto savedSelections=g_scriptSelections;g_scriptSelections.clear();
    const char* names[]={"Alpha","FormQuery","","NoHandler","BadHandler","DuplicateA","DuplicateB","WrongSlot","ShowMessage","ChangedEntry"};
    for(size_t i=0;i<g_generalTestTable.size();++i) {
        auto& entry=g_generalTestTable[i];entry={};entry.longName=names[i];entry.opcode=0x1000+static_cast<UInt32>(i);entry.execute=GeneralTestExecute;
    }
    g_generalTestTable[3].execute=nullptr;g_generalTestTable[4].execute=reinterpret_cast<CommandExecute>(1);
    g_generalTestTable[6].opcode=g_generalTestTable[5].opcode;
    g_commandHooks[0].entry=&g_generalTestTable[8];g_messageHookMask=1;
    NVSECommandTableInterface table{};table.version=1;table.Start=GeneralTestStart;table.End=GeneralTestEnd;
    table.GetByOpcode=GeneralTestOpcode;table.GetReturnType=GeneralTestReturnType;g_commands=&table;
    InstallGeneralCommandHooks();
    Check(g_generalInstalled==2 && g_generalCoverageRows.size()==10,"general table coverage count is wrong");
    const auto coverage=GeneralCommandCoverage();
    for(const char* reason:{"unnamed-padding","no-execute-handler","nonexecutable-handler","duplicate-opcode","opcode-entry-mismatch","entry-changed"})
        Check(coverage.find(Quote(reason))!=std::string::npos,"general coverage lost a skipped reason");
    Check(coverage.find("\"special\":1")!=std::string::npos,"special message hooks were duplicated");
    g_capture=true;g_connected=true;g_captureGeneration=321;g_events.clear();
    EmitGeneralCommandCoverage(901,321);
    Check(g_events.size()==1 && g_events[0].json.size()<MaxPayload && g_events[0].json.find("\"entries\":[")!=std::string::npos,
        "bounded general coverage detail missing");
    g_generalFixtureCoverage="{"+GeneralCommandCoverage().substr(1)+"}";
    const auto savedCoverage=g_generalCoverageRows;
    g_generalCoverageRows.assign(65,{0xFFFFFFFF,0xFFFFFFFF,std::string(128,static_cast<char>(0x80)),"nonexecutable-handler"});
    g_events.clear();EmitGeneralCommandCoverage(901,321);
    Check(g_events.size()==2 && g_events[0].json.size()<MaxPayload && g_events[1].json.size()<MaxPayload &&
        g_events[0].json.find("\"parts\":2")!=std::string::npos,"escaped coverage names exceeded wire bound or lost chunk count");
    g_generalCoverageRows=savedCoverage;
    for(const auto fault:{CommandLocationFault::None,CommandLocationFault::Stop,CommandLocationFault::Disconnect,
        CommandLocationFault::Restart,CommandLocationFault::RequestChange,CommandLocationFault::Inactive}) {
        std::uint8_t code[]={0x1D,0,0,0,0x1F,0x10,0,0};UInt32 script[20]{},reference[4]{},containing[4]{},events[4]{},offset=2;
        ParamInfo parameters{};double result=0;
        script[1]=0x11;script[3]=0x123456;script[8]=sizeof(code);script[12]=reinterpret_cast<UInt32>(code);reference[3]=0x14;containing[3]=0x99;
        g_locationFault=fault;g_locationParameters=&parameters;g_locationData=code;g_locationCode=code;g_locationHeader=script;
        g_locationScript=reinterpret_cast<Script*>(script);g_locationReference=reinterpret_cast<TESObjectREFR*>(reference);
        g_locationContaining=reinterpret_cast<TESObjectREFR*>(containing);g_locationEvents=reinterpret_cast<ScriptEventList*>(events);
        g_locationResult=&result;g_locationOffset=&offset;g_locationCalls=0;g_events.clear();
        g_pcLayoutVerified=true;g_captureGeneration=321;g_currentRequest=901;g_capture=fault!=CommandLocationFault::Inactive;g_connected=true;g_scriptTraceMode=3;
        g_scriptParentCall=77;g_scriptParentGeneration=321;SetLastError(0x6371);
        Check(!g_generalTestTable[0].execute(&parameters,code,g_locationReference,g_locationContaining,g_locationScript,g_locationEvents,&result,&offset),
            "general wrapper changed return");
        Check(g_locationCalls==1 && offset==5 && result==1.0 && GetLastError()==0x6372,"general wrapper changed handler effects");
        const bool emitted=fault==CommandLocationFault::None || fault==CommandLocationFault::RequestChange;
        Check(g_events.size()==(emitted?1u:0u),"general command escaped capture boundary");
        if(emitted) {
            const auto& json=g_events[0].json;
            Check(g_events[0].id==901 && json.find("\"observedScriptCallId\":77")!=std::string::npos &&
                json.find("\"resultBits\":\"3ff0000000000000\"")!=std::string::npos && json.find("\"numericValue\":1")!=std::string::npos &&
                json.find("\"normalizedOffsetStatus\":\"unverified\"")!=std::string::npos,"general command lost raw/result/scope evidence");
        }
        // Same handler ABI for a form return: preserve raw bits, omit numeric semantic alias.
        if(fault==CommandLocationFault::None) {
            const auto savedScriptOriginal=g_scriptExecuteOriginal;
            g_scriptExecuteOriginal=reinterpret_cast<ScriptExecuteFunction>(GeneralTestScriptExecute);
            g_events.clear();g_sequence=0;g_dropped=0;offset=2;g_locationCalls=0;g_scriptParentCall=0;g_scriptParentGeneration=0;
            Emit("capture-start",901,",\"session\":\"synthetic-native-command-fixture\""+GeneralCommandCoverage(),321);
            EmitGeneralCommandCoverage(901,321);
            Check(!TraceScriptExecute(g_locationScript,nullptr,g_locationReference,g_locationEvents,g_locationContaining,false) &&
                g_events.size()==5 && g_locationCalls==1,"generic command fixture did not retain its actual script entry/return pair");
            Emit("capture-end",901,",\"status\":\"completed\"",321);
            for(const auto& event:g_events)g_generalFixtureEvents.push_back(event.json);
            g_scriptExecuteOriginal=savedScriptOriginal;g_scriptParentCall=77;g_scriptParentGeneration=321;
            g_events.clear();offset=2;g_locationCalls=0;g_scriptParentGeneration=320;SetLastError(0x6371);
            g_generalTestTable[1].execute(&parameters,code,g_locationReference,g_locationContaining,g_locationScript,g_locationEvents,&result,&offset);
            Check(g_events.size()==1 && g_events[0].json.find("\"returnType\":1")!=std::string::npos &&
                g_events[0].json.find("numericValue")==std::string::npos && g_events[0].json.find("\"observedScriptCallId\":null")!=std::string::npos,
                "form result or stale scope was promoted to numeric/current scope");
            for(const UInt32 mode:{0u,1u,2u}) {
                g_scriptTraceMode=mode;g_currentRequest=0;g_events.clear();offset=2;SetLastError(0x6371);
                g_generalTestTable[0].execute(&parameters,code,g_locationReference,g_locationContaining,g_locationScript,g_locationEvents,&result,&offset);
                Check(g_events.empty() && GetLastError()==0x6372 && offset==5,"unselected general command was observed or changed");
            }
        }
    }
    g_generalTestTable[0].execute=OtherExecute;RestoreGeneralCommandHooks();
    Check(g_generalTestTable[0].execute==OtherExecute && g_generalTestTable[1].execute==GeneralTestExecute,
        "general teardown overwrote a later hook or failed to restore original");
    g_commands=savedCommands;g_commandHooks[0]=savedSpecial;g_messageHookMask=savedMask;g_scriptTraceMode=savedMode;
    g_scriptSelections=savedSelections;
    g_scriptParentCall=0;g_scriptParentGeneration=0;g_currentRequest=0;g_capture=true;g_connected=true;g_events.clear();
}
void TestCommandResultKinds() {
    double value=1.0;
    for(const UInt32 type:{0u,1u,2u,3u,4u,5u,6u,UINT32_MAX}) {
        const auto fields=CommandResultFields(&value,type);
        Check(fields.find("\"resultBits\":\"3ff0000000000000\"")!=std::string::npos &&
            (fields.find("numericValue")!=std::string::npos)==(type==0),"SDK return kind became an incorrect numeric value");
        Check(fields.find(type<6?"\"returnTypeStatus\":\"observed\"":"\"returnTypeStatus\":\"unavailable\"")!=std::string::npos,
            "unknown SDK return kind was promoted to known");
    }
    const std::uint64_t nanBits=0x7FF8000000000000ULL;memcpy(&value,&nanBits,sizeof(value));
    const auto nonfinite=CommandResultFields(&value,0);
    Check(nonfinite.find("\"resultBits\":\"7ff8000000000000\"")!=std::string::npos &&
        nonfinite.find("numericValue")==std::string::npos,"nonfinite result lost raw bits or became JSON number");
    const auto unreadable=CommandResultFields(reinterpret_cast<double*>(1),0);
    Check(unreadable.find("\"resultStatus\":\"unavailable\"")!=std::string::npos &&
        unreadable.find("\"resultBits\":null")!=std::string::npos,"inaccessible result was observed");
}
int g_evalLifecycleCalls=0;
bool LifecycleTestEval(TESObjectREFR* reference,void* first,void* second,double* result) {
    ++g_evalLifecycleCalls;
    Check(GetLastError()==0x6371 && reference==g_locationReference && first==reinterpret_cast<void*>(17) &&
        second==reinterpret_cast<void*>(23) && result==g_locationResult,"eval changed incoming context");
    *result=0;
    if(g_locationFault==CommandLocationFault::Stop){g_capture=false;++g_captureGeneration;}
    if(g_locationFault==CommandLocationFault::Disconnect){g_capture=false;g_connected=false;++g_captureGeneration;}
    if(g_locationFault==CommandLocationFault::Restart){++g_captureGeneration;g_currentRequest=902;}
    if(g_locationFault==CommandLocationFault::RequestChange)g_currentRequest=902;
    SetLastError(0x6372);return false;
}
void TestEvalCaptureLifecycle() {
    const auto saved=g_commandHooks[3].eval;g_commandHooks[3].eval=LifecycleTestEval;
    const auto savedWatched=g_watchedActors;
    for(const auto fault:{CommandLocationFault::None,CommandLocationFault::Stop,CommandLocationFault::Disconnect,
        CommandLocationFault::Restart,CommandLocationFault::RequestChange,CommandLocationFault::Inactive}) {
        UInt32 reference[4]{};reference[3]=0x14;double result=-1;
        g_locationReference=reinterpret_cast<TESObjectREFR*>(reference);g_locationResult=&result;g_locationFault=fault;
        g_captureGeneration=321;g_currentRequest=901;g_capture=fault!=CommandLocationFault::Inactive;g_connected=true;
        g_scriptParentCall=77;g_scriptParentGeneration=321;g_events.clear();g_evalLifecycleCalls=0;SetLastError(0x6371);
        Check(!g_evalWrappers[0](g_locationReference,reinterpret_cast<void*>(17),reinterpret_cast<void*>(23),&result) &&
            result==0 && g_evalLifecycleCalls==1 && GetLastError()==0x6372,"eval did not forward exactly once transparently");
        const bool emitted=fault==CommandLocationFault::None || fault==CommandLocationFault::RequestChange;
        Check(g_events.size()==(emitted?1u:0u),"eval return escaped its capture generation");
        if(emitted)Check(g_events[0].id==901 && g_events[0].json.find("\"comparisonObserved\":false")!=std::string::npos,
            "eval request changed or function value became comparison truth");
    }
    for(const UInt32 scenario:{0u,1u,2u,3u}) {
        UInt32 reference[4]{};reference[3]=0x14;double result=-1;
        g_locationReference=scenario==2?reinterpret_cast<TESObjectREFR*>(1):reinterpret_cast<TESObjectREFR*>(reference);
        g_locationResult=&result;g_locationFault=CommandLocationFault::None;g_captureGeneration=321;
        g_currentRequest=scenario==3?901:0;g_capture=true;g_connected=true;g_events.clear();g_evalLifecycleCalls=0;
        g_watchedActors={scenario==0?0x14u:0x99u};SetLastError(0x6371);
        g_evalWrappers[0](g_locationReference,reinterpret_cast<void*>(17),reinterpret_cast<void*>(23),&result);
        Check(g_evalLifecycleCalls==1 && result==0 && GetLastError()==0x6372 &&
            g_events.size()==(scenario==0 || scenario==3?1u:0u),"eval watched-actor filter changed dispatch or accepted unknown owner");
    }
    g_watchedActors=savedWatched;
    g_commandHooks[3].eval=saved;g_scriptParentCall=0;g_scriptParentGeneration=0;g_currentRequest=0;
    g_capture=true;g_connected=true;g_events.clear();
}
struct FloatingFields {std::uint16_t control=0,status=0;UInt32 mxcsr=0;};
FloatingFields ReadFloatingFields() {
    CommandFloatingState state;state.Save();FloatingFields fields;
    memcpy(&fields.control,state.bytes,2);memcpy(&fields.status,state.bytes+2,2);memcpy(&fields.mxcsr,state.bytes+24,4);
    return fields;
}
void WriteFloatingFields(FloatingFields fields) {
    CommandFloatingState state;state.Save();
    memcpy(state.bytes,&fields.control,2);memcpy(state.bytes+2,&fields.status,2);memcpy(state.bytes+24,&fields.mxcsr,4);state.Restore();
}
bool EqualFloatingFields(FloatingFields a,FloatingFields b) {return a.control==b.control && a.status==b.status && a.mxcsr==b.mxcsr;}
FloatingFields g_floatingIncoming,g_floatingOutgoing;int g_floatingCalls=0;
bool FloatingTestExecute(ParamInfo*,void*,TESObjectREFR*,TESObjectREFR*,Script*,ScriptEventList*,double* result,UInt32* offset) {
    const auto observed=ReadFloatingFields();
    Check(EqualFloatingFields(observed,g_floatingIncoming),"observer changed incoming x87/SSE state");
    ++g_floatingCalls;*result=0.1;++*offset;WriteFloatingFields(g_floatingOutgoing);
    // FXRSTOR normalizes x87 ES/B when a pending exception is unmasked. The
    // observer must preserve that actual hardware state, not our input template.
    g_floatingOutgoing=ReadFloatingFields();SetLastError(0x6372);return false;
}
bool FloatingTestEval(TESObjectREFR*,void*,void*,double* result) {
    UInt32 unused=0;return FloatingTestExecute(nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,result,&unused);
}
void TestCommandFloatingEnvironment() {
    CommandFloatingGuard restore;
    const auto savedExecute=g_commandHooks[0].execute,savedGeneral=g_generalCommands[0].original,
        savedReference=g_referenceCommandOriginal;
    const auto savedEval=g_commandHooks[3].eval;
    g_commandHooks[0].execute=FloatingTestExecute;g_generalCommands[0].original=FloatingTestExecute;g_commandHooks[3].eval=FloatingTestEval;
    g_referenceCommandOriginal=FloatingTestExecute;
    for(const UInt32 wrapper:{0u,1u,2u,3u})for(const bool active:{false,true}) {
        UInt32 script[20]{};script[1]=0x11;script[3]=0x123456;UInt32 offset=2;double result=0;
        g_capture=active;g_connected=true;g_captureGeneration=321;g_currentRequest=901;g_scriptTraceMode=3;
        g_events.clear();g_floatingCalls=0;
        const auto original=ReadFloatingFields();
        // Unmask divide-by-zero at entry and precision at return. Formatting 0.1
        // must run under the observer environment, without changing the game state.
        g_floatingIncoming={static_cast<std::uint16_t>(0x047B),static_cast<std::uint16_t>(original.status&0x3800),0x5D81};
        g_floatingOutgoing={static_cast<std::uint16_t>(0x0A5F),static_cast<std::uint16_t>((original.status&0x3800)|0x21),0x2FA0};
        WriteFloatingFields(g_floatingIncoming);SetLastError(0x6371);
        const bool returned=wrapper==2?g_evalWrappers[0](nullptr,nullptr,nullptr,&result):
            (wrapper==0?g_executeWrappers[0]:wrapper==1?g_generalWrappers[0]:ReferenceCommandExecute)
                (nullptr,nullptr,nullptr,nullptr,reinterpret_cast<Script*>(script),nullptr,&result,&offset);
        const auto actual=ReadFloatingFields();const auto lastError=GetLastError();
        if(!EqualFloatingFields(actual,g_floatingOutgoing)) {
            CommandObserverFloatingEnvironment();
            std::cerr<<"FP wrapper="<<wrapper<<" active="<<active<<" actual="<<std::hex<<actual.control<<'/'<<actual.status<<'/'<<actual.mxcsr
                <<" expected="<<g_floatingOutgoing.control<<'/'<<g_floatingOutgoing.status<<'/'<<g_floatingOutgoing.mxcsr<<std::dec<<'\n';
        }
        Check(!returned && g_floatingCalls==1 && EqualFloatingFields(actual,g_floatingOutgoing) && lastError==0x6372,
            "observer changed handler outgoing x87/SSE state or forwarding");
        if(active && wrapper==1)Check(g_events.size()==1 &&
            g_events[0].json.find("\"numericValue\":0.10000000000000001")!=std::string::npos,
            "game rounding changed numeric result serialization");
        if(active && wrapper==3)Check(g_events.size()==1 &&
            g_events[0].json.find("\"value\":0.10000000000000001")!=std::string::npos,
            "game rounding changed reference result serialization");
    }
    g_commandHooks[0].execute=savedExecute;g_generalCommands[0].original=savedGeneral;g_commandHooks[3].eval=savedEval;
    g_referenceCommandOriginal=savedReference;
    g_capture=true;g_currentRequest=0;g_events.clear();
}
ProbeMessageCallback g_testMessageCallback=nullptr;
unsigned char g_testMessageButton=0;
int g_testMessageReads=0;
bool __cdecl TestShowMessage(const char*,UInt32,UInt32,ProbeMessageCallback callback,UInt32,UInt32,float,float,...) {
    g_testMessageCallback=callback; return true;
}
unsigned char __cdecl TestReadMessageButton() { ++g_testMessageReads; return g_testMessageButton; }
void TestMessageInstances() {
    g_events.clear(); g_capture=true; g_connected=true; g_captureGeneration=1;
    g_messageApiVerified=true; g_probeShowMessage=TestShowMessage; g_probeReadButton=TestReadMessageButton;
    MessageProbe({8,11,"show-ok"});
    Check(g_events.size()==1 && g_pendingMessageProbe.pending,"probe did not retain callback ownership");
    // An unrelated script-global button read must not complete our pending instance.
    Check(g_testMessageReads==0 && g_events[0].json.find("message-probe-start")!=std::string::npos,"probe consumed a global result before its callback");
    MessageProbe({8,12,"show-ok"});
    Check(g_events.back().json.find("message-probe-active-or-invalid")!=std::string::npos,"second probe reused a pending callback");
    g_testMessageCallback();
    Check(g_testMessageReads==1 && !g_pendingMessageProbe.pending &&
        g_events.back().json.find("\"requestId\":11")!=std::string::npos &&
        g_events.back().json.find("\"value\":0")!=std::string::npos &&
        g_events.back().json.find("dedicated-engine-message-callback")!=std::string::npos,"callback lost instance identity or zero button");
    const auto observed=g_events.size(); g_testMessageCallback();
    Check(g_events.size()==observed && g_testMessageReads==1,"duplicate callback emitted a second choice");
    MessageProbe({8,13,"show-ok"}); ++g_captureGeneration;
    const auto beforeCancelled=g_events.size(); g_testMessageCallback();
    Check(g_events.size()==beforeCancelled && !g_pendingMessageProbe.pending,"cancelled callback leaked into a later capture");
    MessageProbe({8,14,"show-ok"}); g_connected=false;
    const auto beforeDisconnected=g_events.size(); g_testMessageCallback();
    Check(g_events.size()==beforeDisconnected && !g_pendingMessageProbe.pending,"disconnected callback emitted an event");
    g_connected=true; MessageProbe({8,15,"show-ok"}); g_testMessageButton=255; g_testMessageCallback();
    Check(g_events.back().json.find("unexpected-probe-button")!=std::string::npos,"missing button was promoted to a choice");
    const auto beforeStale=g_events.size();
    Emit("message-probe-choice",99,",\"value\":0",g_captureGeneration.load()-1);
    Check(g_events.size()==beforeStale,"stale callback publication raced into a new capture");
    g_messageApiVerified=false; MessageProbe({8,16,"show-ok"});
    Check(g_events.back().json.find("message-probe-api-unavailable")!=std::string::npos,"unverified engine message API was used");
    g_events.clear();
}
void TestRuntimeLayouts() {
    UInt32 form[4]{}; form[1]=0x47; form[3]=0xE61A4;
    UInt32 entry[3]={0,form[3],reinterpret_cast<UInt32>(form)};
    UInt32 bucket=reinterpret_cast<UInt32>(entry);
    UInt32 map[4]={0,1,reinterpret_cast<UInt32>(&bucket),1};
    Check(LookupRuntimeFormAt(reinterpret_cast<std::uintptr_t>(map),form[3])==form,"bounded map lookup lost exact form identity");
    form[3]++;
    Check(!LookupRuntimeFormAt(reinterpret_cast<std::uintptr_t>(map),entry[1]),"mismatched record identity was accepted");
    entry[0]=reinterpret_cast<UInt32>(entry);
    Check(!LookupRuntimeFormAt(reinterpret_cast<std::uintptr_t>(map),0x123),"cyclic bucket did not fail closed");

    UInt32 quest[24]{},script[21]{},eventList[5]{},meta[8]{},node[2]{},local[4]{};
    char name[]="Flag";
    script[1]=0x11; script[3]=0xE7B6D; script[19]=reinterpret_cast<UInt32>(meta);
    quest[7]=reinterpret_cast<UInt32>(script); quest[23]=reinterpret_cast<UInt32>(eventList);
    eventList[0]=reinterpret_cast<UInt32>(script); eventList[3]=reinterpret_cast<UInt32>(node);
    meta[0]=9; meta[6]=reinterpret_cast<UInt32>(name); meta[7]=4;
    node[0]=reinterpret_cast<UInt32>(local); local[0]=9;
    QuestVariableStorage storage{};
    Check(FindQuestVariableStorage(quest,"Flag",storage) && storage.value==0 && storage.index==9,"resident numeric zero was lost");
    Check(!FindQuestVariableStorage(quest,"Missing",storage),"missing variable became zero");
    node[0]=0;
    Check(!FindQuestVariableStorage(quest,"Flag",storage),"missing runtime local became zero");
    node[0]=reinterpret_cast<UInt32>(local);
    UInt32 refMeta[4]={0,0,0,9}; script[17]=reinterpret_cast<UInt32>(refMeta);
    Check(!FindQuestVariableStorage(quest,"Flag",storage),"reference variable was accepted as numeric despite identical flag");

    std::vector<std::string> parts; UInt32 id=0; double value=42;
    Check(ParseQuestPayload({11,1,"FalloutNV.esm\t0E61A4\tFlag\t0"},parts,id,value) && value==0 && id==0xE61A4,"typed quest payload lost zero");
    for (const auto* payload:{"../FalloutNV.esm\t0E61A4\tFlag", "FalloutNV.esm\t010E61A4\tFlag", "FalloutNV.esm\t0E61A4\tFlag\nqqq"}) {
        parts.clear(); Check(!ParseQuestPayload({10,1,payload},parts,id,value),"invalid quest identity was accepted");
    }
    std::string pluginPayload,scope,error;
    Check(ConfigureScriptTraceStart("session\nFalloutNV.esm\n@bmt-script-scope=all",pluginPayload,scope,error) &&
        pluginPayload=="session\nFalloutNV.esm" && scope.find("\"filterApplied\":false")!=std::string::npos,"all trace scope was hidden or broke plugin candidates");
    Check(!ConfigureScriptTraceStart("session\n@bmt-script-scope=selected",pluginPayload,scope,error),"empty selected scope accepted");
    Check(!ConfigureScriptTraceStart("session\n@bmt-script-scope=requested\n@bmt-script-scope=all",pluginPayload,scope,error),"duplicate scope accepted");
    Check(ConfigureScriptTraceStart("session",pluginPayload,scope,error) && scope.find("\"mode\":\"requested\"")!=std::string::npos,"legacy client scope was not reported");
    Check(!TraceSelectedScript(reinterpret_cast<Script*>(script)),"default captured an unrequested natural quest call");
    const auto originalId=script[3]; script[3]=0; g_currentRequest=23;
    Check(TraceSelectedScript(reinterpret_cast<Script*>(script)),"requested temporary script was excluded");
    script[3]=0xFF001234; script[2]=0x00004000;
    Check(TraceSelectedScript(reinterpret_cast<Script*>(script)),"allocated temporary script with nonzero ID was excluded");
    script[3]=0; script[2]=0;
    g_currentRequest=0;
    Check(!TraceSelectedScript(reinterpret_cast<Script*>(script)),"unrequested temporary script was included");
    script[3]=originalId; g_scriptTraceMode=2; g_scriptSelections.insert(originalId);
    Check(TraceSelectedScript(reinterpret_cast<Script*>(script)),"explicit selected script was excluded");
    g_scriptTraceMode=0;
    Check(!TraceSelectedScript(reinterpret_cast<Script*>(script)),"off scope still emitted script events");
    g_scriptTraceMode=3;
    g_events.clear(); g_scriptExecuteOriginal=reinterpret_cast<ScriptExecuteFunction>(TestScriptExecute);
    Check(TraceScriptExecute(reinterpret_cast<Script*>(script),nullptr,nullptr,nullptr,nullptr,true),"script wrapper changed original return");
    Check(g_scriptTestCalls==2 && g_events.size()==4,"script wrapper did not pair nested entry/exit exactly once");
    Check(g_events[0].json.find("\"depth\":0")!=std::string::npos &&
        g_events[1].json.find("\"depth\":1")!=std::string::npos &&
        g_events[2].json.find("\"returned\":false")!=std::string::npos &&
        g_events[3].json.find("\"returned\":true")!=std::string::npos,"nested script provenance or boolean result changed");
    Check(g_scriptParentCall==0 && g_scriptDepth==0,"script wrapper did not restore nesting context");
}
void TestActorObservations() {
    UInt32 item[2]={14,0},head[2]={reinterpret_cast<UInt32>(item),0};
    auto modifiers=ActorModifiers(reinterpret_cast<std::uintptr_t>(head));
    Check(modifiers.find("\"value\":0")!=std::string::npos && modifiers.find("\"status\":\"complete\"")!=std::string::npos,
        "actor modifier zero was lost");
    for (const UInt32 next:{reinterpret_cast<UInt32>(head),1u}) {
        head[1]=next;modifiers=ActorModifiers(reinterpret_cast<std::uintptr_t>(head));
        Check(modifiers.find("\"status\":\"partial\"")!=std::string::npos,"cyclic or unreadable modifier list became complete");
    }
    UInt32 many[65][2]{};
    for(size_t i=0;i<std::size(many);++i) { many[i][0]=reinterpret_cast<UInt32>(item);many[i][1]=i+1<std::size(many)?reinterpret_cast<UInt32>(many[i+1]):0; }
    Check(ActorModifiers(reinterpret_cast<std::uintptr_t>(many)).find("node-limit")!=std::string::npos,"actor modifier bound was ignored");
    head[0]=0;head[1]=0;
    Check(ActorModifiers(reinterpret_cast<std::uintptr_t>(head)).find("\"items\":[]")!=std::string::npos,"empty modifier list invented values");
    Check(ActorNumber(std::numeric_limits<float>::quiet_NaN()).find("\"value\":null")!=std::string::npos,"nonfinite actor value became JSON number");
    UInt32 form=0;std::string reason;
    Check(ParseActorTarget("player",form,reason) && form==0x14,"explicit player target was lost");
    for(const auto* target:{"FalloutNV.esm\t01000014","../FalloutNV.esm\t000014","FalloutNV.esm\t000000","player\ncmd"})
        Check(!ParseActorTarget(target,form,reason),"invalid actor target accepted");
    Check(ActorEffects(0).find("unavailable")!=std::string::npos,"missing actor effects became empty");
    Check(ActorHitRecord(nullptr,0x14).find("unavailable")!=std::string::npos,"missing hit record became zero damage");
}
std::vector<std::string> g_effectFixtureRows;
void TestActorEffectResolver() {
    using A=ActorEffectAvailability;
    struct Case {const char* name;bool player;A availability;const char* reason;};
    const Case cases[]={
        {"player",true,A::Ready,nullptr},{"npc",false,A::Ready,nullptr},
        {"player-empty",true,A::Ready,nullptr},{"npc-empty",false,A::Ready,nullptr},
        {"no-process",false,A::NotResident,"actor-process-not-resident"},
        {"unreadable-process",false,A::Unavailable,"effect-actor-identity-or-fields-unavailable"},
        {"lower-tier",false,A::Unavailable,"effect-process-tier-unsupported"},
        {"tampered-player",true,A::Unavailable,"effect-list-getter-prefix-mismatch"},
        {"tampered-npc",false,A::Unavailable,"effect-list-getter-prefix-mismatch"},
        {"tampered-helper",false,A::Unavailable,"effect-process-accessor-unverified"},
        {"tampered-process-getter",false,A::Unavailable,"effect-process-getter-unverified"},
        {"wrong-slot",false,A::Unavailable,"effect-process-getter-unverified"},
        {"foreign-code",false,A::Unavailable,"effect-list-getter-prefix-mismatch"},
        {"crossing-code-page",false,A::Unavailable,"effect-list-getter-prefix-mismatch"},
        {"changed-base",false,A::Unavailable,"effect-list-identity-changed"},
        {"changed-process",false,A::Unavailable,"effect-list-identity-changed"},
        {"actor-overflow",false,A::Unavailable,"effect-actor-identity-or-fields-unavailable"},
        {"list-overflow",false,A::Unavailable,"effect-list-pointer-unreadable"},
        {"unreadable-list",false,A::Unavailable,"effect-list-pointer-unreadable"},
        {"partial-getter",false,A::Unavailable,"effect-list-getter-prefix-mismatch"},
        {"creature",false,A::Ready,nullptr},{"creature-empty",false,A::Ready,nullptr},
        {"creature-no-process",false,A::NotResident,"actor-process-not-resident"},
        {"creature-wrong-base-type",false,A::Unavailable,"effect-actor-identity-or-fields-unavailable"},
        {"creature-player-identity",false,A::Unavailable,"effect-actor-identity-or-fields-unavailable"},
        {"creature-changed-type",false,A::Unavailable,"effect-list-identity-changed"},
        {"creature-unknown-getter",false,A::Unavailable,"effect-list-getter-prefix-mismatch"},
        {"creature-wrong-process-table",false,A::Unavailable,"effect-process-vtable-unverified"}
    };
    for(const auto& item:cases) {
        const std::string name=item.name;
        const bool creature=name.rfind("creature",0)==0;
        const bool empty=name=="player-empty" || name=="npc-empty" || name=="creature-empty";
        const bool noProcess=name=="no-process" || name=="creature-no-process";
        const std::uintptr_t actor=name=="actor-overflow"?0xFFFFFFF0u:0x20000u;
        const UInt32 base=0x30000,process=name=="list-overflow"?0xFFFFFF00u:0x40000u,head=0x50000;
        const UInt32 id=(item.player || name=="creature-player-identity")?0x14:0x104C0F,baseId=item.player?7:0x104C0C;
        const UInt32 table=item.player?0x0108A9A4:0x010869D4,getter=item.player?0x939710:0x8C4090;
        std::map<std::uint64_t,std::vector<std::uint8_t>> memory;
        auto word=[&](std::uint64_t address,UInt32 value) {auto& block=memory[address];block.resize(4);memcpy(block.data(),&value,4);};
        auto code=[&](UInt32 address,const char* captured) {
            auto digit=[](char c){return c<='9'?c-'0':c-'a'+10;};
            auto& block=memory[address];
            for(size_t i=0;captured[i];i+=2)block.push_back(static_cast<std::uint8_t>((digit(captured[i])<<4)|digit(captured[i+1])));
            if(block.size()<32)block.resize(32,0xCC);
        };
        // Independent retained bytes: PC023 effect-getter-live (player/NPC), PC024 loaded-effect-dependencies (accessor/process getter).
        code(0x939710,"558bec51894dfc8b45fc8b807c0100008be55dc3");
        code(0x8C4090,"558bec83ec08894dfc8b4dfc81e994000000e87944010085c074258b4dfc81e994000000e8674401008945f88b45f88b108b4df88b82b8030000ffd0eb04eb0233c08be55dc3");
        code(0x8D8520,"558bec51894dfc8b45fc8b40688be55dc3");
        code(0x8D8100,"558bec51894dfc8b45fc8b80b80100008be55dc3");
        auto& reference=memory[actor];reference.resize(0x24);reference[4]=creature?0x3C:0x3B;
        const UInt32 actorTable=0x01086A6C;memcpy(reference.data(),&actorTable,4);memcpy(reference.data()+12,&id,4);memcpy(reference.data()+0x20,&base,4);
        auto& baseHeader=memory[base];baseHeader.resize(16);baseHeader[4]=creature && name!="creature-wrong-base-type"?0x2B:0x2A;memcpy(baseHeader.data()+12,&baseId,4);
        word(std::uint64_t(actor)+0x94,table);word(std::uint64_t(table)+8,getter);
        word(std::uint64_t(actor)+0x68,noProcess?0:process);
        word(process,name=="creature-wrong-process-table"?0x01087868:0x01087864);word(std::uint64_t(process)+0x28,name=="lower-tier"?3:0);
        word(0x01087C1C,name=="wrong-slot"?0x8D8110:0x8D8100);
        const auto listField=item.player?std::uint64_t(actor)+0x210:std::uint64_t(process)+0x1B8;
        word(listField,empty?0:head);
        if(name=="tampered-player" || name=="tampered-npc")memory[getter][0]^=1;
        if(name=="tampered-helper")memory[0x8D8520][12]^=1;
        if(name=="tampered-process-getter")memory[0x8D8100][12]^=1;
        if(name=="creature-unknown-getter")word(std::uint64_t(table)+8,0x8C4100);
        if(name=="partial-getter")memory[getter].resize(32);
        if(name=="unreadable-process")memory.erase(std::uint64_t(actor)+0x68);
        if(name=="unreadable-list")memory.erase(listField);
        unsigned actorReads=0;bool wrappedRead=false,listRead=false;
        auto read=[&](std::uintptr_t address,void* destination,size_t length) {
            if(address<0x10000)wrappedRead=true;
            if(address==actor && length==0x24 && ++actorReads==2) {
                if(name=="changed-base") {const UInt32 changed=base+4;memcpy(memory[actor].data()+0x20,&changed,4);}
                if(name=="creature-changed-type") {memory[actor][4]=0x3B;memory[base][4]=0x2A;}
                if(name=="changed-process")word(std::uint64_t(actor)+0x68,process+4);
            }
            if(address==listField)listRead=true;
            const auto found=memory.find(address);
            if(found==memory.end() || found->second.size()<length)return false;
            memcpy(destination,found->second.data(),length);return true;
        };
        auto mapped=[&](std::uintptr_t address,size_t length,bool executable) {
            if(executable && address==getter && (name=="foreign-code" || (name=="crossing-code-page" && length>32)))return false;
            return address>=0x400000 && std::uint64_t(address)+length<=0x01100000;
        };
        const auto result=ReadActorEffectList(true,actor,id,baseId,read,mapped);
        Check(result.availability==item.availability,"effect resolver availability differs");
        Check((!item.reason && !result.reason) || (item.reason && result.reason && !strcmp(item.reason,result.reason)),"effect resolver reason differs");
        Check(!wrappedRead,"effect resolver attempted an overflowing address");
        if(item.availability==A::Ready) {
            Check(result.identityStable && listRead,"effect head was not independently read with stable identity");
            Check(result.head==(empty?0:head),"effect pointer became an inline list or lost null");
            if(!item.player)Check(result.process==process && result.tierRead && result.tier==0 && result.processTable==0x01087864 &&
                result.processGetter==0x8D8100,"NPC process provenance differs");
        }
        if(noProcess)Check(result.identityStable && result.processRead && result.process==0 && !result.tierRead && !listRead,
            "absent process became an observed empty list");
        if(name=="unreadable-process")Check(!result.processRead && result.getterRead && !result.prefix.empty(),"unreadable process lost getter evidence or became zero");
        if(name=="partial-getter" || name=="crossing-code-page")Check(result.prefix.size()==64,"failed full-body admission lost actual prefix diagnostics");
        if(name=="tampered-player" || name=="tampered-npc")Check(result.getterRead && result.getter==getter &&
            result.prefix.size()==64 && result.prefix.substr(0,2)=="54","getter mismatch lost its actual nonzero address/prefix");
        if(name=="lower-tier" || name=="wrong-slot" || name=="creature-wrong-process-table")Check(!listRead,"unsupported process reached list memory");
        if(creature && item.availability!=A::Unavailable) {
            Check(result.referenceType==0x3C && result.baseType==0x2B &&
                std::string(result.route)=="creature-high-process-direct","creature provenance mislabeled as NPC");
            auto changed=result;changed.referenceType=0x3B;changed.baseType=0x2A;
            Check(!SameActorEffectList(result,changed),"effect traversal ignored a paired type change");
        }
        const auto observation=item.availability==A::Ready?"{\"status\":\"resolved\",\"head\":"+std::to_string(result.head)+ActorEffectProvenance(result)+"}":ActorEffectFailure(result);
        if(item.availability!=A::Ready)Check(observation.find("\"items\"")==std::string::npos,"unavailable resolver invented empty items");
        g_effectFixtureRows.push_back("{\"case\":"+Quote(item.name)+",\"observation\":"+observation+"}");
    }
}
std::vector<std::string> g_modifierSelectorFixtureRows;
void TestActorModifierSelectors() {
    struct Case {const char* name;const char* reason;};
    const Case cases[]={
        {"zero",nullptr},{"nonzero",nullptr},{"all-snapshot-codes",nullptr},
        {"unverified-profile","modifier-profile-unverified"},{"wrong-executable","modifier-profile-unverified"},
        {"npc","npc-modifier-selector-route-unavailable"},{"absent-actor","modifier-actor-identity-or-owner-unavailable"},
        {"wrong-reference","modifier-actor-identity-or-owner-unavailable"},{"wrong-base","modifier-actor-identity-or-owner-unavailable"},
        {"wrong-type","modifier-actor-identity-or-owner-unavailable"},{"wrong-owner-table","modifier-actor-identity-or-owner-unavailable"},
        {"wrong-wrapper-slot","modifier-actor-identity-or-owner-unavailable"},
        {"tampered-lookup","modifier-routine-unverified"},{"tampered-wrapper","modifier-routine-unverified"},
        {"partial-lookup","modifier-routine-unverified"},{"foreign-code","modifier-routine-unverified"},
        {"changed-base","modifier-identity-changed"},{"changed-owner","modifier-identity-changed"},
        {"changed-code","modifier-routine-changed"},{"unreadable-value",nullptr},{"nonfinite-value",nullptr},{"changed-value",nullptr},
        {"invalid-av","modifier-actor-value-out-of-range"},{"too-many-values","modifier-sample-bound-exceeded"},
        {"actor-overflow","modifier-actor-identity-or-owner-unavailable"},{"invalid-selector",nullptr}
    };
    for(const auto& item:cases) {
        const std::string name=item.name;
        const UInt32 actor=name=="absent-actor"?0:name=="actor-overflow"?0xFFFFFFF0u:0x20000u,base=0x30000,table=0x0108A974;
        std::map<std::uint64_t,std::vector<std::uint8_t>> memory;
        auto word=[&](std::uint64_t address,UInt32 value) {auto& block=memory[address];block.resize(4);memcpy(block.data(),&value,4);};
        auto code=[&](UInt32 address,const char* captured) {
            auto digit=[](char c){return c<='9'?c-'0':c-'a'+10;};
            auto& block=memory[address];
            for(size_t i=0;captured[i];i+=2)block.push_back(static_cast<std::uint8_t>((digit(captured[i])<<4)|digit(captured[i+1])));
        };
        // Independent fixture bytes: PC025 loaded-modifier-lookups and PC007 actor-av-routines.
        code(0x94C3D0,"558bec83ec10894df8d9eed95dfc8b45088945f4837df400743c837df4017448837df4027402eb508b4d0c894df0837df0107402eb0e8b55f8d982ac040000d95dfceb108b450c8b4df8d98481b0040000d95dfceb228b550c8b45f8d9849044020000d95dfceb108b4d0c8b55f8d9848a78030000d95dfcd945fc8be55dc20800");
        code(0x94C460,"558bec51894dfc8b4508506a008b4dfc81e9a4000000e855ffffff8be55dc20400");
        code(0x94C490,"558bec51894dfc8b4508506a028b4dfc81e9a4000000e825ffffff8be55dc20400");
        code(0x94C4C0,"558bec51894dfc8b4508506a018b4dfc81e9a4000000e8f5feffff8be55dc20400");
        auto& reference=memory[actor];reference.resize(0x24);reference[4]=name=="wrong-type"?0x3C:0x3B;
        UInt32 id=name=="wrong-reference"?0x15:0x14,baseId=name=="wrong-base"?8:7,actorTable=0x01086A6C;
        memcpy(reference.data(),&actorTable,4);memcpy(reference.data()+12,&id,4);memcpy(reference.data()+0x20,&base,4);
        auto& baseHeader=memory[base];baseHeader.resize(16);baseHeader[4]=0x2A;memcpy(baseHeader.data()+12,&baseId,4);
        word(std::uint64_t(actor)+0xA4,name=="wrong-owner-table"?table+4:table);
        const UInt32 slots[]={0x94C460,name=="wrong-wrapper-slot"?0x94C4C0u:0x94C490u,0x94C4C0};
        auto& slotBytes=memory[table+16];slotBytes.resize(sizeof(slots));memcpy(slotBytes.data(),slots,sizeof(slots));
        std::vector<UInt32> codes={16,7,0,76};
        if(name=="all-snapshot-codes")codes={5,6,7,8,9,10,11,14,16,18,34,35,38,41,45,76};
        // Storage fixture is independent of the production address helper, including the Health special field.
        for(UInt32 selector=0;selector<3;++selector)for(const auto av:codes) {
            const auto offset=selector==0?0x244+4*av:selector==1?0x378+4*av:av==16?0x4AC:0x4B0+4*av;
            const float value=name=="zero"?0.0f:static_cast<float>(selector*100+av+1)*(selector==1?-1.0f:1.0f);
            UInt32 raw=0;memcpy(&raw,&value,4);word(std::uint64_t(actor)+offset,raw);
        }
        const auto health=std::uint64_t(actor)+0x284;
        if(name=="unreadable-value")memory.erase(health);
        if(name=="nonfinite-value")word(health,0x7FC00000);
        if(name=="tampered-lookup")memory[0x94C3D0][72]^=1;
        if(name=="tampered-wrapper")memory[0x94C490][12]^=1;
        if(name=="partial-lookup")memory[0x94C3D0].resize(128);
        if(name=="invalid-av")codes={77};
        if(name=="too-many-values")codes.resize(17,16);
        unsigned reads=0,actorReads=0,healthReads=0,lookupReads=0;bool wrappedRead=false;
        auto read=[&](std::uintptr_t address,void* destination,size_t length) {
            ++reads;if(address<0x10000)wrappedRead=true;
            if(address==actor && length==0x24 && ++actorReads==2) {
                if(name=="changed-base") {const UInt32 other=base+4;memcpy(memory[actor].data()+0x20,&other,4);}
                if(name=="changed-owner")word(std::uint64_t(actor)+0xA4,table+4);
            }
            if(address==0x94C3D0 && ++lookupReads==2 && name=="changed-code")memory[0x94C3D0][0]^=1;
            if(address==health && ++healthReads==2 && name=="changed-value")word(health,0);
            const auto found=memory.find(address);
            if(found==memory.end() || found->second.size()<length)return false;
            memcpy(destination,found->second.data(),length);return true;
        };
        auto mapped=[&](std::uintptr_t address,size_t length,bool executable) {
            return !(name=="foreign-code" && executable && address==0x94C3D0) &&
                address>=0x400000 && std::uint64_t(address)+length<=0x01100000;
        };
        const std::string executable=name=="wrong-executable"?std::string(64,'0'):"3a87f92f011e5dc9179ddf733cf08be2b39ea6e5b7a8a9e3a9a72dafcc1b104d";
        const auto result=ReadActorModifierSelectors(name!="unverified-profile",executable,actor,name=="npc"?0x104C0F:0x14,7,
            codes.data(),codes.size(),read,mapped);
        Check((!item.reason && !result.reason) || (item.reason && result.reason && !strcmp(item.reason,result.reason)),"modifier selector admission differs");
        Check(!wrappedRead,"modifier resolver attempted zero or wrapped memory");
        if(name=="unverified-profile" || name=="wrong-executable" || name=="npc" || name=="invalid-av" || name=="too-many-values")
            Check(reads==0 && result.samples.empty(),"rejected modifier request reached memory");
        const bool partial=name=="unreadable-value" || name=="nonfinite-value" || name=="changed-value";
        if(!item.reason) {
            Check(result.identityStable && result.samples.size()==codes.size()*3,"modifier sample scope or identity lost");
            for(const auto& sample:result.samples) {
                if(partial && sample.selector==0 && sample.code==16) {
                    const char* expected=name=="unreadable-value"?"modifier-field-unreadable":name=="nonfinite-value"?"modifier-value-nonfinite":"modifier-value-changed";
                    Check(sample.reason && !strcmp(sample.reason,expected),"invalid modifier value became observed");
                } else Check(!sample.reason && sample.read && sample.repeatRead,"readable modifier became unavailable");
                if(sample.selector==2 && sample.code==16)Check(sample.address==actor+0x4AC,"Health selector2 used ordinary indexed field");
                if(name=="zero")Check(sample.raw==0 && !sample.reason,"observed zero became absence");
            }
            if(name=="nonzero") {
                // Explicit addresses/value observations discriminate all three routes and both AV bounds.
                const UInt32 offsets[]={0x284,0x260,0x244,0x374,0x3B8,0x394,0x378,0x4A8,0x4AC,0x4CC,0x4B0,0x5E0};
                const float values[]={17,8,1,77,-117,-108,-101,-177,217,208,201,277};
                for(size_t i=0;i<std::size(offsets);++i) {
                    float actual=0;memcpy(&actual,&result.samples[i].raw,4);
                    Check(result.samples[i].address==actor+offsets[i] && actual==values[i],"selector chose wrong physical field/value");
                }
            }
        } else for(const auto& sample:result.samples)Check(sample.reason,"changed owner/code retained observed value");
        if(name=="tampered-lookup")Check(result.lookupHex.size()==258 && result.lookupHex.substr(144,2)=="4c","mismatched lookup lost observed bytes");
        if(name=="tampered-wrapper")Check(result.wrapperHex[1].size()==66 && result.wrapperHex[1].substr(24,2)=="03","mismatched wrapper lost observed bytes");
        if(name=="invalid-selector") {
            UInt32 address=123;
            Check(!ActorModifierAddress(actor,3,16,address) && address==123,"unsupported selector reached storage");
            Check(!ActorModifierAddress(0xFFFFFF00u,0,0,address),"overflowing selector storage admitted");
        }
        const auto json=ActorModifierSelectorsJson(result);
        Check(json.size()<12000,"bounded modifier observation exceeded its payload allocation");
        Check(json.find("\"semanticStatus\":\"raw-selectors\"")!=std::string::npos,"raw selector provenance missing");
        if(partial)Check(json.find("\"status\":\"partial\"")==1 && json.find("\"value\":null")!=std::string::npos,"partial JSON promoted unavailable value");
        if(item.reason)Check(json.find("\"status\":\"unavailable\"")==1,"rejected JSON promoted observation");
        if(name=="zero")Check(json.find("\"value\":0")!=std::string::npos && json.find("\"rawHex\":\"00000000\"")!=std::string::npos,"zero JSON lost raw value");
        g_modifierSelectorFixtureRows.push_back("{\"case\":"+Quote(item.name)+",\"observation\":"+json+"}");
    }
}
void TestMenuActions() {
    MenuChoiceRequest choice;std::string reason;
    Check(ParseMenuChoice("choose\tvisible\t0\tCaravan Pack items added to inventory.\tOK",choice),"exact visible menu request rejected");
    RuntimeMenuState state;state.text=choice.text;state.label=choice.label;state.callback=1234;
    g_captureGeneration=44;g_capture=true;g_connected=true;
    Check(MatchMenuChoice(choice,state,44,reason),"exact visible menu mismatch");
    for(const auto* payload:{"choose\tvisible\t1\tText\tOK","choose\tvisible\t0\t\tOK", "choose\tprobe\t0\tText\tOK\tExtra",
        "choose\tunknown\t0\tText\tOK","choose\tvisible\t0\tText\nCommand\tOK"})
        Check(!ParseMenuChoice(payload,choice),"invalid menu protocol request accepted");
    choice={false,"Text","OK"};state.text="Other";
    Check(!MatchMenuChoice(choice,state,44,reason),"wrong message text accepted");
    state.text="Text";state.label="Cancel";
    Check(!MatchMenuChoice(choice,state,44,reason),"wrong button label accepted");
    state.label="OK";state.callback=reinterpret_cast<UInt32>(MessageProbeCallback);
    Check(!MatchMenuChoice(choice,state,44,reason),"probe callback selected as generic message");
    choice.probe=true;g_pendingMessageProbe={true,7,44,3};
    Check(MatchMenuChoice(choice,state,44,reason),"owned current probe rejected");
    for(const auto generation:{43ull,45ull})
        Check(!MatchMenuChoice(choice,state,generation,reason),"stale probe generation accepted");
    g_pendingMessageProbe.pending=false;
    Check(!MatchMenuChoice(choice,state,44,reason),"dismissed probe owner accepted");
    g_pendingMessageProbe={true,7,44,3};state.callback=1234;
    Check(!MatchMenuChoice(choice,state,44,reason),"unrelated callback accepted for probe");
    const auto original=state;state.queued=55;
    Check(!(state==original),"changed queued identity was ignored");
    std::string target;unsigned value=0;
    Check(ParseActorSet("FalloutNV.esm\t104C0F\tEndurance\t4",target,value) && target=="FalloutNV.esm\t104C0F" && value==4,
        "explicit actor target was lost during mutation parsing");
    for(const auto* payload:{"player\tHealth\t5","player\tEndurance\t0","player\tEndurance\t11","player\tEndurance\t4.5","player\tEndurance\t5\nKill"})
        Check(!ParseActorSet(payload,target,value),"unbounded actor mutation accepted");
    g_pendingMessageProbe={};
}
struct MenuFixtureMemory {
    std::map<std::uintptr_t,std::vector<unsigned char>> blocks;
    template<class T> void Put(std::uintptr_t address,const T& value) {
        const auto* bytes=reinterpret_cast<const unsigned char*>(&value);
        blocks[address]=std::vector<unsigned char>(bytes,bytes+sizeof(value));
    }
    template<class T> bool Read(std::uintptr_t address,T& value) const {
        const auto found=blocks.find(address);
        if(found==blocks.end() || found->second.size()!=sizeof(value))return false;
        memcpy(&value,found->second.data(),sizeof(value));return true;
    }
};
size_t g_menuInspectionCases=0;
std::vector<std::string> g_menuFixtureEvents;
void TestMenuInspection() {
    using A=MenuAvailability;
    struct Case {const char* name;A availability;const char* check;};
    const Case cases[]={
        {"ready",A::Ready,""},{"hidden",A::Unavailable,"message-not-visible"},
        {"ui-absent",A::Unavailable,"message-ui-absent"},{"visibility-invalid",A::Invalid,"message-visibility-invalid"},
        {"stack-empty",A::Unavailable,"message-stack-empty"},{"stack-owner",A::Invalid,"message-stack-owner-mismatch"},
        {"stack-extra",A::Invalid,"message-stack-not-exclusive"},{"registry-empty",A::Unavailable,"message-registry-empty"},
        {"registry-empty-inactive",A::Unavailable,"message-registry-empty"},
        {"registry-empty-bad-capacity",A::Invalid,"message-registry-bounds-invalid"},
        {"registry-bad-end",A::Invalid,"message-registry-bounds-invalid"},{"registry-bad-count",A::Invalid,"message-registry-bounds-invalid"},
        {"registry-null-data",A::Invalid,"message-registry-data-missing"},{"root-absent",A::Unavailable,"message-root-absent"},
        {"owner-null",A::Invalid,"message-menu-owner-missing"},{"owner-mismatch",A::Invalid,"message-tile-owner-mismatch"},
        {"type-mismatch",A::Invalid,"message-menu-type-mismatch"},{"vtable-null",A::Invalid,"message-menu-vtable-missing"},
        {"handler-mismatch",A::Invalid,"message-handler-mismatch"},{"queue-empty",A::Unavailable,"message-queue-empty"},
        {"callback-null",A::Invalid,"message-callback-missing"},
        {"empty-stack-invalid-owner",A::Invalid,"message-tile-owner-mismatch"},
        {"empty-stack-bad-registry",A::Invalid,"message-registry-bounds-invalid"},
        {"root-overflow",A::Unreadable,"message-menu-owner-unreadable"},{"vtable-overflow",A::Unreadable,"message-handler-unreadable"},
        {"unreadable-ui",A::Unreadable,"message-ui-unreadable"},{"unreadable-visible",A::Unreadable,"message-visibility-unreadable"},
        {"unreadable-stack",A::Unreadable,"message-stack-unreadable"},{"unreadable-registry",A::Unreadable,"message-registry-unreadable"},
        {"unreadable-root",A::Unreadable,"message-root-unreadable"},{"unreadable-owner",A::Unreadable,"message-menu-owner-unreadable"},
        {"unreadable-menu",A::Unreadable,"message-menu-unreadable"},{"unreadable-handler",A::Unreadable,"message-handler-unreadable"},
        {"unreadable-callback",A::Unreadable,"message-callback-unreadable"}
    };
    const auto savedGeneration=g_captureGeneration.load();const bool savedCapture=g_capture,savedConnected=g_connected;
    g_captureGeneration=500;g_capture=true;g_connected=true;
    for(const auto& test:cases) {
        const std::string name=test.name;
        UInt32 ui=0x1000,registry[4]{0,0x2000,0x00010001,1},stack[10]{1001},tile=0x3000,owner=0x4000,
            menu[16]{0x5000,0x3000},handler=0x7AA070,queued=0x6000,callback=0x7000;
        unsigned char visible=1;menu[8]=1001;
        if(name=="hidden")visible=0;
        if(name=="ui-absent")ui=0;
        if(name=="visibility-invalid")visible=2;
        if(name=="stack-empty" || name=="empty-stack-invalid-owner" || name=="empty-stack-bad-registry")stack[0]=0;
        if(name=="stack-owner")stack[0]=1002;
        if(name=="stack-extra")stack[1]=1002;
        if(name=="registry-empty") {registry[1]=0;registry[2]=0;registry[3]=0;}
        if(name=="registry-empty-inactive") {registry[3]=0;menu[1]=0x3100;handler=0x7AA074;}
        if(name=="registry-empty-bad-capacity") {registry[2]=4097;registry[3]=0;}
        if(name=="registry-bad-end")registry[2]=0x00020001;
        if(name=="registry-bad-count" || name=="empty-stack-bad-registry")registry[3]=2;
        if(name=="registry-null-data")registry[1]=0;
        if(name=="root-absent")tile=0;
        if(name=="root-overflow")tile=0xFFFFFFF0;
        if(name=="owner-null")owner=0;
        if(name=="owner-mismatch" || name=="empty-stack-invalid-owner")menu[1]=0x3100;
        if(name=="type-mismatch")menu[8]=1002;
        if(name=="vtable-null")menu[0]=0;
        if(name=="vtable-overflow")menu[0]=0xFFFFFFFC;
        if(name=="handler-mismatch")handler=0x7AA074;
        if(name=="queue-empty")queued=0;
        if(name=="callback-null")callback=0;
        MenuFixtureMemory memory;
        memory.Put(0x11D8A80,ui);memory.Put(0x11F3478,visible);memory.Put(0x1114,stack);memory.Put(0x11F3508,registry);
        memory.Put(0x2000,tile);memory.Put(0x303C,owner);memory.Put(0x4000,menu);memory.Put(0x500C,handler);memory.Put(0x6028,callback);
        const std::pair<const char*,std::uintptr_t> faults[]={
            {"unreadable-ui",0x11D8A80},{"unreadable-visible",0x11F3478},{"unreadable-stack",0x1114},
            {"unreadable-registry",0x11F3508},{"unreadable-root",0x2000},{"unreadable-owner",0x303C},
            {"unreadable-menu",0x4000},{"unreadable-handler",0x500C},{"unreadable-callback",0x6028}};
        for(const auto& fault:faults)if(name==fault.first)memory.blocks.erase(fault.second);
        auto read=[&](std::uintptr_t address,auto& value){return memory.Read(address,value);};
        RuntimeMenuState state;MenuInspection inspection;
        const bool ready=ReadMenuLayout(read,state,inspection) && ReadMenuOwner(read,queued,state,inspection);
        if(ready!=(test.availability==A::Ready) || inspection.availability!=test.availability || inspection.failedCheck!=test.check)
            throw std::runtime_error("Menu inspection case failed: "+name+" actual="+inspection.failedCheck);
        Check(!inspection.fields.empty(),"menu inspection lost observed reads");
        if(name=="registry-empty-inactive")Check(std::none_of(inspection.fields.begin(),inspection.fields.end(),
            [](const MenuReadField& field){return std::string(field.name)=="rootTile";}),"inactive registry slot was treated as a current owner");
        for(const auto& field:inspection.fields)Check(field.readable?!field.values.empty():field.values.empty(),"unreadable bytes became zero values");
        if(test.availability==A::Unreadable)Check(!inspection.fields.back().readable,"unreadable failure lacks its failed read");
        if(ready) {
            Check(state.tile==tile && state.menu==owner && state.queued==queued && state.callback==callback && state.handler==handler,
                "ready menu lost physical owner identity");
            g_menuFixtureEvents.push_back("{\"case\":"+Quote(name)+MenuInspectionFields(inspection)+"}");
        } else {
            for(const bool inspect:{true,false}) {
                g_events.clear();Request request{13,71,inspect?"inspect":"choose\tvisible\t0\tText\tOK"};
                EmitMenuReadFailure(request,inspection,500);
                Check(g_events.size()==1,"menu failure emitted multiple outcomes");
                const bool unavailable=inspect && test.availability==A::Unavailable;
                Check(g_events[0].json.find(unavailable?"\"kind\":\"message-state\"":"\"kind\":\"error\"")!=std::string::npos,
                    "menu absence admitted a choice or structural failure became unavailable");
                Check(g_events[0].json.find("\"requestId\":71")!=std::string::npos &&
                    g_events[0].json.find("\"menuInspection\":")!=std::string::npos,"menu failure lost request or diagnostics");
                if(unavailable)Check(g_events[0].json.find(name=="hidden"?"menu-not-visible":"menu-not-ready")!=std::string::npos,
                    "unavailable menu reason changed");
                else {
                    const char* legacy=name=="hidden" || name=="visibility-invalid"?"visible-message-menu-unavailable":
                        name=="ui-absent" || name=="unreadable-ui" || name=="unreadable-visible"?"message-visibility-unreadable":
                        name=="queue-empty" || name=="callback-null" || name=="unreadable-callback"?"message-owner-unavailable":
                        "visible-message-menu-layout-or-owner-mismatch";
                    Check(g_events[0].json.find("\"error\":"+Quote(legacy))!=std::string::npos,"legacy menu error category changed");
                }
                g_menuFixtureEvents.push_back("{\"case\":"+Quote(name)+",\"inspect\":"+(inspect?"true":"false")+",\"event\":"+g_events[0].json+"}");
            }
        }
        ++g_menuInspectionCases;
    }
    // The real emitter must also refuse to leak a delayed inspection into a fresh capture.
    MenuInspection absent;absent.Fail("message-queue-empty",A::Unavailable);g_events.clear();
    ++g_captureGeneration;EmitMenuReadFailure({13,71,"inspect"},absent,500);
    Check(g_events.empty(),"stale inspection crossed capture generation");++g_menuInspectionCases;
    g_captureGeneration=savedGeneration;g_capture=savedCapture;g_connected=savedConnected;g_events.clear();
}
void* g_operationOwner=nullptr;
void* g_operationOther=nullptr;
bool g_operationCombat=false,g_operationUnavailable=false;
double g_operationPosition=0,g_inventoryCount=0;
int g_operationMutations=0;
void* LookupTypedTestForm(UInt32 id){return id==0x14?g_operationOwner:id==0x104C0F?g_operationOther:nullptr;}
void* LookupMissingTypedForm(UInt32){return nullptr;}
bool CallTypedTestFunction(Script* script,TESObjectREFR* calling,TESObjectREFR*,NumericElement* result,std::uint8_t count,...) {
    Check(count==3,"typed function argument count changed");
    va_list arguments;va_start(arguments,count);
    auto owner=va_arg(arguments,void*);auto other=va_arg(arguments,void*);auto bits=va_arg(arguments,UInt32);va_end(arguments);
    Check(owner==g_operationOwner && (!other || other==g_operationOther),"typed function lost resolved reference identity");
    UInt32 id=0;std::uint8_t type=0;Check(ReadFormIdentity(owner,id,type),"fixture owner identity missing");
    Check(calling==(type>=0x3A && type<=0x3C?owner:nullptr),"typed function lost calling-reference context or treated a quest as a reference");
    float number=0;memcpy(&number,&bits,4);
    const auto& body=*reinterpret_cast<std::string*>(script);
    result->type=1;result->number=0;
    if(g_operationUnavailable){result->type=0;return true;}
    if(body.find("SetFunctionValue (owner.StartCombat other)")!=std::string::npos){Check(other!=nullptr,"combat target missing");g_operationCombat=true;++g_operationMutations;}
    else if(body.find("SetFunctionValue (owner.StopCombat)")!=std::string::npos){g_operationCombat=false;++g_operationMutations;}
    else if(body.find("SetFunctionValue (owner.SetPos X number)")!=std::string::npos){g_operationPosition=number;++g_operationMutations;}
    else if(body.find("owner.IsInCombat")!=std::string::npos)result->number=g_operationCombat?1:0;
    else if(body.find("owner.GetPos X")!=std::string::npos)result->number=g_operationPosition;
    else if(body.find("SetFunctionValue (number)")!=std::string::npos)result->number=number;
    else if(body.find("owner.GetNumItems")!=std::string::npos)result->number=g_inventoryCount;
    else if(body.find("owner.GetCombatTarget")!=std::string::npos){result->type=2;result->form=g_operationCombat?g_operationOther:nullptr;}
    else if(body.find("owner.GetParentCell")!=std::string::npos || body.find("owner.GetEquippedObject")!=std::string::npos || body.find("owner.GetInventoryObject")!=std::string::npos){result->type=2;result->form=nullptr;}
    return true;
}
void TestTypedOperations() {
    TypedOperation op;
    for(const auto& request:std::initializer_list<Request>{{15,1,"player"},{16,1,"start-combat\tAddon.esm\t123\t@player\t000014"},
        {16,1,"set-position\tFalloutNV.esm\t104C0F\tX\t-123.25"},{17,1,"add-item\t@player\t000014\tFalloutNV.esm\tF\t2"},
        {18,1,"set-quest-stage\tFalloutNV.esm\tE61A4\t0"},{18,1,"set-quest-objective\tAddon.esm\t123\t65535\tdisplayed\t0"}})
        Check(ParseTypedOperation(request,op),"valid typed operation rejected");
    for(const auto& request:std::initializer_list<Request>{{16,1,"start-combat\tFalloutNV.esm\t01000014\t@player\t000014"},
        {16,1,"stop-combat\t@player\t000015"},{16,1,"set-position\t@player\t000014\tX\tnan"},
        {16,1,"set-position\t@player\t000014\tZ\t5\t"},{16,1,"set-position\t@player\t000014\tX\t5\nKill"},
        {17,1,"add-item\t@player\t000014\tFalloutNV.esm\tF\t-1"},{17,1,"remove-item\t@player\t000014\tFalloutNV.esm\tF\t10001"},
        {18,1,"set-quest-stage\t@player\t000014\t10"},{18,1,"set-quest-objective\tAddon.esm\t123\t1\tunknown\t1"}})
        Check(!ParseTypedOperation(request,op),"malformed or unbounded operation accepted");
    UInt32 base[4]={0,0x2A,0,7},subjectBytes[9]={0,0x3B,0,0x14},otherBytes[9]={0,0x3B,0,0x104C0F};
    subjectBytes[8]=reinterpret_cast<UInt32>(base);otherBytes[8]=reinterpret_cast<UInt32>(base);
    TypedForm owner,other;
    Check(TypedValidateForm(subjectBytes,0x14,owner) && TypedValidateForm(otherBytes,0x104C0F,other),"typed actor/base validation failed");
    Check(!TypedValidateForm(subjectBytes,0x15,owner),"mismatched runtime form promoted");
    Check(TypedValidateForm(subjectBytes,0x14,owner),"owner not restored");
    owner.requested={"@player",0x14};other.requested={"FalloutNV.esm",0x104C0F};
    auto saved=g_script;const bool savedNumeric=g_numericEnabled;
    NVSEScriptInterface sdk{};sdk.CompileScript=CompileTestExpression;sdk.CallFunction=CallTypedTestFunction;g_script=&sdk;g_numericEnabled=true;
    g_operationFunctions.clear();g_operationOwner=owner.pointer;g_operationOther=other.pointer;
    g_capture=true;g_connected=true;g_captureGeneration=77;g_events.clear();
    Check(ParseTypedOperation({16,1,"start-combat\t@player\t000014\tFalloutNV.esm\t104C0F"},op),"combat fixture parse failed");
    TypedReferenceAction({16,1,""},op,owner,&other,77,LookupTypedTestForm);
    Check(g_operationMutations==1 && g_operationCombat && g_events.back().json.find("\"accepted\":true")!=std::string::npos &&
        g_events.back().json.find("\"IsInCombat\":{\"status\":\"observed\",\"value\":0}")!=std::string::npos &&
        g_events.back().json.find("\"IsInCombat\":{\"status\":\"observed\",\"value\":1}")!=std::string::npos,
        "typed combat did not preserve separate before/after observations");
    op.op="stop-combat";TypedReferenceAction({16,2,""},op,owner,nullptr,77,LookupTypedTestForm);
    Check(g_operationMutations==2 && !g_operationCombat,"stop combat did not target original actor");
    op.op="set-position";op.axis="X";
    for(const auto value:{-123.25,0.0,2295.96435546875}) {
        op.value=value;TypedReferenceAction({16,3,""},op,owner,nullptr,77,LookupTypedTestForm);
        Check(g_operationPosition==value && g_events.back().json.find("\"requestedValue\":"+NumberField(value))!=std::string::npos &&
            g_events.back().json.find("\"argumentEcho\":{\"status\":\"observed\",\"value\":"+NumberField(value)+"}")!=std::string::npos,
            "float parameter/readback lost sign, zero, precision or requested value");
    }
    const auto mutations=g_operationMutations;const auto events=g_events.size();
    TypedReferenceAction({16,4,""},op,owner,nullptr,76,LookupTypedTestForm);
    Check(g_operationMutations==mutations && g_events.size()==events,"stale generation mutated state or published");
    g_operationUnavailable=true;TypedReferenceAction({16,5,""},op,owner,nullptr,77,LookupTypedTestForm);
    Check(g_events.back().json.find("\"accepted\":false")!=std::string::npos && g_events.back().json.find("unavailable")!=std::string::npos,
        "missing SDK value became successful zero");g_operationUnavailable=false;
    TypedReferenceAction({16,7,""},op,owner,nullptr,77,LookupMissingTypedForm);
    Check(g_events.back().json.find("subject-identity-unavailable-after-action")!=std::string::npos &&
        g_events.back().json.find("\"afterIdentityResolved\":false")!=std::string::npos,"vanished owner received stale-pointer readback");
    op={};op.op="read-inventory";
    for(const auto count:{0,129}){
        g_inventoryCount=count;TypedInventory({17,6,""},op,owner,nullptr,77);
        Check(g_events.back().json.find(count?"\"status\":\"partial\"":"\"status\":\"complete\"")!=std::string::npos,
            "inventory limit or empty inventory status changed");
    }
    UInt32 questBytes[4]={0,0x47,0,0x123};TypedForm quest;
    Check(TypedValidateForm(questBytes,0x123,quest),"fixture quest identity invalid");
    g_operationOwner=quest.pointer;std::string reason;
    Check(TypedMutate("SetStage owner number",quest,nullptr,10,reason),"fixed quest expression rejected zero command result");
    g_script=saved;g_numericEnabled=savedNumeric;g_operationFunctions.clear();g_events.clear();
}
enum class SafetyFault { None, One, NoInterface, NoReturnType, NoEntry, UnreadableEntry, UnreadableName, WrongName,
    WrongOpcode, NoParent, HasParameters, NoExecute, NonExecutable, FormReturn, UnknownReturn, MissingTarget,
    WrongActor, WrongBase, ReplacedTarget, FailedCall, NonNumeric, NonFinite, NonBoolean,
    ChangedId, ChangedBase, ChangedCommand, ChangedReturnType, Stop, Disconnect, Restart, Reconnect };
struct SafetyFixture {
    CommandInfo command{};UInt32 returnType=0;
    UInt32 base[4]={0,0x2A,0,7},alternateBase[4]={0,0x2A,0,7};
    UInt32 actor[9]={0,0x3B,0,0x14},alternateActor[9]={0,0x3B,0,0x14};
    TypedForm owner;SafetyFault fault=SafetyFault::None;int calls=0;
};
SafetyFixture* g_safetyFixture=nullptr;
std::vector<std::string> g_safetyFixtureRows;
const CommandInfo* SafetyCommandByName(const char*) {
    return g_safetyFixture->fault==SafetyFault::NoEntry?nullptr:g_safetyFixture->fault==SafetyFault::UnreadableEntry?
        reinterpret_cast<const CommandInfo*>(1):&g_safetyFixture->command;
}
const CommandInfo* SafetyCommandByOpcode(UInt32 opcode) {
    return g_safetyFixture->fault==SafetyFault::WrongOpcode || opcode!=g_safetyFixture->command.opcode?nullptr:&g_safetyFixture->command;
}
UInt32 SafetyReturnType(const CommandInfo*){return g_safetyFixture->returnType;}
void* SafetyLookup(UInt32 id) {
    auto& f=*g_safetyFixture;
    if(f.fault==SafetyFault::MissingTarget || id!=f.owner.id)return nullptr;
    return f.fault==SafetyFault::ReplacedTarget?f.alternateActor:f.actor;
}
bool SafetyCall(Script* script,TESObjectREFR* calling,TESObjectREFR* containing,NumericElement* result,std::uint8_t count,...) {
    auto& f=*g_safetyFixture;++f.calls;
    Check(count==3 && calling==reinterpret_cast<TESObjectREFR*>(f.actor) && !containing,"safety getter lost explicit calling reference");
    va_list args;va_start(args,count);const auto owner=va_arg(args,void*);const auto other=va_arg(args,void*);const auto number=va_arg(args,UInt32);va_end(args);
    Check(owner==f.actor && !other && number==0,"safety getter lost bound owner or added arguments");
    const auto& text=*reinterpret_cast<std::string*>(script);
    Check(text.find(std::string("SetFunctionValue (owner.")+f.command.longName+")")!=std::string::npos &&
        text.find("; active PC opcode "+std::to_string(f.command.opcode))!=std::string::npos,"safety getter reused another command's compiled body");
    result->type=1;result->number=f.fault==SafetyFault::One?1:0;
    switch(f.fault) {
    case SafetyFault::FailedCall:return false;
    case SafetyFault::NonNumeric:result->type=2;break;
    case SafetyFault::NonFinite:result->number=std::numeric_limits<double>::infinity();break;
    case SafetyFault::NonBoolean:result->number=0.5;break;
    case SafetyFault::ChangedId:++f.actor[3];break;
    case SafetyFault::ChangedBase:f.actor[8]=reinterpret_cast<UInt32>(f.alternateBase);break;
    case SafetyFault::ChangedCommand:f.command.execute=OtherExecute;break;
    case SafetyFault::ChangedReturnType:f.returnType=1;break;
    case SafetyFault::Stop:g_capture=false;break;
    case SafetyFault::Disconnect:g_connected=false;break;
    case SafetyFault::Restart:++g_captureGeneration;break;
    case SafetyFault::Reconnect:++g_connectionGeneration;break;
    default:break;
    }
    return true;
}
void TestActorSafetyReadbacks() {
    g_safetyFixtureRows.clear();
    const auto savedCommands=g_commands;const auto savedScript=g_script;const bool savedNumeric=g_numericEnabled;
    const bool savedCapture=g_capture,savedConnected=g_connected;
    const auto savedGeneration=g_captureGeneration.load(),savedConnection=g_connectionGeneration.load();
    NVSECommandTableInterface commands{};commands.version=1;commands.GetByName=SafetyCommandByName;
    commands.GetByOpcode=SafetyCommandByOpcode;commands.GetReturnType=SafetyReturnType;
    NVSEScriptInterface script{};script.CompileScript=CompileTestExpression;script.CallFunction=SafetyCall;
    const std::pair<SafetyFault,const char*> cases[]={
        {SafetyFault::None,"zero"},{SafetyFault::One,"one"},{SafetyFault::NoInterface,"missing-interface"},
        {SafetyFault::NoReturnType,"missing-return-metadata"},{SafetyFault::NoEntry,"missing-command"},
        {SafetyFault::UnreadableEntry,"unreadable-command"},{SafetyFault::UnreadableName,"unreadable-name"},
        {SafetyFault::WrongName,"different-command"},{SafetyFault::WrongOpcode,"opcode-disagreement"},
        {SafetyFault::NoParent,"unbound-command"},{SafetyFault::HasParameters,"parameterized-command"},
        {SafetyFault::NoExecute,"missing-handler"},{SafetyFault::NonExecutable,"nonexecutable-handler"},
        {SafetyFault::FormReturn,"form-return"},{SafetyFault::UnknownReturn,"unknown-return"},
        {SafetyFault::MissingTarget,"missing-target"},{SafetyFault::WrongActor,"wrong-reference-type"},
        {SafetyFault::WrongBase,"wrong-base-type"},{SafetyFault::ReplacedTarget,"same-id-replaced-pointer"},
        {SafetyFault::FailedCall,"failed-call"},{SafetyFault::NonNumeric,"nonnumeric-value"},
        {SafetyFault::NonFinite,"nonfinite-value"},{SafetyFault::NonBoolean,"nonboolean-value"},
        {SafetyFault::ChangedId,"changed-form-id"},{SafetyFault::ChangedBase,"same-id-replaced-base"},
        {SafetyFault::ChangedCommand,"changed-handler"},{SafetyFault::ChangedReturnType,"changed-return-metadata"},
        {SafetyFault::Stop,"capture-stopped"},{SafetyFault::Disconnect,"disconnected"},
        {SafetyFault::Restart,"capture-restarted"},{SafetyFault::Reconnect,"connection-replaced"}};
    for(const bool player:{true,false}) for(const auto* name:{"GetDead","IsEssential"}) for(const auto& test:cases) {
        // The shared rejection path needs each fault once. Both names/owners still
        // exercise zero, one and warm-cache opcode remapping independently.
        if(test.first!=SafetyFault::None && test.first!=SafetyFault::One && (!player || strcmp(name,"GetDead")))continue;
        SafetyFixture f;g_safetyFixture=&f;f.fault=test.first;
        f.actor[3]=f.alternateActor[3]=player?0x14:0x01000802;
        f.base[3]=f.alternateBase[3]=player?7:0x01000800;
        f.actor[8]=f.alternateActor[8]=reinterpret_cast<UInt32>(f.base);
        Check(TypedValidateForm(f.actor,f.actor[3],f.owner),"safety fixture identity failed");
        f.owner.requested={player?"@player":"Control.esp",player?0x14u:0x802u};
        f.command.longName=name;f.command.opcode=!strcmp(name,"GetDead")?0x102E:0x1162;
        f.command.needsParent=1;f.command.execute=TestExecute;
        g_commands=&commands;commands.GetReturnType=SafetyReturnType;g_script=&script;g_numericEnabled=true;
        g_capture=true;g_connected=true;g_captureGeneration=301;g_connectionGeneration=501;g_operationFunctions.clear();
        switch(f.fault) {
        case SafetyFault::NoInterface:g_commands=nullptr;break;
        case SafetyFault::NoReturnType:commands.GetReturnType=nullptr;break;
        case SafetyFault::UnreadableName:f.command.longName=reinterpret_cast<const char*>(1);break;
        case SafetyFault::WrongName:f.command.longName="GetLevel";break;
        case SafetyFault::NoParent:f.command.needsParent=0;break;
        case SafetyFault::HasParameters:f.command.numParams=1;break;
        case SafetyFault::NoExecute:f.command.execute=nullptr;break;
        case SafetyFault::NonExecutable:f.command.execute=reinterpret_cast<CommandExecute>(&f.actor[0]);break;
        case SafetyFault::FormReturn:f.returnType=1;break;
        case SafetyFault::UnknownReturn:f.returnType=UINT32_MAX;break;
        case SafetyFault::WrongActor:f.actor[1]=0x3A;break;
        case SafetyFault::WrongBase:f.base[1]=0x2B;break;
        default:break;
        }
        const auto row=TypedActorSafety(name,f.owner,SafetyLookup);
        const bool success=f.fault==SafetyFault::None || f.fault==SafetyFault::One;
        const bool called=success || f.fault>=SafetyFault::FailedCall;
        Check(f.calls==(called?1:0),"safety getter called an unadmitted target/command or repeated a call");
        Check(row.find(success?"\"status\":\"observed\"":"\"status\":\"unavailable\"")!=std::string::npos,
            "safety getter promoted an unavailable observation");
        if(success) {
            Check(row.find(f.fault==SafetyFault::One?"\"value\":1,":"\"value\":0,")!=std::string::npos &&
                row.find("\"engineTargetFormId\":"+std::to_string(f.owner.id))!=std::string::npos &&
                row.find("\"engineTargetBaseFormId\":"+std::to_string(f.owner.baseId))!=std::string::npos,
                "safety Boolean or owner identity changed");
            if(f.fault==SafetyFault::None) {
                // Name remapping must compile against the new observed opcode, even with a warm cache.
                f.command.opcode+=0x1000;
                const auto remapped=TypedActorSafety(name,f.owner,SafetyLookup);
                Check(remapped.find("\"status\":\"observed\"")!=std::string::npos && f.calls==2,
                    "safety command opcode remapping reused stale bytecode");
                g_safetyFixtureRows.push_back("{\"case\":\"opcode-remapped\",\"command\":"+Quote(name)+
                    ",\"role\":"+Quote(player?"player":"npc")+",\"observation\":"+remapped+"}");
            }
        } else Check(row.find("\"value\":null")!=std::string::npos,"safety unavailable result became Boolean zero");
        g_safetyFixtureRows.push_back("{\"case\":"+Quote(test.second)+",\"command\":"+Quote(name)+
            ",\"role\":"+Quote(player?"player":"npc")+",\"observation\":"+row+"}");
    }
    // One CREA positive path proves the other admitted reference/base pairing;
    // the same generic failures above do not need a third Cartesian repetition.
    {
        SafetyFixture f;g_safetyFixture=&f;f.actor[1]=0x3C;f.base[1]=0x2B;
        f.actor[3]=0x01000803;f.base[3]=0x01000804;f.actor[8]=reinterpret_cast<UInt32>(f.base);
        Check(TypedValidateForm(f.actor,f.actor[3],f.owner),"creature safety fixture identity failed");
        f.owner.requested={"Control.esp",0x803};f.command.longName="GetDead";f.command.opcode=0x102E;
        f.command.needsParent=1;f.command.execute=TestExecute;
        g_commands=&commands;commands.GetReturnType=SafetyReturnType;g_script=&script;g_numericEnabled=true;
        g_capture=true;g_connected=true;g_captureGeneration=301;g_connectionGeneration=501;g_operationFunctions.clear();
        const auto row=TypedActorSafety("GetDead",f.owner,SafetyLookup);
        Check(f.calls==1 && row.find("\"status\":\"observed\",\"value\":0,")!=std::string::npos &&
            row.find("\"engineTargetFormId\":"+std::to_string(f.owner.id))!=std::string::npos &&
            row.find("\"engineTargetBaseFormId\":"+std::to_string(f.owner.baseId))!=std::string::npos,
            "creature reference/base safety read failed");
        g_safetyFixtureRows.push_back("{\"case\":\"zero\",\"command\":\"GetDead\",\"role\":\"creature\",\"observation\":"+row+"}");
    }
    g_safetyFixture=nullptr;g_commands=savedCommands;g_script=savedScript;g_numericEnabled=savedNumeric;
    g_capture=savedCapture;g_connected=savedConnected;g_captureGeneration=savedGeneration;g_connectionGeneration=savedConnection;
    g_operationFunctions.clear();
}
void TestCalibrationInputs() {
    const auto verified=g_pcLayoutVerified;g_pcLayoutVerified=true;
    std::uint8_t base[0xC4]{};UInt32 actor[9]={0,0x3B,0,0x14};
    actor[8]=reinterpret_cast<UInt32>(base);base[4]=0x2A;UInt32 id=7;memcpy(base+0xC,&id,4);
    const UInt32 flags=0x80;memcpy(base+0x34,&flags,4);
    const std::uint16_t level=1300,min=1,max=50,templates=0x8000;
    memcpy(base+0x3C,&level,2);memcpy(base+0x3E,&min,2);memcpy(base+0x40,&max,2);memcpy(base+0x4A,&templates,2);base[0xBE]=4;
    for(const UInt32 health:{0u,100u,0xFFFFFFFFu}) {
        memcpy(base+0xB4,&health,4);const auto row=ActorBaseData(reinterpret_cast<std::uintptr_t>(actor),7,0x3B);
        Check(row.find("\"status\":\"observed\"")!=std::string::npos && row.find("\"storedHealth\":"+std::to_string(health))!=std::string::npos &&
            row.find("\"levelEncodedUnsigned\":1300")!=std::string::npos && row.find("\"storedEndurance\":4")!=std::string::npos &&
            row.find("\"inheritance\":\"not-evaluated\"")!=std::string::npos,"raw base inputs changed units, sign, identity or inheritance status");
    }
    Check(ActorBaseData(reinterpret_cast<std::uintptr_t>(actor),8,0x3B).find("identity-mismatch")!=std::string::npos,"wrong base identity accepted");
    Check(ActorBaseData(reinterpret_cast<std::uintptr_t>(actor),7,0x3C).find("identity-mismatch")!=std::string::npos,"creature/NPC base mismatch accepted");
    const char settingName[]="fAVDCritLuckBase";UInt32 setting[3]={0,0,reinterpret_cast<UInt32>(settingName)};
    for(const float value:{0.f,-1.f,20.f}) {
        memcpy(setting+1,&value,4);const auto row=ActorGameSetting(settingName,reinterpret_cast<std::uintptr_t>(setting));
        Check(row.find("\"status\":\"observed\"")!=std::string::npos && row.find("\"value\":"+NumberField(value))!=std::string::npos,
            "verified setting discarded zero/negative/finite value");
    }
    const float invalid=std::numeric_limits<float>::infinity();memcpy(setting+1,&invalid,4);
    const auto nonfinite=ActorGameSetting(settingName,reinterpret_cast<std::uintptr_t>(setting));
    Check(nonfinite.find("nonfinite-setting")!=std::string::npos && nonfinite.find("\"rawHex\":\"")!=std::string::npos,
        "nonfinite setting lost its raw evidence or became a value");
    Check(ActorGameSetting("wrong",reinterpret_cast<std::uintptr_t>(setting)).find("name-mismatch")!=std::string::npos,"setting address inferred name without matching it");
    UInt32 table[77]{};std::uint8_t info[0x5C]{};table[18]=reinterpret_cast<UInt32>(info);
    const char token[]="DamageResist";const auto name=reinterpret_cast<UInt32>(token);memcpy(info+0x38,&name,4);
    std::string parsed;Check(ActorValueToken(18,parsed,reinterpret_cast<std::uintptr_t>(table)) && parsed==token,"engine AV token not retained");
    Check(!ActorValueToken(77,parsed,reinterpret_cast<std::uintptr_t>(table)),"AV table upper bound exceeded");
    const char injected[]="DamageResist\nKill";const auto badName=reinterpret_cast<UInt32>(injected);memcpy(info+0x38,&badName,4);
    Check(!ActorValueToken(18,parsed,reinterpret_cast<std::uintptr_t>(table)),"AV token accepted script syntax");
    g_pcLayoutVerified=false;
    Check(ActorGameSetting(settingName,1).find("layout-unavailable")!=std::string::npos &&
        ActorBaseData(1,7,0x3B).find("layout-unavailable")!=std::string::npos,"unverified profile read typed calibration memory");
    g_pcLayoutVerified=verified;
}
void TestBaseOverrideObservation() {
    for(const bool player:{false,true})for(const char* fault:{"absent","zero","positive","negative","nonfinite",
        "wrong-base","wrong-type","wrong-getter","unreadable","changed-body","changed-owner","unverified"}) {
        constexpr UInt32 actor=0x02000000,base=0x02100000,table=0x02200000;
        const UInt32 id=player?0x14:0x104C0F,baseId=player?7:0x104C0C;
        std::unordered_map<std::uintptr_t,std::vector<std::uint8_t>> memory;
        memory[actor].resize(0x24);memory[base].resize(16);memory[table].resize(0x490);
        auto put=[&](UInt32 address,size_t offset,UInt32 value){memcpy(memory[address].data()+offset,&value,4);};
        memory[actor][4]=0x3B;memory[base][4]=0x2A;
        put(actor,0,table);put(actor,12,id);put(actor,0x20,base);put(base,12,baseId);
        put(table,0x48C,player?0x94C640:0x880660);
        for(const auto& proof:ActorOverrideRoutines) {
            auto& bytes=memory[proof.address];const auto length=strlen(proof.hex)/2;
            for(size_t index=0;index<length;++index) {
                const std::string pair(proof.hex+index*2,2);
                bytes.push_back(static_cast<std::uint8_t>(strtoul(pair.c_str(),nullptr,16)));
            }
        }
        const std::string problem=fault;
        if(problem=="wrong-base")put(base,12,baseId+1);
        if(problem=="wrong-type")memory[actor][4]=0x3C;
        if(problem=="wrong-getter")put(table,0x48C,0x123456);
        if(problem=="unreadable")memory.erase(0x937760);
        if(problem=="changed-body")memory[0x9376E0][12]^=1;
        int calls=0;
        const bool present=problem!="absent";
        const float expected=problem=="positive"?65.f:problem=="negative"?-1.f:problem=="nonfinite"?
            std::numeric_limits<float>::infinity():0.f;
        const auto result=ReadActorBaseOverride(problem!="unverified",actor,id,baseId,
            [&](std::uintptr_t address,void* data,size_t length){
                for(const auto& region:memory)if(address>=region.first && address-region.first<=region.second.size() &&
                    length<=region.second.size()-(address-region.first)) {
                    memcpy(data,region.second.data()+(address-region.first),length);return true;
                }
                return false;
            },[&](std::uintptr_t address,std::uintptr_t target,UInt32 code,bool& hasValue){
                ++calls;Check(address==(player?0x94C640u:0x880660u) && target==actor && code==16,
                    "override invocation lost its verified target or Health code");
                hasValue=present;
                if(problem=="changed-owner")put(base,12,baseId+1);
                return expected;
            });
        const bool valid=problem=="absent" || problem=="zero" || problem=="positive" || problem=="negative";
        Check(result.observed==valid,"override absence/zero or invalid input was misclassified");
        const bool called=valid || problem=="nonfinite" || problem=="changed-owner";
        Check(calls==(called?1:0),"override getter executed without admission or more than once");
        if(valid)Check(result.present==present && result.value==expected && result.reason==nullptr,
            "override observation lost absence, zero, sign or value");
        else Check(result.reason!=nullptr,"override failure lost its reason");
    }
}
std::vector<std::string> g_creatureReadFixtureRows;
void TestCreatureReadInputs() {
    // Only the new type boundary is varied here; the existing 24 player/NPC
    // controls retain generic pointer, body, zero/sign and profile failures.
    for(const char* fault:{"absent","zero","present","mixed-base","player-id","unknown-getter",
        "changed-type","changed-base","nonfinite","tampered-body"}) {
        constexpr UInt32 actor=0x02000000,base=0x02100000,table=0x02200000;
        const std::string problem=fault;
        const UInt32 id=problem=="player-id"?0x14:0x02000810,baseId=problem=="player-id"?7:0x02000800;
        std::map<std::uintptr_t,std::vector<std::uint8_t>> memory;
        memory[actor].resize(0x24);memory[base].resize(16);memory[table].resize(0x490);
        auto put=[&](UInt32 address,size_t offset,UInt32 value){memcpy(memory[address].data()+offset,&value,4);};
        memory[actor][4]=0x3C;memory[base][4]=problem=="mixed-base"?0x2A:0x2B;
        put(actor,0,table);put(actor,12,id);put(actor,0x20,base);put(base,12,baseId);
        put(table,0x48C,problem=="unknown-getter"?0x880664:0x880660);
        for(const auto& proof:ActorOverrideRoutines) {
            auto& bytes=memory[proof.address];
            for(size_t index=0;index<strlen(proof.hex);index+=2) {
                const std::string pair(proof.hex+index,2);
                bytes.push_back(static_cast<std::uint8_t>(strtoul(pair.c_str(),nullptr,16)));
            }
        }
        if(problem=="tampered-body")memory[0x880660][21]^=1;
        int calls=0;
        const float supplied=problem=="present"?17.25f:problem=="nonfinite"?
            std::numeric_limits<float>::infinity():0.f;
        const auto result=ReadActorBaseOverride(true,actor,id,baseId,
            [&](std::uintptr_t address,void* data,size_t length){
                for(const auto& region:memory)if(address>=region.first && address-region.first<=region.second.size() &&
                    length<=region.second.size()-(address-region.first)) {
                    memcpy(data,region.second.data()+(address-region.first),length);return true;
                }
                return false;
            },[&](std::uintptr_t address,std::uintptr_t target,UInt32 code,bool& present){
                ++calls;Check(address==0x880660 && target==actor && code==16,
                    "creature override lost the observed getter, owner or Health argument");
                present=problem!="absent";
                if(problem=="changed-type") {memory[actor][4]=0x3B;memory[base][4]=0x2A;}
                if(problem=="changed-base")put(base,12,baseId+1);
                return supplied;
            });
        const bool valid=problem=="absent" || problem=="zero" || problem=="present";
        const bool called=valid || problem=="changed-type" || problem=="changed-base" || problem=="nonfinite";
        Check(result.observed==valid && calls==(called?1:0),"creature override bypassed admission or repeated the getter");
        if(valid)Check(result.identityRead && result.getterRead && result.referenceType==0x3C && result.baseType==0x2B &&
            result.base==base && result.table==table && result.getter==0x880660 && result.present==(problem!="absent") &&
            result.value==supplied && !result.reason,"creature override changed actual provenance or absence/zero/value");
        if(problem=="unknown-getter")Check(result.identityRead && result.getterRead && result.getter==0x880664,
            "unsupported getter lost its observed identity");
        if(problem=="changed-type" || problem=="changed-base")Check(result.reason && !strcmp(result.reason,"override-identity-changed"),
            "post-call creature identity change was accepted");
        if(!valid)Check(result.reason,"unavailable creature override lost its reason");
        g_creatureReadFixtureRows.push_back("{\"case\":"+Quote("creature-override-"+problem)+
            ",\"observed\":"+(result.observed?"true":"false")+",\"calls\":"+std::to_string(calls)+
            ",\"reason\":"+(result.reason?Quote(result.reason):"null")+",\"referenceType\":"+
            std::to_string(result.referenceType)+",\"baseType\":"+std::to_string(result.baseType)+"}");
    }
    // Common TESActorBase layout is independently declared by TESCreature's SDK
    // inheritance; this checks stored bytes, not an effective Health formula.
    const auto verified=g_pcLayoutVerified;g_pcLayoutVerified=true;
    std::uint8_t raw[0xC4]{};UInt32 actor[9]={0,0x3C,0,0x02000810};
    const UInt32 baseId=0x02000800,stored=137;actor[8]=reinterpret_cast<UInt32>(raw);
    raw[4]=0x2B;memcpy(raw+12,&baseId,4);memcpy(raw+0xB4,&stored,4);raw[0xBE]=4;
    const auto row=ActorBaseData(reinterpret_cast<std::uintptr_t>(actor),baseId,0x3C);
    g_pcLayoutVerified=verified;
    Check(row.find("\"status\":\"observed\"")!=std::string::npos && row.find("\"formType\":43")!=std::string::npos &&
        row.find("\"storedHealth\":137")!=std::string::npos && row.find("\"storedEndurance\":4")!=std::string::npos &&
        row.find("\"inheritance\":\"not-evaluated\"")!=std::string::npos,"creature stored data changed layout or implied inheritance");
    g_creatureReadFixtureRows.push_back("{\"case\":\"creature-common-base-layout\",\"observation\":"+row+"}");
}

void TestActionBoundaries() {
    const bool loaded=g_loadedGameObserved;
    g_loadedGameObserved=false;g_capture=true;g_connected=true;g_captureGeneration=78;g_events.clear();
    const auto sequence=g_sequence;
    Execute({15,501,"player"});
    Check(g_events.size()==2 && g_sequence==sequence+2 && g_events[0].id==501 && g_events[1].id==501 &&
        g_events[0].json.find("\"kind\":\"action-begin\"")!=std::string::npos &&
        g_events[0].json.find("\"requestKind\":15")!=std::string::npos &&
        g_events[1].json.find("\"kind\":\"error\"")!=std::string::npos,
        "game-thread request boundary did not precede dispatch with the same identity");
    g_capture=false;g_events.clear();Execute({15,502,"player"});
    Check(g_events.size()==1 && g_events[0].json.find("\"kind\":\"error\"")!=std::string::npos,
        "inactive capture emitted an action boundary");
    g_loadedGameObserved=loaded;g_capture=true;g_events.clear();
}
CommandInfo g_referenceTestCommand{};
const CommandInfo* GetReferenceTestCommand(const char* name){return !strcmp(name,"SetPos")?&g_referenceTestCommand:nullptr;}
int g_referenceExecuteCount=0;
std::string g_referenceFault;
bool ReferenceTestExecute(ParamInfo*,void*,TESObjectREFR* ref,TESObjectREFR*,Script*,ScriptEventList*,double* result,UInt32* offset) {
    Check(GetLastError()==0x6371,"reference observer changed incoming LastError");
    ++g_referenceExecuteCount;*result=0;*offset+=7;
    if(g_referenceFault=="changed-position") {const float value=0;memcpy(reinterpret_cast<std::uint8_t*>(ref)+0x30,&value,4);}
    if(g_referenceFault=="changed-identity") {const UInt32 value=0x1235;memcpy(reinterpret_cast<std::uint8_t*>(ref)+12,&value,4);}
    if(g_referenceFault=="new-generation")++g_captureGeneration;
    SetLastError(0x6372);
    return false;
}
void TestReferenceCommandObservation() {
    auto* savedCommands=g_commands;const auto savedVerified=g_pcLayoutVerified;const auto savedRequest=g_currentRequest;
    NVSECommandTableInterface commands{};commands.GetByName=GetReferenceTestCommand;g_commands=&commands;
    g_referenceTestCommand.longName="SetPos";g_referenceTestCommand.opcode=0x1005;g_referenceTestCommand.execute=ReferenceTestExecute;
    g_referenceCommandAttempted=false;InstallReferenceCommandHook();
    Check(g_referenceCommandHooked && g_referenceTestCommand.execute==ReferenceCommandExecute,"SetPos SDK entry was not hooked");
    for(const auto* fault:{"unchanged","changed-position","changed-identity","nonfinite","unreadable","unverified","no-request","inactive","new-generation"}) {
        g_referenceFault=fault;g_referenceExecuteCount=0;g_capture=true;g_connected=true;g_captureGeneration=900;
        g_currentRequest=g_referenceFault=="no-request"?0:123;g_pcLayoutVerified=g_referenceFault!="unverified";
        if(g_referenceFault=="inactive")g_capture=false;
        UInt32 base[4]={0,0x15,0,0x4321},cell[4]={0,0x39,0,0x5678};std::uint8_t raw[0x68]{};
        raw[4]=0x3A;const UInt32 id=0x1234,basePointer=reinterpret_cast<UInt32>(base),cellPointer=reinterpret_cast<UInt32>(cell);
        memcpy(raw+12,&id,4);memcpy(raw+0x20,&basePointer,4);memcpy(raw+0x40,&cellPointer,4);
        const float position=g_referenceFault=="nonfinite"?std::numeric_limits<float>::infinity():-123.25f;memcpy(raw+0x30,&position,4);
        auto* ref=reinterpret_cast<TESObjectREFR*>(g_referenceFault=="unreadable"?reinterpret_cast<void*>(1):raw);
        g_events.clear();double result=123;UInt32 offset=11;
        SetLastError(0x6371);
        Check(!g_referenceTestCommand.execute(nullptr,nullptr,ref,nullptr,nullptr,nullptr,&result,&offset) &&
            result==0 && offset==18 && g_referenceExecuteCount==1 && GetLastError()==0x6372,
            "reference hook changed dispatch, result, argument offset or LastError");
        const bool suppressed=g_referenceFault=="inactive" || g_referenceFault=="no-request" || g_referenceFault=="new-generation";
        Check(g_events.size()==(suppressed?size_t{0}:size_t{1}),"reference observation crossed request/capture generation scope");
        if(suppressed)continue;
        const auto& event=g_events[0].json;
        Check(event.find("\"kind\":\"reference-command\"")!=std::string::npos && event.find("\"handlerReturned\":false")!=std::string::npos &&
            event.find("\"requestId\":123")!=std::string::npos && event.find("\"opcodeOffsetBefore\":11")!=std::string::npos &&
            event.find("\"opcodeOffsetAfter\":18")!=std::string::npos && event.find("\"argumentEvaluation\":\"not-repeated\"")!=std::string::npos &&
            event.find("\"publishedHandlerAddress\":"+std::to_string(reinterpret_cast<UInt32>(ReferenceTestExecute)))!=std::string::npos &&
            event.find("\"handlerRoute\":\"published-table\"")!=std::string::npos,
            "reference event lost actual dispatch/request/offset evidence");
        if(g_referenceFault=="changed-position")Check(event.find("\"value\":-123.25")!=std::string::npos && event.find("\"position\":[{\"status\":\"observed\",\"value\":0}")!=std::string::npos,"raw changed position/zero was lost");
        if(g_referenceFault=="changed-identity")Check(event.find("\"engineTargetFormId\":4660")!=std::string::npos && event.find("\"sameIdentityAfter\":false")!=std::string::npos,"post-call identity replaced dispatch identity");
        if(g_referenceFault=="nonfinite")Check(event.find("nonfinite-field")!=std::string::npos,"nonfinite position became a value");
        if(g_referenceFault=="unreadable" || g_referenceFault=="unverified")Check(event.find("\"status\":\"unavailable\"")!=std::string::npos,"unreadable/unverified fields became observed");
    }
    RestoreReferenceCommandHook();Check(g_referenceTestCommand.execute==ReferenceTestExecute,"reference original was not restored");
    const auto savedGeneral=g_generalCommands[0];const auto savedInstalled=g_generalInstalled.load();
    g_generalCommands[0]={&g_referenceTestCommand,ReferenceTestExecute,0x1005,0,"SetPos"};
    g_generalInstalled=1;g_referenceTestCommand.execute=g_generalWrappers[0];
    g_referenceCommandAttempted=false;InstallReferenceCommandHook();
    Check(g_referenceCommandOriginal==g_generalWrappers[0] && g_referencePublishedHandler==ReferenceTestExecute &&
        g_referencePublishedPrefix.size()==512 && !strcmp(g_referenceHandlerRoute,"bridge-general-wrapper"),
        "reference observer lost the published handler behind the bridge wrapper");
    g_referenceFault="unchanged";g_capture=true;g_connected=true;g_currentRequest=123;g_events.clear();
    double chainResult=123;UInt32 chainOffset=11;SetLastError(0x6371);
    Check(!g_referenceTestCommand.execute(nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,&chainResult,&chainOffset) &&
        chainResult==0 && chainOffset==18 && GetLastError()==0x6372,"reference/general chain changed the original result");
    bool chainObserved=false;
    for(const auto& event:g_events)if(event.json.find("\"kind\":\"reference-command\"")!=std::string::npos) {
        chainObserved=event.json.find("\"publishedHandlerAddress\":"+std::to_string(reinterpret_cast<UInt32>(ReferenceTestExecute)))!=std::string::npos &&
            event.json.find("\"handlerRoute\":\"bridge-general-wrapper\"")!=std::string::npos;
    }
    Check(chainObserved,"reference/general event lost the published handler identity");
    RestoreReferenceCommandHook();
    Check(g_referenceTestCommand.execute==g_generalWrappers[0],"reference teardown broke the general wrapper chain");
    g_generalCommands[0]=savedGeneral;g_generalInstalled=savedInstalled;g_referenceTestCommand.execute=ReferenceTestExecute;
    g_referenceCommandAttempted=false;InstallReferenceCommandHook();g_referenceTestCommand.execute=OtherExecute;
    RestoreReferenceCommandHook();Check(g_referenceTestCommand.execute==OtherExecute,"reference teardown overwrote a later hook");
    g_commands=savedCommands;g_pcLayoutVerified=savedVerified;g_currentRequest=savedRequest;g_capture=true;g_events.clear();
}
std::unordered_map<UInt32,std::vector<std::uint8_t>> g_membershipMemory;
std::unordered_map<UInt32,unsigned> g_membershipReads;
UInt32 g_membershipChange=0;
size_t g_membershipChangeOffset=0;
bool ReadMembershipFixture(std::uintptr_t address,void* output,size_t length) {
    if(address==g_membershipChange && ++g_membershipReads[static_cast<UInt32>(address)]==2)
        g_membershipMemory[g_membershipChange][g_membershipChangeOffset]^=1;
    for(const auto& entry:g_membershipMemory) {
        if(address>=entry.first && address-entry.first<=entry.second.size() &&
            length<=entry.second.size()-(address-entry.first)) {
            memcpy(output,entry.second.data()+(address-entry.first),length);return true;
        }
    }
    return false;
}
void MembershipPut(UInt32 address,size_t offset,UInt32 value) {
    auto& bytes=g_membershipMemory.at(address);
    Check(offset+4<=bytes.size(),"fixture write out of bounds");memcpy(bytes.data()+offset,&value,4);
}
void MembershipForm(UInt32 address,size_t length,std::uint8_t type,UInt32 id) {
    g_membershipMemory[address]=std::vector<std::uint8_t>(length,0);
    g_membershipMemory[address][4]=type;MembershipPut(address,12,id);
}
void TestMembershipReaders() {
    for(const std::string fault:{"none","empty","indirect","duplicate","unreadable","cycle","count","bound","changed","ambiguous","overflow","slot-changed","info-changed","owner-changed"}) {
        g_membershipMemory.clear();g_membershipReads.clear();g_membershipChange=0;
        g_membershipChangeOffset=0;
        MembershipForm(0x1000,0x40,0x45,0xA00);
        MembershipForm(0x5000,0x30,0x47,0x900);
        MembershipForm(0x6000,0x18,0x46,0xA10);
        MembershipForm(0x6100,0x18,0x46,0xA11);
        g_membershipMemory[0x4000]=std::vector<std::uint8_t>(0x1C,0);
        g_membershipMemory[0x7000]=std::vector<std::uint8_t>(12,0);
        MembershipPut(0x1000,0x2C,0x4000);MembershipPut(0x4000,0,0x5000);
        MembershipPut(0x4000,8,0x7000);MembershipPut(0x4000,12,3);
        MembershipPut(0x4000,16,3);MembershipPut(0x4000,20,2);
        MembershipPut(0x7000,0,0x6000);MembershipPut(0x7000,8,0x6100);
        if(fault=="empty")MembershipPut(0x1000,0x2C,0);
        if(fault=="indirect") {
            g_membershipMemory[0x3000]=std::vector<std::uint8_t>(4,0);
            MembershipPut(0x3000,0,0x4000);MembershipPut(0x1000,0x2C,0x3000);
        }
        if(fault=="duplicate")MembershipPut(0x7000,8,0x6000);
        if(fault=="unreadable")g_membershipMemory.erase(0x6100);
        if(fault=="cycle")MembershipPut(0x1000,0x30,0x102C);
        if(fault=="count")MembershipPut(0x4000,20,3);
        if(fault=="bound") {MembershipPut(0x4000,12,257);MembershipPut(0x4000,16,257);}
        if(fault=="changed")g_membershipChange=0x4000;
        if(fault=="slot-changed")g_membershipChange=0x7000;
        if(fault=="info-changed") {g_membershipChange=0x6000;g_membershipChangeOffset=12;}
        if(fault=="ambiguous") {
            // Both candidate paths validate structurally; neither can be selected.
            MembershipForm(0x8000,0x18,0x47,0x901);MembershipPut(0x5000,0,0x8000);
        }
        if(fault=="overflow")MembershipPut(0x4000,8,0xFFFFFFFC);
        const auto result=MembershipDialogue(0x1000,ReadMembershipFixture,fault=="owner-changed"?0xA01:0xA00);
        Check(result.size()<MaxPayload-4096,"bounded membership output exceeded its frame budget");
        if(fault=="none" || fault=="indirect" || fault=="duplicate") {
            Check(result.find("\"status\":\"observed\",\"topicFormId\":2560")!=std::string::npos,"valid topic membership was not observed");
            Check(result.find("\"index\":1,\"address\":0,\"status\":\"empty\"")!=std::string::npos,"sparse INFO slot was compacted away");
            const auto selected="\"pointerPath\":\""+std::string(fault=="indirect"?"node.data->pointer":"node.data")+"\"";
            Check(result.find(selected)!=std::string::npos,"actual pointer path was not retained");
            Check(result.find("\"formId\":"+std::to_string(fault=="duplicate"?0xA10:0xA11),result.find("\"index\":2"))!=std::string::npos,"INFO order/duplicate identity was lost");
        } else if(fault=="empty")Check(result.find("\"status\":\"empty\",\"topicFormId\":2560")!=std::string::npos,"empty topic became unavailable");
        else if(fault=="owner-changed")Check(result.find("topic-identity-changed-before-read")!=std::string::npos,"resolved owner was silently replaced");
        else {
            Check(result.find("\"status\":\"partial\",\"topicFormId\":2560")!=std::string::npos,"incomplete topic became complete");
            const auto expected=fault=="unreadable"?"info-identity-unavailable":fault=="cycle"?"cyclic-group-list":
                fault=="count"?"array-count-mismatch":fault=="bound"?"slot-limit":fault=="changed"?"group-changed-during-read":
                fault=="ambiguous"?"\"status\":\"ambiguous\"":fault=="slot-changed"?"slot-changed-during-read":
                fault=="info-changed"?"info-identity-changed-during-read":"slot-unreadable";
            Check(result.find(expected)!=std::string::npos,"membership failure lost its exact cause");
        }
    }
    for(const std::string fault:{"land","empty","unloaded","unreadable","type","state","changed","land-changed","owner-changed"}) {
        g_membershipMemory.clear();g_membershipReads.clear();g_membershipChange=0;
        g_membershipChangeOffset=0;
        MembershipForm(0x1000,0x50,0x39,0xB10);MembershipForm(0x6000,0x18,0x42,0xB20);
        g_membershipMemory[0x1000][0x26]=6;MembershipPut(0x1000,0x4C,0x6000);
        if(fault=="empty" || fault=="unloaded")MembershipPut(0x1000,0x4C,0);
        if(fault=="unloaded")g_membershipMemory[0x1000][0x26]=0;
        if(fault=="unreadable")g_membershipMemory.erase(0x6000);
        if(fault=="type")g_membershipMemory[0x6000][4]=0x45;
        if(fault=="state")g_membershipMemory[0x1000][0x26]=7;
        if(fault=="land-changed") {g_membershipChange=0x6000;g_membershipChangeOffset=12;}
        if(fault=="changed") {g_membershipChange=0x1000;g_membershipReads[0x1000]=0;}
        // Change only the second identity read, while leaving readable storage intact.
        const auto reader=fault=="changed" ? +[](std::uintptr_t address,void* output,size_t length) {
            if(address==0x1000 && ++g_membershipReads[0x1000]==2)MembershipPut(0x1000,0x4C,0);
            return ReadMembershipFixture(address,output,length);
        } : ReadMembershipFixture;
        if(fault=="changed")g_membershipChange=0;
        const auto result=MembershipTerrain(0x1000,reader,fault=="owner-changed"?0xB11:0xB10);
        const auto expected=fault=="land"?"\"formId\":2848":fault=="empty"?"\"status\":\"empty\",\"formId\":null":
            fault=="unloaded"?"cell-not-loaded":fault=="state"?"cell-load-state-invalid":fault=="changed" || fault=="land-changed"?"\"stableRead\":false":
            fault=="owner-changed"?"cell-identity-changed-before-read":"land-identity-unavailable";
        Check(result.find(expected)!=std::string::npos,"CELL/LAND recovery state mismatch");
    }
    std::uint8_t bytes[16]{};
    Check(!MembershipReadAt(ReadMembershipFixture,0xFFFFFFFC,bytes),"32-bit address overflow was accepted");
    g_membershipMemory.clear();g_membershipReads.clear();g_membershipChange=0;
    const bool savedCapture=g_capture,savedLoaded=g_loadedGameObserved,savedNumeric=g_numericEnabled,savedLayout=g_pcLayoutVerified;
    for(const auto fault:{"capture","load","numeric","layout"}) {
        g_capture=strcmp(fault,"capture")!=0;g_loadedGameObserved=strcmp(fault,"load")!=0;
        g_numericEnabled=strcmp(fault,"numeric")!=0;g_pcLayoutVerified=strcmp(fault,"layout")!=0;
        g_connected=true;g_events.clear();g_captureGeneration=73;
        MembershipRequest({19,301,"read-dialogue-state\tAddon.esm\t000A00"});
        Check(g_events.size()==1 && g_events[0].json.find("membership-api-unavailable")!=std::string::npos,
            "unadmitted membership request did not return its explicit error");
    }
    g_capture=savedCapture;g_loadedGameObserved=savedLoaded;g_numericEnabled=savedNumeric;g_pcLayoutVerified=savedLayout;
    g_events.clear();
}
struct LeaseFixture {
    UInt32 base[4]{0,0x2A,0,0x800},playerBase[4]{0,0x2A,0,7};
    UInt32 attackerBytes[9]{0,0x3B,0,0x01000801},targetBytes[9]{0,0x3B,0,0x14},replacement[9]{};
    TypedForm attacker,target;TypedOperation op;CombatLeaseRecord admitted;
    ULONGLONG now=GetTickCount64();
    bool combat[2]{true,true},disabled=false,profile=true,startAccepted=true,armedAtStart=false;
    std::string fault;std::vector<std::string> calls;
    int reads=0;
};
LeaseFixture* g_leaseFixture=nullptr;
std::vector<std::string> g_leaseFixtureRows;
void* LeaseLookup(UInt32 id) {
    auto& f=*g_leaseFixture;
    if(id==f.attacker.id)return f.fault=="missing"?nullptr:f.fault=="pointer"?f.replacement:f.attackerBytes;
    return id==f.target.id?f.targetBytes:nullptr;
}
bool LeaseProfile(){return g_leaseFixture->profile;}
bool LeaseMutate(const std::string& statement,const TypedForm& owner,const TypedForm* other,float,std::string& error) {
    auto& f=*g_leaseFixture;const bool attacker=owner.id==f.attacker.id;
    Check(owner.pointer==(attacker?f.attackerBytes:f.targetBytes),"cleanup used an unbound actor pointer");
    f.calls.push_back(statement+(attacker?":attacker":":target"));
    if(statement=="owner.StartCombat other") {
        Check(other && other->pointer==f.targetBytes,"leased start lost target");f.armedAtStart=CombatLeaseActive();
        if(!f.startAccepted){error="fixture-start-rejected";return false;}return true;
    }
    Check(statement=="owner.StopCombat" || (statement=="owner.Disable" && attacker && owner.id!=0x14),"cleanup mutated an unrelated/player actor");
    if(f.fault=="rejected"){error="fixture-rejected";return false;}
    if(f.fault=="load-during-call")++g_gameLoadEpoch;
    if(f.fault!="wrong-readback") {
        if(statement=="owner.StopCombat"){if(f.fault!="deferred-combat")f.combat[attacker?0:1]=false;}
        else f.disabled=true;
    }
    return true;
}
bool LeaseNumber(const std::string& expression,const TypedForm& owner,const TypedForm*,float,double& value,std::string& error) {
    auto& f=*g_leaseFixture;++f.reads;
    if(f.fault=="missing-readback"){error="fixture-unavailable";return false;}
    value=expression=="owner.GetDisabled"?(f.disabled?1:0):(f.combat[owner.id==f.attacker.id?0:1]?1:0);
    if(f.fault=="nan-readback")value=std::numeric_limits<double>::quiet_NaN();
    if(f.fault=="identity-during-read")++f.attackerBytes[3];
    return true;
}
CombatLeaseIo LeaseIo(){return {LeaseLookup,LeaseMutate,LeaseNumber,LeaseProfile};}
void ResetLease(LeaseFixture& f) {
    g_leaseFixture=&f;g_combatLease={};g_lastCombatCleanup="null";g_combatLastService=0;g_frame=1000;
    g_capture=true;g_connected=true;g_captureGeneration=601;g_connectionGeneration=71;g_gameLoadEpoch=3;
    g_loadedGameObserved=true;g_session="fixture-lease-origin";g_script=nullptr;g_events.clear();g_requests.clear();
    f.attackerBytes[8]=reinterpret_cast<UInt32>(f.base);f.targetBytes[8]=reinterpret_cast<UInt32>(f.playerBase);
    memcpy(f.replacement,f.attackerBytes,sizeof(f.replacement));
    Check(TypedValidateForm(f.attackerBytes,0x01000801,f.attacker) && TypedValidateForm(f.targetBytes,0x14,f.target),"lease fixture identity invalid");
    f.attacker.requested={"Control.esp",0x801};f.target.requested={"@player",0x14};
    Check(ParseTypedOperation({16,401,"start-combat-leased\tControl.esp\t801\t@player\t14\t5000"},f.op),"lease fixture parse failed");
}
bool ArmLease(LeaseFixture& f,std::string& error) {
    return CombatLeaseArm({16,401,""},f.op,f.attacker,&f.target,601,f.now,f.admitted,error,LeaseIo());
}
void SaveLeaseCase(const std::string& name) {
    std::string row="{\"case\":"+Quote(name)+",\"terminal\":"+g_lastCombatCleanup+",\"events\":[";
    for(size_t i=0;i<g_events.size();++i){if(i)row+=',';row+=g_events[i].json;}
    row+="]}";g_leaseFixtureRows.push_back(row);
}
void FinishLeaseFixture() {
    if(!CombatLeaseActive())return;
    ++g_frame;
    ServiceCombatLease(g_combatLease.cleanupStarted+(g_leaseFixture->fault=="wrong-readback"?CombatObservationMilliseconds:1),LeaseIo());
}
void TestCombatLease() {
    const auto savedScript=g_script;
    for(const auto* duration:{"1","5000","0","5001","-1","1.5","5000\textra"}) {
        TypedOperation op;const bool valid=ParseTypedOperation({16,401,std::string("start-combat-leased\tControl.esp\t801\t@player\t14\t")+duration},op);
        Check(valid==(!strcmp(duration,"1") || !strcmp(duration,"5000")),"lease duration parser boundary failed");
        g_leaseFixtureRows.push_back("{\"case\":"+Quote(std::string("parse-")+duration)+",\"accepted\":"+(valid?"true":"false")+"}");
    }
    for(const std::string fault:{"player","self","missing-target","wrong-type","missing-base","missing","pointer","profile","not-loaded","not-connected","not-capturing","stale-generation","duration","concurrent","empty-session","long-session"}) {
        LeaseFixture f;ResetLease(f);std::string error;
        if(fault=="concurrent")Check(ArmLease(f,error),"first lease not armed");
        auto subject=f.attacker;const TypedForm* target=&f.target;
        if(fault=="player")subject=f.target;
        if(fault=="self")target=&f.attacker;
        if(fault=="missing-target")target=nullptr;
        if(fault=="wrong-type")subject.type=0x3A;
        if(fault=="missing-base")subject.baseId=0;
        if(fault=="missing" || fault=="pointer")f.fault=fault;
        if(fault=="profile")f.profile=false;
        if(fault=="not-loaded")g_loadedGameObserved=false;
        if(fault=="not-connected")g_connected=false;
        if(fault=="not-capturing")g_capture=false;
        if(fault=="duration")f.op.index=5001;
        if(fault=="empty-session")g_session.clear();
        if(fault=="long-session")g_session=std::string(129,'x');
        const auto generation=fault=="stale-generation"?600u:601u;
        CombatLeaseStartCore({16,402,""},f.op,subject,target,generation,f.now,LeaseIo());
        Check(f.calls.empty(),"prearm rejection called engine");
        for(const auto& row:g_events)Check(row.json.find("\"kind\":\"error\"")!=std::string::npos && row.json.find("\"leaseId\":")==std::string::npos,"prearm rejection became lease admission");
        Check(fault=="concurrent"?CombatLeaseActive():!CombatLeaseActive(),"prearm rejection changed active lease");SaveLeaseCase("reject-"+fault);
    }
    for(const bool accepted:{true,false}) {
        LeaseFixture f;ResetLease(f);f.startAccepted=accepted;
        CombatLeaseStartCore({16,401,""},f.op,f.attacker,&f.target,601,f.now,LeaseIo());
        Check(f.armedAtStart,"StartCombat ran before lease armed");
        Check(g_events[0].json.find("\"kind\":\"action-result\"")!=std::string::npos && g_events[0].json.find("\"leaseId\":")!=std::string::npos,"armed start lacks mandatory cleanup identity");
        if(!accepted)FinishLeaseFixture();
        Check(CombatLeaseActive()==accepted && f.calls.size()==(accepted?1u:4u),"start rejection skipped cleanup or accepted start cleaned early");
        SaveLeaseCase(accepted?"start-accepted":"start-rejected");
    }
    for(const std::string trigger:{"deadline","stop","cancel","disconnect","capture-ended","load-epoch-changed","queue-full","engine-exiting"}) {
        LeaseFixture f;ResetLease(f);if(trigger=="deadline")f.now=GetTickCount64()-5000;
        std::string error;Check(ArmLease(f,error),"lease not armed");
        if(trigger=="deadline") {
            ServiceCombatLease(f.admitted.deadline-1,LeaseIo());Check(f.calls.empty() && CombatLeaseActive(),"deadline fired early");
            // Use an expired admitted deadline without sleeping; service's clock is a supplied input.
            ServiceCombatLease(f.admitted.deadline,LeaseIo());
        } else if(trigger=="stop" || trigger=="cancel") {
            g_requests.push_back({3,499,"unrelated"});
            CombatLeaseEndCapture({static_cast<std::uint16_t>(trigger=="stop"?4:5),402,""},LeaseIo());
            Check(CombatLeaseActive() && g_capture && g_events.empty(),"capture ended before later-frame observation");
            FinishLeaseFixture();
            Check(g_events.size()==2 && g_events[0].json.find("combat-cleanup")!=std::string::npos &&
                g_events[1].json.find("capture-end")!=std::string::npos && g_requests.empty() && !g_capture,"capture ended before cleanup or retained queued mutation");
        } else {
            if(trigger=="disconnect"){g_connected=false;g_capture=false;++g_connectionGeneration;++g_captureGeneration;}
            if(trigger=="capture-ended")++g_captureGeneration;
            if(trigger=="load-epoch-changed")++g_gameLoadEpoch;
            if(trigger=="queue-full") {for(size_t i=0;i<MaxQueue;++i)g_requests.push_back({3,500+i,"unrelated"});CombatLeaseRequestCleanup("queue-full",71);}
            ServiceCombatLease(f.now,LeaseIo(),trigger=="engine-exiting");
        }
        FinishLeaseFixture();
        const bool unavailable=trigger=="load-epoch-changed" || trigger=="engine-exiting";
        Check(!CombatLeaseActive() && f.calls.size()==(unavailable?0u:3u),"cleanup did not perform exactly its eligible fixed steps");
        Check(g_lastCombatCleanup.find("\"trigger\":"+Quote(trigger))!=std::string::npos &&
            g_lastCombatCleanup.find("\"status\":"+Quote(unavailable?"unavailable":"observed"))!=std::string::npos,"terminal lost trigger/outcome");
        if(trigger=="disconnect" || trigger=="capture-ended")Check(g_events.empty(),"old cleanup crossed capture/connection");
        if(trigger=="queue-full")Check(g_requests.size()==MaxQueue,"independent cleanup consumed action queue");
        const auto receipt=g_lastCombatCleanup;ServiceCombatLease(f.now,LeaseIo());Check(receipt==g_lastCombatCleanup,"terminal receipt changed after cleanup");
        SaveLeaseCase(trigger);
    }
    for(const std::string fault:{"missing","pointer","type","id","base-id","base-pointer","missing-readback","nan-readback","rejected","wrong-readback","load-during-call","identity-during-read"}) {
        LeaseFixture f;ResetLease(f);std::string error;Check(ArmLease(f,error),"lease not armed");f.fault=fault;
        if(fault=="type")f.attackerBytes[1]=0x3A;
        if(fault=="id")++f.attackerBytes[3];
        if(fault=="base-id")++f.base[3];
        if(fault=="base-pointer"){f.playerBase[3]=f.base[3];f.attackerBytes[8]=reinterpret_cast<UInt32>(f.playerBase);}
        CombatLeaseRequestCleanup("cancel");ServiceCombatLease(f.now,LeaseIo());FinishLeaseFixture();
        Check(g_lastCombatCleanup.find("\"status\":\"observed\",\"trigger\"")==std::string::npos,"unobserved/failed cleanup became successful");
        const bool failed=fault=="rejected" || fault=="wrong-readback";
        Check(g_lastCombatCleanup.find("\"status\":"+Quote(failed?"failed":"unavailable")+",\"trigger\"")!=std::string::npos,"cleanup failure class changed");
        if(fault=="load-during-call")Check(f.calls.size()==1 && f.reads==0,"load transition reused old actors");
        if(fault=="missing" || fault=="pointer" || fault=="type" || fault=="id" || fault=="base-id")
            Check(f.calls.size()==1 && f.calls[0]=="owner.StopCombat:target","stale attacker used or valid target skipped");
        SaveLeaseCase(fault);
    }
    {
        LeaseFixture f;ResetLease(f);std::string error;Check(ArmLease(f,error),"lease not armed");
        CombatLeaseRequestCleanup("stale-connection",70);ServiceCombatLease(f.now,LeaseIo());Check(f.calls.empty(),"stale connection cancelled lease");
        g_capture=false;g_connected=false;++g_captureGeneration;++g_connectionGeneration;
        Check(CombatLeaseBlocksMutation({2,500,"new-session"}),"new capture admitted while old cleanup pending");
        g_connected=true;g_session="fresh-connection";
        ServiceCombatLease(f.now,LeaseIo());FinishLeaseFixture();
        Check(g_events.empty() && CombatLeaseHello().find("fixture-lease-origin")!=std::string::npos &&
            CombatLeaseHello().find("\"requestId\":401")!=std::string::npos,"retained terminal lost original attribution or leaked into new connection");
        SaveLeaseCase("reconnect-origin");
    }
    {
        LeaseFixture f;ResetLease(f);std::string error;f.now=GetTickCount64()-5001;Check(ArmLease(f,error),"lease not armed");
        Check(CombatLeaseHello().find("awaiting-game-thread-cleanup")!=std::string::npos && g_lastCombatCleanup=="null" && f.calls.empty(),"stalled game acquired fabricated cleanup");
        SaveLeaseCase("stalled-hello");
    }
    const std::vector<std::pair<Request,bool>> gates={
        {{2,1,"new-session"},true},{{3,1,"player.SetAV Health 1"},true},{{8,1,"probe"},true},{{11,1,"x"},true},{{14,1,"x"},true},
        {{13,1,"inspect"},false},{{13,1,"choose\tprobe"},true},{{16,1,"stop-combat\tControl.esp\t801"},false},
        {{16,1,"stop-combat\t@player\t14"},false},{{16,1,"stop-combat\tOther.esp\t802"},true},
        {{16,1,"start-combat\tControl.esp\t801\t@player\t14"},true},{{16,1,"set-position\t@player\t14\tX\t1"},true},
        {{17,1,"read-inventory\t@player\t14"},false},{{17,1,"add-item\t@player\t14\tFalloutNV.esm\tF\t1"},true},
        {{18,1,"read-quest-state\tControl.esp\t800"},false},{{18,1,"set-quest-stage\tControl.esp\t800\t10"},true},
        {{4,1,""},false},{{5,1,""},false},{{12,1,"player"},false},{{19,1,"read"},false}};
    for(size_t i=0;i<gates.size();++i) {
        LeaseFixture f;ResetLease(f);std::string error;Check(ArmLease(f,error),"lease not armed");
        Check(CombatLeaseBlocksMutation(gates[i].first)==gates[i].second,"armed mutation gate mismatch");
        g_combatLease={};Check(!CombatLeaseBlocksMutation(gates[i].first),"lease altered legacy unarmed dispatch");SaveLeaseCase("gate-"+std::to_string(i));
    }
    g_combatLease={};g_lastCombatCleanup="null";g_events.clear();g_requests.clear();g_script=savedScript;g_leaseFixture=nullptr;
}
#include "RuntimeCombatObservationTests.inl"

void TestDeferredCombatLease() {
    const auto savedScript=g_script;
    for(const auto kind:{4u,5u}) {
        LeaseFixture f;ResetLease(f);std::string error;f.fault="deferred-combat";Check(ArmLease(f,error),"arm failed");
        g_requests.push_back({3,499,"unrelated"});
        CombatLeaseEndCapture({static_cast<std::uint16_t>(kind),402,""},LeaseIo());
        Check(CombatLeaseActive() && g_capture && g_events.empty() && f.calls.size()==3,"end did not defer after exactly three mutations");
        Check(CombatLeaseBlocksMutation({2,450,"new-capture"}) &&
            CombatLeaseBlocksMutation({3,451,"player.SetAV Health 1"}) &&
            CombatLeaseBlocksMutation({16,452,"set-position\t@player\t14\tX\t1"}) &&
            !CombatLeaseBlocksMutation({15,453,"read-reference-state\t@player\t14"}) &&
            !CombatLeaseBlocksMutation({4,454,""}),"pending observation weakened mutation/new-capture gate");
        const auto reads=f.reads;const auto started=g_combatLease.cleanupStarted;
        ServiceCombatLease(started+10,LeaseIo());Check(f.reads==reads && f.calls.size()==3,"same-frame service repeated engine work");
        ++g_frame;ServiceCombatLease(started+20,LeaseIo());
        Check(CombatLeaseActive() && g_events.empty() && f.calls.size()==3,"mismatch did not remain pending");
        f.combat[0]=f.combat[1]=false;++g_frame;ServiceCombatLease(started+30,LeaseIo());
        Check(!CombatLeaseActive() && !g_capture && g_requests.empty() && f.calls.size()==3,"settled cleanup did not finish without mutation retry");
        Check(g_events.size()==2 && g_events[0].json.find("combat-cleanup")!=std::string::npos &&
            g_events[1].json.find("capture-end")!=std::string::npos,"terminal/footer order changed");
        Check(g_lastCombatCleanup.find("\"status\":\"observed\",\"trigger\"")!=std::string::npos &&
            g_lastCombatCleanup.find("\"initialReadback\":{\"statistic\":\"IsInCombat\",\"status\":\"observed\",\"value\":1")!=std::string::npos &&
            g_lastCombatCleanup.find("\"observationFrames\":2")!=std::string::npos,"immediate/final observation evidence missing");
        SaveLeaseCase(kind==4?"deferred-stop":"deferred-cancel");
    }
    {
        LeaseFixture f;ResetLease(f);std::string error;f.fault="deferred-combat";Check(ArmLease(f,error),"arm failed");
        CombatLeaseRequestCleanup("stop");ServiceCombatLease(f.now,LeaseIo());
        const auto deadline=g_combatLease.observationDeadline;
        ++g_frame;ServiceCombatLease(deadline-1,LeaseIo());Check(CombatLeaseActive(),"timeout fired early");
        f.combat[0]=f.combat[1]=false;++g_frame;ServiceCombatLease(deadline,LeaseIo());
        Check(!CombatLeaseActive() && f.calls.size()==3 && g_lastCombatCleanup.find("cleanup-observation-timeout")!=std::string::npos &&
            g_lastCombatCleanup.find("\"status\":\"failed\",\"trigger\"")!=std::string::npos,"late sample promoted bounded success");
        SaveLeaseCase("deferred-timeout");
    }
    for(const std::string fault:{"pointer","load-epoch","profile"}) {
        LeaseFixture f;ResetLease(f);std::string error;f.fault="deferred-combat";Check(ArmLease(f,error),"arm failed");
        CombatLeaseRequestCleanup("stop");ServiceCombatLease(f.now,LeaseIo());
        if(fault=="pointer")f.fault="pointer";
        if(fault=="load-epoch")++g_gameLoadEpoch;
        if(fault=="profile")f.profile=false;
        f.combat[0]=f.combat[1]=false;++g_frame;ServiceCombatLease(f.now+20,LeaseIo());
        Check(!CombatLeaseActive() && f.calls.size()==3 && g_lastCombatCleanup.find("\"status\":\"unavailable\",\"trigger\"")!=std::string::npos,
            "identity/lifecycle loss became deferred success");
        SaveLeaseCase("deferred-"+fault);
    }
    {
        LeaseFixture f;ResetLease(f);std::string error;f.fault="deferred-combat";Check(ArmLease(f,error),"arm failed");
        CombatLeaseEndCapture({4,402,""},LeaseIo());
        const auto started=g_combatLease.cleanupStarted;
        g_capture=false;g_connected=false;++g_captureGeneration;++g_connectionGeneration;
        Check(CombatLeaseBlocksMutation({2,500,"new-session"}),"new capture admitted before old cleanup");
        g_connected=true;g_capture=true;g_session="new-origin";
        f.combat[0]=f.combat[1]=false;++g_frame;ServiceCombatLease(started+20,LeaseIo());
        Check(g_events.empty() && g_capture && !CombatLeaseActive() && f.calls.size()==3 &&
            g_lastCombatCleanup.find("fixture-lease-origin")!=std::string::npos &&
            g_lastCombatCleanup.find("\"requestId\":401")!=std::string::npos,"old receipt/end entered new connection");
        SaveLeaseCase("deferred-disconnect-reconnect");
    }
    {
        LeaseFixture f;ResetLease(f);std::string error;f.fault="deferred-combat";Check(ArmLease(f,error),"arm failed");
        CombatLeaseEndCapture({4,402,""},LeaseIo());
        ServiceCombatLease(g_combatLease.cleanupStarted+1,LeaseIo(),true);
        Check(!CombatLeaseActive() && f.calls.size()==3 && g_events.size()==2 &&
            g_lastCombatCleanup.find("\"status\":\"unavailable\",\"trigger\"")!=std::string::npos &&
            g_lastCombatCleanup.find("engine-exiting")!=std::string::npos,"teardown fabricated observation or repeated mutation");
        SaveLeaseCase("deferred-teardown");
    }
    {
        LeaseFixture f;ResetLease(f);std::string error;f.fault="deferred-combat";Check(ArmLease(f,error),"arm failed");
        CombatLeaseEndCapture({4,402,""},LeaseIo());g_combatLease.observationDeadline=GetTickCount64()-1;
        Check(CombatLeaseHello().find("awaiting-game-thread-cleanup")!=std::string::npos &&
            CombatLeaseActive() && g_lastCombatCleanup=="null" && g_events.empty(),"stalled observer fabricated terminal");
        SaveLeaseCase("deferred-no-game-loop");
    }
    {
        LeaseFixture f;ResetLease(f);std::string error;f.fault="deferred-combat";Check(ArmLease(f,error),"arm failed");
        TypedOperation op;
        Check(CombatLeaseStopCore({16,402,""},op,f.attacker,601,LeaseIo()),"explicit stop not handled");
        Check(CombatLeaseStopCore({16,403,""},op,f.target,601,LeaseIo()),"second actor stop not handled");
        Check(g_events.empty() && f.calls.size()==3,"explicit stop acknowledged ahead of cleanup");
        f.combat[0]=f.combat[1]=false;++g_frame;ServiceCombatLease(g_combatLease.cleanupStarted+20,LeaseIo());
        Check(g_events.size()==3 && g_events[0].json.find("combat-cleanup")!=std::string::npos &&
            g_events[1].json.find("\"requestId\":402")!=std::string::npos &&
            g_events[2].json.find("\"requestId\":403")!=std::string::npos && f.calls.size()==3,"explicit stop completion lost original requests");
        SaveLeaseCase("deferred-explicit-stop");
    }
    g_combatLease={};g_lastCombatCleanup="null";g_events.clear();g_requests.clear();g_script=savedScript;g_leaseFixture=nullptr;
}

std::vector<std::string> g_dispatchFixtureRows;
void SaveDispatchCase(const char* name) {
    g_dispatchFixtureRows.push_back("{\"case\":"+Quote(name)+DispatchDiagnostics()+"}");
}
void TestDispatchDiagnostics() {
    const auto savedRequests=g_requests;
    const auto savedReceived=g_lastReceived,savedQueued=g_lastQueued,savedDispatched=g_lastDispatched;
    const auto savedNonDiagnostic=g_lastNonDiagnosticReceived;
    const auto savedLoop=g_lastMainGameLoop;const auto savedClear=g_lastQueueClear,savedNonempty=g_lastNonemptyQueueClear;
    const auto savedConnection=g_connectionGeneration.load(),savedCapture=g_captureGeneration.load(),savedFrame=g_frame;
    const auto savedConnected=g_connected.load(),savedActive=g_capture.load();
    auto reset=[] {
        g_requests.clear();g_lastReceived={};g_lastNonDiagnosticReceived={};g_lastQueued={};g_lastDispatched={};
        g_lastMainGameLoop={};g_lastQueueClear={};g_lastNonemptyQueueClear={};
        g_connected=true;g_capture=false;g_connectionGeneration=41;g_captureGeneration=71;g_frame=0;
    };
    auto receive=[](std::uint16_t kind,std::uint64_t id,ULONGLONG at) {
        Request request{kind,id,"fixture"};RecordReceivedRequest(request,at);return request;
    };
    reset();
    Check(!g_lastMainGameLoop.observed && !g_lastDispatched.observed && g_requests.empty(),"initial diagnostics invented game-thread progress");
    SaveDispatchCase("no-loop-yet");
    auto start=receive(2,101,100);
    Check(g_lastReceived.observed && g_lastReceived.id==101 && !g_lastReceived.queuedObserved &&
        !g_lastQueued.observed && !g_lastDispatched.observed && g_requests.empty(),"receive became queue or execution evidence");
    SaveDispatchCase("received-not-queued");
    Check(QueueRequest(start,false,101),"ordinary queue admission failed");
    Check(g_requests.size()==1 && g_requests.front().id==101 && g_lastQueued.id==101 &&
        g_lastQueued.originConnection==41 && g_lastQueued.captureAtReceipt==71 && g_lastQueued.received==100 &&
        g_lastQueued.queued==101 && !g_lastDispatched.observed,"queued Start lost origin or became execution evidence");
    SaveDispatchCase("queued-no-loop");
    RecordMainGameLoop(102);
    Check(g_lastMainGameLoop.observed && g_lastMainGameLoop.frame==1 && g_lastMainGameLoop.qpc>0 &&
        g_lastMainGameLoop.thread==GetCurrentThreadId() && g_requests.size()==1 && !g_lastDispatched.observed,
        "loop entry became command dispatch evidence");
    SaveDispatchCase("loop-before-dispatch");
    Request popped{};Check(TakeDispatchRequest(popped,103),"queued request was not taken");
    Check(popped.id==101 && g_requests.empty() && g_lastDispatched.id==101 && g_lastDispatched.frame==1 &&
        g_lastDispatched.originConnection==41 && g_lastDispatched.observedConnection==41 &&
        g_lastDispatched.captureAtReceipt==71 && g_lastDispatched.observedCapture==71 &&
        g_lastDispatched.observedAt==103,"dispatch lost request or observation provenance");
    SaveDispatchCase("dispatch-selected-request");
    Check(!TakeDispatchRequest(popped,104) && g_lastDispatched.id==101 && g_lastDispatched.observedAt==103,
        "empty queue fabricated a new dispatch");
    SaveDispatchCase("empty-dequeue-preserves-observation");
    reset();
    for(size_t i=0;i<MaxQueue;++i)Check(QueueRequest(receive(3,1000+i,200+i),false,300+i),"queue filled early");
    Check(!QueueRequest(receive(3,2000,500),false,501) && g_requests.size()==MaxQueue &&
        g_lastQueued.id==1000+MaxQueue-1 && g_lastReceived.id==2000 && !g_lastDispatched.observed,
        "full queue falsely reported overflow request queued or dispatched");
    SaveDispatchCase("full-queue-rejected-request");
    reset();
    Check(QueueRequest(receive(2,201,600),false,601) && QueueRequest(receive(3,202,602),false,603),"priority setup failed");
    Check(QueueRequest(receive(5,203,604),true,605),"priority cancel failed");
    Check(g_requests.size()==2 && g_requests.front().id==201 && g_requests.back().id==203 &&
        g_requests.front().receivedMilliseconds==600 && g_requests.front().queuedMilliseconds==601 &&
        g_lastQueueClear.reason=="cancel-priority" && g_lastQueueClear.removed==1 &&
        g_lastQueueClear.retainedStart && g_lastQueueClear.retainedId==201 && g_lastQueueClear.retainedConnection==41,
        "priority cancel changed the preserved Start or lost dropped queue evidence");
    Check(g_lastQueueClear.frontRemoved.id==202,"queue clear reported retained Start as a removed request");
    SaveDispatchCase("cancel-retains-pending-start");
    reset();
    Check(QueueRequest(receive(16,301,700),false,701),"stale capture setup failed");
    ++g_captureGeneration;RecordMainGameLoop(702);Check(TakeDispatchRequest(popped,703),"stale capture request not taken");
    Check(g_lastDispatched.captureAtReceipt==71 && g_lastDispatched.observedCapture==72,
        "new capture relabeled old request receipt");
    SaveDispatchCase("capture-epoch-keeps-origin");
    reset();
    RecordMainGameLoop(800);Check(QueueRequest(receive(2,401,801),false,802) &&
        QueueRequest(receive(4,402,803),false,804),"reconnect setup failed");
    g_connected=false;++g_captureGeneration;++g_connectionGeneration;
    {std::lock_guard<std::mutex> lock(g_mutex);ClearRequestsLocked("disconnect",805,41);}
    ++g_connectionGeneration;g_connected=true;receive(1,1,806);
    Check(g_requests.empty() && g_lastReceived.id==1 && g_lastReceived.originConnection==43 &&
        g_lastQueued.id==402 && g_lastQueued.originConnection==41 && g_lastMainGameLoop.connection==41 &&
        g_lastQueueClear.connection==41 && g_lastQueueClear.removed==2 && g_lastQueueClear.frontRemoved.id==401 &&
        g_lastNonDiagnosticReceived.id==402 && g_lastNonDiagnosticReceived.originConnection==41 && !g_lastDispatched.observed,
        "reconnect hid old queue origin or invented successful Start dispatch");
    SaveDispatchCase("reconnect-keeps-old-origin-and-clear");
    receive(6,2,807);g_connected=false;++g_captureGeneration;++g_connectionGeneration;
    {std::lock_guard<std::mutex> lock(g_mutex);ClearRequestsLocked("disconnect",808,43);}
    ++g_connectionGeneration;g_connected=true;receive(1,1,809);
    Check(g_lastQueueClear.connection==43 && g_lastQueueClear.removed==0 && !g_lastQueueClear.frontRemoved.observed &&
        g_lastNonemptyQueueClear.connection==41 && g_lastNonemptyQueueClear.removed==2 &&
        g_lastNonemptyQueueClear.frontRemoved.id==401 && g_lastNonemptyQueueClear.frontRemoved.originConnection==41 &&
        g_lastNonDiagnosticReceived.id==402 && g_lastNonDiagnosticReceived.originConnection==41,
        "empty diagnostic reconnect erased original unserviced Start evidence");
    SaveDispatchCase("empty-diagnostic-reconnect-preserves-failure");
    g_requests=savedRequests;g_lastReceived=savedReceived;g_lastQueued=savedQueued;g_lastDispatched=savedDispatched;
    g_lastNonDiagnosticReceived=savedNonDiagnostic;g_lastMainGameLoop=savedLoop;g_lastQueueClear=savedClear;
    g_lastNonemptyQueueClear=savedNonempty;g_connectionGeneration=savedConnection;
    g_captureGeneration=savedCapture;g_frame=savedFrame;g_connected=savedConnected;g_capture=savedActive;
}
#include "RuntimeGameSettingTests.inl"
#include "RuntimeScriptLocalTests.inl"
#include "RuntimeScriptLocalSdkTests.inl"
#include "RuntimeDispatchFingerprintTests.inl"
#include "RuntimeNotificationTests.inl"
#include "RuntimeWeaponCriticalTests.inl"
#include "RuntimeCreatureScalingTests.inl"
#include "RuntimeDamageInvocationTests.inl"
#include "RuntimeCriticalInvocationTests.inl"
#include "RuntimeLiveTests.inl"
}
int main(int argc,char** argv) {
    if(argc>1 && std::string(argv[1])=="--live-only") {
        try { TestLiveControl(); std::cout<<"{\"status\":\"passed\",\"suite\":\"live-control\",\"gameLaunched\":false}\n";return 0; }
        catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}
    }
    try {
        if(argc>1 && std::string(argv[1])=="--damage-invocation-only") {
            TestDamageInvocation();TestCriticalInvocationFlow();TestCriticalCallAbi();TestCriticalProbeAbi();
            std::cout<<"{\"status\":\"passed\",\"suite\":\"damage-and-critical-invocation\",\"admissionCases\":25,\"extendedCases\":27,\"epochCases\":14,\"probeCases\":12,\"criticalFlow\":\"passed\",\"criticalCallAbi\":\"passed\",\"criticalProbeAbi\":\"passed\",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--creature-scaling-only") {
            TestCreatureHealthScaling();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);
                output<<"{\"basis\":\"synthetic-owned-memory\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_creatureScalingFixtureRows.size();++i){if(i)output<<',';output<<g_creatureScalingFixtureRows[i];}
                output<<"]}\n";output.flush();Check(output.good(),"creature scaling fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"creatureScalingCases\":"<<g_creatureScalingFixtureRows.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--weapon-critical-only") {
            TestWeaponCriticalStage();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);
                output<<"{\"basis\":\"synthetic-owned-reader-and-native-abi\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_weaponCriticalFixtureRows.size();++i){if(i)output<<',';output<<g_weaponCriticalFixtureRows[i];}
                output<<"]}\n";output.flush();Check(output.good(),"weapon critical fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"weaponCriticalCases\":"<<g_weaponCriticalFixtureRows.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--script-local-sdk-only") {
            TestScriptLocalSdk();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);
                output<<"{\"basis\":\"synthetic-sdk-command-table-explicit-reference-abi\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_scriptLocalSdkRows.size();++i){if(i)output<<',';output<<g_scriptLocalSdkRows[i];}
                output<<"]}\n";output.flush();Check(output.good(),"script-local SDK fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"scriptLocalSdkCases\":"<<g_scriptLocalSdkRows.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--dispatch-fingerprint-only") {
            TestDispatchFingerprint();
            std::cout<<"{\"status\":\"passed\",\"dispatchFingerprintCases\":7,\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--script-locals-only") {
            TestScriptLocals();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);
                output<<"{\"basis\":\"synthetic-sdk-layout-storage\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_scriptLocalFixtureRows.size();++i){if(i)output<<',';output<<g_scriptLocalFixtureRows[i];}
                output<<"]}\n";output.flush();Check(output.good(),"script-local fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"scriptLocalCases\":"<<g_scriptLocalFixtureRows.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--game-settings-only") {
            TestGameSettings();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);output<<"{\"basis\":\"synthetic-setting-map-and-sdk-results\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_gmstFixtureRows.size();++i){if(i)output<<',';output<<g_gmstFixtureRows[i];}
                output<<"]}\n";output.flush();Check(output.good(),"game-setting fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"gameSettingCases\":"<<g_gmstFixtureRows.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--modifier-selectors-only") {
            TestActorModifierSelectors();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);
                output<<"{\"basis\":\"synthetic-memory-with-retained-pc007-pc025-code-bytes\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_modifierSelectorFixtureRows.size();++i){if(i)output<<',';output<<g_modifierSelectorFixtureRows[i];}
                output<<"]}\n";output.flush();Check(output.good(),"modifier selector fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"modifierSelectorCases\":"<<g_modifierSelectorFixtureRows.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--creature-reads-only") {
            TestCreatureReadInputs();TestActorEffectResolver();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);
                output<<"{\"basis\":\"synthetic-memory-and-invocation-with-retained-loaded-bodies\",\"gameLaunched\":false,\"cases\":[";
                bool first=true;
                for(const auto* rows:{&g_creatureReadFixtureRows,&g_effectFixtureRows})for(const auto& row:*rows) {
                    if(!first)output<<',';first=false;output<<row;
                }
                output<<"]}\n";output.flush();Check(output.good(),"creature read fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"creatureReadCases\":"<<g_creatureReadFixtureRows.size()+g_effectFixtureRows.size()
                <<",\"newCases\":19,\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--effects-only") {
            TestActorEffectResolver();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);
                output<<"{\"basis\":\"synthetic-memory-with-retained-pc023-pc024-code-bytes\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_effectFixtureRows.size();++i){if(i)output<<',';output<<g_effectFixtureRows[i];}
                output<<"]}\n";output.flush();Check(output.good(),"effect fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"effectResolverCases\":"<<g_effectFixtureRows.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--actor-safety-only") {
            TestActorSafetyReadbacks();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);
                output<<"{\"basis\":\"synthetic-pc-command-table-and-explicit-owner\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_safetyFixtureRows.size();++i){if(i)output<<',';output<<g_safetyFixtureRows[i];}
                output<<"]}\n";output.flush();Check(output.good(),"safety fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"actorSafetyCases\":"<<g_safetyFixtureRows.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--dispatch-only") {
            TestDispatchDiagnostics();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);output<<"{\"basis\":\"synthetic-queue-and-loop-observation\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_dispatchFixtureRows.size();++i){if(i)output<<',';output<<g_dispatchFixtureRows[i];}
                output<<"]}\n";output.flush();Check(output.good(),"dispatch fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"dispatchDiagnosticCases\":"<<g_dispatchFixtureRows.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--notifications-only") {
            TestNotifications();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);
                output<<"{\"basis\":\"synthetic-native-callback-and-queue\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_notificationFixtureRows.size();++i){if(i)output<<',';output<<g_notificationFixtureRows[i];}
                output<<"]}\n";output.flush();Check(output.good(),"notification fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"notificationCases\":"<<g_notificationFixtureRows.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--combat-observation-only") {
            TestCombatObservation();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);
                output<<"{\"basis\":\"synthetic-sdk-command-and-lease-observation\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_combatObservationCases.size();++i){if(i)output<<',';output<<g_combatObservationCases[i];}
                output<<"]}\n";output.flush();Check(output.good(),"combat observation fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"combatObservationCases\":"<<g_combatObservationCases.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        if(argc>1 && std::string(argv[1])=="--combat-only") {
            TestCombatLease();TestDeferredCombatLease();
            if(argc>2) {
                std::ofstream output(argv[2],std::ios::binary|std::ios::trunc);output<<"{\"basis\":\"synthetic-engine-io\",\"gameLaunched\":false,\"cases\":[";
                for(size_t i=0;i<g_leaseFixtureRows.size();++i){if(i)output<<',';output<<g_leaseFixtureRows[i];}
                output<<"]}\n";output.flush();Check(output.good(),"combat fixture output failed");
            }
            std::cout<<"{\"status\":\"passed\",\"combatLeaseCases\":"<<g_leaseFixtureRows.size()<<",\"gameLaunched\":false}\n";return 0;
        }
        for (size_t index=0;index<std::size(g_testCommands);++index) {
            auto& command=g_testCommands[index]; command.longName=g_commandHooks[index].name;
            command.opcode=static_cast<UInt32>(0x1000+index); command.execute=TestExecute; command.eval=TestEval;
        }
        NVSECommandTableInterface table{}; table.version=1; table.GetByName=GetTestCommand;
        g_commands=&table; g_capture=true; g_connected=true; g_captureGeneration=1;
        InstallCommandHooks();
        Check(g_messageHookMask==7 && g_conditionHookCount==8,"expected SDK handlers were not hooked");
        for (size_t index=0;index<std::size(g_testCommands);++index) {
            double result=-123.0; UInt32 offset=7;
            const bool returned=index<3
                ? g_testCommands[index].execute(nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,&result,&offset)
                : g_testCommands[index].eval(nullptr,reinterpret_cast<void*>(17),reinterpret_cast<void*>(23),&result);
            Check(!returned && result==0.0,"wrapper changed the original return or numeric zero");
            Check(offset==(index<3?8u:7u),"wrapper changed or repeated argument processing");
        }
        Check(g_executeCalls==3 && g_evalCalls==8,"an original handler was called more or less than once");
        Check(g_events.size()==11,"observations did not match the actual handler calls");
        Check(g_events[2].json.find("\"kind\":\"message-choice-read\"")!=std::string::npos &&
            g_events[2].json.find("\"value\":0")!=std::string::npos,"zero button choice was lost");
        Check(g_events[3].json.find("\"comparisonObserved\":false")!=std::string::npos,"function result was promoted to CTDA outcome");
        UInt32 messageOwner[4]{}; messageOwner[3]=0x1234;
        double button=0; UInt32 messageOffset=0;
        for (const size_t index:{1u,2u})
            g_testCommands[index].execute(nullptr,nullptr,nullptr,nullptr,reinterpret_cast<Script*>(messageOwner),nullptr,&button,&messageOffset);
        Check(g_events.back().json.find("\"candidateMessageInstance\":")!=std::string::npos &&
            g_events.back().json.find("\"attributionConfirmed\":false")!=std::string::npos &&
            g_events.back().json.find("\"messageInstance\":")==std::string::npos,"shared owner heuristic was promoted to a confirmed message instance");
        // A later plugin may replace our table entry. Teardown must preserve that replacement.
        g_testCommands[0].execute=OtherExecute;
        RestoreCommandHooks();
        Check(g_testCommands[0].execute==OtherExecute,"teardown overwrote another plugin's later hook");
        for (size_t index=1;index<std::size(g_testCommands);++index)
            Check(index<3?g_testCommands[index].execute==TestExecute:g_testCommands[index].eval==TestEval,"original handler was not restored");
        NVSEScriptInterface scripts{}; scripts.CompileExpression=CompileTestExpression; scripts.CallFunction=CallTestExpression;
        g_script=&scripts; g_numericEnabled=true; g_loadedGameObserved=true;
        const auto namespaceResult=CapturePluginNamespace("session\nAddon.esm\nInactive.esp\nFalloutNV.esm");
        Check(namespaceResult.find("\"status\":\"complete\"")!=std::string::npos &&
            namespaceResult.find("{\"index\":0,\"name\":\"FalloutNV.esm\"}")!=std::string::npos,
            "engine slot zero was lost or candidate order became load order");
        Check(CapturePluginNamespace("session\nFalloutNV.esm").find("\"status\":\"partial\"")!=std::string::npos,
            "incomplete candidate coverage became a complete namespace");
        Check(CapturePluginNamespace("session\nFalloutNV.esm\" injected").find("invalid-candidate-name")!=std::string::npos,
            "unsafe filename entered a compiled expression");
        g_loadedGameObserved=false;
        Check(CapturePluginNamespace("session\nFalloutNV.esm").find("no-successful-game-load-observed")!=std::string::npos,
            "pre-load namespace became complete");
        TestMessageInstances();
        TestRuntimeLayouts();
        TestScriptCaptureLifecycle();
        TestCommandLocationObservation();
        TestActorObservations();
        TestMenuActions();
        TestMenuInspection();
        TestTypedOperations();
        TestActorSafetyReadbacks();
        TestCalibrationInputs();
        TestBaseOverrideObservation();
        TestCreatureReadInputs();
        TestCreatureHealthScaling();
        TestDamageInvocation();
        TestCriticalInvocationFlow();
        TestCriticalCallAbi();
        TestCriticalProbeAbi();
        TestActionBoundaries();
        TestReferenceCommandObservation();
        TestMembershipReaders();
        TestGeneralCommandObservation();
        TestCommandResultKinds();
        TestEvalCaptureLifecycle();
        TestCommandFloatingEnvironment();
        TestDispatchDiagnostics();
        if(argc>1) {
            std::ofstream output(argv[1],std::ios::binary|std::ios::trunc);
            output<<"{\"schema\":\"bmt/native-command-fixture\",\"version\":1,\"basis\":\"synthetic-sdk-table\",\"gameLaunched\":false,\"coverage\":"<<g_generalFixtureCoverage<<",\"events\":[";
            for(size_t index=0;index<g_generalFixtureEvents.size();++index) {if(index)output<<',';output<<g_generalFixtureEvents[index];}
            output<<"],\"menuInspections\":[";
            for(size_t index=0;index<g_menuFixtureEvents.size();++index) {if(index)output<<',';output<<g_menuFixtureEvents[index];}
            output<<"]}\n";output.flush();Check(output.good(),"native command fixture output could not be saved");
        }
        std::cout<<"{\"status\":\"passed\",\"executeHandlers\":3,\"evalHandlers\":8,\"forwardedOnce\":true,\"zeroPreserved\":true,\"laterHookPreserved\":true,\"runtimeLayoutChecks\":11,\"scriptScopeChecks\":10,\"nestedScriptCalls\":2,\"scriptCaptureLifecycleCases\":8,\"commandLocationCases\":19,\"generalCommandTransitions\":6,\"generalCommandTable\":\"passed\",\"evalCaptureTransitions\":6,\"evalScopeCases\":4,\"commandReturnCases\":10,\"commandFloatingCases\":8,\"actorChecks\":13,\"menuAndMutationChecks\":22,\"menuInspectionCases\":"<<g_menuInspectionCases<<",\"typedOperations\":\"passed\",\"actionBoundaries\":\"passed\",\"gameLaunched\":false}\n";
        return 0;
    } catch (const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; }
}
