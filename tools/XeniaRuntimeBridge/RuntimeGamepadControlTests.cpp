#include "overlay/runtime_gamepad_control.h"
#include <cstdio>
#include <stdexcept>
#include <thread>
using namespace xe::cpu::gamepad;
namespace {
unsigned cases=0,failed=0;
void Check(bool yes,const char* text){if(!yes)throw std::runtime_error(text);}
template<class F> void Case(const std::string& name,F test){++cases;try{test();}catch(const std::exception& e){++failed;std::fprintf(stderr,"%s: %s\n",name.c_str(),e.what());}}
const std::string guest(64,'a'),manifest(64,'b');
const std::string payload="gamepad-pulse/1\n0\n16\n100\n1000\n"+manifest;
struct Fixture {
  WindowState window{41,123,true,true};
  std::shared_ptr<Control> control=std::make_shared<Control>([this]{return window;});
  VerifiedRunBinding proof{guest,manifest,"capture-001",123,41,7,11};
  Fixture(){control->AttachProfile(guest,123);control->Lifecycle(7,11,true,true,"capture-001",1000);}
  void Bind(){Check(control->BindVerifiedRun(proof),"valid internal proof binds");}
  void Start(){Bind();Check(control->Submit(101,payload,1000).empty(),"admitted pulse");}
  model::State Poll(uint64_t now){GuestCall call(control,CallKind::State,0,1,now);model::State state;
    Check(call.Supplies(CallKind::State,0)&&call.Supply(state,now),"reserved guest state supplied");
    call.Observe(0,&state,false,now);return state;}
};
}
int main(){
  Case("unbound-declines-driver-and-wire-mutation",[]{Fixture f;
    Check(!f.control->Capable()&&!f.control->Reserved(0),"flag/profile alone not admission");
    Check(f.control->Submit(101,payload,1000)=="verified-guest-run-binding-required","unbound request refused");
    GuestCall call(f.control,CallKind::State,0,1,1000);model::State state;state.buttons=0x55;
    Check(!call.Supplies(CallKind::State,0)&&!call.Supply(state,1000),"manual drivers remain eligible");
    Check(state.buttons==0x55&&f.control->Drain().empty(),"no fabricated state or receipt");});
  for(int variant=0;variant<8;++variant) Case("binding-mismatch-"+std::to_string(variant),[=]{Fixture f;auto proof=f.proof;
    switch(variant){case 0:proof.guest_sha256=manifest;break;case 1:proof.process=124;break;case 2:++proof.window;break;
      case 3:++proof.connection;break;case 4:++proof.capture;break;case 5:proof.session="other";break;
      case 6:proof.manifest_sha256="claimed";break;case 7:f.window.foreground=false;break;}
    Check(!f.control->BindVerifiedRun(proof)&&!f.control->Reserved(0),"mismatched actual identity refused");});
  const std::array<std::string,9> bad{{"gamepad-pulse/2\n0\n16\n100\n1000\n"+manifest,
    "gamepad-pulse/1\n0\n16\n100\n1000\n"+manifest+"\n", "gamepad-pulse/1\n0\n16\n100\n1000",
    "gamepad-pulse/1\n0\n+16\n100\n1000\n"+manifest,"gamepad-pulse/1\n0\n016\n100\n1000\n"+manifest,
    "gamepad-pulse/1\n0\n65536\n100\n1000\n"+manifest,"gamepad-pulse/1\n0\n16\n100\n100\n"+manifest,
    "gamepad-pulse/1\n1\n16\n100\n1000\n"+manifest,std::string(193,'x')}};
  for(size_t i=0;i<bad.size();++i)Case("wire-rejection-"+std::to_string(i),[&,i]{Pulse parsed;parsed.buttons=99;
    Check(!ParsePulse(bad[i],parsed)&&parsed.buttons==99,"invalid typed payload leaves output untouched");});
  Case("binding-digest-is-not-payload-authorization",[]{Fixture f;f.Bind();
    auto other=payload;other.replace(other.size()-64,64,guest);
    Check(f.control->Submit(101,other,1000)=="gamepad-run-manifest-mismatch","wire cannot rebind proof");
    Check(f.control->Drain().empty(),"no queued action");});
  Case("guest-only-actual-return-and-normal-release",[]{Fixture f;f.Start();
    Check(GuestCall::Current()==nullptr,"host poll has no scoped guest access");
    auto events=f.control->Drain();Check(events.size()==1&&events[0].kind==EventKind::Queued,"queued is not observed");
    {GuestCall call(f.control,CallKind::State,0,1,1400);model::State state;
      Check(call.Supply(state,1400)&&state.buttons==16,"actual driver source supplies requested state");
      Check(f.control->Drain().empty(),"supply is not delivery");
      call.Observe(0,&state,false,1400);call.Observe(0,&state,false,1400);}
    events=f.control->Drain();Check(events.size()==1&&events[0].kind==EventKind::Press,"one exact guest observation");
    f.control->Tick(1500);Check(f.control->Drain().empty(),"local release is not guest release");
    const auto neutral=f.Poll(1501);Check(neutral.buttons==0,"neutral supplied after hold");
    events=f.control->Drain();Check(events.size()==2&&events[0].kind==EventKind::Release&&events[1].kind==EventKind::Completed,"release precedes terminal");
    Check(events[1].snapshot.press.observedMilliseconds==1400&&events[1].snapshot.releaseObserved,"retained real values");});
  Case("host-thread-cannot-inherit-guest-scope",[]{Fixture f;f.Start();
    GuestCall guestCall(f.control,CallKind::State,0,1,1000);
    bool hostSawGuest=true;std::thread host([&]{hostSawGuest=GuestCall::Current()!=nullptr;});host.join();
    Check(!hostSawGuest&&GuestCall::Current()==&guestCall,"scope thread-local");});
  Case("nested-capability-scope-restores-state-boundary",[]{Fixture f;f.Start();
    GuestCall outer(f.control,CallKind::State,0,1,1000);
    {GuestCall nested(f.control,CallKind::Capabilities,0,1,1000);
      Check(!nested.Supplies(CallKind::State,0)&&nested.Supplies(CallKind::Capabilities,0),"distinct call kinds");
      model::State state;Check(!nested.Supply(state,1000),"capability call cannot consume pulse");}
    Check(GuestCall::Current()==&outer,"outer call restored");});
  for(int reason=0;reason<3;++reason)Case("bound-failure-stays-neutral-"+std::to_string(reason),[=]{Fixture f;f.Start();f.control->Drain();
    if(reason==0)f.control->Tick(2000);
    else if(reason==1){f.window.foreground=false;f.control->Tick(1001);}
    else {GuestCall call(f.control,CallKind::State,0,1,1001);model::State zero;call.Observe(0,&zero,true,1001);}
    auto events=f.control->Drain();Check(events.size()==1&&events[0].kind==EventKind::Failed,"failure explicit");
    Check(events[0].snapshot.hostButtonsCleared&&!events[0].snapshot.releaseObserved,"clear distinct from release");
    Check(f.control->Reserved(0),"reservation retained after pulse failure");
    const auto neutral=f.Poll(reason==0?2001:1002);Check(neutral.buttons==0,"no fallthrough to manual input while bound");});
  Case("capture-teardown-explicitly-releases-reservation",[]{Fixture f;f.Start();f.control->Drain();
    f.control->Lifecycle(7,11,true,false,{},1001);auto events=f.control->Drain();
    Check(events.size()==1&&events[0].kind==EventKind::Failed,"pending failure retained before teardown");
    Check(!f.control->Reserved(0)&&!f.control->Capable(),"manual drivers eligible again");});
  Case("delayed-return-cannot-satisfy-rebound-capture",[]{Fixture f;f.Start();f.control->Drain();
    GuestCall old(f.control,CallKind::State,0,1,1001);model::State pressed;Check(old.Supply(pressed,1001),"old actual supply");
    f.control->Lifecycle(7,11,true,false,{},1002);f.control->Drain();
    f.control->Lifecycle(7,12,true,true,"capture-002",1003);f.proof.capture=12;f.proof.session="capture-002";
    f.Bind();Check(f.control->Submit(102,payload,1004).empty(),"new pulse admitted");f.control->Drain();
    old.Observe(0,&pressed,false,1005);auto adjusted=f.control->Drain();
    Check(pressed.buttons==0&&adjusted.size()==1&&adjusted[0].kind==EventKind::OutputAdjusted&&
      adjusted[0].snapshot.request.requestId==101&&adjusted[0].originalOutput.buttons==16,
      "old output neutralized with original identity, not new delivery");
    Check(f.Poll(1006).buttons==16,"new action remains intact");auto events=f.control->Drain();
    Check(events.size()==1&&events[0].snapshot.request.requestId==102,"new observed owner");});
  Case("admission-during-old-poll-is-neutral-not-fallthrough",[]{Fixture f;
    GuestCall old(f.control,CallKind::State,0,1,1000);f.Start();f.control->Drain();
    model::State state;Check(old.Supplies(CallKind::State,0)&&old.Supply(state,1001),"new reservation enforced");
    Check(state.buttons==0,"old poll cannot acquire new request");old.Observe(0,&state,false,1001);
    Check(f.control->Drain().empty(),"no false new action delivery");});
  Case("driver-close-outlives-scoped-poll-safely",[]{Fixture f;f.Start();f.control->Drain();
    GuestCall call(f.control,CallKind::State,0,1,1000);model::State state;call.Supply(state,1000);
    f.control->Close();f.control->Drain();f.control.reset();
    call.Observe(0,&state,false,1001);Check(!call.Supplies(CallKind::State,0)&&state.buttons==0,"closed shared state safely retained and neutralized");});
  for(int reason=0;reason<3;++reason)Case("final-return-revalidates-supplied-press-"+std::to_string(reason),[=]{Fixture f;f.Start();f.control->Drain();
    GuestCall call(f.control,CallKind::State,0,1,1000);model::State state;Check(call.Supply(state,1000)&&state.buttons==16,"press was supplied");
    const uint64_t now=reason==1?2000:1001;
    if(reason==0)f.window.foreground=false;
    else if(reason==2)f.control->Lifecycle(7,11,true,false,{},now);
    call.Observe(0,&state,false,now);
    const auto events=f.control->Drain();Check(state.buttons==0,"stale press cannot be the final guest buffer");
    bool boundary=false;for(const auto& event:events){Check(event.kind!=EventKind::Press&&event.kind!=EventKind::Completed,"no invented delivery");
      if(event.kind==EventKind::OutputAdjusted)boundary=event.originalOutput.buttons==16&&event.finalOutput.buttons==0;}
    Check(boundary,"actual buffer adjustment retained");});
  for(uint32_t flags:{2U,0x40000000U})Case("reserved-keyboard-only-query-"+std::to_string(flags),[=]{Fixture f;
    {GuestCall manual(f.control,CallKind::State,0,flags,1000);Check(manual.StateQuerySupported(),"unbound flags unchanged");}
    f.Start();f.control->Drain();GuestCall call(f.control,CallKind::State,0,flags,1001);
    Check(!call.StateQuerySupported(),"bound unsupported flags rejected before driver arbitration");
    model::State neutral;call.Observe(50,&neutral,false,1001);const auto events=f.control->Drain();
    Check(neutral.buttons==0&&events.size()==1&&events[0].kind==EventKind::Failed,"explicit failure with neutral output");});
  Case("bounded-event-queue-fails-closed",[]{Fixture f;f.Bind();
    for(uint64_t i=0;i<23;++i){const auto now=1000+i*200;
      const auto result=f.control->Submit(101+i,payload,now);if(!result.empty())break;
      f.Poll(now);f.control->Tick(now+100);f.Poll(now+101);}
    Check(f.control->EventsLost()&&!f.control->Capable(),"overflow cannot remain enabled");
    Check(f.control->Drain().size()<=64,"bounded retention");});
  std::printf("{\"cases\":%u,\"passed\":%u,\"failed\":%u}\n",cases,cases-failed,failed);return failed?1:0;
}
