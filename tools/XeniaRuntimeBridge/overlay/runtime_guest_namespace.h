// Snapshot the calibrated guest compiled-file table; no disk identity inferred.
#ifndef XENIA_CPU_RUNTIME_GUEST_NAMESPACE_H_
#define XENIA_CPU_RUNTIME_GUEST_NAMESPACE_H_
#include "runtime_guest_identity.h"
#include <algorithm>
#include <string>
#include <utility>
#include <vector>

namespace xe::cpu::bmt {
struct NamespaceLayout {
  uint32_t handler_global;
  uint32_t count_offset;
  uint32_t files_offset;
};
struct PluginEntry {
  uint32_t index;
  std::string name;
  uint32_t file_va;
  bool operator==(const PluginEntry&) const = default;
};
struct PluginNamespace {
  const char* status="unavailable";
  const char* detail="Unreadable";
  uint32_t handler_va=0;
  uint32_t observed_count=0;
  std::vector<PluginEntry> entries;
};
inline std::string AsciiLower(std::string text) {
  for(auto& c:text) if(c>='A' && c<='Z') c=char(c-'A'+'a');
  return text;
}

// Reader calls stay <=64 bytes, matching the adapter's guarded memory accessor.
template<class Reader> PluginNamespace ReadPluginNamespaceOnce(Reader&& read,const NamespaceLayout& layout) {
  PluginNamespace result;
  auto bytes=[&](uint32_t va,uint8_t* data,size_t size) {
    return va && uint64_t(va)+size<=0x100000000ULL && read(va,data,size);
  };
  auto word=[&](uint32_t va,uint32_t& value) {
    std::array<uint8_t,4> data{};
    if((va&3)||!bytes(va,data.data(),data.size())) return false;
    value=Be32(data.data());return true;
  };
  if(!word(layout.handler_global,result.handler_va)||!result.handler_va||(result.handler_va&3)||
     uint64_t(result.handler_va)+layout.files_offset+255*4>0x100000000ULL||
     !word(result.handler_va+layout.count_offset,result.observed_count)) return result;
  if(!result.observed_count) {result.detail="NoCompiledFiles";return result;}
  if(result.observed_count>255) {result.detail="InvalidCount";return result;}
  result.status="partial";
  std::vector<std::string> names;
  for(uint32_t index=0;index<result.observed_count;++index) {
    uint32_t file=0;
    uint8_t stored_index=0;
    if(!word(result.handler_va+layout.files_offset+index*4,file)||!file||(file&3)||
       uint64_t(file)+1037>0x100000000ULL||!bytes(file+1036,&stored_index,1)) return result;
    if(stored_index!=index) {result.detail="SlotIndexMismatch";return result;}
    std::string name;
    bool terminated=false;
    for(uint32_t offset=0;offset<260 && !terminated;offset+=64) {
      std::array<uint8_t,64> chunk{};
      const auto size=std::min(uint32_t(chunk.size()),260-offset);
      if(!bytes(file+32+offset,chunk.data(),size)) return result;
      for(uint32_t i=0;i<size;++i) {
        const auto c=chunk[i];
        if(!c) {terminated=true;break;}
        if(c<32 || c>=127 || c=='"' || c=='\\' || c=='/' || c==':') {
          result.detail="InvalidFilename";return result;
        }
        name+=char(c);
      }
    }
    if(!terminated || name.empty()) {result.detail="InvalidFilename";return result;}
    auto canonical=AsciiLower(name);
    if(!(canonical.ends_with(".esm")||canonical.ends_with(".esp"))) {
      result.detail="InvalidFilename";return result;
    }
    if(std::find(names.begin(),names.end(),canonical)!=names.end()) {
      result.detail="DuplicateFilename";return result;
    }
    names.push_back(std::move(canonical));
    result.entries.push_back({index,std::move(name),file});
  }
  result.status="complete";result.detail="ValidatedCompiledTable";
  return result;
}

// A stable complete table is required. Retain the first candidates on change,
// but never promote them to a complete namespace or on-disk plugin identity.
template<class Reader> PluginNamespace ReadPluginNamespace(Reader&& read,const NamespaceLayout& layout) {
  auto first=ReadPluginNamespaceOnce(read,layout);
  if(std::string_view(first.status)!="complete") return first;
  const auto second=ReadPluginNamespaceOnce(read,layout);
  if(std::string_view(second.status)!="complete" || first.handler_va!=second.handler_va ||
     first.observed_count!=second.observed_count || first.entries!=second.entries) {
    first.status="partial";first.detail="ChangedDuringRead";
  }
  return first;
}
} // namespace xe::cpu::bmt
#endif
