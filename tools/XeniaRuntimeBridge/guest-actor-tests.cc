#include "overlay/runtime_guest_actor.h"
#include "overlay/runtime_profiles.h"
#include <functional>
#include <iostream>
#include <map>
#include <stdexcept>
#include <vector>
using namespace xe::cpu::bmt;
struct Memory {
  std::map<uint32_t,uint8_t> data;
  bool change_base=false;int singleton_reads=0;
  const ActorProfile& profile;
  const uint32_t singleton,player_vtable;
  static constexpr uint32_t player=0x40000000,base=0x40001000;
  void region(uint32_t va,size_t size){for(size_t i=0;i<size;++i)data[va+uint32_t(i)]=0;}
  void word(uint32_t va,uint32_t value){for(int i=0;i<4;++i)data[va+i]=uint8_t(value>>(24-i*8));}
  void text(uint32_t va,std::string_view value){for(size_t i=0;i<value.size();++i)data[va+uint32_t(i)]=uint8_t(value[i]);}
  Memory(const ActorProfile& actor_profile,const RuntimeProfile& runtime_profile):
    profile(actor_profile),singleton(runtime_profile.player_pointer),player_vtable(runtime_profile.player_vtable) {
    word(singleton,player);region(player,0x34);word(player,player_vtable);data[player+4]=0x3A;word(player+12,0x14);word(player+0x30,base);
    region(base,16);word(base,0x82001004);data[base+4]=0x29;word(base+12,7);
    for(uint32_t va:{0x82001000U,0x82002000U,0x82003000U,0x82004000U,0x82005000U,0x82006000U,0x82007000U})region(va,64);
    word(0x82001000,0x82002000);word(0x8200200C,0x82003000);word(0x82002010,0x82004000);
    text(0x82003008,".?AVTESNPC@@");word(0x82004008,1);word(0x8200400C,0x82005000);
    word(0x82005000,0x82006000);word(0x82006000,0x82007000);word(0x8200600C,UINT32_MAX);text(0x82007008,".?AVTESForm@@");
    for(size_t i=0;i<kActorValueCodes.size();++i) {
      const auto info=0x40002000+uint32_t(i)*0x100,name=info+0x80;
      word(profile.info_table+kActorValueCodes[i]*4,info);word(info+0x48,name);region(name,32);text(name,kActorValueNames[i]);
    }
    auto digit=[](char c){return uint8_t(c<='9'?c-'0':c-'a'+10);};
    for(const auto& routine:profile.routines)
      for(size_t i=0;i<routine.hex.size()/2;++i)data[routine.address+uint32_t(i)]=uint8_t(digit(routine.hex[i*2])*16+digit(routine.hex[i*2+1]));
  }
  bool read(uint32_t va,uint8_t* out,size_t size){
    if(size>64)throw std::runtime_error("unbounded read");
    if(va==singleton && ++singleton_reads==2 && change_base)word(base+12,8);
    for(size_t i=0;i<size;++i){const auto found=data.find(va+uint32_t(i));if(found==data.end())return false;out[i]=found->second;}
    return true;
  }
};
int main(){
  struct Case {const char* name;std::function<void(Memory&)> change;const char* expected;};
  const std::vector<Case> cases{
    {"player state admitted",[](auto&){},"Observed"},
    {"wrong reference",[](auto& m){m.word(m.player+12,0x15);},"PlayerIdentityUnavailable"},
    {"wrong player vtable",[](auto& m){m.word(m.player,0x82000000);},"PlayerIdentityUnavailable"},
    {"wrong base",[](auto& m){m.word(m.base+12,8);},"PlayerBaseIdentityUnavailable"},
    {"wrong base RTTI",[](auto& m){m.data[0x8200300C]='X';},"PlayerBaseIdentityUnavailable"},
    {"missing AV info",[](auto& m){m.word(m.profile.info_table+16*4,0);},"ActorValueInfoUnavailable"},
    {"wrong AV name",[](auto& m){m.data[0x40002080]='X';},"ActorValueIndexMismatch"},
    {"changed base",[](auto& m){m.change_base=true;},"ChangedDuringRead"}
  };
  try {
    size_t count=0;
    for(const auto& profile:kActorProfiles) {
      const auto* runtime=SelectProfile(profile.guest_sha256);
      if(!runtime || FindActorProfile(runtime->xex_sha256)!=&profile)throw std::runtime_error("profile identity selection");
      ++count;
      for(const auto& item:cases){Memory m(profile,*runtime);item.change(m);auto read=[&](uint32_t va,uint8_t* out,size_t size){return m.read(va,out,size);};
        const auto result=ReadPlayerActorInputs(read,profile,m.singleton,m.player_vtable);
        if(std::string_view(result.status)!=item.expected)throw std::runtime_error(item.name);
        if(std::string_view(result.status)=="Observed" && (result.base_id!=7 || result.player!=m.player))throw std::runtime_error("identity fields");
        ++count;}
      for(bool corrupt:{false,true}){Memory m(profile,*runtime);if(corrupt)m.data[profile.routines[0].address+100]^=1;
        if(ValidateActorRoutines([&](uint32_t va,uint8_t* out,size_t size){return m.read(va,out,size);},profile)==corrupt)throw std::runtime_error("full routine body validation");
        ++count;}
      const auto& other=kActorProfiles[profile.guest_sha256==kActorProfiles[0].guest_sha256?1:0];
      Memory m(profile,*runtime);auto read=[&](uint32_t va,uint8_t* out,size_t size){return m.read(va,out,size);};
      if(ValidateActorRoutines(read,other))throw std::runtime_error("wrong build bodies accepted");
      if(std::string_view(ReadPlayerActorInputs(read,other,m.singleton,m.player_vtable).status)=="Observed")throw std::runtime_error("wrong build AV table accepted");
      count+=2;
    }
    if(FindActorProfile("unknown") || FindActorProfile(""))throw std::runtime_error("unknown profile accepted");
    count+=2;
    struct EvidenceCase {const char* guest;const char* base;const char* index;};
    const std::array<EvidenceCase,2> evidence{{
      {"9c3a51dbde21918bf3f09f7eecedeb05893b42628eb32b50ce7085a9ab1b7cfc",
       "validated-july-handler-base-access-and-TESNPC-RTTI","validated-july-script-name-table"},
      {"1e91b9b9fb9f9602005f4c03f47e95f437bfe81883b6ce1072f8f7c68d892736",
       "validated-2011-handler-base-access-and-TESNPC-RTTI","validated-2011-script-name-table"}
    }};
    for(const auto& item:evidence) {
      const auto* profile=FindActorProfile(item.guest);
      if(!profile || profile->base_identity_evidence!=item.base || profile->actor_value_index_evidence!=item.index)
        throw std::runtime_error("selected guest provenance");
      ++count;
    }
    struct ValueCase{bool executed,returned;uint64_t bits;bool expected;};
    const std::vector<ValueCase> values{{true,true,0,true},{true,true,0x4050400000000000ULL,true},
      {false,true,0,false},{true,false,0,false},{true,true,0x7FF8000000000000ULL,false},{true,true,0x7FF0000000000000ULL,false}};
    for(const auto& test:values){std::array<uint8_t,8> raw{};for(size_t i=0;i<8;++i)raw[i]=uint8_t(test.bits>>(56-i*8));double value=123;
      if(ActorQueryValue(test.executed,test.returned,raw,value)!=test.expected)throw std::runtime_error("result admission");
      if(test.expected && test.bits==0 && value!=0)throw std::runtime_error("observed zero");}
    std::cout<<"Passed "<<count+values.size()<<" actor input/body/result cases\n";return 0;
  }catch(const std::exception& error){std::cerr<<"FAILED: "<<error.what()<<'\n';return 1;}
}
