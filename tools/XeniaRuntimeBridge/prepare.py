"""Apply the narrowly scoped adapter to the pinned source checkout; no build/download."""
from pathlib import Path
import argparse
import shutil
import subprocess

PIN = 'c3cd8617b18ef018ea8d638c865e0cced7399846'
parser = argparse.ArgumentParser()
parser.add_argument('source', type=Path)
args = parser.parse_args()
source = args.source.resolve()
if subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip() != PIN:
    raise SystemExit('Refusing a source revision other than the pinned Canary commit.')

def replace(path, before, after):
    target = source / path
    text = target.read_text(encoding='utf-8')
    if after in text:
        return
    if text.count(before) != 1:
        raise SystemExit(f'{path}: expected one known insertion anchor')
    target.write_text(text.replace(before, after), encoding='utf-8', newline='\n')

replace('src/xenia/cpu/ppc/ppc_hir_builder.cc',
        '#include "xenia/cpu/processor.h"',
        '#include "xenia/cpu/processor.h"\n#include "xenia/cpu/runtime_bridge.h"')
replace('src/xenia/cpu/ppc/ppc_hir_builder.cc',
        '    MaybeBreakOnInstruction(address);',
        '    if (auto hook = RuntimeBridge::FindHook(frontend_->processor(), address)) {\n'
        '      ContextBarrier();\n      CallExtern(hook);\n      ContextBarrier();\n    }\n\n'
        '    MaybeBreakOnInstruction(address);')
replace('src/xenia/emulator.h', 'class Processor;', 'class Processor;\nclass RuntimeBridge;')
replace('src/xenia/emulator.h', '  std::unique_ptr<cpu::Processor> processor_;',
        '  std::unique_ptr<cpu::Processor> processor_;\n  std::unique_ptr<cpu::RuntimeBridge> runtime_bridge_;')
replace('src/xenia/emulator.cc', '#include "xenia/cpu/thread_state.h"',
        '#include "xenia/cpu/thread_state.h"\n#include "xenia/cpu/runtime_bridge.h"\n'
        'DEFINE_bool(bmt_runtime, false, "Enable the BMT guest observation bridge for verified prototype executables", "CPU");')
replace('src/xenia/emulator.cc', '  processor_.reset();', '  runtime_bridge_.reset();\n  processor_.reset();')
# Migrate the already retained observation-only bridge call before matching the
# full insertion block. Only this exact earlier call form is accepted.
if 'runtime_bridge_->Initialize(path)' in (source / 'src/xenia/emulator.cc').read_text(encoding='utf-8'):
    replace('src/xenia/emulator.cc','runtime_bridge_->Initialize(path)','runtime_bridge_->Initialize(path, this)')
replace('src/xenia/emulator.cc', '  auto main_thread = kernel_state_->LaunchModule(module);',
        '  if (cvars::bmt_runtime) {\n'
        '    if (runtime_bridge_) {\n'
        '      XELOGE("BMT runtime: restart Xenia before launching another guest");\n'
        '      return X_STATUS_UNSUCCESSFUL;\n    }\n'
        '    runtime_bridge_ = std::make_unique<cpu::RuntimeBridge>(processor_.get());\n'
        '    if (!runtime_bridge_->Initialize(path, this)) return X_STATUS_UNSUCCESSFUL;\n  }\n\n'
        '  auto main_thread = kernel_state_->LaunchModule(module);')
# Query identity from the actual open handle; never infer a backing path from a
# filename or the launch arguments. Other host backends explicitly return none.
replace('src/xenia/base/filesystem.h',
        '  const std::filesystem::path& path() const { return path_; }',
        '  struct BackingIdentity {\n'
        '    std::filesystem::path path;\n'
        '    uint64_t size, write_time, volume, file_index;\n'
        '    bool operator==(const BackingIdentity&) const = default;\n  };\n'
        '  virtual std::optional<BackingIdentity> QueryBackingIdentity() const { return std::nullopt; }\n\n'
        '  const std::filesystem::path& path() const { return path_; }')
replace('src/xenia/base/filesystem_win.cc',
        '  void Flush() override { FlushFileBuffers(handle_); }',
        '  void Flush() override { FlushFileBuffers(handle_); }\n'
        '  std::optional<BackingIdentity> QueryBackingIdentity() const override {\n'
        '    BY_HANDLE_FILE_INFORMATION info{};\n'
        '    if (!GetFileInformationByHandle(handle_, &info)) return std::nullopt;\n'
        '    const DWORD size = GetFinalPathNameByHandleW(handle_, nullptr, 0, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);\n'
        '    if (!size || size > 32768) return std::nullopt;\n'
        '    std::wstring path(size, L\'\\0\');\n'
        '    const DWORD copied = GetFinalPathNameByHandleW(handle_, path.data(), size, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);\n'
        '    if (!copied || copied >= size) return std::nullopt;\n'
        '    path.resize(copied);\n'
        '    return BackingIdentity{std::filesystem::path(path),\n'
        '      (uint64_t(info.nFileSizeHigh) << 32) | info.nFileSizeLow,\n'
        '      (uint64_t(info.ftLastWriteTime.dwHighDateTime) << 32) | info.ftLastWriteTime.dwLowDateTime,\n'
        '      info.dwVolumeSerialNumber, (uint64_t(info.nFileIndexHigh) << 32) | info.nFileIndexLow};\n'
        '  }')
replace('src/xenia/vfs/devices/host_path_entry.cc',
        '#include "xenia/vfs/devices/host_path_file.h"',
        '#include "xenia/vfs/devices/host_path_file.h"\n#include "xenia/cpu/runtime_bridge.h"')
replace('src/xenia/vfs/devices/host_path_entry.cc',
        '  *out_file = new HostPathFile(desired_access, this, std::move(file_handle));',
        '  xe::cpu::RuntimeBridge::ObserveHostOpen(absolute_path(), *file_handle, is_read_only());\n'
        '  *out_file = new HostPathFile(desired_access, this, std::move(file_handle));')
# Controller wiring is inert by default and until the isolated guest-run
# validator admits the actual environment, profile and current capture.
replace('src/xenia/app/xenia_main.cc', '#include "xenia/emulator.h"',
        '#include "xenia/emulator.h"\n#include "xenia/cpu/runtime_gamepad_driver.h"')
replace('src/xenia/kernel/xam/xam_input.cc', '#include "xenia/emulator.h"',
        '#include "xenia/emulator.h"\n#include "xenia/cpu/runtime_gamepad_driver.h"')

def patch_function(path, begin, end, changes):
    target = source / path
    text = target.read_text(encoding='utf-8')
    if text.count(begin) != 1 or text.count(end) != 1:
        raise SystemExit(f'{path}: expected one known function boundary')
    first, last = text.index(begin), text.index(end)
    if first >= last:
        raise SystemExit(f'{path}: invalid function boundary')
    before = text[first:last]
    after = before
    for old, new in changes:
        if new in after:
            continue
        if after.count(old) != 1:
            raise SystemExit(f'{path}: expected one known controller insertion anchor')
        after = after.replace(old, new)
    if before != after:
        target.write_text(text[:first] + after + text[last:], encoding='utf-8', newline='\n')

replace('src/xenia/config.cc','#include "config.h"',
        '#include "config.h"\n#include "xenia/cpu/runtime_bridge.h"')
replace('src/xenia/config.cc','  XELOGI("Loaded config: {}", file_path);',
        '  xe::cpu::RuntimeBridge::ObserveConfigRoute("global-read", file_path, "loaded");\n'
        '  XELOGI("Loaded config: {}", file_path);')
replace('src/xenia/config.cc','  XELOGI("Loaded game config: {}", file_path);',
        '  xe::cpu::RuntimeBridge::ObserveConfigRoute("game-read", file_path, "loaded");\n'
        '  XELOGI("Loaded game config: {}", file_path);')
patch_function('src/xenia/config.cc','void SetupConfig(', 'void LoadGameConfig(', [
    ('  config::config_folder = config_folder;',
     '  xe::cpu::RuntimeBridge::ObserveConfigRoute("global-reset", {}, "unavailable");\n'
     '  config::config_folder = config_folder;'),
    ('    config_path = xe::to_path(cvars::config);',
     '    config_path = xe::to_path(cvars::config);\n'
     '    xe::cpu::RuntimeBridge::ObserveConfigRoute("global-route", config_path, "selected");'),
    ('    config_path = config_folder / config_name;',
     '    config_path = config_folder / config_name;\n'
     '    xe::cpu::RuntimeBridge::ObserveConfigRoute("global-route", config_path, "selected");')])
patch_function('src/xenia/config.cc','void LoadGameConfig(', '}  // namespace config', [
    ('  if (std::filesystem::exists(game_config_path)) {',
     '  const bool bmt_config_exists = std::filesystem::exists(game_config_path);\n'
     '  xe::cpu::RuntimeBridge::ObserveConfigRoute("game-route", game_config_path, bmt_config_exists ? "pending" : "absent");\n'
     '  if (bmt_config_exists) {')])

patch_function('src/xenia/app/xenia_main.cc',
    'std::vector<std::unique_ptr<hid::InputDriver>> EmulatorApp::CreateInputDrivers(',
    'bool EmulatorApp::OnInitialize()', [
        ('  return drivers;',
         '  if (auto bmt_driver = cpu::CreateRuntimeGamepad(window, EmulatorWindow::kZOrderHidInput)) {\n'
         '    if (XSUCCEEDED(bmt_driver->Setup())) drivers.insert(drivers.begin(), std::move(bmt_driver));\n'
         '  }\n  return drivers;')])
# Upgrade the retained first integration attempt's integer host-address calls.
# TypedPointerParam provides operator T*; host_address() returns uintptr_t.
if 'input_state ? input_state.host_address() : nullptr' in (source / 'src/xenia/kernel/xam/xam_input.cc').read_text(encoding='utf-8'):
    patch_function('src/xenia/kernel/xam/xam_input.cc',
        'dword_result_t XamInputGetState_entry(',
        'DECLARE_XAM_EXPORT2(XamInputGetState,', [
            ('bmt_scope.Observe(X_ERROR_SUCCESS, input_state ? input_state.host_address() : nullptr, true);',
             'bmt_scope.Observe(X_ERROR_SUCCESS, static_cast<X_INPUT_STATE*>(input_state), true);'),
            ('bmt_scope.Observe(X_ERROR_NOT_SUPPORTED, input_state ? input_state.host_address() : nullptr);',
             'bmt_scope.Observe(X_ERROR_NOT_SUPPORTED, static_cast<X_INPUT_STATE*>(input_state));'),
            ('bmt_scope.Observe(result, input_state ? input_state.host_address() : nullptr);',
             'bmt_scope.Observe(result, static_cast<X_INPUT_STATE*>(input_state));')])
patch_function('src/xenia/kernel/xam/xam_input.cc',
    'dword_result_t XamInputGetCapabilitiesEx_entry(',
    'DECLARE_XAM_EXPORT1(XamInputGetCapabilitiesEx,', [
        ('  auto input_system = kernel_state()->emulator()->input_system();',
         '  cpu::RuntimeGamepadScope bmt_scope(cpu::RuntimeGamepadScope::Kind::Capabilities, actual_user_index, actual_flags);\n'
         '  auto input_system = kernel_state()->emulator()->input_system();')])
patch_function('src/xenia/kernel/xam/xam_input.cc',
    'dword_result_t XamInputGetState_entry(',
    'DECLARE_XAM_EXPORT2(XamInputGetState,', [
        ('  if (input_state) {',
         '  cpu::RuntimeGamepadScope bmt_scope(cpu::RuntimeGamepadScope::Kind::State, user_index, flags);\n'
         '  if (input_state) {'),
        ('  if (kernel_state()->xam_state()->IsUIActive()) {\n    return X_ERROR_SUCCESS;\n  }',
         '  if (kernel_state()->xam_state()->IsUIActive()) {\n'
         '    bmt_scope.Observe(X_ERROR_SUCCESS, static_cast<X_INPUT_STATE*>(input_state), true);\n'
         '    return X_ERROR_SUCCESS;\n  }'),
        ('  // Games call this with a NULL state ptr, probably as a query.',
         '  // Games call this with a NULL state ptr, probably as a query.\n'
         '  if (!input_state && bmt_scope.Reserved()) {\n'
         '    bmt_scope.Observe(X_ERROR_BAD_ARGUMENTS, nullptr);\n'
         '    return X_ERROR_BAD_ARGUMENTS;\n  }'),
        ('  X_RESULT result;',
         '  if (!bmt_scope.StateQuerySupported()) {\n'
         '    bmt_scope.Observe(X_ERROR_NOT_SUPPORTED, static_cast<X_INPUT_STATE*>(input_state));\n'
         '    return X_ERROR_NOT_SUPPORTED;\n  }\n\n  X_RESULT result;'),
        ('  return result;',
         '  bmt_scope.Observe(result, static_cast<X_INPUT_STATE*>(input_state));\n'
         '  return result;')])
patch_function('src/xenia/kernel/xam/xam_input.cc',
    'dword_result_t XamInputSetState_entry(',
    'DECLARE_XAM_EXPORT1(XamInputSetState,', [
        ('  auto input_system = kernel_state()->emulator()->input_system();',
         '  cpu::RuntimeGamepadScope bmt_scope(cpu::RuntimeGamepadScope::Kind::Vibration, user_index, flags);\n'
         '  auto input_system = kernel_state()->emulator()->input_system();')])
patch_function('src/xenia/kernel/xam/xam_input.cc',
    'dword_result_t XamInputGetKeystrokeEx_entry(',
    'DECLARE_XAM_EXPORT1(XamInputGetKeystrokeEx,', [
        ('      auto result = input_system->GetKeystroke(i, flags, keystroke);',
         '      if (cpu::RuntimeGamepadScope::ReservedSlot(i)) continue;\n'
         '      auto result = input_system->GetKeystroke(i, flags, keystroke);'),
        ('  auto result = input_system->GetKeystroke(user_index, flags, keystroke);',
         '  if (cpu::RuntimeGamepadScope::ReservedSlot(user_index)) return X_ERROR_EMPTY;\n'
         '  auto result = input_system->GetKeystroke(user_index, flags, keystroke);')])

# Preserve the already-tested pure model verbatim; it is not a second copy to edit.
model = Path(__file__).parent / 'RuntimeGamepadModel.h'
model_target = source / 'src/xenia/cpu/RuntimeGamepadModel.h'
if not model_target.exists() or model_target.read_bytes() != model.read_bytes():
    shutil.copyfile(model, model_target)
for overlay in (Path(__file__).parent / 'overlay').iterdir():
    target = source / 'src/xenia/cpu' / overlay.name
    if not target.exists() or target.read_bytes() != overlay.read_bytes():
        shutil.copyfile(overlay, target)
print(f'Prepared {source} at {PIN}')
