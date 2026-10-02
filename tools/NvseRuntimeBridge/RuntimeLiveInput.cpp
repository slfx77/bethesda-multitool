#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define DIRECTINPUT_VERSION 0x0800
#include <Windows.h>
#include <dinput.h>
#pragma comment(lib,"dxguid.lib")
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <deque>
#include <limits>
#include <mutex>
#include <new>
#include <sstream>
#include <utility>
#include "RuntimeLiveInput.h"
#include "RuntimeLiveJson.h"

namespace bmt_live_input {
namespace {
using Json=bmt_live_json::Value;
struct Step {
    enum Kind { Input, Wait, Snapshot } kind=Wait;
    std::uint64_t duration=0;
    std::array<BYTE,256> keys{};
    std::array<BYTE,8> buttons{};
    LONG dx=0,dy=0;
    std::vector<std::string> fields;
};
std::mutex mutex;
std::vector<Step> steps;
size_t stepIndex=0;
std::uint64_t started=0,stepStarted=0,generation=0;
bool active=false,boundary=false,installed=false;
std::array<BYTE,256> keys{};
std::array<BYTE,8> buttons{};
LONG pendingX=0,pendingY=0,emittedX=0,emittedY=0;
std::string completion;
std::vector<std::string> snapshots;
// Leave room for completion metadata and the native protocol envelope.
constexpr size_t MaxRetainedSnapshotBytes=48*1024;
size_t snapshotBytes=0;
std::function<std::string(const std::vector<std::string>&)> snapshotCallback;
std::atomic<std::uint64_t> ticks{0},backgroundTicks{0},keyboardPolls{0},mousePolls{0},backgroundKeyboardPolls{0},backgroundMousePolls{0};
std::atomic<std::uint64_t> immediateKeyboardPolls{0},bufferedKeyboardPolls{0},immediateMousePolls{0},bufferedMousePolls{0};
std::atomic<std::uint64_t> backgroundImmediateKeyboardPolls{0},backgroundBufferedKeyboardPolls{0},backgroundImmediateMousePolls{0},backgroundBufferedMousePolls{0};
std::atomic<std::uint64_t> deliveredImmediateKeyboardTransitions{0},deliveredBufferedKeyboardTransitions{0},deliveredImmediateMouseButtonTransitions{0},deliveredBufferedMouseButtonTransitions{0};
std::atomic<DWORD> wrappedKeyboard{0},wrappedMouse{0},badFormats{0};
std::atomic<DWORD> readyKeyboard{0},readyMouse{0};
std::atomic<std::uint64_t> syntheticKeyboardSamples{0},syntheticMouseSamples{0};
std::atomic<std::int64_t> deliveredMouseX{0},deliveredMouseY{0};
std::atomic<LONG> lastPhysicalResult{DI_OK};
std::atomic<std::uint64_t> inputEpoch{0};
std::string installError;
#ifdef BMT_LIVE_INPUT_TEST
std::uint64_t testNow=0;
bool testForeground=false;
std::uint64_t Now(){return testNow;}
#else
std::uint64_t Now(){return GetTickCount64();}
#endif
bool Foreground() {
#ifdef BMT_LIVE_INPUT_TEST
    return testForeground;
#else
    DWORD pid=0;GetWindowThreadProcessId(GetForegroundWindow(),&pid);return pid==GetCurrentProcessId();
#endif
}
std::string Quote(const std::string& value) {
    std::string result="\"";
    for(const unsigned char c:value) {
        if(c=='"' || c=='\\'){result+='\\';result+=char(c);}
        else if(c<32){const char hex[]="0123456789abcdef";result+="\\u00";result+=hex[c>>4];result+=hex[c&15];}
        else result+=char(c);
    }
    return result+'"';
}
void ClearStateLocked(){keys.fill(0);buttons.fill(0);pendingX=pendingY=emittedX=emittedY=0;++inputEpoch;}
void FinishLocked(const char* status,const std::string& error={}) {
    completion="{\"status\":"+Quote(status)+",\"completedSteps\":"+std::to_string(stepIndex)+
        ",\"elapsedMs\":"+std::to_string(Now()-started)+",\"snapshots\":[";
    for(size_t i=0;i<snapshots.size();++i){if(i)completion+=',';completion+=snapshots[i];}
    completion+=']';if(!error.empty())completion+=",\"error\":"+Quote(error);completion+='}';
    active=false;boundary=false;ClearStateLocked();
}
void EnterLocked(std::uint64_t now) {
    stepStarted=now;emittedX=emittedY=0;keys.fill(0);buttons.fill(0);
    if(stepIndex<steps.size() && steps[stepIndex].kind==Step::Input) {
        keys=steps[stepIndex].keys;buttons=steps[stepIndex].buttons;
    }
}
bool Integer(const Json* value,long long minimum,long long maximum,long long& result) {
    if(!value || value->type!=Json::Number || !std::isfinite(value->number) ||
        std::floor(value->number)!=value->number || value->number<double(minimum) || value->number>double(maximum))return false;
    result=static_cast<long long>(value->number);return true;
}
int KeyCode(const std::string& name) {
    static const std::pair<const char*,int> names[]={
        {"ESC",DIK_ESCAPE},{"ESCAPE",DIK_ESCAPE},{"1",DIK_1},{"2",DIK_2},{"3",DIK_3},{"4",DIK_4},{"5",DIK_5},
        {"6",DIK_6},{"7",DIK_7},{"8",DIK_8},{"9",DIK_9},{"0",DIK_0},{"MINUS",DIK_MINUS},{"EQUALS",DIK_EQUALS},
        {"BACKSPACE",DIK_BACK},{"TAB",DIK_TAB},{"Q",DIK_Q},{"W",DIK_W},{"E",DIK_E},{"R",DIK_R},{"T",DIK_T},
        {"Y",DIK_Y},{"U",DIK_U},{"I",DIK_I},{"O",DIK_O},{"P",DIK_P},{"LBRACKET",DIK_LBRACKET},{"RBRACKET",DIK_RBRACKET},
        {"ENTER",DIK_RETURN},{"LCTRL",DIK_LCONTROL},{"A",DIK_A},{"S",DIK_S},{"D",DIK_D},{"F",DIK_F},{"G",DIK_G},
        {"H",DIK_H},{"J",DIK_J},{"K",DIK_K},{"L",DIK_L},{"SEMICOLON",DIK_SEMICOLON},{"APOSTROPHE",DIK_APOSTROPHE},
        {"GRAVE",DIK_GRAVE},{"LSHIFT",DIK_LSHIFT},{"BACKSLASH",DIK_BACKSLASH},{"Z",DIK_Z},{"X",DIK_X},{"C",DIK_C},
        {"V",DIK_V},{"B",DIK_B},{"N",DIK_N},{"M",DIK_M},{"COMMA",DIK_COMMA},{"PERIOD",DIK_PERIOD},{"SLASH",DIK_SLASH},
        {"RSHIFT",DIK_RSHIFT},{"LALT",DIK_LMENU},{"SPACE",DIK_SPACE},{"CAPSLOCK",DIK_CAPITAL},
        {"F1",DIK_F1},{"F2",DIK_F2},{"F3",DIK_F3},{"F4",DIK_F4},{"F5",DIK_F5},{"F6",DIK_F6},
        {"F7",DIK_F7},{"F8",DIK_F8},{"F9",DIK_F9},{"F10",DIK_F10},{"F11",DIK_F11},{"F12",DIK_F12},
        {"RCTRL",DIK_RCONTROL},{"RALT",DIK_RMENU},{"UP",DIK_UP},{"DOWN",DIK_DOWN},{"LEFT",DIK_LEFT},{"RIGHT",DIK_RIGHT},
        {"HOME",DIK_HOME},{"END",DIK_END},{"PAGEUP",DIK_PRIOR},{"PAGEDOWN",DIK_NEXT},{"INSERT",DIK_INSERT},{"DELETE",DIK_DELETE}};
    for(const auto& item:names)if(name==item.first)return item.second;
    return -1;
}
bool ParseSteps(const std::string& text,std::vector<Step>& output,std::string& error) {
    Json root;if(!bmt_live_json::Parse(text,root,error))return false;
    const Json* sequence=root.Get("sequence");if(!sequence)sequence=&root;
    long long value=0;if(!Integer(sequence->Get("version"),1,1,value)){error="sequence.version must be 1";return false;}
    const auto* list=sequence->Get("steps");
    if(!list || list->type!=Json::Array || list->array.empty() || list->array.size()>128){error="sequence.steps must contain 1..128 steps";return false;}
    std::uint64_t total=0;
    for(const auto& item:list->array) {
        Step step;const auto* type=item.Get("type");const std::string kind=type?type->StringOr():"";
        if(kind=="snapshot") {
            step.kind=Step::Snapshot;const auto* fields=item.Get("fields");
            if(!fields || fields->type!=Json::Array || fields->array.empty() || fields->array.size()>32){error="snapshot.fields must contain 1..32 names";return false;}
            for(const auto& field:fields->array){if(field.type!=Json::String || field.text.empty() || field.text.size()>128){error="invalid snapshot field";return false;}step.fields.push_back(field.text);}
        } else {
            if(kind!="input" && kind!="wait"){error="step.type must be input, wait, or snapshot";return false;}
            step.kind=kind=="input"?Step::Input:Step::Wait;
            if(!Integer(item.Get("durationMs"),1,600000,value)){error="durationMs must be an integer in 1..600000";return false;}
            step.duration=static_cast<std::uint64_t>(value);total+=step.duration;
            if(total>3600000){error="sequence exceeds one hour";return false;}
            if(step.kind==Step::Input) {
                if(const auto* names=item.Get("keys")) {
                    if(names->type!=Json::Array || names->array.size()>32){error="keys must be an array with at most 32 names";return false;}
                    for(const auto& name:names->array){const int code=KeyCode(name.StringOr());if(code<0){error="unknown key: "+name.StringOr();return false;}step.keys[code]=0x80;}
                }
                if(const auto* names=item.Get("mouseButtons")) {
                    if(names->type!=Json::Array || names->array.size()>8){error="mouseButtons must be an array with at most eight names";return false;}
                    for(const auto& name:names->array){const auto n=name.StringOr();const int code=n=="left"?0:n=="right"?1:n=="middle"?2:n=="x1"?3:n=="x2"?4:n=="button6"?5:n=="button7"?6:n=="button8"?7:-1;if(code<0){error="unknown mouse button: "+n;return false;}step.buttons[code]=0x80;}
                }
                if(const auto* mouse=item.Get("mouse")) {
                    if(mouse->type!=Json::Object){error="mouse must be an object";return false;}
                    if(const auto* x=mouse->Get("dx")){if(!Integer(x,-1000000,1000000,value)){error="mouse.dx must be an integer in -1000000..1000000";return false;}step.dx=static_cast<LONG>(value);}
                    if(const auto* y=mouse->Get("dy")){if(!Integer(y,-1000000,1000000,value)){error="mouse.dy must be an integer in -1000000..1000000";return false;}step.dy=static_cast<LONG>(value);}
                }
            } else if(item.Get("keys") || item.Get("mouseButtons") || item.Get("mouse")){error="wait steps cannot contain input";return false;}
        }
        output.push_back(std::move(step));
    }
    return true;
}
void TickAt(std::uint64_t now) {
    ++ticks;if(!Foreground())++backgroundTicks;
    std::vector<std::string> fields;
    std::function<std::string(const std::vector<std::string>&)> callback;
    std::uint64_t observedGeneration=0;
    {
        std::lock_guard<std::mutex> guard(mutex);if(!active)return;
        if(boundary) {
            if(pendingX || pendingY) {
                keys.fill(0);buttons.fill(0);
                if(now-stepStarted>steps[stepIndex].duration+1000)FinishLocked("failed","mouse input was not polled before the step deadline");
                return;
            }
            boundary=false;++stepIndex;EnterLocked(now);
        }
        if(stepIndex==steps.size()){FinishLocked("completed");return;}
        const auto& step=steps[stepIndex];
        if(step.kind==Step::Snapshot){fields=step.fields;callback=snapshotCallback;observedGeneration=generation;}
        else {
            const auto elapsed=std::min(now-stepStarted,step.duration);
            if(step.kind==Step::Input) {
                const auto x=static_cast<LONG>((static_cast<std::int64_t>(step.dx)*static_cast<std::int64_t>(elapsed))/static_cast<std::int64_t>(step.duration));
                const auto y=static_cast<LONG>((static_cast<std::int64_t>(step.dy)*static_cast<std::int64_t>(elapsed))/static_cast<std::int64_t>(step.duration));
                pendingX+=x-emittedX;pendingY+=y-emittedY;emittedX=x;emittedY=y;
            }
            // Leave the final sample available for the input poll in this frame.
            if(elapsed==step.duration)boundary=true;
            return;
        }
    }
    const std::string result=callback?callback(fields):"";
    std::lock_guard<std::mutex> guard(mutex);
    if(!active || generation!=observedGeneration)return;
    const auto separatorBytes=snapshots.empty()?0u:1u;
    if(snapshotBytes+separatorBytes>MaxRetainedSnapshotBytes ||
        result.size()>MaxRetainedSnapshotBytes-snapshotBytes-separatorBytes){FinishLocked("failed","snapshot-result-limit");return;}
    Json parsed;std::string parseError;
    if(result.empty() || !bmt_live_json::Parse(result,parsed,parseError)){FinishLocked("failed","snapshot callback returned no valid JSON");return;}
    snapshotBytes+=result.size()+separatorBytes;snapshots.push_back(result);++stepIndex;EnterLocked(now);
    if(stepIndex==steps.size())FinishLocked("completed");
}

// DirectInput state is never merged with physical input during takeover. Mouse
// deltas are shared between immediate and buffered readers and consumed once.
struct InputSample {std::array<BYTE,256> keys{};std::array<BYTE,8> buttons{};LONG dx=0,dy=0;};
InputSample Sample(bool mouse) {
    std::lock_guard<std::mutex> guard(mutex);
    InputSample result;result.keys=keys;result.buttons=buttons;
    if(mouse){result.dx=pendingX;result.dy=pendingY;pendingX=pendingY=0;}
    return result;
}
bool Takeover(){std::lock_guard<std::mutex> guard(mutex);return active || !Foreground();}
void Poll(bool mouse,bool buffered) {
    if(mouse)++mousePolls;else ++keyboardPolls;
    ++(mouse?(buffered?bufferedMousePolls:immediateMousePolls):(buffered?bufferedKeyboardPolls:immediateKeyboardPolls));
    if(!Foreground()){
        if(mouse)++backgroundMousePolls;else ++backgroundKeyboardPolls;
        ++(mouse?(buffered?backgroundBufferedMousePolls:backgroundImmediateMousePolls):(buffered?backgroundBufferedKeyboardPolls:backgroundImmediateKeyboardPolls));
    }
}

template<bool Wide> struct Api;
template<> struct Api<false> {
    using Input=IDirectInput8A;using Device=IDirectInputDevice8A;using Instance=DIDEVICEINSTANCEA;
    using Object=DIDEVICEOBJECTINSTANCEA;using Effect=DIEFFECTINFOA;using Action=DIACTIONFORMATA;
    using Image=DIDEVICEIMAGEINFOHEADERA;using Configure=DICONFIGUREDEVICESPARAMSA;
    using EnumDevices=LPDIENUMDEVICESCALLBACKA;using EnumObjects=LPDIENUMDEVICEOBJECTSCALLBACKA;
    using EnumEffects=LPDIENUMEFFECTSCALLBACKA;using EnumSemantic=LPDIENUMDEVICESBYSEMANTICSCBA;using Char=CHAR;
    static bool IsInput(REFIID id){return id==IID_IDirectInput8A;}
    static bool IsDevice(REFIID id){return id==IID_IDirectInputDevice8A || id==IID_IDirectInputDevice7A || id==IID_IDirectInputDevice2A || id==IID_IDirectInputDeviceA;}
};
template<> struct Api<true> {
    using Input=IDirectInput8W;using Device=IDirectInputDevice8W;using Instance=DIDEVICEINSTANCEW;
    using Object=DIDEVICEOBJECTINSTANCEW;using Effect=DIEFFECTINFOW;using Action=DIACTIONFORMATW;
    using Image=DIDEVICEIMAGEINFOHEADERW;using Configure=DICONFIGUREDEVICESPARAMSW;
    using EnumDevices=LPDIENUMDEVICESCALLBACKW;using EnumObjects=LPDIENUMDEVICEOBJECTSCALLBACKW;
    using EnumEffects=LPDIENUMEFFECTSCALLBACKW;using EnumSemantic=LPDIENUMDEVICESBYSEMANTICSCBW;using Char=WCHAR;
    static bool IsInput(REFIID id){return id==IID_IDirectInput8W;}
    static bool IsDevice(REFIID id){return id==IID_IDirectInputDevice8W || id==IID_IDirectInputDevice7W || id==IID_IDirectInputDevice2W || id==IID_IDirectInputDeviceW;}
};
template<bool Wide> class Device final:public Api<Wide>::Device {
    using A=Api<Wide>;
    typename A::Device* real;
    std::atomic<ULONG> refs{1};
    bool mouse=false,acquired=false,validFormat=false,wasTakeover=false,countedReady=false;
    DWORD dataSize=0;std::uint64_t epoch=0;
    std::array<BYTE,256> previousKeys{};std::array<BYTE,8> previousButtons{};
    // Diagnostics only: do not share state with buffered event generation.
    std::array<BYTE,256> lastImmediateKeys{};std::array<BYTE,8> lastImmediateButtons{};
    std::deque<DIDEVICEOBJECTDATA> events;
    DWORD eventSequence=0;bool overflow=false;
    std::mutex stateMutex;
    void UpdateReady() {
        const bool ready=acquired && validFormat;if(ready==countedReady)return;
        auto& count=mouse?readyMouse:readyKeyboard;if(ready)++count;else --count;countedReady=ready;
    }
    void Event(DWORD offset,DWORD data) {
        if(events.size()==256){events.pop_front();overflow=true;}
        DIDEVICEOBJECTDATA item{};item.dwOfs=offset;item.dwData=data;item.dwTimeStamp=GetTickCount();item.dwSequence=++eventSequence;events.push_back(item);
    }
    void ClearBuffered(bool forget=false){events.clear();if(forget){previousKeys.fill(0);previousButtons.fill(0);}overflow=false;}
    bool BeginPoll(bool buffered=false) {
        const bool takeover=Takeover();const auto current=inputEpoch.load();
        if(current!=epoch || wasTakeover!=takeover){ClearBuffered();epoch=current;DWORD count=INFINITE;real->GetDeviceData(sizeof(DIDEVICEOBJECTDATA),nullptr,&count,0);}
        if(wasTakeover && !takeover) {
            if(mouse){for(size_t i=0;i<previousButtons.size();++i)if(previousButtons[i]){Event(DIMOFS_BUTTON0+static_cast<DWORD>(i),0);previousButtons[i]=0;}}
            else for(size_t i=0;i<previousKeys.size();++i)if(previousKeys[i]){Event(static_cast<DWORD>(i),0);previousKeys[i]=0;}
        }
        wasTakeover=takeover;return takeover || (buffered && !events.empty());
    }
    bool SupportedFormat(LPCDIDATAFORMAT format) {
        if(!format || format->dwSize!=sizeof(DIDATAFORMAT) || format->dwObjSize!=sizeof(DIOBJECTDATAFORMAT) || !format->rgodf)return false;
        if(!mouse && (format->dwDataSize!=256 || format->dwNumObjs!=256))return false;
        if(mouse && ((format->dwDataSize!=sizeof(DIMOUSESTATE) && format->dwDataSize!=sizeof(DIMOUSESTATE2)) || (format->dwFlags&DIDF_RELAXIS)==0 || format->dwNumObjs!=(format->dwDataSize==sizeof(DIMOUSESTATE)?7u:11u)))return false;
        for(DWORD i=0;i<format->dwNumObjs;++i) {
            const auto& object=format->rgodf[i];
            if(!mouse){if(object.dwOfs!=i || (DIDFT_GETTYPE(object.dwType)&DIDFT_BUTTON)==0)return false;}
            else if(i<3){if(object.dwOfs!=i*sizeof(LONG) || (DIDFT_GETTYPE(object.dwType)&DIDFT_RELAXIS)==0)return false;}
            else if(object.dwOfs!=12+i-3 || (DIDFT_GETTYPE(object.dwType)&DIDFT_BUTTON)==0)return false;
        }
        return true;
    }
public:
    Device(typename A::Device* value,bool isMouse):real(value),mouse(isMouse){if(mouse)++wrappedMouse;else ++wrappedKeyboard;}
    ~Device(){if(countedReady){if(mouse)--readyMouse;else --readyKeyboard;}real->Release();if(mouse)--wrappedMouse;else --wrappedKeyboard;}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override {if(!out)return E_POINTER;if(id==IID_IUnknown || A::IsDevice(id)){*out=static_cast<typename A::Device*>(this);AddRef();return S_OK;}return real->QueryInterface(id,out);}
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{const ULONG n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE GetCapabilities(LPDIDEVCAPS v) override{return real->GetCapabilities(v);}
    HRESULT STDMETHODCALLTYPE EnumObjects(typename A::EnumObjects a,LPVOID b,DWORD c) override{return real->EnumObjects(a,b,c);}
    HRESULT STDMETHODCALLTYPE GetProperty(REFGUID a,LPDIPROPHEADER b) override{return real->GetProperty(a,b);}
    HRESULT STDMETHODCALLTYPE SetProperty(REFGUID a,LPCDIPROPHEADER b) override{return real->SetProperty(a,b);}
    HRESULT STDMETHODCALLTYPE Acquire() override {
        std::lock_guard<std::mutex> guard(stateMutex);const auto hr=real->Acquire();lastPhysicalResult=hr;
        acquired=SUCCEEDED(hr) || (validFormat && Takeover());UpdateReady();if(!acquired)bmt_live_input::Release();return acquired?DI_OK:hr;
    }
    HRESULT STDMETHODCALLTYPE Unacquire() override {std::lock_guard<std::mutex> guard(stateMutex);acquired=false;UpdateReady();ClearBuffered(true);bmt_live_input::Release();return real->Unacquire();}
    HRESULT STDMETHODCALLTYPE GetDeviceState(DWORD size,LPVOID data) override {
        std::lock_guard<std::mutex> guard(stateMutex);if(!BeginPoll()){const auto hr=real->GetDeviceState(size,data);lastPhysicalResult=hr;if(FAILED(hr))bmt_live_input::Release();else if(data && validFormat && size==dataSize){if(mouse)std::memcpy(previousButtons.data(),static_cast<BYTE*>(data)+12,size-12);else std::memcpy(previousKeys.data(),data,256);}return hr;}
        if(!data || !validFormat || size!=dataSize){bmt_live_input::Release();return DIERR_INVALIDPARAM;}
        if(!acquired){bmt_live_input::Release();return DIERR_NOTACQUIRED;}
        bmt_live_input::Poll(mouse,false);const auto sample=Sample(mouse);std::memset(data,0,size);
        if(mouse){++syntheticMouseSamples;deliveredMouseX+=sample.dx;deliveredMouseY+=sample.dy;}else ++syntheticKeyboardSamples;
        if(mouse){
            auto* value=static_cast<DIMOUSESTATE*>(data);value->lX=sample.dx;value->lY=sample.dy;std::memcpy(value->rgbButtons,sample.buttons.data(),size-sizeof(LONG)*3);
            for(size_t i=0;i<size-sizeof(LONG)*3;++i)if(sample.buttons[i]!=lastImmediateButtons[i])++deliveredImmediateMouseButtonTransitions;
            lastImmediateButtons=sample.buttons;
        } else {
            std::memcpy(data,sample.keys.data(),256);
            for(size_t i=0;i<sample.keys.size();++i)if(sample.keys[i]!=lastImmediateKeys[i])++deliveredImmediateKeyboardTransitions;
            lastImmediateKeys=sample.keys;
        }
        return DI_OK;
    }
    HRESULT STDMETHODCALLTYPE GetDeviceData(DWORD size,LPDIDEVICEOBJECTDATA data,LPDWORD count,DWORD flags) override {
        std::lock_guard<std::mutex> guard(stateMutex);if(!BeginPoll(true)){const auto hr=real->GetDeviceData(size,data,count,flags);lastPhysicalResult=hr;if(FAILED(hr))bmt_live_input::Release();else if(data && count && !(flags&DIGDD_PEEK) && (size==sizeof(DIDEVICEOBJECTDATA) || size==offsetof(DIDEVICEOBJECTDATA,uAppData)))for(DWORD i=0;i<*count;++i){const auto* entry=reinterpret_cast<const DIDEVICEOBJECTDATA*>(reinterpret_cast<BYTE*>(data)+i*size);if(!mouse && entry->dwOfs<256)previousKeys[entry->dwOfs]=static_cast<BYTE>(entry->dwData);else if(mouse && entry->dwOfs>=DIMOFS_BUTTON0 && entry->dwOfs<DIMOFS_BUTTON0+8)previousButtons[entry->dwOfs-DIMOFS_BUTTON0]=static_cast<BYTE>(entry->dwData);}return hr;}
        if(!count || (size!=sizeof(DIDEVICEOBJECTDATA) && size!=offsetof(DIDEVICEOBJECTDATA,uAppData)) || (flags&~DIGDD_PEEK)){bmt_live_input::Release();return DIERR_INVALIDPARAM;}
        if(!validFormat){bmt_live_input::Release();return DIERR_INVALIDPARAM;}if(!acquired){bmt_live_input::Release();return DIERR_NOTACQUIRED;}
        bmt_live_input::Poll(mouse,true);const auto sample=Sample(mouse);
        if(mouse){++syntheticMouseSamples;deliveredMouseX+=sample.dx;deliveredMouseY+=sample.dy;}else ++syntheticKeyboardSamples;
        if(mouse){for(size_t i=0;i<(dataSize-sizeof(LONG)*3);++i)if(sample.buttons[i]!=previousButtons[i]){Event(DIMOFS_BUTTON0+static_cast<DWORD>(i),sample.buttons[i]);previousButtons[i]=sample.buttons[i];}if(sample.dx)Event(DIMOFS_X,static_cast<DWORD>(sample.dx));if(sample.dy)Event(DIMOFS_Y,static_cast<DWORD>(sample.dy));}
        else for(size_t i=0;i<256;++i)if(sample.keys[i]!=previousKeys[i]){Event(static_cast<DWORD>(i),sample.keys[i]);previousKeys[i]=sample.keys[i];}
        const auto take=std::min<size_t>(*count,events.size());*count=static_cast<DWORD>(take);
        if(data)for(size_t i=0;i<take;++i)std::memcpy(reinterpret_cast<BYTE*>(data)+size*i,&events[i],size);
        // Peeks can repeat an event, and a null destination only discards it.
        if(data && !(flags&DIGDD_PEEK))for(size_t i=0;i<take;++i){
            if(!mouse && events[i].dwOfs<256)++deliveredBufferedKeyboardTransitions;
            else if(mouse && events[i].dwOfs>=DIMOFS_BUTTON0 && events[i].dwOfs<DIMOFS_BUTTON0+8)++deliveredBufferedMouseButtonTransitions;
        }
        const auto hr=overflow?DI_BUFFEROVERFLOW:DI_OK;
        if(!(flags&DIGDD_PEEK)){events.erase(events.begin(),events.begin()+take);overflow=false;}
        return hr;
    }
    HRESULT STDMETHODCALLTYPE SetDataFormat(LPCDIDATAFORMAT format) override {
        std::lock_guard<std::mutex> guard(stateMutex);const auto hr=real->SetDataFormat(format);validFormat=SUCCEEDED(hr) && SupportedFormat(format);dataSize=validFormat?format->dwDataSize:0;if(!validFormat){++badFormats;bmt_live_input::Release();}UpdateReady();ClearBuffered(true);return hr;
    }
    HRESULT STDMETHODCALLTYPE SetEventNotification(HANDLE v) override{return real->SetEventNotification(v);}
    HRESULT STDMETHODCALLTYPE SetCooperativeLevel(HWND window,DWORD) override {return real->SetCooperativeLevel(window,DISCL_BACKGROUND|DISCL_NONEXCLUSIVE);}
    HRESULT STDMETHODCALLTYPE GetObjectInfo(typename A::Object* a,DWORD b,DWORD c) override{return real->GetObjectInfo(a,b,c);}
    HRESULT STDMETHODCALLTYPE GetDeviceInfo(typename A::Instance* v) override{return real->GetDeviceInfo(v);}
    HRESULT STDMETHODCALLTYPE RunControlPanel(HWND a,DWORD b) override{return real->RunControlPanel(a,b);}
    HRESULT STDMETHODCALLTYPE Initialize(HINSTANCE a,DWORD b,REFGUID c) override{return real->Initialize(a,b,c);}
    HRESULT STDMETHODCALLTYPE CreateEffect(REFGUID a,LPCDIEFFECT b,LPDIRECTINPUTEFFECT* c,LPUNKNOWN d) override{return real->CreateEffect(a,b,c,d);}
    HRESULT STDMETHODCALLTYPE EnumEffects(typename A::EnumEffects a,LPVOID b,DWORD c) override{return real->EnumEffects(a,b,c);}
    HRESULT STDMETHODCALLTYPE GetEffectInfo(typename A::Effect* a,REFGUID b) override{return real->GetEffectInfo(a,b);}
    HRESULT STDMETHODCALLTYPE GetForceFeedbackState(LPDWORD v) override{return real->GetForceFeedbackState(v);}
    HRESULT STDMETHODCALLTYPE SendForceFeedbackCommand(DWORD v) override{return real->SendForceFeedbackCommand(v);}
    HRESULT STDMETHODCALLTYPE EnumCreatedEffectObjects(LPDIENUMCREATEDEFFECTOBJECTSCALLBACK a,LPVOID b,DWORD c) override{return real->EnumCreatedEffectObjects(a,b,c);}
    HRESULT STDMETHODCALLTYPE Escape(LPDIEFFESCAPE v) override{return real->Escape(v);}
    HRESULT STDMETHODCALLTYPE Poll() override {std::lock_guard<std::mutex> guard(stateMutex);const auto hr=real->Poll();lastPhysicalResult=hr;if(validFormat && acquired && Takeover())return DI_OK;if(FAILED(hr))bmt_live_input::Release();return hr;}
    HRESULT STDMETHODCALLTYPE SendDeviceData(DWORD a,LPCDIDEVICEOBJECTDATA b,LPDWORD c,DWORD d) override{return real->SendDeviceData(a,b,c,d);}
    HRESULT STDMETHODCALLTYPE EnumEffectsInFile(const typename A::Char* a,LPDIENUMEFFECTSINFILECALLBACK b,LPVOID c,DWORD d) override{return real->EnumEffectsInFile(a,b,c,d);}
    HRESULT STDMETHODCALLTYPE WriteEffectToFile(const typename A::Char* a,DWORD b,LPDIFILEEFFECT c,DWORD d) override{return real->WriteEffectToFile(a,b,c,d);}
    HRESULT STDMETHODCALLTYPE BuildActionMap(typename A::Action* a,const typename A::Char* b,DWORD c) override{return real->BuildActionMap(a,b,c);}
    HRESULT STDMETHODCALLTYPE SetActionMap(typename A::Action* a,const typename A::Char* b,DWORD c) override{return real->SetActionMap(a,b,c);}
    HRESULT STDMETHODCALLTYPE GetImageInfo(typename A::Image* v) override{return real->GetImageInfo(v);}
};
template<bool Wide> class Input final:public Api<Wide>::Input {
    using A=Api<Wide>;typename A::Input* real;std::atomic<ULONG> refs{1};
public:
    explicit Input(typename A::Input* value):real(value){}
    ~Input(){real->Release();}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** out) override {if(!out)return E_POINTER;if(id==IID_IUnknown || A::IsInput(id)){*out=static_cast<typename A::Input*>(this);AddRef();return S_OK;}return real->QueryInterface(id,out);}
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{const auto n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE CreateDevice(REFGUID id,typename A::Device** output,LPUNKNOWN outer) override {
        const auto hr=real->CreateDevice(id,output,outer);if(FAILED(hr) || !output || !*output || outer || (id!=GUID_SysKeyboard && id!=GUID_SysMouse))return hr;
        auto* wrapped=new(std::nothrow) Device<Wide>(*output,id==GUID_SysMouse);if(!wrapped){(*output)->Release();*output=nullptr;return E_OUTOFMEMORY;}*output=wrapped;return hr;
    }
    HRESULT STDMETHODCALLTYPE EnumDevices(DWORD a,typename A::EnumDevices b,LPVOID c,DWORD d) override{return real->EnumDevices(a,b,c,d);}
    HRESULT STDMETHODCALLTYPE GetDeviceStatus(REFGUID id) override{return real->GetDeviceStatus(id);}
    HRESULT STDMETHODCALLTYPE RunControlPanel(HWND a,DWORD b) override{return real->RunControlPanel(a,b);}
    HRESULT STDMETHODCALLTYPE Initialize(HINSTANCE a,DWORD b) override{return real->Initialize(a,b);}
    HRESULT STDMETHODCALLTYPE FindDevice(REFGUID a,const typename A::Char* b,LPGUID c) override{return real->FindDevice(a,b,c);}
    HRESULT STDMETHODCALLTYPE EnumDevicesBySemantics(const typename A::Char* a,typename A::Action* b,typename A::EnumSemantic c,LPVOID d,DWORD e) override{return real->EnumDevicesBySemantics(a,b,c,d,e);}
    HRESULT STDMETHODCALLTYPE ConfigureDevices(LPDICONFIGUREDEVICESCALLBACK a,typename A::Configure* b,DWORD c,LPVOID d) override{return real->ConfigureDevices(a,b,c,d);}
};
using CreateInput=HRESULT(WINAPI*)(HINSTANCE,DWORD,REFIID,LPVOID*,LPUNKNOWN);
CreateInput originalCreate=nullptr;
HRESULT WINAPI CreateInputHook(HINSTANCE instance,DWORD version,REFIID id,LPVOID* output,LPUNKNOWN outer) {
    const auto hr=originalCreate(instance,version,id,output,outer);if(FAILED(hr) || !output || !*output || outer)return hr;
    if(id==IID_IDirectInput8A){auto* real=static_cast<IDirectInput8A*>(*output);auto* value=new(std::nothrow) Input<false>(real);if(!value){real->Release();*output=nullptr;return E_OUTOFMEMORY;}*output=value;}
    else if(id==IID_IDirectInput8W){auto* real=static_cast<IDirectInput8W*>(*output);auto* value=new(std::nothrow) Input<true>(real);if(!value){real->Release();*output=nullptr;return E_OUTOFMEMORY;}*output=value;}
    return hr;
}
}

bool Install() {
    std::lock_guard<std::mutex> guard(mutex);if(installed)return true;
    auto* base=reinterpret_cast<BYTE*>(GetModuleHandleW(nullptr));
    const auto* dos=reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if(!base || dos->e_magic!=IMAGE_DOS_SIGNATURE){installError="invalid executable DOS header";return false;}
    const auto* nt=reinterpret_cast<const IMAGE_NT_HEADERS*>(base+dos->e_lfanew);
    if(nt->Signature!=IMAGE_NT_SIGNATURE){installError="invalid executable PE header";return false;}
    const auto imageSize=nt->OptionalHeader.SizeOfImage;
    const auto directory=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if(!directory.VirtualAddress || directory.VirtualAddress>=imageSize || directory.Size>imageSize-directory.VirtualAddress){installError="executable imports unavailable";return false;}
    const auto* descriptor=reinterpret_cast<const IMAGE_IMPORT_DESCRIPTOR*>(base+directory.VirtualAddress);
    const auto descriptors=directory.Size/sizeof(IMAGE_IMPORT_DESCRIPTOR);
    for(size_t d=0;d<descriptors && descriptor[d].Name;++d) {
        const auto& entry=descriptor[d];if(entry.Name>=imageSize || !std::memchr(base+entry.Name,0,imageSize-entry.Name))continue;
        if(_stricmp(reinterpret_cast<const char*>(base+entry.Name),"dinput8.dll") || !entry.OriginalFirstThunk || entry.OriginalFirstThunk>=imageSize || entry.FirstThunk>=imageSize)continue;
        auto* names=reinterpret_cast<IMAGE_THUNK_DATA*>(base+entry.OriginalFirstThunk);auto* slots=reinterpret_cast<IMAGE_THUNK_DATA*>(base+entry.FirstThunk);
        const auto count=std::min((imageSize-entry.OriginalFirstThunk)/sizeof(IMAGE_THUNK_DATA),(imageSize-entry.FirstThunk)/sizeof(IMAGE_THUNK_DATA));
        for(size_t i=0;i<count && names[i].u1.AddressOfData;++i) {
            const auto address=names[i].u1.AddressOfData;
            if(IMAGE_SNAP_BY_ORDINAL(address) || address>=imageSize || imageSize-address<sizeof(WORD)+1)continue;
            const auto* name=reinterpret_cast<const char*>(base+address+sizeof(WORD));
            if(!std::memchr(name,0,imageSize-address-sizeof(WORD)) || std::strcmp(name,"DirectInput8Create"))continue;
            DWORD protection=0;auto* slot=reinterpret_cast<void**>(&slots[i].u1.Function);
            if(!VirtualProtect(slot,sizeof(void*),PAGE_READWRITE,&protection)){installError="DirectInput import protection failed";return false;}
            originalCreate=reinterpret_cast<CreateInput>(*slot);
            InterlockedExchangePointer(slot,reinterpret_cast<void*>(&CreateInputHook));
            DWORD ignored=0;VirtualProtect(slot,sizeof(void*),protection,&ignored);installed=true;return true;
        }
    }
    installError="DirectInput8Create named import not found";return false;
}
void Tick(){TickAt(Now());}
void Release(){std::lock_guard<std::mutex> guard(mutex);++generation;if(active)FinishLocked("cancelled");else ClearStateLocked();}
bool StartSequence(const std::string& json,std::string& error) {
    std::vector<Step> parsed;if(!ParseSteps(json,parsed,error))return false;
    std::lock_guard<std::mutex> guard(mutex);
    if(!installed){error="DirectInput interception unavailable";return false;}
    if(active){error="another sequence is active";return false;}
#ifndef BMT_LIVE_INPUT_TEST
    bool needsKeyboard=false,needsMouse=false;
    for(const auto& step:parsed) {
        needsKeyboard|=std::any_of(step.keys.begin(),step.keys.end(),[](BYTE value){return value!=0;});
        needsMouse|=step.dx!=0 || step.dy!=0 || std::any_of(step.buttons.begin(),step.buttons.end(),[](BYTE value){return value!=0;});
    }
    if((needsKeyboard && readyKeyboard==0) || (needsMouse && readyMouse==0)){error="required input device has no supported format or acquisition; inspect input diagnostics";return false;}
#endif
    for(const auto& step:parsed)if(step.kind==Step::Snapshot && !snapshotCallback){error="snapshot callback unavailable";return false;}
    steps=std::move(parsed);stepIndex=0;started=Now();++generation;completion.clear();snapshots.clear();snapshotBytes=0;ClearStateLocked();active=true;boundary=false;EnterLocked(started);return true;
}
bool Active(){std::lock_guard<std::mutex> guard(mutex);return active;}
std::string TakeCompletionJson(){std::lock_guard<std::mutex> guard(mutex);return std::exchange(completion,{});}
void SetSnapshotCallback(std::function<std::string(const std::vector<std::string>&)> callback){std::lock_guard<std::mutex> guard(mutex);snapshotCallback=std::move(callback);}
std::string DiagnosticsJson() {
    std::lock_guard<std::mutex> guard(mutex);
    return "{\"installed\":"+std::string(installed?"true":"false")+",\"error\":"+Quote(installError)+
        ",\"virtualAcquisition\":true,\"active\":"+(active?"true":"false")+",\"step\":"+std::to_string(stepIndex)+
        ",\"ticks\":"+std::to_string(ticks.load())+",\"backgroundTicks\":"+std::to_string(backgroundTicks.load())+
        ",\"keyboardDevices\":"+std::to_string(wrappedKeyboard.load())+",\"mouseDevices\":"+std::to_string(wrappedMouse.load())+
        ",\"readyKeyboardDevices\":"+std::to_string(readyKeyboard.load())+",\"readyMouseDevices\":"+std::to_string(readyMouse.load())+
        ",\"keyboardPolls\":"+std::to_string(keyboardPolls.load())+",\"mousePolls\":"+std::to_string(mousePolls.load())+
        ",\"backgroundKeyboardPolls\":"+std::to_string(backgroundKeyboardPolls.load())+",\"backgroundMousePolls\":"+std::to_string(backgroundMousePolls.load())+
        ",\"immediateKeyboardPolls\":"+std::to_string(immediateKeyboardPolls.load())+",\"bufferedKeyboardPolls\":"+std::to_string(bufferedKeyboardPolls.load())+
        ",\"immediateMousePolls\":"+std::to_string(immediateMousePolls.load())+",\"bufferedMousePolls\":"+std::to_string(bufferedMousePolls.load())+
        ",\"backgroundImmediateKeyboardPolls\":"+std::to_string(backgroundImmediateKeyboardPolls.load())+",\"backgroundBufferedKeyboardPolls\":"+std::to_string(backgroundBufferedKeyboardPolls.load())+
        ",\"backgroundImmediateMousePolls\":"+std::to_string(backgroundImmediateMousePolls.load())+",\"backgroundBufferedMousePolls\":"+std::to_string(backgroundBufferedMousePolls.load())+
        ",\"deliveredImmediateKeyboardTransitions\":"+std::to_string(deliveredImmediateKeyboardTransitions.load())+",\"deliveredBufferedKeyboardTransitions\":"+std::to_string(deliveredBufferedKeyboardTransitions.load())+
        ",\"deliveredImmediateMouseButtonTransitions\":"+std::to_string(deliveredImmediateMouseButtonTransitions.load())+",\"deliveredBufferedMouseButtonTransitions\":"+std::to_string(deliveredBufferedMouseButtonTransitions.load())+
        ",\"backgroundPollingObserved\":"+(backgroundTicks && backgroundKeyboardPolls && backgroundMousePolls?"true":"false")+
        ",\"syntheticKeyboardSamples\":"+std::to_string(syntheticKeyboardSamples.load())+",\"syntheticMouseSamples\":"+std::to_string(syntheticMouseSamples.load())+
        ",\"deliveredMouseX\":"+std::to_string(deliveredMouseX.load())+",\"deliveredMouseY\":"+std::to_string(deliveredMouseY.load())+
        ",\"unsupportedFormats\":"+std::to_string(badFormats.load())+",\"lastPhysicalResult\":"+std::to_string(lastPhysicalResult.load())+"}";
}
#ifdef BMT_LIVE_INPUT_TEST
namespace testing {
void Reset(unsigned long long now){std::lock_guard<std::mutex> guard(mutex);testNow=now;active=false;boundary=false;installed=true;completion.clear();snapshots.clear();snapshotBytes=0;steps.clear();stepIndex=0;ClearStateLocked();}
void TickAt(unsigned long long now){testNow=now;bmt_live_input::Tick();}
void SetForeground(bool value){testForeground=value;}
Sample Consume(){const auto source=bmt_live_input::Sample(true);Sample result;std::memcpy(result.keys,source.keys.data(),256);std::memcpy(result.buttons,source.buttons.data(),8);result.dx=source.dx;result.dy=source.dy;return result;}
}
#endif
}
