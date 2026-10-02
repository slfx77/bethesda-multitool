// BMT observation adapter for the pinned Xenia Canary checkout. BSD-3-Clause.
#ifndef XENIA_CPU_RUNTIME_BRIDGE_H_
#define XENIA_CPU_RUNTIME_BRIDGE_H_
#include <cstdint>
#include <filesystem>
#include <memory>
#include <string_view>

namespace xe::filesystem { class FileHandle; }
namespace xe { class Emulator; }

namespace xe::cpu {
class Processor;
class Function;
class RuntimeBridge {
 public:
  explicit RuntimeBridge(Processor* processor);
  ~RuntimeBridge();
  // Call once after loading the guest module, before starting guest threads.
  // Unknown files, non-matching code bytes and already compiled probes fail closed.
  bool Initialize(const std::filesystem::path& guest_path, xe::Emulator* emulator);
  // Invoked by the actual config loader/route selection, before guest startup.
  static void ObserveConfigRoute(std::string_view kind,
      const std::filesystem::path& path, std::string_view status);
  static Function* FindHook(Processor* processor, uint32_t address);
  static void ObserveHostOpen(std::string_view guest_path,
      xe::filesystem::FileHandle& file, bool read_only);
 private:
  struct Impl;
  std::unique_ptr<Impl> impl_;
};
}  // namespace xe::cpu
#endif
