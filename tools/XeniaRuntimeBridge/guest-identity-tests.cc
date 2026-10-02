#include "overlay/runtime_guest_identity.h"
#include <functional>
#include <iostream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>
using namespace xe::cpu::bmt;
struct Memory {
  std::map<uint32_t,uint8_t> bytes;
  int object_reads=0;
  bool change_id=false,change_flags=false;
  void region(uint32_t va,uint32_t size){for(uint32_t i=0;i<size;++i)bytes[va+i]=0;}
  void word(uint32_t va,uint32_t value){for(uint32_t i=0;i<4;++i)bytes[va+i]=uint8_t(value>>(24-i*8));}
  void text(uint32_t va,const char* value){for(uint32_t i=0;value[i];++i)bytes[va+i]=uint8_t(value[i]);}
  bool read(uint32_t va,uint8_t* out,size_t size){
    if(va==0x40000000 && ++object_reads==2){
      if(change_id)word(va+12,0x01002FD1);
      if(change_flags)word(va+8,0x80000000);
    }
    for(size_t i=0;i<size;++i){auto found=bytes.find(va+uint32_t(i));if(found==bytes.end())return false;out[i]=found->second;}
    return true;
  }
  Memory(){
    region(0x40000000,16);word(0x40000000,0x82001004);bytes[0x40000004]=0x11;word(0x4000000C,0x01002FD0);
    for(uint32_t va: {0x82001000U,0x82002000U,0x82003000U,0x82004000U,0x82005000U,0x82006000U,0x82007000U})region(va,64);
    word(0x82001000,0x82002000);word(0x8200200C,0x82003000);word(0x82002010,0x82004000);
    text(0x82003008,".?AVScript@@");word(0x82004008,1);word(0x8200400C,0x82005000);
    word(0x82005000,0x82006000);word(0x82006000,0x82007000);word(0x8200600C,UINT32_MAX);text(0x82007008,".?AVTESForm@@");
  }
};
int main(){
  struct Case{const char* name;std::function<void(Memory&)> mutate;bool valid_rtti;const char* identity;uint32_t form;};
  const std::vector<Case> cases{
    {"mapped Script",[](auto&){},true,"Resolved",0x01002FD0},
    {"wrong form type",[](auto& m){m.bytes[0x40000004]=0x46;},true,"TypeMismatch",0},
    {"wrong vtable",[](auto& m){m.word(0x40000000,0x82002000);},true,"TypeMismatch",0},
    {"anonymous script",[](auto& m){m.word(0x4000000C,0);},true,"Anonymous",0},
    {"invalid ID",[](auto& m){m.word(0x4000000C,UINT32_MAX);},true,"InvalidFormId",0},
    {"missing object bytes",[](auto& m){m.bytes.erase(0x40000008);},true,"Unreadable",0},
    {"identity changes",[](auto& m){m.change_id=true;},true,"ChangedDuringRead",0},
    {"flags change only",[](auto& m){m.change_flags=true;},true,"Resolved",0x01002FD0},
    {"secondary vtable",[](auto& m){m.word(0x82002004,4);},false,nullptr,0},
    {"wrong RTTI class",[](auto& m){m.bytes[0x8200300C]='X';},false,nullptr,0},
    {"missing TESForm base",[](auto& m){m.bytes[0x8200700C]='X';},false,nullptr,0},
    {"displaced TESForm base",[](auto& m){m.word(0x82006008,4);},false,nullptr,0},
    {"virtual TESForm base",[](auto& m){m.word(0x8200600C,0);},false,nullptr,0},
    {"missing RTTI bytes",[](auto& m){m.bytes.erase(0x8200200C);},false,nullptr,0}
  };
  try{
    for(const auto& test:cases){
      Memory memory;test.mutate(memory);auto read=[&](uint32_t va,uint8_t* data,size_t size){return memory.read(va,data,size);};
      if(ValidateScriptVtable(read,0x82001004)!=test.valid_rtti)throw std::runtime_error(test.name);
      if(test.identity){auto result=ReadScriptIdentity(read,0x40000000,0x82001004);
        if(std::string(result.status)!=test.identity || result.form_id!=test.form)throw std::runtime_error(test.name);}
    }
    for(const auto& [form,expected]:std::vector<std::pair<uint32_t,const char*>>{
        {0x14,"Resolved"},{0x15,"UnexpectedPlayerFormId"},{0,"Anonymous"}}){
      Memory memory;memory.region(0x82003008,32);memory.text(0x82003008,".?AVPlayerCharacter@@");
      memory.word(0x4000000C,form);memory.bytes[0x40000004]=0x3A;
      auto read=[&](uint32_t va,uint8_t* data,size_t size){return memory.read(va,data,size);};
      if(!ValidateFormVtable(read,0x82001004,".?AVPlayerCharacter@@"))throw std::runtime_error("Player RTTI");
      auto identity=ReadPlayerIdentity(read,0x40000000,0x82001004);
      if(std::string(identity.status)!=expected || identity.form_id!=(form==0x14?form:0))throw std::runtime_error("Player target identity");
    }
    std::cout<<"Passed "<<cases.size()+3<<" parameterized guest identity cases\n";return 0;
  }catch(const std::exception& e){std::cerr<<"FAILED: "<<e.what()<<'\n';return 1;}
}
