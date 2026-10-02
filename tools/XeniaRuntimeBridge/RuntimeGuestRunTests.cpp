#include "overlay/runtime_guest_run.h"
#include "third_party/rapidjson/include/rapidjson/stringbuffer.h"
#include "third_party/rapidjson/include/rapidjson/writer.h"
#define NOMINMAX
#include <windows.h>
#include "overlay/runtime_guest_run_io.h"
#include <chrono>
#include <cstdio>
using namespace xe::cpu::guest_run;
namespace {
unsigned cases=0,failed=0;
void Check(bool value,const char* message){if(!value)throw std::runtime_error(message);}
template<class F> void Case(const std::string& name,F test){++cases;try{test();}catch(const std::exception& e){++failed;std::fprintf(stderr,"%s: %s\n",name.c_str(),e.what());}}
template<class F> void Refuses(F action,const char* reason){try{action();}catch(const std::exception& e){Check(std::string(e.what())==reason,e.what());return;}throw std::runtime_error("unexpected admission");}
const std::string sha(64,'a'),other(64,'b');
std::string Dump(const rapidjson::Value& value){rapidjson::StringBuffer text;rapidjson::Writer<rapidjson::StringBuffer> writer(text);value.Accept(writer);return {text.GetString(),text.GetSize()};}
struct Fixture {
  rapidjson::Document doc;
  Evidence environment{true,true,false,false,"C:/run/storage","C:/run/content","C:/run/cache","C:/game",
    "C:/run/storage/xenia-canary.config.toml","C:/run/storage/xenia-canary.config.toml","loaded",
    "C:/run/storage/config/12345678.config.toml","absent","C:/tool/xenia.exe",sha,"C:/game/default.xex",sha};
  unsigned checks=0,profile_reads=0;int fault=0;Io io;
  Fixture(){
    doc.Parse(R"({"schema":"bmt/runtime-guest-run-profile","version":1,"root":"C:/run",
      "environment":{"gameRoot":"C:/game","storageRoot":"C:/run/storage","contentRoot":"C:/run/content","cacheRoot":"C:/run/cache","configPath":"C:/run/storage/xenia-canary.config.toml","gameConfigPath":null},
      "guestManifest":{"schema":"bmt/runtime-guest-manifest","version":1,
        "emulator":{"name":"xenia.exe","path":"C:/tool/xenia.exe","sha256":"HASH","length":1,"lastWriteFileTime":2,"volumeSerial":3,"fileIndex":4},
        "guest":{"name":"default.xex","path":"C:/game/default.xex","sha256":"HASH","length":1,"lastWriteFileTime":2,"volumeSerial":3,"fileIndex":4},
        "plugins":[{"name":"FalloutNV.esm","path":"C:/game/FalloutNV.esm","sha256":"HASH","length":1,"lastWriteFileTime":2,"volumeSerial":3,"fileIndex":4}]},
      "guestManifestFile":{"originalPath":"C:/original/manifest.json","copyPath":"C:/run/inputs/guest-manifest.json","sha256":"HASH"},
      "scenario":{"originalPath":"C:/original/scenario.json","copyPath":"C:/run/inputs/scenario.json","sha256":"HASH"},
      "configuration":{"originalPath":"C:/original/config.toml","copyPath":"C:/run/storage/xenia-canary.config.toml","sha256":"HASH"},
      "gameConfiguration":null,"initialSave":null,
      "sourceRoots":[{"path":"C:/game","existed":true,"directories":[]},{"path":"C:/saves","existed":true,"directories":[]}],
      "sourceFiles":[{"path":"C:/tool/xenia.exe","sha256":"HASH"},{"path":"C:/game/default.xex","sha256":"HASH"},{"path":"C:/game/FalloutNV.esm","sha256":"HASH"},
        {"path":"C:/original/manifest.json","sha256":"HASH"},{"path":"C:/original/scenario.json","sha256":"HASH"},{"path":"C:/original/config.toml","sha256":"HASH"}],
      "files":[],"initialDirectories":[]})");
    std::function<void(rapidjson::Value&)> fill=[&](rapidjson::Value& v){
      if(v.IsObject())for(auto i=v.MemberBegin();i!=v.MemberEnd();++i)fill(i->value);
      else if(v.IsArray())for(auto& child:v.GetArray())fill(child);
      else if(v.IsString()&&std::string(v.GetString())=="HASH")v.SetString(sha.c_str(),rapidjson::SizeType(sha.size()),doc.GetAllocator());};fill(doc);
    for(const char* key:{"guestManifestFile","scenario","configuration"}) {rapidjson::Value copy(doc[key],doc.GetAllocator());doc["files"].PushBack(copy,doc.GetAllocator());}
    io.check=[&]{if(fault==1&&++checks>3)throw std::runtime_error("cancelled");};
    io.absent=[&](const std::string&){return fault!=2;};
    io.normalize=[&](const std::string& path,bool absent){
      if(fault==2&&absent)throw std::runtime_error("run-absent-route-exists");
      if(fault==3&&Same(path,"C:/run/content"))throw std::runtime_error("run-path-reparse");return path;};
    io.read=[&](const std::string& path,uint64_t,bool){
      File file{path,sha,{},1,2,4,3};
      if(Same(path,"C:/run/runtime-guest-profile.json")){file.bytes=Dump(doc);if(fault==4&&++profile_reads>1)file.sha256=other;}
      else if(Same(path,"C:/run/inputs/guest-manifest.json"))file.bytes=Dump(doc["guestManifest"]);
      if(fault==5&&Same(path,"C:/original/config.toml"))file.sha256=other;
      if(fault==6&&Same(path,"C:/run/inputs/scenario.json"))file.sha256=other;
      if(fault==7&&Same(path,"C:/game/default.xex"))file.index=9;
      if(fault==8&&Same(path,"C:/run/storage/xenia-canary.config.toml"))file.sha256=other; // Mutable startup output.
      return file;};
    io.scan=[&](const std::string& path,bool writable){
      if(fault==9&&writable)throw std::runtime_error("run-writable-file-alias");
      if(Same(path,"C:/game"))return Inventory{fault==10?std::vector<std::string>{"C:/game/default.xex"}:
        std::vector<std::string>{"C:/game/default.xex","C:/game/FalloutNV.esm"},{}};
      return Inventory{};};
  }
  Admission Run(){return Validate("C:/run/runtime-guest-profile.json",sha,environment,io);}
  void Set(rapidjson::Value& value,const char* key,const char* text){value[key].SetString(text,doc.GetAllocator());}
};
struct PipeRead {
  std::shared_ptr<PendingRead> read=std::make_shared<PendingRead>();
  HANDLE writer=INVALID_HANDLE_VALUE;
  PipeRead(){
    static unsigned serial=0;
    const auto name=L"\\\\.\\pipe\\BMT.GuestRun.Test."+std::to_wstring(GetCurrentProcessId())+L"."+std::to_wstring(++serial);
    read->file=CreateNamedPipeW(name.c_str(),PIPE_ACCESS_INBOUND|FILE_FLAG_OVERLAPPED,
      PIPE_TYPE_BYTE|PIPE_WAIT,1,4096,4096,0,nullptr);
    Check(read->file!=INVALID_HANDLE_VALUE,"test pipe creation");
    writer=CreateFileW(name.c_str(),GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr);
    Check(writer!=INVALID_HANDLE_VALUE,"test pipe client");
  }
  ~PipeRead(){if(writer!=INVALID_HANDLE_VALUE)CloseHandle(writer);}
};
}
int main(){
  Case("bounded-profile-admitted-from-independent-io",[]{Fixture f;auto a=f.Run();Check(a.source_bytes==6&&a.scenario_sha256==sha,"admission retains evidence totals");});
  Case("unknown-future-field-allowed",[]{Fixture f;f.doc.AddMember("futureField",true,f.doc.GetAllocator());f.Run();});
  Case("mutable-copied-config-not-rehashed-as-initial",[]{Fixture f;f.fault=8;f.Run();});
  const std::pair<int,const char*> failures[]={{1,"cancelled"},{2,"run-absent-route-exists"},{3,"run-path-reparse"},
    {4,"profile-changed-during-admission"},{5,"profile-original-changed"},{6,"profile-immutable-copy-changed"},
    {7,"profile-backing-changed"},{9,"run-writable-file-alias"},{10,"profile-original-inventory-changed"}};
  for(auto [fault,reason]:failures)Case("io-failure-"+std::to_string(fault),[=]{Fixture f;f.fault=fault;Refuses([&]{f.Run();},reason);});
  for(unsigned i=0;i<7;++i)Case("effective-environment-mismatch-"+std::to_string(i),[=]{Fixture f;
    switch(i){case 0:f.environment.observed=false;break;case 1:f.environment.game_read_only=false;break;case 2:f.environment.allow_plugins=true;break;
      case 3:f.environment.allow_game_writes=true;break;case 4:f.environment.cache="C:/original";break;case 5:f.environment.global_status="unavailable";break;
      case 6:f.environment.content="C:/different";break;}Refuses([&]{f.Run();},"profile-effective-environment-mismatch");});
  Case("wrong-profile-hash",[]{Fixture f;Refuses([&]{Validate("C:/run/runtime-guest-profile.json",other,f.environment,f.io);},"profile-hash-mismatch");});
  Case("wrong-version",[]{Fixture f;f.doc["version"].SetInt(2);Refuses([&]{f.Run();},"profile-version");});
  Case("wrong-number-type",[]{Fixture f;f.Set(f.doc,"version","1");Refuses([&]{f.Run();},"profile-number-invalid");});
  Case("writable-root-escape",[]{Fixture f;f.Set(f.doc["environment"],"storageRoot","C:/elsewhere");Refuses([&]{f.Run();},"profile-private-roots");});
  Case("original-root-overlap",[]{Fixture f;f.Set(f.doc["environment"],"gameRoot","C:/run/game");Refuses([&]{f.Run();},"profile-original-overlap");});
  Case("global-config-loader-path-mismatch",[]{Fixture f;f.environment.global_read="C:/original/config.toml";Refuses([&]{f.Run();},"profile-global-config-mismatch");});
  Case("unprepared-game-config",[]{Fixture f;f.environment.game_status="loaded";Refuses([&]{f.Run();},"profile-game-config-mismatch");});
  Case("nested-absent-game-route-refused",[]{Fixture f;f.environment.game_config="C:/run/storage/config/nested/123.config.toml";Refuses([&]{f.Run();},"profile-game-config-mismatch");});
  Case("prepared-loaded-game-config",[]{Fixture f;f.environment.game_status="loaded";
    rapidjson::Value copy(f.doc["configuration"],f.doc.GetAllocator());f.Set(copy,"copyPath",f.environment.game_config.c_str());
    f.doc["gameConfiguration"].CopyFrom(copy,f.doc.GetAllocator());f.doc["files"].PushBack(copy,f.doc.GetAllocator());
    f.Set(f.doc["environment"],"gameConfigPath",f.environment.game_config.c_str());f.Run();});
  Case("deleted-mutable-save-retains-original-pin",[]{Fixture f;
    rapidjson::Value copy(f.doc["configuration"],f.doc.GetAllocator());f.Set(copy,"copyPath","C:/run/content/user/save.fos");
    f.doc["initialSave"].CopyFrom(copy,f.doc.GetAllocator());f.doc["files"].PushBack(copy,f.doc.GetAllocator());
    auto normalize=f.io.normalize;f.io.normalize=[&](const std::string& path,bool missing){
      if(Same(path,"C:/run/content/user/save.fos")&&!missing)throw std::runtime_error("deleted save must be allowed");return normalize(path,missing);};f.Run();});
  Case("duplicate-original",[]{Fixture f;rapidjson::Value row(f.doc["sourceFiles"][0],f.doc.GetAllocator());f.doc["sourceFiles"].PushBack(row,f.doc.GetAllocator());Refuses([&]{f.Run();},"profile-original-invalid");});
  Case("copy-outside-root",[]{Fixture f;f.Set(f.doc["files"][0],"copyPath","C:/elsewhere/manifest.json");Refuses([&]{f.Run();},"profile-copy-invalid");});
  Case("selected-input-not-copy-inventory",[]{Fixture f;f.Set(f.doc["scenario"],"copyPath","C:/run/inputs/other.json");Refuses([&]{f.Run();},"profile-selected-input-not-copied");});
  Case("emulator-identity-mismatch",[]{Fixture f;f.environment.executable_hash=other;Refuses([&]{f.Run();},"profile-emulator-mismatch");});
  Case("guest-identity-mismatch",[]{Fixture f;f.environment.guest="C:/game/other.xex";Refuses([&]{f.Run();},"profile-guest-mismatch");});
  Case("duplicate-json-key",[]{Refuses([]{Parse("{\"version\":1,\"version\":2}");},"profile-duplicate-key");});
  Case("malformed-utf8",[]{Refuses([]{Parse("{\"x\":\"\xFF\"}");},"profile-json-invalid");});
  Case("embedded-null",[]{Refuses([]{Parse("{\"x\":\"a\\u0000b\"}");},"profile-text-invalid");});
  Case("depth-limit",[]{std::string s="{}";for(unsigned i=0;i<26;++i)s="{\"x\":"+s+"}";Refuses([&]{Parse(s);},"profile-depth-limit");});
  Case("overlapped-completed-read",[]{PipeRead p;DWORD sent=0;Check(WriteFile(p.writer,"data",4,&sent,nullptr)&&sent==4,"test write");
    Check(ReadChunk(p.read,0,4,[]{})==4&&std::string(reinterpret_cast<char*>(p.read->buffer.data()),4)=="data","completed bytes");});
  Case("overlapped-delayed-read",[]{PipeRead p;std::thread writer([&]{Sleep(80);DWORD sent=0;WriteFile(p.writer,"next",4,&sent,nullptr);});
    DWORD count=0;try{count=ReadChunk(p.read,0,4,[]{});}catch(...){writer.join();throw;}writer.join();
    Check(count==4&&!p.read->pending,"delayed bytes complete");});
  Case("overlapped-timeout-cancels-and-drains",[]{PipeRead p;const auto start=GetTickCount64();
    Refuses([&]{ReadChunk(p.read,0,4,[&]{if(GetTickCount64()-start>=80)throw std::runtime_error("cancelled");});},"cancelled");
    Check(GetTickCount64()-start<2000&&!p.read->pending&&!read_drain_pending.load(),"bounded cancellation drains before release");});
  Case("overlapped-disconnect-refused",[]{PipeRead p;CloseHandle(p.writer);p.writer=INVALID_HANDLE_VALUE;
    Refuses([&]{ReadChunk(p.read,0,4,[]{});},"run-file-read-failed");Check(!p.read->pending,"disconnect completed operation");});
  std::printf("{\"cases\":%u,\"passed\":%u,\"failed\":%u}\n",cases,cases-failed,failed);return failed?1:0;
}
