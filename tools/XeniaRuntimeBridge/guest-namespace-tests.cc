#include "overlay/runtime_guest_namespace.h"
#include <iostream>
#include <map>
#include <stdexcept>

namespace {
using namespace xe::cpu::bmt;
struct Fixture {
  static constexpr uint32_t global=0x83170000,handler=0x90000000,file0=0x90001000,file1=0x90002000;
  std::map<uint32_t,uint8_t> memory;
  NamespaceLayout layout;
  int global_reads=0;
  const char* mutation="";
  explicit Fixture(bool july):layout{global,july?532u:536u,july?536u:540u} {
    word(global,handler);word(handler+layout.count_offset,2);
    word(handler+layout.files_offset,file0);word(handler+layout.files_offset+4,file1);
    file(file0,0,"FalloutNV.esm");file(file1,1,"Developer.esp");
  }
  void word(uint32_t address,uint32_t value) {
    for(int i=0;i<4;++i) memory[address+i]=uint8_t(value>>(24-i*8));
  }
  void file(uint32_t address,uint8_t index,std::string_view name) {
    for(uint32_t i=0;i<260;++i) memory[address+32+i]=0;
    for(uint32_t i=0;i<name.size();++i) memory[address+32+i]=uint8_t(name[i]);
    memory[address+1036]=index;
  }
  bool read(uint32_t address,uint8_t* bytes,size_t size) {
    if(size>64) throw std::runtime_error("unbounded reader request");
    if(address==global && ++global_reads==2) {
      if(std::string_view(mutation)=="count") word(handler+layout.count_offset,1);
      if(std::string_view(mutation)=="name") file(file1,1,"Changed.esp");
      if(std::string_view(mutation)=="pointer") word(global,handler+4);
    }
    for(size_t i=0;i<size;++i) {
      const auto at=memory.find(address+uint32_t(i));if(at==memory.end()) return false;
      bytes[i]=at->second;
    }
    return true;
  }
};
}
int main() {
  struct Case {const char* name;bool july;const char* status;const char* detail;};
  const Case cases[]={
    {"july",true,"complete","ValidatedCompiledTable"},
    {"2011",false,"complete","ValidatedCompiledTable"},
    {"empty",true,"unavailable","NoCompiledFiles"},
    {"excess",false,"unavailable","InvalidCount"},
    {"missing-global",true,"unavailable","Unreadable"},
    {"missing-slot",false,"partial","Unreadable"},
    {"wrong-index",true,"partial","SlotIndexMismatch"},
    {"duplicate",false,"partial","DuplicateFilename"},
    {"unterminated",true,"partial","InvalidFilename"},
    {"path",false,"partial","InvalidFilename"},
    {"wrong-extension",true,"partial","InvalidFilename"},
    {"missing-name",false,"partial","Unreadable"},
    {"count",true,"partial","ChangedDuringRead"},
    {"name",false,"partial","ChangedDuringRead"},
    {"pointer",true,"partial","ChangedDuringRead"},
    {"overflow",false,"partial","Unreadable"}
  };
  for(const auto& test:cases) {
    Fixture f(test.july);const std::string_view name=test.name;
    if(name=="empty") f.word(Fixture::handler+f.layout.count_offset,0);
    if(name=="excess") f.word(Fixture::handler+f.layout.count_offset,256);
    if(name=="missing-global") f.memory.erase(Fixture::global);
    if(name=="missing-slot") f.word(Fixture::handler+f.layout.files_offset+4,0);
    if(name=="wrong-index") f.memory[Fixture::file1+1036]=2;
    if(name=="duplicate") f.file(Fixture::file1,1,"falloutnv.ESM");
    if(name=="unterminated") for(uint32_t i=0;i<260;++i) f.memory[Fixture::file1+32+i]='a';
    if(name=="path") f.file(Fixture::file1,1,"..\\Developer.esp");
    if(name=="wrong-extension") f.file(Fixture::file1,1,"image.dds");
    if(name=="missing-name") f.memory.erase(Fixture::file1+32);
    if(name=="count"||name=="name"||name=="pointer") f.mutation=test.name;
    if(name=="overflow") f.word(Fixture::handler+f.layout.files_offset+4,0xFFFFFE00);
    auto result=ReadPluginNamespace([&](uint32_t va,uint8_t* data,size_t size){return f.read(va,data,size);},f.layout);
    if(std::string_view(result.status)!=test.status || std::string_view(result.detail)!=test.detail)
      throw std::runtime_error(std::string(test.name)+": "+result.status+"/"+result.detail);
    if(std::string_view(test.status)=="complete" && (result.entries.size()!=2 ||
       result.entries[0].name!="FalloutNV.esm" || result.entries[1].index!=1))
      throw std::runtime_error("valid namespace lost order or content");
  }
  std::cout<<"Passed "<<std::size(cases)<<" parameterized namespace cases\n";
}
