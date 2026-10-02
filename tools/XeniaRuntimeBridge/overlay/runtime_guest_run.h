// Bounded profile admission shared by the native bridge and offline fixtures.
// Filesystem/process/window evidence is supplied by the real adapter, never IPC booleans.
#pragma once
#include "third_party/rapidjson/include/rapidjson/document.h"
#include <algorithm>
#include <cstdint>
#include <functional>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace xe::cpu::guest_run {
constexpr uint64_t kSourceLimit=64ULL*1024*1024*1024;
struct File {
  std::string path,sha256,bytes;
  uint64_t size=0,write_time=0,index=0;
  uint32_t volume=0;
};
struct Inventory {std::vector<std::string> files,directories;};
struct Evidence {
  bool observed=false,game_read_only=false,allow_plugins=true,allow_game_writes=true;
  std::string storage,content,cache,game,config,global_read,global_status,game_config,game_status;
  std::string executable,executable_hash,guest,guest_hash;
};
struct Io {
  // Normalize is also a no-reparse admission check. Missing paths are permitted
  // for absent per-game routes and mutable copies; reads still require a file.
  std::function<std::string(const std::string&,bool)> normalize;
  std::function<bool(const std::string&)> absent;
  std::function<File(const std::string&,uint64_t,bool)> read;
  std::function<Inventory(const std::string&,bool)> scan;
  std::function<void()> check;
  std::function<void(const char*,uint64_t,uint64_t)> progress;
};
struct Admission {std::string profile_path,profile_sha256,root,scenario_sha256;uint64_t source_bytes=0;};
inline void Require(bool valid,const char* reason) {if(!valid)throw std::runtime_error(reason);}
inline std::string Key(std::string value) {
  for(char& c:value) {if(c=='/')c='\\';if(c>='A'&&c<='Z')c=char(c+('a'-'A'));}
  while(value.size()>3&&value.back()=='\\')value.pop_back();return value;
}
inline bool Same(const std::string& a,const std::string& b) {return Key(a)==Key(b);}
inline bool Within(const std::string& root,const std::string& path) {
  auto r=Key(root),p=Key(path);return p.size()>r.size()&&p.compare(0,r.size(),r)==0&&p[r.size()]=='\\';
}
inline bool Overlap(const std::string& a,const std::string& b) {return Same(a,b)||Within(a,b)||Within(b,a);}
inline bool ImmediateChild(const std::string& root,const std::string& path) {
  const auto r=Key(root),p=Key(path);return Within(r,p)&&p.find('\\',r.size()+1)==std::string::npos;
}
inline bool Hash(const std::string& value) {
  return value.size()==64&&std::all_of(value.begin(),value.end(),[](char c){return(c>='0'&&c<='9')||(c>='a'&&c<='f');});
}
inline const rapidjson::Value& Field(const rapidjson::Value& value,const char* key) {
  Require(value.IsObject()&&value.HasMember(key),"profile-field-missing");return value[key];
}
inline std::string Text(const rapidjson::Value& value,const char* key) {
  const auto& item=Field(value,key);Require(item.IsString(),"profile-field-type");
  std::string result(item.GetString(),item.GetStringLength());
  Require(result.find('\0')==std::string::npos&&result.size()<=32768,"profile-text-invalid");return result;
}
inline uint64_t Number(const rapidjson::Value& value,const char* key) {
  const auto& item=Field(value,key);Require(item.IsUint64(),"profile-number-invalid");return item.GetUint64();
}
inline void Shape(const rapidjson::Value& value,unsigned depth=0) {
  Require(depth<=24,"profile-depth-limit");
  if(value.IsObject()) {
    std::unordered_set<std::string> keys;
    for(auto i=value.MemberBegin();i!=value.MemberEnd();++i) {
      Require(keys.insert(std::string(i->name.GetString(),i->name.GetStringLength())).second,"profile-duplicate-key");
      Shape(i->value,depth+1);
    }
  } else if(value.IsArray()) for(const auto& child:value.GetArray())Shape(child,depth+1);
  else if(value.IsString())Require(value.GetStringLength()<=32768&&
    std::string_view(value.GetString(),value.GetStringLength()).find('\0')==std::string_view::npos,"profile-text-invalid");
}
inline rapidjson::Document Parse(const std::string& text) {
  Require(!text.empty()&&text.size()<=32*1024*1024,"profile-size-limit");
  rapidjson::Document result;result.Parse<rapidjson::kParseValidateEncodingFlag|rapidjson::kParseIterativeFlag>(text.data(),text.size());
  Require(!result.HasParseError()&&result.IsObject(),"profile-json-invalid");Shape(result);return result;
}
inline std::unordered_set<std::string> Keys(const std::vector<std::string>& values) {
  std::unordered_set<std::string> result;
  for(const auto& value:values)Require(result.insert(Key(value)).second,"profile-inventory-duplicate");return result;
}
inline std::string CopyKey(const rapidjson::Value& value) {
  return Key(Text(value,"originalPath"))+"\n"+Key(Text(value,"copyPath"))+"\n"+Text(value,"sha256");
}
inline Admission Validate(const std::string& profile_path,const std::string& expected_hash,
    const Evidence& evidence,const Io& io) {
  io.check();Require(Hash(expected_hash),"profile-hash-invalid");
  const auto file=io.read(profile_path,32*1024*1024,true);
  Require(file.sha256==expected_hash,"profile-hash-mismatch");
  const auto document=Parse(file.bytes);
  Require(Text(document,"schema")=="bmt/runtime-guest-run-profile"&&Number(document,"version")==1,"profile-version");
  Admission result{file.path,expected_hash,io.normalize(Text(document,"root"),false),{},0};
  Require(Same(file.path,result.root+"\\runtime-guest-profile.json"),"profile-root-mismatch");
  const auto& env=Field(document,"environment");
  const auto storage=io.normalize(Text(env,"storageRoot"),false),content=io.normalize(Text(env,"contentRoot"),false),
    cache=io.normalize(Text(env,"cacheRoot"),false),game=io.normalize(Text(env,"gameRoot"),false),
    config=io.normalize(Text(env,"configPath"),false);
  Require(Same(storage,result.root+"\\storage")&&Same(content,result.root+"\\content")&&Same(cache,result.root+"\\cache"),"profile-private-roots");
  Require(!Overlap(game,result.root),"profile-original-overlap");
  Require(evidence.observed&&evidence.game_read_only&&!evidence.allow_plugins&&!evidence.allow_game_writes&&
    Same(storage,evidence.storage)&&Same(content,evidence.content)&&Same(cache,evidence.cache)&&Same(game,evidence.game)&&
    Same(config,evidence.config)&&evidence.global_status=="loaded","profile-effective-environment-mismatch");
  const auto& configuration=Field(document,"configuration");
  Require(Same(config,Text(configuration,"copyPath"))&&Same(config,storage+"\\xenia-canary.config.toml")&&
    Same(config,evidence.global_read),"profile-global-config-mismatch");
  const auto& game_configuration=Field(document,"gameConfiguration");
  const auto& env_game=Field(env,"gameConfigPath");
  if(game_configuration.IsNull()) {
    Require(env_game.IsNull()&&evidence.game_status=="absent"&&
      ImmediateChild(storage+"\\config",io.normalize(evidence.game_config,true)),"profile-game-config-mismatch");
    Require(io.absent(evidence.game_config),"run-absent-route-exists");
  } else Require(env_game.IsString()&&evidence.game_status=="loaded"&&
    Same(Text(env,"gameConfigPath"),Text(game_configuration,"copyPath"))&&
    Same(io.normalize(Text(game_configuration,"copyPath"),false),evidence.game_config)&&
    ImmediateChild(storage+"\\config",evidence.game_config),"profile-game-config-mismatch");
  const auto& sources=Field(document,"sourceFiles");
  Require(sources.IsArray()&&!sources.Empty()&&sources.Size()<=100000,"profile-source-count");
  std::unordered_map<std::string,std::string> originals;
  uint64_t completed_files=0;
  for(const auto& source:sources.GetArray()) {
    if(io.progress)io.progress("originals",completed_files,result.source_bytes);
    io.check();const auto path=io.normalize(Text(source,"path"),false),hash=Text(source,"sha256");
    Require(Hash(hash)&&!Within(result.root,path)&&!Same(result.root,path)&&originals.emplace(Key(path),hash).second,"profile-original-invalid");
    const auto observed=io.read(path,kSourceLimit-result.source_bytes,false);
    Require(observed.sha256==hash&&observed.size<=kSourceLimit-result.source_bytes,"profile-original-changed");result.source_bytes+=observed.size;
    ++completed_files;
  }
  const auto& roots=Field(document,"sourceRoots");Require(roots.IsArray()&&roots.Size()==2,"profile-source-roots");
  std::vector<std::string> root_paths;
  if(io.progress)io.progress("inventories",completed_files,result.source_bytes);
  for(const auto& root:roots.GetArray()) {
    io.check();const auto path=io.normalize(Text(root,"path"),false);
    Require(Field(root,"existed").IsTrue()&&!Overlap(path,result.root),"profile-source-root-invalid");
    for(const auto& prior:root_paths)Require(!Overlap(prior,path),"profile-source-root-overlap");root_paths.push_back(path);
    const auto observed=io.scan(path,false);std::unordered_set<std::string> expected_files,expected_dirs;
    for(const auto& [name,hash]:originals)if(Within(path,name))expected_files.insert(name);
    const auto& dirs=Field(root,"directories");Require(dirs.IsArray()&&dirs.Size()<=100000,"profile-directory-count");
    for(const auto& dir:dirs.GetArray()) {
      Require(dir.IsString(),"profile-directory-type");const auto normalized=io.normalize(path+"\\"+dir.GetString(),false);
      Require(Within(path,normalized)&&expected_dirs.insert(Key(normalized)).second,"profile-directory-invalid");
    }
    Require(Keys(observed.files)==expected_files&&Keys(observed.directories)==expected_dirs,"profile-original-inventory-changed");
  }
  Require(std::any_of(root_paths.begin(),root_paths.end(),[&](const auto& p){return Same(p,game);}),"profile-game-root-not-inventoried");
  const auto& copies=Field(document,"files");Require(copies.IsArray()&&copies.Size()>=3&&copies.Size()<=4096,"profile-copy-count");
  if(io.progress)io.progress("copies-and-backing",completed_files,result.source_bytes);
  std::unordered_set<std::string> copy_paths,copy_rows;
  for(const auto& copy:copies.GetArray()) {
    io.check();const auto original=io.normalize(Text(copy,"originalPath"),false);
    // Startup may rotate or remove copied save content. Its original identity is
    // retained, while immutable inputs are checked again by read below.
    const auto copy_path=Text(copy,"copyPath");
    const auto path=io.normalize(copy_path,Within(content,copy_path)),hash=Text(copy,"sha256");
    const auto found=originals.find(Key(original));
    Require(Hash(hash)&&found!=originals.end()&&found->second==hash&&Within(result.root,path)&&!Same(path,file.path)&&
      copy_paths.insert(Key(path)).second,"profile-copy-invalid");copy_rows.insert(CopyKey(copy));
  }
  for(const char* name:{"guestManifestFile","scenario","configuration","gameConfiguration","initialSave"}) {
    const auto& chosen=Field(document,name);if(chosen.IsNull()){Require(std::string(name)=="gameConfiguration"||std::string(name)=="initialSave","profile-selected-input-missing");continue;}
    Require(copy_rows.contains(CopyKey(chosen)),"profile-selected-input-not-copied");
    if(std::string(name)=="initialSave")Require(Within(content,Text(chosen,"copyPath")),"profile-save-outside-content");
  }
  for(const char* name:{"guestManifestFile","scenario"}) {
    const auto& input=Field(document,name);const auto observed=io.read(Text(input,"copyPath"),32*1024*1024,std::string(name)=="guestManifestFile");
    Require(observed.sha256==Text(input,"sha256"),"profile-immutable-copy-changed");
    if(std::string(name)=="scenario")result.scenario_sha256=observed.sha256;
    else {
      const auto saved=Parse(observed.bytes);
      Require(saved==Field(document,"guestManifest"),"profile-guest-manifest-copy-mismatch");
    }
  }
  const auto& manifest=Field(document,"guestManifest");
  Require(Text(manifest,"schema")=="bmt/runtime-guest-manifest"&&Number(manifest,"version")==1,"profile-guest-manifest-version");
  const auto backing=[&](const rapidjson::Value& item) {
    const auto path=io.normalize(Text(item,"path"),false),hash=Text(item,"sha256");
    Require(Hash(hash)&&!Overlap(result.root,path),"profile-backing-invalid");
    const auto actual=io.read(path,1024ULL*1024*1024,false);
    Require(Same(actual.path,path)&&actual.sha256==hash&&actual.size==Number(item,"length")&&
      actual.write_time==Number(item,"lastWriteFileTime")&&actual.volume==Number(item,"volumeSerial")&&actual.index==Number(item,"fileIndex"),"profile-backing-changed");
    const auto original=originals.find(Key(path));Require(original!=originals.end()&&original->second==hash,"profile-backing-not-inventoried");
    return path;
  };
  const auto& emulator=Field(manifest,"emulator");const auto& guest=Field(manifest,"guest");
  Require(Same(backing(emulator),evidence.executable)&&Text(emulator,"sha256")==evidence.executable_hash,"profile-emulator-mismatch");
  Require(Same(backing(guest),evidence.guest)&&Text(guest,"sha256")==evidence.guest_hash&&Within(game,evidence.guest),"profile-guest-mismatch");
  const auto& plugins=Field(manifest,"plugins");Require(plugins.IsArray()&&!plugins.Empty()&&plugins.Size()<=255,"profile-plugin-count");
  std::unordered_set<std::string> names;
  for(const auto& plugin:plugins.GetArray()) {
    const auto name=Text(plugin,"name"),key=Key(name);
    Require(name.find_first_of("/\\:")==std::string::npos&&(key.ends_with(".esm")||key.ends_with(".esp"))&&names.insert(key).second&&
      Within(game,backing(plugin)),"profile-plugin-invalid");
  }
  if(io.progress)io.progress("writable-roots",completed_files,result.source_bytes);
  for(const auto& path:{storage,content,cache}){io.check();io.scan(path,true);}
  io.check();Require(io.read(profile_path,32*1024*1024,false).sha256==expected_hash,"profile-changed-during-admission");
  return result;
}
} // namespace xe::cpu::guest_run
