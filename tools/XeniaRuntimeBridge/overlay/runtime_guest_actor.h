// Bounded typed reads for the separately admitted player profiles.
#ifndef XENIA_CPU_RUNTIME_GUEST_ACTOR_H_
#define XENIA_CPU_RUNTIME_GUEST_ACTOR_H_
#include "runtime_actor_profiles.h"
#include "runtime_guest_identity.h"
#include <algorithm>
#include <cmath>
#include <string>
namespace xe::cpu::bmt {
template<class Reader> bool ValidateActorRoutines(Reader&& read,const ActorProfile& profile) {
  auto digit=[](char c)->uint8_t{return uint8_t(c<='9'?c-'0':c-'a'+10);};
  for(const auto& routine:profile.routines) {
    for(size_t offset=0;offset<routine.hex.size()/2;offset+=64) {
      const auto count=std::min(size_t(64),routine.hex.size()/2-offset);
      std::array<uint8_t,64> actual{};
      if(!read(routine.address+uint32_t(offset),actual.data(),count)) return false;
      for(size_t i=0;i<count;++i)
        if(actual[i]!=uint8_t((digit(routine.hex[(offset+i)*2])<<4)|digit(routine.hex[(offset+i)*2+1]))) return false;
    }
  }
  return true;
}
struct ActorInputs {
  const char* status="Unreadable";
  uint32_t player=0,base=0,base_id=0;
  uint8_t reference_type=0,base_type=0;
  std::array<uint32_t,4> info{},name_pointer{};
  bool SameIdentity(const ActorInputs& other) const {
    return player==other.player && base==other.base && base_id==other.base_id &&
      reference_type==other.reference_type && base_type==other.base_type &&
      info==other.info && name_pointer==other.name_pointer;
  }
};
template<class Reader> ActorInputs ReadPlayerActorInputsOnce(Reader&& read,const ActorProfile& profile,uint32_t singleton,uint32_t player_vtable) {
  ActorInputs result;
  auto word=[&](uint32_t va,uint32_t& value) {
    std::array<uint8_t,4> raw{};
    if(!va || (va&3) || uint64_t(va)+raw.size()>0x100000000ULL || !read(va,raw.data(),raw.size())) return false;
    value=Be32(raw.data());return true;
  };
  if(!word(singleton,result.player) || result.player>UINT32_MAX-0x34 ||
     ReadPlayerIdentity(read,result.player,player_vtable).form_id!=0x14) {
    result.status="PlayerIdentityUnavailable";return result;
  }
  std::array<uint8_t,16> actor{},base{};
  if(!read(result.player,actor.data(),actor.size()) || Be32(actor.data())!=player_vtable || Be32(actor.data()+12)!=0x14 ||
     !word(result.player+profile.base_offset,result.base) || !result.base ||
     result.base>UINT32_MAX-base.size() || !read(result.base,base.data(),base.size())) return result;
  result.reference_type=actor[4];result.base_type=base[4];result.base_id=Be32(base.data()+12);
  if(result.base_id!=7 || !ValidateFormVtable(read,Be32(base.data()),".?AVTESNPC@@")) {
    result.status="PlayerBaseIdentityUnavailable";return result;
  }
  for(size_t i=0;i<kActorValueCodes.size();++i) {
    if(!word(profile.info_table+kActorValueCodes[i]*4,result.info[i]) ||
       !result.info[i] || result.info[i]>UINT32_MAX-0x4C ||
       !word(result.info[i]+0x48,result.name_pointer[i]) || !result.name_pointer[i] ||
       result.name_pointer[i]>UINT32_MAX-32) {result.status="ActorValueInfoUnavailable";return result;}
    std::array<uint8_t,32> name{};
    const auto expected=kActorValueNames[i];
    if(!read(result.name_pointer[i],name.data(),name.size()) || name[expected.size()]!=0 ||
       std::memcmp(name.data(),expected.data(),expected.size())!=0) {result.status="ActorValueIndexMismatch";return result;}
  }
  result.status="Observed";return result;
}
template<class Reader> ActorInputs ReadPlayerActorInputs(Reader&& read,const ActorProfile& profile,uint32_t singleton,uint32_t player_vtable) {
  auto first=ReadPlayerActorInputsOnce(read,profile,singleton,player_vtable);
  if(std::string_view(first.status)!="Observed") return first;
  const auto second=ReadPlayerActorInputsOnce(read,profile,singleton,player_vtable);
  if(std::string_view(second.status)!="Observed" || !first.SameIdentity(second)) first.status="ChangedDuringRead";
  return first;
}
inline bool ActorQueryValue(bool executed,bool handler_returned,const std::array<uint8_t,8>& raw,double& value) {
  uint64_t bits=0;for(auto byte:raw)bits=(bits<<8)|byte;
  std::memcpy(&value,&bits,sizeof(value));
  return executed && handler_returned && std::isfinite(value);
}
} // namespace xe::cpu::bmt
#endif
