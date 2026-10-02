// July addresses use its matching PDB. The 2011 XEX does NOT match its bundled PDB:
// its addresses use full-function instruction correspondence against the older
// named functions, including Run's direct call into EvaluateLine. See evidence
// 2011-function-comparison.{json,txt}; the actual XEX CodeView identity is retained.
// Every profile additionally requires exact guest hash and live entry bytes.
#ifndef XENIA_CPU_RUNTIME_PROFILES_H_
#define XENIA_CPU_RUNTIME_PROFILES_H_
#include <array>
#include <cstdint>
#include <string_view>
#include "runtime_guest_namespace.h"
namespace xe::cpu::bmt {
struct NamespaceProof {
  NamespaceLayout layout;
  uint32_t global_high_instruction;
  uint32_t global_low_instruction;
  uint32_t count_store_instruction;
  uint32_t filename_instruction;
  uint32_t compiled_file_function;
  uint32_t file_index_function;
};
struct ProbeProfile {
  const char* name;
  uint32_t address;
  std::array<uint8_t, 16> bytes;
};
struct RuntimeProfile {
  const char* identity;
  const char* xex_sha256;
  const char* pdb_guid;
  const char* symbol_basis;
  uint32_t script_vtable;
  uint32_t script_type_value_instruction;
  uint32_t script_type_store_instruction;
  uint32_t form_id_store_instruction;
  uint32_t player_pointer;
  uint32_t player_vtable;
  uint32_t get_dead_condition;
  NamespaceProof namespace_proof;
  std::array<ProbeProfile, 3> probes;
};
inline constexpr std::array<RuntimeProfile, 2> kProfiles{{
  {"july-2010-release-beta-patched", "9c3a51dbde21918bf3f09f7eecedeb05893b42628eb32b50ce7085a9ab1b7cfc",
   "661E6AAF-C71E-4E23-9987-D70A32B29C6A", "matching-pdb-and-entry-bytes",
   0x82030894,0x8242CCDC,0x8242CCFC,0x8231D08C,0x8318758C,0x82008E94,0x824206D0,
   {{0x8317A808,532,536},0x822E82C0,0x822E82CC,0x82303F6C,0x822F4170,0x822F0D20,0x82305550}, {{
    {"Main::RunScripts", 0x826E9AE0, {0x7D,0x88,0x02,0xA6,0x91,0x81,0xFF,0xF8,0x94,0x21,0xFF,0xA0,0x3D,0x00,0x83,0x21}},
    {"ScriptRunner::Run", 0x824680C8, {0x7D,0x88,0x02,0xA6,0x48,0x93,0x1E,0xD9,0xDB,0xE1,0xFF,0x88,0x94,0x21,0xF7,0xE0}},
    {"ScriptRunner::EvaluateLine", 0x82467078, {0x7D,0x88,0x02,0xA6,0x48,0x93,0x2F,0x29,0xDB,0xE1,0xFF,0x88,0x94,0x21,0xF1,0x00}}
   }}},
  {"2011-bundle-release-beta-xenia-patched", "1e91b9b9fb9f9602005f4c03f47e95f437bfe81883b6ce1072f8f7c68d892736",
   "5B7FD6B4-EEFE-4F75-B919-89BBDE89BB56", "full-function-instruction-correspondence-and-entry-bytes",
   0x82030974,0x8242F16C,0x8242F18C,0x8231CD44,0x831980B4,0x82008EBC,0x82422BA8,
   {{0x8318AD0C,536,540},0x822E76B0,0x822E76BC,0x82303714,0x822F3718,0x822F00E8,0x82304CF8}, {{
    {"Main::RunScripts", 0x826F72B8, {0x7D,0x88,0x02,0xA6,0x91,0x81,0xFF,0xF8,0x94,0x21,0xFF,0xA0,0x3D,0x00,0x83,0x22}},
    {"ScriptRunner::Run", 0x8246B120, {0x7D,0x88,0x02,0xA6,0x48,0x94,0x53,0x61,0xDB,0xE1,0xFF,0x88,0x94,0x21,0xF7,0xE0}},
    {"ScriptRunner::EvaluateLine", 0x8246A0D0, {0x7D,0x88,0x02,0xA6,0x48,0x94,0x63,0xB1,0xDB,0xE1,0xFF,0x88,0x94,0x21,0xF1,0x00}}
   }}}
}};
inline const RuntimeProfile* SelectProfile(std::string_view hash) {
  for (const auto& profile : kProfiles) if (hash == profile.xex_sha256) return &profile;
  return nullptr;
}
}  // namespace xe::cpu::bmt
#endif
