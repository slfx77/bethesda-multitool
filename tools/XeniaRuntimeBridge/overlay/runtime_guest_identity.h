// Bounded guest Script identity reads. Layout proof is executable-specific.
#ifndef XENIA_CPU_RUNTIME_GUEST_IDENTITY_H_
#define XENIA_CPU_RUNTIME_GUEST_IDENTITY_H_
#include <array>
#include <cstdint>
#include <cstring>
#include <optional>
#include <string_view>

namespace xe::cpu::bmt {
inline uint32_t Be32(const uint8_t* value) {
  return (uint32_t(value[0])<<24)|(uint32_t(value[1])<<16)|
         (uint32_t(value[2])<<8)|value[3];
}
inline bool ModulePointer(uint32_t value) { return value>=0x82000000 && value<0x84000000 && !(value&3); }

// The chain follows BMT Core/Minidump/RttiReader's Xbox MSVC layout.
// Requiring Script plus a non-virtual TESForm base at offset zero excludes
// unrelated/secondary vtables before the common TESForm header is interpreted.
template<class Reader> bool ValidateFormVtable(Reader&& read,uint32_t vtable,std::string_view class_name) {
  auto word=[&](uint32_t va,uint32_t& value) {
    std::array<uint8_t,4> bytes{};
    if (!ModulePointer(va)||!read(va,bytes.data(),bytes.size())) return false;
    value=Be32(bytes.data()); return true;
  };
  auto named=[&](uint32_t td,std::string_view expected) {
    std::array<uint8_t,32> bytes{};
    if (!ModulePointer(td)||!read(td+8,bytes.data(),bytes.size())) return false;
    return expected.size()<bytes.size() && bytes[expected.size()]==0 &&
      std::memcmp(bytes.data(),expected.data(),expected.size())==0;
  };
  if (!ModulePointer(vtable)) return false;
  uint32_t col=0,signature=0,offset=0,displacement=0,td=0,chd=0,count=0,array=0;
  if (!word(vtable-4,col)||!word(col,signature)||signature||!word(col+4,offset)||offset||
      !word(col+8,displacement)||displacement||!word(col+12,td)||!named(td,class_name)||
      !word(col+16,chd)||!word(chd+8,count)||!count||count>32||!word(chd+12,array)) return false;
  for(uint32_t i=0;i<count;++i) {
    uint32_t base=0,base_td=0,mdisp=0,pdisp=0;
    if(!word(array+i*4,base)||!word(base,base_td)||!word(base+8,mdisp)||!word(base+12,pdisp)) return false;
    if(named(base_td,".?AVTESForm@@") && mdisp==0 && pdisp==UINT32_MAX) return true;
  }
  return false;
}
template<class Reader> bool ValidateScriptVtable(Reader&& read,uint32_t vtable) {
  return ValidateFormVtable(read,vtable,".?AVScript@@");
}

struct ScriptIdentity {
  const char* status="Unreadable";
  uint32_t form_id=0;
};

// TESForm cFormType+4 and iFormID+12 are proved by matching July PDB and
// separately checked 2011 Script::InitializeData / TESForm::SetFormID code.
template<class Reader> ScriptIdentity ReadFormIdentity(Reader&& read,uint32_t va,uint32_t expected_vtable,
                                                     std::optional<uint8_t> expected_type) {
  std::array<uint8_t,16> first{},second{};
  if(!va || (va&3) || uint64_t(va)+first.size()>0x100000000ULL ||
     !read(va,first.data(),first.size())) return {};
  if(Be32(first.data())!=expected_vtable || (expected_type && first[4]!=*expected_type)) return {"TypeMismatch",0};
  if(!read(va,second.data(),second.size())) return {};
  // Other fields may change normally. Only identity-bearing fields must agree.
  if(Be32(second.data())!=expected_vtable || second[4]!=first[4] ||
     Be32(second.data()+12)!=Be32(first.data()+12)) return {"ChangedDuringRead",0};
  const uint32_t form=Be32(first.data()+12);
  if(!form) return {"Anonymous",0};
  if(form==UINT32_MAX) return {"InvalidFormId",0};
  return {"Resolved",form};
}
template<class Reader> ScriptIdentity ReadScriptIdentity(Reader&& read,uint32_t va,uint32_t expected_vtable) {
  return ReadFormIdentity(read,va,expected_vtable,uint8_t(0x11));
}
template<class Reader> ScriptIdentity ReadPlayerIdentity(Reader&& read,uint32_t va,uint32_t expected_vtable) {
  auto identity=ReadFormIdentity(read,va,expected_vtable,std::nullopt);
  if(identity.form_id && identity.form_id!=0x14) return {"UnexpectedPlayerFormId",0};
  return identity;
}
} // namespace xe::cpu::bmt
#endif
