// BMT guest input control; pure adapter core, no window or guest APIs.
#pragma once
#include "RuntimeGamepadModel.h"
#include <array>
#include <charconv>
#include <deque>
#include <functional>
#include <mutex>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace xe::cpu::gamepad {
namespace model = ::bmt::gamepad;

inline bool Digest(std::string_view value) {
  if(value.size()!=64) return false;
  for(char c:value) if(!((c>='0'&&c<='9')||(c>='a'&&c<='f'))) return false;
  return true;
}
struct Pulse {
  uint32_t slot=0, buttons=0, hold_ms=0, deadline_ms=0;
  std::string manifest;
};
// Six LF-separated fields. Strict ASCII/version, unsigned decimal and lowercase
// SHA256; no ad-hoc JSON coercions or caller-supplied admission booleans.
inline bool ParsePulse(std::string_view value,Pulse& result) {
  if(value.size()>192) return false;
  std::array<std::string_view,6> fields{};
  for(size_t i=0;i<fields.size();++i) {
    const auto end=value.find('\n');
    if((i+1==fields.size())!=(end==std::string_view::npos)) return false;
    fields[i]=value.substr(0,end);
    if(end!=std::string_view::npos) value.remove_prefix(end+1);
  }
  if(fields[0]!="gamepad-pulse/1"||!Digest(fields[5])) return false;
  Pulse parsed; uint32_t* numbers[]={&parsed.slot,&parsed.buttons,&parsed.hold_ms,&parsed.deadline_ms};
  for(size_t i=0;i<4;++i) {
    const auto text=fields[i+1];
    if(text.empty()||text.size()>10||(text.size()>1&&text.front()=='0')) return false;
    for(char c:text) if(c<'0'||c>'9') return false;
    const auto decoded=std::from_chars(text.data(),text.data()+text.size(),*numbers[i]);
    if(decoded.ec!=std::errc{}||decoded.ptr!=text.data()+text.size()) return false;
  }
  if(parsed.buttons>UINT16_MAX) return false;
  model::Request legal{{1,1,1,1},1,parsed.slot,uint16_t(parsed.buttons),parsed.hold_ms,parsed.deadline_ms};
  if(!model::Model::LegalRequest(legal)) return false;
  parsed.manifest=fields[5]; result=std::move(parsed); return true;
}

struct WindowState { uint64_t identity=0; uint32_t process=0; bool valid=false,foreground=false; };
struct VerifiedRunBinding {
  // Internal trusted-validator seam. No wire request constructs this proof.
  std::string guest_sha256,manifest_sha256,session;
  uint32_t process=0;
  uint64_t window=0,connection=0,capture=0;
};
struct PollTicket {
  model::Token token;
  uint64_t request=0;
  uint32_t slot=0,flags=0;
  bool reserved=false,selected=false;
  model::Request admitted;
  model::State supplied;
  std::string manifest;
};
enum class EventKind { Queued, Press, Release, Completed, Failed, OutputAdjusted };
struct Event { EventKind kind; model::Snapshot snapshot; std::string manifest; uint32_t flags=0;
  model::State originalOutput,finalOutput; };

inline const char* FailureName(model::Failure value) {
  switch(value) {
    case model::Failure::None:return "none";
    case model::Failure::Closed:return "closed";
    case model::Failure::Busy:return "busy";
    case model::Failure::InvalidToken:return "invalid-token";
    case model::Failure::NotConnected:return "disconnected";
    case model::Failure::NotCapturing:return "capture-ended";
    case model::Failure::ProfileUnavailable:return "profile-unavailable";
    case model::Failure::GuestUnavailable:return "guest-unavailable";
    case model::Failure::WindowUnavailable:return "window-unavailable";
    case model::Failure::FocusLost:return "focus-lost";
    case model::Failure::BindingChanged:return "binding-changed";
    case model::Failure::InvalidRequest:return "invalid-request";
    case model::Failure::ReusedRequest:return "reused-request";
    case model::Failure::ClockRegressed:return "clock-regressed";
    case model::Failure::Deadline:return "deadline";
    case model::Failure::Cancelled:return "cancelled";
    case model::Failure::Shutdown:return "shutdown";
    case model::Failure::GuestError:return "guest-error";
    case model::Failure::NoOutput:return "no-output";
    case model::Failure::UiSuppressed:return "ui-suppressed";
    case model::Failure::ReturnedStateMismatch:return "returned-state-mismatch";
  }
  return "unknown";
}

class Control {
 public:
  using ReadWindow=std::function<WindowState()>;
  explicit Control(ReadWindow read_window):read_window_(std::move(read_window)) {}
  void AttachProfile(std::string guest_sha256,uint32_t process) {
    std::lock_guard lock(mutex_);
    if(!closed_ && guest_.empty() && Digest(guest_sha256) && process) {guest_=std::move(guest_sha256);process_=process;}
  }
  void Lifecycle(uint64_t connection,uint64_t capture,bool connected,bool capturing,
      std::string session,uint64_t now) {
    std::lock_guard lock(mutex_);
    connection_=connection;capture_=capture;connected_=connected;capturing_=capturing;session_=std::move(session);
    Update(now);
    // Capture teardown explicitly releases reservation; a failed pulse alone does not.
    if(!connected || !capturing || binding_.connection!=connection || binding_.capture!=capture) bound_=false;
  }
  // Trusted admission only: the isolated-guest validator must establish actual
  // environment and directory/original-file proof before invoking this seam.
  bool BindVerifiedRun(const VerifiedRunBinding& proof) {
    std::lock_guard lock(mutex_);
    const auto window=read_window_();
    if(closed_||bound_||!connected_||!capturing_||guest_.empty()||session_.empty()||
       !window.valid||!window.foreground||window.process!=process_||
       proof.process!=process_||proof.window!=window.identity||!proof.window||
       proof.connection!=connection_||proof.capture!=capture_||
       proof.session!=session_||proof.guest_sha256!=guest_||!Digest(proof.manifest_sha256)) return false;
    binding_=proof;bound_=true;++binding_serial_;return true;
  }
  bool Capable() const {
    std::lock_guard lock(mutex_);
    const auto context=Context();
    return bound_&&!closed_&&context.connected&&context.capturing&&context.profileVerified&&
      context.guestIdentityVerified&&context.windowIdentityVerified&&context.foreground;
  }
  bool Reserved(uint32_t slot) const {
    std::lock_guard lock(mutex_);return !closed_&&bound_&&slot==0;
  }
  static bool StateFlagsSupported(uint32_t flags) {return flags==0||(flags&1)!=0;}
  std::string Submit(uint64_t id,std::string_view payload,uint64_t now) {
    std::lock_guard lock(mutex_);Update(now);
    if(!bound_||closed_) return "verified-guest-run-binding-required";
    Pulse pulse;
    if(!ParsePulse(payload,pulse)) return "invalid-gamepad-pulse";
    if(pulse.manifest!=binding_.manifest_sha256) return "gamepad-run-manifest-mismatch";
    const auto context=Context();
    model::Request request{context.token,id,pulse.slot,uint16_t(pulse.buttons),pulse.hold_ms,pulse.deadline_ms};
    const auto status=model_.Admit(context,request,now);
    if(status!=model::Failure::None) return FailureName(status);
    Push(EventKind::Queued);return events_lost_?"gamepad-event-buffer-full":"";
  }
  PollTicket Begin(uint32_t slot,uint32_t flags,uint64_t now) {
    std::lock_guard lock(mutex_);Update(now);
    PollTicket ticket;ticket.slot=slot;ticket.flags=flags;ticket.reserved=bound_&&!closed_&&slot==0;
    if(ticket.reserved) {ticket.token=Context().token;ticket.request=model_.Active()?model_.Current().request.requestId:0;
      ticket.manifest=binding_.manifest_sha256;if(ticket.request)ticket.admitted=model_.Current().request;}
    return ticket;
  }
  bool Supply(PollTicket& ticket,model::State& output,uint64_t now) {
    std::lock_guard lock(mutex_);Update(now);
    if(!bound_||closed_||ticket.slot!=0) return false;
    output={};output.packet=model_.Current().desired.packet;
    if(StateFlagsSupported(ticket.flags)&&ticket.reserved&&ticket.token==Context().token&&ticket.request&&ticket.request==model_.Current().request.requestId)
      model_.StateForPoll(Context(),model::Origin::GuestXam,0,now,output);
    ticket.selected=true;ticket.supplied=output;return true; // Bound failure/idle stays neutral, not fallback.
  }
  void Observe(const PollTicket& ticket,uint32_t result,model::State* actual,
      bool suppressed,uint64_t now) {
    std::lock_guard lock(mutex_);
    // Use one current boundary snapshot throughout revalidation and attribution.
    // Mixing multiple focus reads could accept a press, then fail the model on
    // a later read without clearing that already-selected output.
    const auto context=Context();const bool was_active=model_.Active();
    model_.Tick(context,now);if(was_active&&!model_.Active())Push(EventKind::Failed);
    const auto original=actual?*actual:model::State{};
    const bool same= ticket.request&&ticket.token==context.token&&ticket.request==model_.Current().request.requestId;
    const bool valid_press= ticket.selected&&same&&model_.Active()&&StateFlagsSupported(ticket.flags)&&
      result==0&&!suppressed&&actual&&*actual==model_.Current().desired;
    if(ticket.selected&&same&&model_.Active()&&model_.Current().desired.buttons&&result==0&&!suppressed&&actual&&
       actual->packet==model_.Current().desired.packet&&!(*actual==model_.Current().desired)) {
      model::GuestReturn bad{model::Origin::GuestXam,ticket.token,ticket.request,++ordinal_,ticket.slot,result,true,false,*actual};
      if(model_.Observe(context,bad,now)==model::ObservationResult::Failed)Push(EventKind::Failed,ticket.flags);
    }
    const bool guard=ticket.selected||ticket.reserved||(bound_&&ticket.slot==0);
    bool adjusted=false;
    if(guard&&actual&&!valid_press&&(actual->buttons||actual->leftTrigger||actual->rightTrigger||
       actual->leftX||actual->leftY||actual->rightX||actual->rightY)) {
      const auto packet=model_.Current().desired.buttons?ticket.supplied.packet+1:model_.Current().desired.packet;
      *actual={};actual->packet=packet;adjusted=true;
    }
    if(adjusted&&ticket.request) {
      model::Snapshot boundary;boundary.request=ticket.admitted;boundary.desired=*actual;
      if(events_.size()<64) events_.push_back({EventKind::OutputAdjusted,boundary,ticket.manifest,ticket.flags,original,*actual});
      else {events_lost_=true;model_.Close();closed_=true;bound_=false;}
    }
    if(!ticket.reserved||!ticket.request||(!ticket.selected&&!suppressed&&actual!=nullptr&&result==0)) return;
    model::GuestReturn returned{model::Origin::GuestXam,ticket.token,ticket.request,++ordinal_,
      ticket.slot,result,actual!=nullptr,suppressed,actual?*actual:model::State{}};
    const auto outcome=model_.Observe(context,returned,now);
    if(outcome==model::ObservationResult::PressObserved) Push(EventKind::Press,ticket.flags);
    else if(outcome==model::ObservationResult::ReleaseObserved) {Push(EventKind::Release,ticket.flags);Push(EventKind::Completed,ticket.flags);}
    else if(outcome==model::ObservationResult::Failed) Push(EventKind::Failed,ticket.flags);
  }
  void Tick(uint64_t now) {std::lock_guard lock(mutex_);Update(now);}
  void LostFocus(uint64_t now) {
    std::lock_guard lock(mutex_);const auto active=model_.Active();auto context=Context();context.foreground=false;
    model_.Tick(context,now);if(active&&!model_.Active()) Push(EventKind::Failed);
  }
  void Close() {
    std::lock_guard lock(mutex_);const auto active=model_.Active();model_.Close();closed_=true;bound_=false;
    if(active) Push(EventKind::Failed);
  }
  std::vector<Event> Drain() {
    std::lock_guard lock(mutex_);std::vector<Event> result;
    result.reserve(events_.size());while(!events_.empty()){result.push_back(std::move(events_.front()));events_.pop_front();}return result;
  }
  bool EventsLost() const {std::lock_guard lock(mutex_);return events_lost_;}
 private:
  model::Context Context() const {
    const auto window=read_window_();
    return {{connection_,capture_,binding_serial_,binding_.window},connected_,capturing_,
      bound_&&!closed_&&session_==binding_.session&&connection_==binding_.connection&&capture_==binding_.capture,
      !guest_.empty()&&guest_==binding_.guest_sha256,
      window.valid&&window.process==process_&&window.identity==binding_.window,
      window.foreground};
  }
  void Update(uint64_t now) {
    const bool active=model_.Active();model_.Tick(Context(),now);
    if(active&&!model_.Active()) Push(EventKind::Failed);
  }
  void Push(EventKind kind,uint32_t flags=0) {
    if(events_.size()>=64) {events_lost_=true;model_.Close();closed_=true;bound_=false;return;}
    events_.push_back({kind,model_.Current(),binding_.manifest_sha256,flags,{},{}});
  }
  mutable std::mutex mutex_;
  ReadWindow read_window_;
  model::Model model_;
  VerifiedRunBinding binding_;
  std::deque<Event> events_;
  std::string guest_,session_;
  uint32_t process_=0;
  uint64_t connection_=0,capture_=0,binding_serial_=0,ordinal_=0;
  bool connected_=false,capturing_=false,bound_=false,closed_=false,events_lost_=false;
};

enum class CallKind { State, Capabilities, Vibration };
// The same scoped call boundary is used by the real driver and the standalone
// fixture. Host InputSystem callers have no GuestCall and cannot receive a pulse.
class GuestCall {
 public:
  GuestCall(std::shared_ptr<Control> control,CallKind kind,uint32_t user,uint32_t flags,uint64_t now):
    control_(std::move(control)),kind_(kind),user_(user),previous_(current_) {
    if(control_) ticket_=control_->Begin(user,flags,now);
    current_=this;
  }
  ~GuestCall() {current_=previous_;}
  GuestCall(const GuestCall&)=delete;
  GuestCall& operator=(const GuestCall&)=delete;
  static GuestCall* Current() {return current_;}
  bool Reserved() const {return control_&&control_->Reserved(user_);}
  bool GuardsOutput() const {return ticket_.reserved||Reserved();}
  bool StateQuerySupported() const {return !Reserved()||Control::StateFlagsSupported(ticket_.flags);}
  bool Supplies(CallKind kind,uint32_t user) const {
    return kind==kind_&&user==user_&&control_&&control_->Reserved(user);
  }
  bool Supply(model::State& output,uint64_t now) {
    return control_&&kind_==CallKind::State&&control_->Supply(ticket_,output,now);
  }
  void Observe(uint32_t result,model::State* actual,bool suppressed,uint64_t now) {
    if(observed_||kind_!=CallKind::State||!control_) return;
    observed_=true;control_->Observe(ticket_,result,actual,suppressed,now);
  }
 private:
  std::shared_ptr<Control> control_;
  CallKind kind_;
  uint32_t user_;
  PollTicket ticket_;
  GuestCall* previous_;
  bool observed_=false;
  inline static thread_local GuestCall* current_=nullptr;
};
} // namespace xe::cpu::gamepad
