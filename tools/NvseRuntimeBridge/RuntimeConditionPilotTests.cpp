// Draft offline fixtures. Not built/run. No game process or fixed-address engine call.
// Build against the candidate Bridge/CommandHooks and the pinned unchanged bridge includes.
#define BMT_CONDITION_FRAME_TESTS 1
#include "NvseRuntimeBridge.cpp"
#include <iostream>
#include <stdexcept>

namespace {
void Require(bool value, const char* reason) { if (!value) throw std::runtime_error(reason); }
#include "RuntimeConditionPatchTests.inl"
void Put32(std::array<std::uint8_t,28>& raw, size_t offset, UInt32 value) { memcpy(raw.data()+offset,&value,4); }
bmt::ctda::Snapshot Fixture() {
    bmt::ctda::Snapshot snapshot;
    snapshot.stable=true; snapshot.owner={bmt::ctda::OwnerKind::Quest,0x1000,0x01000800}; snapshot.headAddress=0x1054;
    for (UInt32 row=0;row<3;++row) {
        bmt::ctda::Node node;
        node.address=row==0?0x1054:0x1100+row*8;node.itemAddress=0x2000+row*32;node.next=row==2?0:0x1100+(row+1)*8;node.hasItem=true;
        memcpy(node.raw.data(),&node.itemAddress,4);memcpy(node.raw.data()+4,&node.next,4);
        auto raw=node.item.raw;raw[0]=row==0?1:0;Put32(raw,4,0x3F800000);Put32(raw,8,72);
        Put32(raw,12,row==2?0x7000:0x8000);Put32(raw,20,row);Put32(raw,24,row==2?0x5000:0);
        node.item=bmt::ctda::ParseItem(raw);snapshot.nodes.push_back(node);
    }
    return snapshot;
}
void TestShape() {
    const condition_pilot::SourceBindings bindings{0x5000,0x6000,0x7000,0x8000};
    Require(condition_pilot::CheckFixture(Fixture(),bindings)==condition_pilot::Rejection::None,"valid authored three-row source rejected");
    enum Fault { Unstable, MissingRow, Duplicate, Padding, Global, WrongLiteral, WrongFunction, UpperFunction,
        RawSavedIdInsteadOfPointer, Parameter2, RunOn, Reference };
    for (const auto fault:{Unstable,MissingRow,Duplicate,Padding,Global,WrongLiteral,WrongFunction,UpperFunction,
                          RawSavedIdInsteadOfPointer,Parameter2,RunOn,Reference}) {
        auto snapshot=Fixture();auto& first=snapshot.nodes[0];auto raw=first.item.raw;
        switch(fault) {
        case Unstable:snapshot.stable=false;break;
        case MissingRow:snapshot.nodes.pop_back();break;
        case Duplicate:snapshot.nodes[1].itemAddress=first.itemAddress;break;
        case Padding:raw[1]=1;break;
        case Global:raw[0]|=4;break;
        case WrongLiteral:Put32(raw,4,0x7FC00001);break;
        case WrongFunction:Put32(raw,8,46);break;
        case UpperFunction:Put32(raw,8,0x10048);break;
        case RawSavedIdInsteadOfPointer:Put32(raw,12,0x104C0C);break;
        case Parameter2:Put32(raw,16,0x8000);break;
        case RunOn:Put32(raw,20,3);break;
        case Reference:Put32(raw,24,0x14);break;
        }
        first.item=bmt::ctda::ParseItem(raw);
        Require(condition_pilot::CheckFixture(snapshot,bindings)!=condition_pilot::Rejection::None,"unsupported source admitted");
    }
    for(int fault=0;fault<5;++fault) {
        auto before=Fixture(),after=before;
        if(fault==0)after.ownerHeader[8]^=1;
        if(fault==1)after.nodes[1].address+=4;
        if(fault==2)after.nodes[1].raw[4]^=1;
        if(fault==3)after.nodes[2].item.raw[12]^=1;
        if(fault==4)after.stable=false;
        Require(!condition_pilot::SameSnapshot(before,after),"changed source remained associated");
    }
    Require(condition_pilot::ExpectedSubject(0,bindings)==0x5000 &&
        condition_pilot::ExpectedSubject(1,bindings)==0x6000 &&
        condition_pilot::ExpectedSubject(2,bindings)==0x5000,"run-on binding changed");
    condition_pilot::Buffer buffer;
    for(size_t i=0;i<condition_pilot::MaxItemCalls;++i)Require(buffer.Begin(0)!=nullptr,"bounded buffer shortened");
    Require(!buffer.Begin(0) && buffer.overflow,"overflow did not remain explicit");
}
struct Floating {std::uint16_t control,status;UInt32 mxcsr;};
Floating GetFloating() {
    CommandFloatingState state;state.Save();Floating result{};
    memcpy(&result.control,state.bytes,2);memcpy(&result.status,state.bytes+2,2);memcpy(&result.mxcsr,state.bytes+24,4);return result;
}
void SetFloating(Floating value) {
    CommandFloatingState state;state.Save();memcpy(state.bytes,&value.control,2);
    memcpy(state.bytes+2,&value.status,2);memcpy(state.bytes+24,&value.mxcsr,4);state.Restore();
}
bool SameFloating(Floating a,Floating b){return a.control==b.control && a.status==b.status && a.mxcsr==b.mxcsr;}
Floating incoming{},outgoing{};
void* expectedItem=nullptr;TESObjectREFR* expectedSubject=nullptr;TESObjectREFR* expectedTarget=nullptr;bool* expectedAux=nullptr;
int originalCalls=0,transition=0;
void ChangeEpoch() {
    if(transition==1)++g_captureGeneration;
    if(transition==2)++g_connectionGeneration;
    if(transition==3)++g_gameLoadEpoch;
    if(transition==4)++g_currentRequest;
    if(transition==5)g_capture=false;
    if(transition==6)g_connected=false;
}
bool __fastcall ItemOriginal(void* item,void*,TESObjectREFR* subject,TESObjectREFR* target,bool* auxiliary) {
    Require(GetLastError()==0x6371 && SameFloating(GetFloating(),incoming),"item incoming observer effects leaked");
    Require(item==expectedItem && subject==expectedSubject && target==expectedTarget && auxiliary==expectedAux,"item arguments changed");
    ++originalCalls;*auxiliary=true;ChangeEpoch();SetFloating(outgoing);outgoing=GetFloating();SetLastError(0x6372);return false;
}
bool __cdecl CompareOriginal(UInt32 op,float actual,float compare) {
    UInt32 a=0,b=0;memcpy(&a,&actual,4);memcpy(&b,&compare,4);
    Require(GetLastError()==0x6371 && SameFloating(GetFloating(),incoming),"comparison incoming observer effects leaked");
    Require(op==0 && a==0x3F000000 && b==0x3F800000,"comparison operands changed");
    ++originalCalls;ChangeEpoch();SetFloating(outgoing);outgoing=GetFloating();SetLastError(0x6372);return false;
}
void TestForwarding() {
    CommandFloatingGuard outer;
    // Eight discriminating states on both distinct ABIs, rather than a Cartesian metadata matrix.
    for(const bool comparison:{false,true})for(int state=0;state<8;++state) {
        CommandObserverFloatingEnvironment();g_capture=true;g_connected=true;
        g_captureGeneration=10;g_connectionGeneration=20;g_gameLoadEpoch=30;g_currentRequest=40;
        ConditionInvocation scope;scope.epoch=ConditionCurrentEpoch();scope.invocation=1;scope.before=Fixture();
        std::array<std::uint8_t,28> item{};bool auxiliary=false;
        expectedItem=item.data();expectedSubject=reinterpret_cast<TESObjectREFR*>(0x5000);
        expectedTarget=reinterpret_cast<TESObjectREFR*>(0x6000);expectedAux=&auxiliary;
        scope.before.nodes[0].itemAddress=reinterpret_cast<UInt32>(item.data());
        scope.item=comparison?scope.observations.Begin(0):nullptr;
        g_conditionInvocation=state==7?nullptr:&scope;transition=state;originalCalls=0;
        incoming={0x047B,0,0x5D81};outgoing={0x0A5F,0x21,0x2FA0};SetFloating(incoming);incoming=GetFloating();SetLastError(0x6371);
        const bool result=comparison?ConditionForwardComparison(CompareOriginal,0,0.5f,1.0f):
            ConditionForwardItem<0>(reinterpret_cast<ConditionItemFunction>(&ItemOriginal),item.data(),expectedSubject,expectedTarget,&auxiliary);
        const auto lastError=GetLastError();const auto actual=GetFloating();CommandObserverFloatingEnvironment();
        Require(!result && originalCalls==1 && lastError==0x6372 && SameFloating(actual,outgoing),"original result/LastError/FP state changed");
        if(!comparison)Require(auxiliary,"original auxiliary write lost");
        if(state==0)Require(comparison?scope.item->comparison.returned:scope.observations.items[0].returned,"current return missing");
        if(state>0 && state<7)Require(comparison?!scope.item->comparison.returned:!scope.observations.items[0].returned,"stale return accepted");
        g_conditionInvocation=nullptr;
    }
}
void TestRequestOffAndParse() {
    TypedIdentity owner,subject,target;
    Require(ConditionParse({22,1,"quest-conditions-v1\tBMTConditionControl.esp\t800\t@player\t14\tFalloutNV.esm\t104C0F"},owner,subject,target),"Doc context rejected");
    Require(ConditionParse({22,1,"quest-conditions-v1\tBMTConditionControl.esp\t800\t@player\t14\t@player\t14"},owner,subject,target),"player negative control rejected");
    for(const char* payload:{"quest-conditions-v1\tBMTConditionControl.esp\t801\t@player\t14\t@player\t14",
        "quest-conditions-v1\tBMTConditionControl.esp\t800\t@player\t14\tFalloutNV.esm\t104C0C",
        "quest-conditions-v1\tBMTConditionControl.esp\t800\t@player\t14\t@player\t14\textra"})
        Require(!ConditionParse({22,1,payload},owner,subject,target),"payload scope escaped");
    g_capture=true;g_connected=true;g_events.clear();g_currentRequest=90;g_captureGeneration=91;
    ConditionOwnerRequest({22,90,"quest-conditions-v1\tBMTConditionControl.esp\t800\t@player\t14\t@player\t14"});
    Require(!ConditionPilotAvailable() && g_events.size()==1 &&
        g_events.front().json.find("owner-condition-pilot-not-admitted")!=std::string::npos,"unreviewed draft became callable");
}
void TestExpectedIdentities() {
    condition_pilot::SourceBindings bindings{0x5000,0x6000,0x7000,0x8000};
    bindings.playerBaseIdentity={0x7000,7,0x2A};bindings.docBaseIdentity={0x8000,0x104C0C,0x2A};
    bindings.playerIdentity={{0x5000,0x14,0x3B},bindings.playerBaseIdentity};
    bindings.targetIdentity={{0x6000,0x104C0F,0x3B},bindings.docBaseIdentity};
    const auto valid = [&](UInt32 row) {
        condition_pilot::ItemSample item;
        item.sourceRow=row;item.entered=true;item.returned=true;item.rawBeforeReadable=true;item.rawAfterReadable=true;
        item.subject=bindings.player;item.target=bindings.target;
        const auto& subject=row==1?bindings.targetIdentity:bindings.playerIdentity;
        const auto& parameter=row==2?bindings.playerBaseIdentity:bindings.docBaseIdentity;
        auto& eval=item.eval;eval.entered=true;eval.returned=true;eval.handlerReturned=true;eval.resultReadable=true;
        eval.identityBefore=true;eval.identityStable=true;eval.resultBits=0x3FF0000000000000ull;
        eval.subject=subject.form.address;eval.subjectId=subject.form.id;eval.subjectType=subject.form.type;
        eval.subjectBaseAddress=subject.base.address;eval.subjectBaseId=subject.base.id;eval.subjectBaseType=subject.base.type;
        eval.parameter1=parameter.address;eval.parameter1Id=parameter.id;eval.parameter1Type=parameter.type;
        item.comparison.entered=true;item.comparison.returned=true;item.comparison.comparisonBits=0x3F800000;
        return item;
    };
    for(UInt32 row=0;row<3;++row)Require(condition_pilot::CompleteObservedItem(valid(row),bindings),"expected resolved identity rejected");
    for(int fault=0;fault<7;++fault) {
        auto item=valid(0);
        switch(fault) {
        case 0:item.eval.subjectBaseAddress+=0x100;break; // Same ID/type at a different base pointer.
        case 1:++item.eval.subjectBaseId;break;
        case 2:++item.eval.subjectBaseType;break;
        case 3:++item.eval.subjectId;break;
        case 4:++item.eval.subjectType;break;
        case 5:++item.eval.parameter1Id;break;
        case 6:++item.eval.parameter1Type;break;
        }
        Require(!condition_pilot::CompleteObservedItem(item,bindings),"wrong expected identity remained complete");
    }
    // Actual observer callbacks: an alias base with identical FormID/type is still changed.
    g_capture=true;g_connected=true;g_captureGeneration=10;g_connectionGeneration=20;g_gameLoadEpoch=30;g_currentRequest=40;
    ConditionInvocation scope;scope.epoch=ConditionCurrentEpoch();scope.invocation=51;scope.item=scope.observations.Begin(0);
    UInt32 actor[12]{},base[4]{},alias[4]{};actor[1]=0x3B;actor[3]=0x14;base[1]=alias[1]=0x2A;base[3]=alias[3]=7;
    actor[8]=reinterpret_cast<UInt32>(&base[0]);g_conditionInvocation=&scope;
    const auto token=ConditionEvalEnter(10,reinterpret_cast<TESObjectREFR*>(&actor[0]),&base[0],nullptr);
    actor[8]=reinterpret_cast<UInt32>(&alias[0]);double result=1.0;ConditionEvalReturn(token,true,&result);
    Require(scope.item->eval.returned && !scope.item->eval.identityStable,"same-ID base alias escaped callback identity gate");
    g_conditionInvocation=nullptr;
}
#include "RuntimeConditionFrameTests.inl"
#include "RuntimeQuestConditionTests.inl"
}
int main(int argc, char** argv) {
    try {
        if(argc==2 && std::string(argv[1])=="--quest-v2-only") {
            const auto cases=TestQuestV2();
            std::cout<<"{\"status\":\"Passed\",\"questV2Cases\":"<<cases<<",\"gameLaunched\":false,\"hooksInstalled\":false}\n";return 0;
        }
        if (argc==2 && std::string(argv[1])=="--abi-only") {
            TestForwarding();
            std::cout<<"{\"status\":\"Passed\",\"abiCases\":16,\"gameLaunched\":false,\"hooksInstalled\":false}\n"; return 0;
        }
        if (argc==2 && std::string(argv[1])=="--identity-only") {
            TestExpectedIdentities();
            std::cout<<"{\"status\":\"Passed\",\"identityCases\":11,\"gameLaunched\":false,\"hooksInstalled\":false}\n";return 0;
        }
        TestShape();TestForwarding();TestRequestOffAndParse();TestExpectedIdentities();TestPatchTransaction();TestThreadInventory();TestFrameAdmission();TestFrameLifecycle();TestFrameSequence();TestFrameAbi();
        std::cout<<"{\"status\":\"Passed\",\"sourceCases\":13,\"snapshotCases\":5,\"runOnAndOverflowCases\":2,\"abiCases\":16,\"payloadCases\":6,\"identityCases\":11,\"transactionCases\":20,\"threadInventoryCases\":9,\"frameAdmissionCases\":12,\"frameLifecycleCases\":10,\"frameSequenceCases\":9,\"frameAbiCases\":8,\"gameLaunched\":false,\"hooksInstalled\":false}\n";return 0;
    } catch(const std::exception& error) {CommandObserverFloatingEnvironment();std::cerr<<error.what()<<'\n';return 1;}
}
