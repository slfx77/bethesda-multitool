#include "xenia/cpu/runtime_gamepad_driver.h"
#include "xenia/base/cvar.h"
#include "xenia/ui/window_listener.h"
#include <chrono>
#include <cstring>
#include <mutex>
#if XE_PLATFORM_WIN32
#include "xenia/ui/window_win.h"
#endif

DEFINE_bool(bmt_gamepad,false,"Enable the BMT reserved guest-controller adapter; a verified run binding is also required","HID");

namespace xe::cpu {
namespace {
std::mutex registry_mutex;
std::weak_ptr<gamepad::Control> registered;

class Driver final:public hid::InputDriver,public ui::WindowListener {
 public:
  Driver(ui::Window* window,size_t z_order,std::shared_ptr<gamepad::Control> control):
    InputDriver(window,z_order),control_(std::move(control)) {window->AddListener(this);}
  ~Driver() override {control_->Close();window()->RemoveListener(this);}
  X_STATUS Setup() override {return X_STATUS_SUCCESS;}
  hid::InputType GetInputType() const override {return hid::InputType::Controller;}
  X_RESULT GetCapabilities(uint32_t user,uint32_t,hid::X_INPUT_CAPABILITIES* output) override {
    auto scope=gamepad::GuestCall::Current();
    if(!scope || (!scope->Supplies(RuntimeGamepadScope::Kind::Capabilities,user)&&
       !scope->Supplies(RuntimeGamepadScope::Kind::State,user))) return X_ERROR_DEVICE_NOT_CONNECTED;
    if(!output) return X_ERROR_BAD_ARGUMENTS;
    *output={};output->type=hid::XINPUT_DEVTYPE_GAMEPAD;output->sub_type=hid::XINPUT_DEVSUBTYPE_GAMEPAD;
    output->gamepad.buttons=::bmt::gamepad::kAllowedButtons;
    // Report ordinary axis ranges for InputSystem's deadzone bookkeeping; actual
    // typed actions remain digital and all returned axes are neutral.
    output->gamepad.thumb_lx=32767;output->gamepad.thumb_ly=32767;
    output->gamepad.thumb_rx=32767;output->gamepad.thumb_ry=32767;
    return X_ERROR_SUCCESS;
  }
  X_RESULT GetState(uint32_t user,hid::X_INPUT_STATE* output) override {
    auto scope=gamepad::GuestCall::Current();
    if(!scope||!scope->Supplies(RuntimeGamepadScope::Kind::State,user)) return X_ERROR_DEVICE_NOT_CONNECTED;
    if(!output) return X_ERROR_BAD_ARGUMENTS;
    ::bmt::gamepad::State supplied;
    if(!scope->Supply(supplied,RuntimeGamepadMilliseconds())) return X_ERROR_DEVICE_NOT_CONNECTED;
    *output={};output->packet_number=supplied.packet;output->gamepad.buttons=supplied.buttons;
    output->gamepad.left_trigger=supplied.leftTrigger;output->gamepad.right_trigger=supplied.rightTrigger;
    output->gamepad.thumb_lx=supplied.leftX;output->gamepad.thumb_ly=supplied.leftY;
    output->gamepad.thumb_rx=supplied.rightX;output->gamepad.thumb_ry=supplied.rightY;
    return X_ERROR_SUCCESS;
  }
  X_RESULT SetState(uint32_t user,hid::X_INPUT_VIBRATION*) override {
    auto scope=gamepad::GuestCall::Current();
    return scope&&scope->Supplies(RuntimeGamepadScope::Kind::Vibration,user)?X_ERROR_SUCCESS:X_ERROR_DEVICE_NOT_CONNECTED;
  }
  X_RESULT GetKeystroke(uint32_t,uint32_t,hid::X_INPUT_KEYSTROKE*) override {
    return X_ERROR_DEVICE_NOT_CONNECTED; // Reserved-slot XAM queries are explicitly empty.
  }
  void OnLostFocus(ui::UISetupEvent&) override {control_->LostFocus(RuntimeGamepadMilliseconds());}
  void OnClosing(ui::UIEvent&) override {control_->Close();}
 private:
  std::shared_ptr<gamepad::Control> control_;
};
}

uint64_t RuntimeGamepadMilliseconds() {
  return uint64_t(std::chrono::duration_cast<std::chrono::milliseconds>(
    std::chrono::steady_clock::now().time_since_epoch()).count());
}
std::shared_ptr<gamepad::Control> AcquireRuntimeGamepad() {
  std::lock_guard lock(registry_mutex);return registered.lock();
}
std::unique_ptr<hid::InputDriver> CreateRuntimeGamepad(ui::Window* window,size_t z_order) {
  if(!cvars::bmt_gamepad) return nullptr;
#if XE_PLATFORM_WIN32
  // Called during driver construction on the window's normal setup path. Never
  // request activation, synthesize input or read mutable Window fields off-thread.
  const auto hwnd=static_cast<ui::Win32Window*>(window)->hwnd();
  const auto pid=GetCurrentProcessId();DWORD owner=0;
  const auto thread=GetWindowThreadProcessId(hwnd,&owner);
  if(!hwnd||!thread||owner!=pid) return nullptr;
  auto control=std::make_shared<gamepad::Control>([hwnd,pid,thread] {
    DWORD actual=0;const auto actual_thread=GetWindowThreadProcessId(hwnd,&actual);
    const bool valid=IsWindow(hwnd)&&actual==pid&&actual_thread==thread;
    return gamepad::WindowState{uint64_t(reinterpret_cast<uintptr_t>(hwnd)),pid,valid,
      valid&&IsWindowVisible(hwnd)&&!IsIconic(hwnd)&&GetForegroundWindow()==hwnd};
  });
  std::lock_guard lock(registry_mutex);
  if(!registered.expired()) return nullptr;
  auto driver=std::make_unique<Driver>(window,z_order,control);
  registered=control;return driver;
#else
  return nullptr;
#endif
}

RuntimeGamepadScope::RuntimeGamepadScope(Kind kind,uint32_t user,uint32_t flags):
  call_(AcquireRuntimeGamepad(),kind,user,flags,RuntimeGamepadMilliseconds()) {}
void RuntimeGamepadScope::Observe(uint32_t result,hid::X_INPUT_STATE* state,bool suppressed) {
  if(!call_.GuardsOutput()) return; // Default-off/unbound output remains untouched.
  ::bmt::gamepad::State actual;
  if(state) actual={uint32_t(state->packet_number),uint16_t(state->gamepad.buttons),
    state->gamepad.left_trigger,state->gamepad.right_trigger,int16_t(state->gamepad.thumb_lx),
    int16_t(state->gamepad.thumb_ly),int16_t(state->gamepad.thumb_rx),int16_t(state->gamepad.thumb_ry)};
  call_.Observe(result,state?&actual:nullptr,suppressed,RuntimeGamepadMilliseconds());
  // This is the final guest-visible buffer, after arbitration and deadzone work.
  // Revalidation may have neutralized a supplied press after focus/lifecycle loss.
  if(state) {
    state->packet_number=actual.packet;state->gamepad.buttons=actual.buttons;
    state->gamepad.left_trigger=actual.leftTrigger;state->gamepad.right_trigger=actual.rightTrigger;
    state->gamepad.thumb_lx=actual.leftX;state->gamepad.thumb_ly=actual.leftY;
    state->gamepad.thumb_rx=actual.rightX;state->gamepad.thumb_ry=actual.rightY;
  }
}
bool RuntimeGamepadScope::ReservedSlot(uint32_t slot) {
  auto control=AcquireRuntimeGamepad();return control&&control->Reserved(slot);
}
} // namespace xe::cpu
