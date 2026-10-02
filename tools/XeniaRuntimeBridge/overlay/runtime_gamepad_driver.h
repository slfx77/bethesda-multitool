#pragma once
#include "xenia/cpu/runtime_gamepad_control.h"
#include "xenia/hid/input_driver.h"
#include <memory>

namespace xe::cpu {
uint64_t RuntimeGamepadMilliseconds();
std::shared_ptr<gamepad::Control> AcquireRuntimeGamepad();
std::unique_ptr<hid::InputDriver> CreateRuntimeGamepad(ui::Window* window,size_t z_order);

// Only XAM exports establish this scope. ImGui/host InputSystem polls do not.
class RuntimeGamepadScope {
 public:
  using Kind=gamepad::CallKind;
  RuntimeGamepadScope(Kind kind,uint32_t user,uint32_t flags);
  RuntimeGamepadScope(const RuntimeGamepadScope&)=delete;
  RuntimeGamepadScope& operator=(const RuntimeGamepadScope&)=delete;
  bool Reserved() const {return call_.Reserved();}
  bool StateQuerySupported() const {return call_.StateQuerySupported();}
  void Observe(uint32_t result,hid::X_INPUT_STATE* state,bool ui_suppressed=false);
  // The XAM keystroke path skips only the reserved slot; other slots retain their drivers.
  static bool ReservedSlot(uint32_t slot);
 private:
  gamepad::GuestCall call_;
};
} // namespace xe::cpu
