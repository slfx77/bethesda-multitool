#define BMT_LIVE_INPUT_TEST
#include "RuntimeLiveInput.cpp"
#include <iostream>
#include <stdexcept>

namespace {
void Require(bool value,const char* why){if(!value)throw std::runtime_error(why);}
using namespace bmt_live_input;
using namespace bmt_live_input::testing;
void Begin(const std::string& json){std::string error;Require(StartSequence(json,error),error.c_str());}
class FakeDevice final:public IDirectInputDevice8A {
public:
    unsigned physicalReads=0;HRESULT acquireResult=DIERR_OTHERAPPHASPRIO;DWORD cooperativeFlags=0;
    std::array<BYTE,256> physical{};ULONG references=1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID,void**) override{return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override{return ++references;}
    ULONG STDMETHODCALLTYPE Release() override{const auto n=--references;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE GetCapabilities(LPDIDEVCAPS) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE EnumObjects(LPDIENUMDEVICEOBJECTSCALLBACKA,LPVOID,DWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetProperty(REFGUID,LPDIPROPHEADER) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE SetProperty(REFGUID,LPCDIPROPHEADER) override{return DI_OK;}
    HRESULT STDMETHODCALLTYPE Acquire() override{return acquireResult;}
    HRESULT STDMETHODCALLTYPE Unacquire() override{return DI_OK;}
    HRESULT STDMETHODCALLTYPE GetDeviceState(DWORD size,LPVOID data) override{++physicalReads;if(!data || size>physical.size())return DIERR_INVALIDPARAM;std::memcpy(data,physical.data(),size);return DI_OK;}
    HRESULT STDMETHODCALLTYPE GetDeviceData(DWORD,LPDIDEVICEOBJECTDATA,LPDWORD count,DWORD) override{++physicalReads;if(count)*count=0;return DI_OK;}
    HRESULT STDMETHODCALLTYPE SetDataFormat(LPCDIDATAFORMAT) override{return DI_OK;}
    HRESULT STDMETHODCALLTYPE SetEventNotification(HANDLE) override{return DI_OK;}
    HRESULT STDMETHODCALLTYPE SetCooperativeLevel(HWND,DWORD flags) override{cooperativeFlags=flags;return DI_OK;}
    HRESULT STDMETHODCALLTYPE GetObjectInfo(LPDIDEVICEOBJECTINSTANCEA,DWORD,DWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetDeviceInfo(LPDIDEVICEINSTANCEA) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE RunControlPanel(HWND,DWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE Initialize(HINSTANCE,DWORD,REFGUID) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE CreateEffect(REFGUID,LPCDIEFFECT,LPDIRECTINPUTEFFECT*,LPUNKNOWN) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE EnumEffects(LPDIENUMEFFECTSCALLBACKA,LPVOID,DWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetEffectInfo(LPDIEFFECTINFOA,REFGUID) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetForceFeedbackState(LPDWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE SendForceFeedbackCommand(DWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE EnumCreatedEffectObjects(LPDIENUMCREATEDEFFECTOBJECTSCALLBACK,LPVOID,DWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE Escape(LPDIEFFESCAPE) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE Poll() override{return acquireResult;}
    HRESULT STDMETHODCALLTYPE SendDeviceData(DWORD,LPCDIDEVICEOBJECTDATA,LPDWORD,DWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE EnumEffectsInFile(LPCSTR,LPDIENUMEFFECTSINFILECALLBACK,LPVOID,DWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE WriteEffectToFile(LPCSTR,DWORD,LPDIFILEEFFECT,DWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE BuildActionMap(LPDIACTIONFORMATA,LPCSTR,DWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE SetActionMap(LPDIACTIONFORMATA,LPCSTR,DWORD) override{return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetImageInfo(LPDIDEVICEIMAGEINFOHEADERA) override{return E_NOTIMPL;}
};
struct Fixture {
    FakeDevice* real=new FakeDevice;
    bmt_live_input::Device<false>* device;
    std::vector<DIOBJECTDATAFORMAT> objects;
    DIDATAFORMAT format{};
    Fixture(bool mouse):device(new bmt_live_input::Device<false>(real,mouse)),objects(mouse?11:256) {
        for(size_t i=0;i<objects.size();++i){objects[i].dwOfs=static_cast<DWORD>(mouse?(i<3?i*4:12+i-3):i);objects[i].dwType=(mouse && i<3?DIDFT_RELAXIS:DIDFT_BUTTON)|DIDFT_MAKEINSTANCE(static_cast<DWORD>(i));}
        format={sizeof(DIDATAFORMAT),sizeof(DIOBJECTDATAFORMAT),static_cast<DWORD>(mouse?DIDF_RELAXIS:DIDF_ABSAXIS),static_cast<DWORD>(mouse?sizeof(DIMOUSESTATE2):256),static_cast<DWORD>(objects.size()),objects.data()};
        Require(device->SetDataFormat(&format)==DI_OK,"SetDataFormat");Require(device->SetCooperativeLevel(nullptr,DISCL_FOREGROUND|DISCL_EXCLUSIVE)==DI_OK,"SetCooperativeLevel");
        Require(real->cooperativeFlags==(DISCL_BACKGROUND|DISCL_NONEXCLUSIVE),"background cooperative flags");Require(device->Acquire()==DI_OK,"virtual Acquire");
    }
    ~Fixture(){device->Release();}
};
void SchedulerCases() {
    testing::Reset(1000);testing::SetForeground(false);
    Begin(R"({"version":1,"steps":[{"type":"input","durationMs":500,"keys":["W","LSHIFT"],"mouseButtons":["left"],"mouse":{"dx":120,"dy":-25}}]})");
    testing::TickAt(1250);auto sample=Consume();Require(sample.keys[DIK_W] && sample.keys[DIK_LSHIFT] && sample.buttons[0],"held chord");Require(sample.dx==60 && sample.dy==-12,"proportional mouse");
    Require(Consume().dx==0,"mouse consumed once");testing::TickAt(1500);sample=Consume();Require(sample.dx==60 && sample.dy==-13,"signed mouse remainder");
    testing::TickAt(1501);Require(!Active() && !Consume().keys[DIK_W],"completion releases");Require(TakeCompletionJson().find("completed")!=std::string::npos,"completion result");
    testing::Reset(0);Begin(R"({"version":1,"steps":[{"type":"input","durationMs":100,"keys":["W"]},{"type":"input","durationMs":100,"keys":["E"]}]})");
    testing::TickAt(100);testing::TickAt(101);sample=Consume();Require(!sample.keys[DIK_W] && sample.keys[DIK_E],"complete state replacement");Release();Require(!Active() && !Consume().keys[DIK_E],"Stop releases immediately");Require(TakeCompletionJson().find("cancelled")!=std::string::npos,"cancelled result");
    testing::Reset(0);Begin(R"({"version":1,"steps":[{"type":"input","durationMs":100,"keys":["W"]}]})");
    const char* bad[]={R"({"version":2,"steps":[]})",R"({"version":1,"steps":[{"type":"input","durationMs":1.5}]})",R"({"version":1,"steps":[{"type":"input","durationMs":1,"keys":["BAD"]}]})",R"({"version":1,"steps":[{"type":"input","durationMs":1,"mouseButtons":["BAD"]}]})",R"({"version":1,"steps":[{"type":"wait","durationMs":1,"keys":["W"]}]})"};
    for(const auto* json:bad){std::string error;Require(!StartSequence(json,error) && !error.empty(),"reject malformed sequence");Require(Active() && Consume().keys[DIK_W],"rejection preserves active sequence");}
    Release();testing::Reset(0);unsigned calls=0;SetSnapshotCallback([&](const std::vector<std::string>& fields){++calls;Require(fields.at(0)=="player.position","snapshot fields");return "{\"x\":1}";});
    Begin(R"({"sequence":{"version":1,"steps":[{"type":"input","durationMs":10,"mouse":{"dx":5}},{"type":"snapshot","fields":["player.position"]}]}})");
    testing::TickAt(10);Require(calls==0 && Consume().dx==5,"final input available before snapshot");testing::TickAt(11);Require(calls==1 && !Active(),"snapshot after input frame");Require(TakeCompletionJson().find("\"x\":1")!=std::string::npos,"snapshot in result");
    testing::Reset(0);Begin(R"({"version":1,"steps":[{"type":"input","durationMs":10,"keys":["W"],"mouse":{"dx":5}}]})");
    testing::TickAt(10);testing::TickAt(1011);Require(!Active() && TakeCompletionJson().find("not polled")!=std::string::npos,"unconsumed mouse fails instead of false completion");
}
void DeviceCases() {
    testing::Reset(0);testing::SetForeground(false);Fixture keyboard(false),mouse(true);
    Begin(R"({"version":1,"steps":[{"type":"input","durationMs":100,"keys":["W"],"mouseButtons":["left"],"mouse":{"dx":20}}]})");
    BYTE immediate[256]{};Require(keyboard.device->GetDeviceState(sizeof(immediate),immediate)==DI_OK && immediate[DIK_W],"virtual immediate keyboard");
    DIDEVICEOBJECTDATA events[8]{};DWORD count=8;Require(keyboard.device->GetDeviceData(sizeof(events[0]),events,&count,DIGDD_PEEK)==DI_OK && count==1 && events[0].dwData==0x80,"buffered press peek");
    count=8;Require(keyboard.device->GetDeviceData(sizeof(events[0]),events,&count,0)==DI_OK && count==1,"peek retained event");
    testing::TickAt(50);DIMOUSESTATE2 state{};Require(mouse.device->GetDeviceState(sizeof(state),&state)==DI_OK && state.lX==10,"immediate mouse");
    count=8;Require(mouse.device->GetDeviceData(sizeof(events[0]),events,&count,0)==DI_OK && count==1 && events[0].dwOfs==DIMOFS_BUTTON0,"buffered reader does not repeat mouse delta");
    Release();count=8;Require(keyboard.device->GetDeviceData(sizeof(events[0]),events,&count,0)==DI_OK && count==1 && events[0].dwOfs==DIK_W && events[0].dwData==0,"Stop emits buffered release");
    count=8;Require(mouse.device->GetDeviceData(sizeof(events[0]),events,&count,0)==DI_OK && count==1 && events[0].dwData==0,"Stop emits mouse release");
    Require(mouse.device->GetDeviceState(1,&state)==DIERR_INVALIDPARAM,"invalid format rejected");
    keyboard.real->physical[DIK_A]=0x80;Require(keyboard.device->GetDeviceState(sizeof(immediate),immediate)==DI_OK && !immediate[DIK_A],"background physical typing suppressed");
    testing::SetForeground(true);Require(keyboard.device->GetDeviceState(sizeof(immediate),immediate)==DI_OK && immediate[DIK_A],"foreground physical input restored");
    testing::SetForeground(false);count=8;Require(keyboard.device->GetDeviceData(sizeof(events[0]),events,&count,0)==DI_OK && count==1 && events[0].dwOfs==DIK_A && !events[0].dwData,"background transition releases physical held key");
    keyboard.device->Unacquire();Require(keyboard.device->GetDeviceState(sizeof(immediate),immediate)==DIERR_NOTACQUIRED,"virtual unacquire respected");
    void* identity=nullptr;Require(mouse.device->QueryInterface(IID_IUnknown,&identity)==S_OK && identity==static_cast<IDirectInputDevice8A*>(mouse.device),"COM identity");static_cast<IUnknown*>(identity)->Release();
}
void DeviceFailureCases() {
    testing::Reset(0);testing::SetForeground(false);Fixture keyboard(false);
    const std::string hold=R"({"version":1,"steps":[{"type":"input","durationMs":1000,"keys":["W"]}]})";
    BYTE state[256]{};
    for(int failure=0;failure<3;++failure) {
        Begin(hold);
        if(failure==0)Require(keyboard.device->GetDeviceState(1,state)==DIERR_INVALIDPARAM,"invalid immediate read result");
        else if(failure==1)Require(keyboard.device->GetDeviceData(sizeof(DIDEVICEOBJECTDATA),nullptr,nullptr,0)==DIERR_INVALIDPARAM,"invalid buffered read result");
        else {auto unsupported=keyboard.format;unsupported.dwDataSize=1;keyboard.device->SetDataFormat(&unsupported);}
        Require(!Active() && !Consume().keys[DIK_W],"device failure retained synthetic hold");
        Require(TakeCompletionJson().find("cancelled")!=std::string::npos,"device failure did not cancel sequence");
    }
    keyboard.device->SetDataFormat(&keyboard.format);Require(keyboard.device->Acquire()==DI_OK,"background virtual acquire regressed");
    Begin(hold);Require(keyboard.device->Poll()==DI_OK && Active(),"physical acquisition failure cancelled valid virtual input");Release();
}
void DiagnosticCases() {
    for(bool isMouse:{false,true})for(bool foreground:{false,true}) {
        testing::Reset(0);testing::SetForeground(false);Fixture fixture(isMouse);testing::SetForeground(foreground);
        auto diagnostic=[](){bmt_live_json::Value value;std::string error;Require(bmt_live_json::Parse(DiagnosticsJson(),value,error),"diagnostic JSON");return value;};
        const auto before=diagnostic();
        const std::string hold=R"({"version":1,"steps":[{"type":"input","durationMs":1000,"keys":["ENTER"],"mouseButtons":["left"]},{"type":"wait","durationMs":1000}]})";
        Begin(hold);BYTE state[256]{};const DWORD size=isMouse?sizeof(DIMOUSESTATE2):sizeof(state);
        for(int i=0;i<2;++i)Require(fixture.device->GetDeviceState(size,state)==DI_OK,"diagnostic immediate read");
        DIDEVICEOBJECTDATA event{};DWORD count=1;
        Require(fixture.device->GetDeviceData(sizeof(event),&event,&count,DIGDD_PEEK)==DI_OK && count==1,"diagnostic peek");
        count=1;Require(fixture.device->GetDeviceData(sizeof(event),&event,&count,0)==DI_OK && count==1,"diagnostic press delivery");
        testing::TickAt(1000);testing::TickAt(1001);Require(fixture.device->GetDeviceState(size,state)==DI_OK,"diagnostic immediate release");
        count=1;Require(fixture.device->GetDeviceData(sizeof(event),&event,&count,0)==DI_OK && count==1 && event.dwData==0,"diagnostic release delivery");
        Release();Begin(hold);count=1;Require(fixture.device->GetDeviceData(sizeof(event),nullptr,&count,0)==DI_OK && count==1,"diagnostic discard");Release();
        const auto after=diagnostic();
        const auto delta=[&](const std::string& name){const auto* a=after.Get(name);const auto* b=before.Get(name);Require(a && b && a->type==bmt_live_json::Value::Number && b->type==bmt_live_json::Value::Number,"missing diagnostic counter");return a->number-b->number;};
        const std::string device=isMouse?"Mouse":"Keyboard";
        const std::string transition=isMouse?"MouseButtonTransitions":"KeyboardTransitions";
        Require(delta("immediate"+device+"Polls")==3 && delta("buffered"+device+"Polls")==4,"immediate/buffered poll attribution");
        Require(delta("backgroundImmediate"+device+"Polls")== (foreground?0:3) && delta("backgroundBuffered"+device+"Polls")== (foreground?0:4),"background poll attribution");
        Require(delta("deliveredImmediate"+transition)==2,"immediate hold counted as repeated transitions");
        Require(delta("deliveredBuffered"+transition)==2,"peek or discard counted as delivered transition");
    }
}
void SnapshotLimitCases() {
    const auto snapshot=[](size_t size){const std::string prefix="{\"payload\":\"",suffix="\"}";return prefix+std::string(size-prefix.size()-suffix.size(),'x')+suffix;};
    for(int boundaryCase:{-1,0,1,2}) {
        testing::Reset(0);size_t calls=0;
        const auto first=boundaryCase==2?MaxRetainedSnapshotBytes:size_t(20000);
        const auto second=boundaryCase==2?size_t(20):MaxRetainedSnapshotBytes+boundaryCase-first-1;
        SetSnapshotCallback([&](const std::vector<std::string>&){return snapshot(calls++?second:first);});
        Begin(R"({"version":1,"steps":[{"type":"snapshot","fields":["frame"]},{"type":"snapshot","fields":["frame"]}]})");
        testing::TickAt(1);Require(Active(),"first bounded snapshot completed sequence early");testing::TickAt(2);
        Require(!Active() && calls==2,"snapshot limit did not terminate sequence");
        const auto completionJson=TakeCompletionJson();bmt_live_json::Value result;std::string error;
        Require(completionJson.size()<65536-1024 && bmt_live_json::Parse(completionJson,result,error),"bounded snapshot completion exceeds protocol/parser limit");
        const bool overflow=boundaryCase>0;
        Require(result.Get("status")->StringOr()==(overflow?"failed":"completed"),"snapshot limit boundary status");
        Require(result.Get("snapshots")->array.size()==(overflow?1u:2u),"overflowing snapshot was retained");
        if(overflow)Require(result.Get("error") && result.Get("error")->StringOr()=="snapshot-result-limit","snapshot overflow lost explicit reason");
    }
}
}
int main(){try{SchedulerCases();DeviceCases();DeviceFailureCases();DiagnosticCases();SnapshotLimitCases();std::cout<<"RuntimeLiveInputTests passed (scheduler, cancellation, snapshots and limits, DirectInput immediate/buffered, background isolation, device failure, diagnostics, COM).\n";return 0;}catch(const std::exception& error){std::cerr<<error.what()<<'\n';return 1;}}
