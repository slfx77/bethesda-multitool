#pragma once
#include <functional>
#include <string>
#include <vector>

namespace bmt_live_input {
// Install during NVSEPlugin_Load, before xNVSE chains the game's DirectInput import.
bool Install();
void Tick();
// May be called by the pipe thread. Clears all synthetic state synchronously.
void Release();
bool StartSequence(const std::string& json, std::string& error);
bool Active();
std::string TakeCompletionJson();
std::string DiagnosticsJson();
void SetSnapshotCallback(std::function<std::string(const std::vector<std::string>&)> callback);
#ifdef BMT_LIVE_INPUT_TEST
namespace testing {
struct Sample { unsigned char keys[256]{}, buttons[8]{}; long dx=0,dy=0; };
void Reset(unsigned long long now);
void TickAt(unsigned long long now);
void SetForeground(bool value);
Sample Consume();
}
#endif
}
