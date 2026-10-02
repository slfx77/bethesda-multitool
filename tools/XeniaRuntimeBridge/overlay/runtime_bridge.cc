// BMT observation adapter for Xenia Canary. BSD-3-Clause.
#include "xenia/cpu/runtime_bridge.h"
#include "xenia/cpu/runtime_profiles.h"
#include "xenia/cpu/runtime_guest_identity.h"
#include "xenia/cpu/runtime_guest_actor.h"
#include "xenia/cpu/runtime_pending_write.h"
#include "xenia/cpu/runtime_gamepad_driver.h"
#include "xenia/cpu/runtime_guest_run.h"
#include "xenia/base/logging.h"
#include "xenia/base/cvar.h"
#include "xenia/base/filesystem.h"
#include "xenia/base/string.h"
#include "xenia/cpu/processor.h"
#include "xenia/cpu/ppc/ppc_context.h"
#include "xenia/memory.h"
#include "xenia/emulator.h"
#include "xenia/vfs/devices/host_path_entry.h"
#include <algorithm>
#include <atomic>
#include <cmath>
#include <chrono>
#include <deque>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <sstream>
#include <shared_mutex>
#include <thread>
#include <vector>

#if XE_PLATFORM_WIN32
#include "xenia/base/platform_win.h"
#include "xenia/ui/window_win.h"
#include "xenia/cpu/runtime_guest_run_io.h"
#include <sddl.h>
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "advapi32.lib")
#endif

DECLARE_bool(allow_plugins);
DECLARE_bool(allow_game_relative_writes);

namespace xe::cpu {
namespace {
struct ConfigRoutes {
  std::filesystem::path global_path, global_read_path, game_path;
  std::string global_status="unavailable",game_status="unavailable";
};
std::mutex config_routes_mutex;
ConfigRoutes config_routes;
std::string Quote(const std::string& value) {
  std::ostringstream out; out << '"';
  for (const unsigned char c : value) {
    if (c == '"' || c == '\\') out << '\\' << c;
    else if (c < 32) out << "\\u00" << std::hex << std::setw(2) << std::setfill('0') << int(c) << std::dec;
    else out << c;
  }
  out << '"'; return out.str();
}
std::string Hex(const uint8_t* data, size_t size) {
  std::ostringstream out; out << std::hex << std::setfill('0');
  for (size_t i=0; i<size; ++i) out << std::setw(2) << unsigned(data[i]);
  return out.str();
}
#if XE_PLATFORM_WIN32
std::string FileHash(const std::filesystem::path& path) {
  std::ifstream file(path, std::ios::binary);
  if (!file) return {};
  BCRYPT_ALG_HANDLE algorithm=nullptr; BCRYPT_HASH_HANDLE hash=nullptr;
  if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
  if (BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) < 0) {
    BCryptCloseAlgorithmProvider(algorithm, 0); return {};
  }
  std::array<uint8_t,65536> buffer{}; bool valid=true;
  while (file) {
    file.read(reinterpret_cast<char*>(buffer.data()), buffer.size());
    if (file.gcount() && BCryptHashData(hash, buffer.data(), static_cast<ULONG>(file.gcount()), 0) < 0) { valid=false; break; }
  }
  std::array<uint8_t,32> digest{};
  valid = valid && file.eof() && BCryptFinishHash(hash, digest.data(), ULONG(digest.size()), 0) >= 0;
  BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm,0);
  return valid ? Hex(digest.data(),digest.size()) : std::string{};
}
std::string SecureRunPath(const std::string& input,bool allow_missing=false) {
  guest_run::Require(!input.empty()&&input.size()<=32768&&input.find('\0')==std::string::npos,"run-path-invalid");
  auto path=xe::to_path(input);
  guest_run::Require(path.is_absolute(),"run-path-not-absolute");
  std::error_code error;path=std::filesystem::absolute(path,error).lexically_normal();
  guest_run::Require(!error,"run-path-invalid");
  auto text=xe::path_to_utf8(path);
  // Local drive paths only; reject device namespaces, UNC and alternate streams.
  guest_run::Require(text.size()>3&&text[1]==':'&&text.find(':',2)==std::string::npos,"run-path-kind-unsupported");
  auto current=path;
  while(!current.empty()) {
    const auto attributes=GetFileAttributesW(current.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES) {
      const auto code=GetLastError();
      guest_run::Require(allow_missing&&(code==ERROR_FILE_NOT_FOUND||code==ERROR_PATH_NOT_FOUND),"run-path-unavailable");
    } else {
      guest_run::Require(!(attributes&FILE_ATTRIBUTE_REPARSE_POINT),"run-path-reparse");
      if(current!=path)guest_run::Require(attributes&FILE_ATTRIBUTE_DIRECTORY,"run-parent-not-directory");
    }
    auto parent=current.parent_path();if(parent==current)break;current=std::move(parent);
  }
  return text;
}
struct RunHandle {
  HANDLE value=INVALID_HANDLE_VALUE;
  ~RunHandle(){if(value!=INVALID_HANDLE_VALUE)CloseHandle(value);}
};
struct RunHash {
  BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;
  ~RunHash(){if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);}
};
bool SameRunFile(const BY_HANDLE_FILE_INFORMATION& a,const BY_HANDLE_FILE_INFORMATION& b) {
  return a.dwVolumeSerialNumber==b.dwVolumeSerialNumber&&a.nFileIndexHigh==b.nFileIndexHigh&&a.nFileIndexLow==b.nFileIndexLow&&
    a.nFileSizeHigh==b.nFileSizeHigh&&a.nFileSizeLow==b.nFileSizeLow&&
    a.ftLastWriteTime.dwHighDateTime==b.ftLastWriteTime.dwHighDateTime&&a.ftLastWriteTime.dwLowDateTime==b.ftLastWriteTime.dwLowDateTime;
}
guest_run::Io RunIo(std::function<void()> check) {
  guest_run::Io io;io.check=check;io.normalize=SecureRunPath;
  io.absent=[check](const std::string& input){check();const auto path=SecureRunPath(input,true);
    const auto attributes=GetFileAttributesW(xe::to_path(path).c_str());const auto error=GetLastError();
    return attributes==INVALID_FILE_ATTRIBUTES&&(error==ERROR_FILE_NOT_FOUND||error==ERROR_PATH_NOT_FOUND);};
  io.read=[check](const std::string& input,uint64_t maximum,bool retain) {
    check();guest_run::Require(!guest_run::read_drain_pending.load(),"run-read-drain-pending");
    const auto path=SecureRunPath(input);auto read=std::make_shared<guest_run::PendingRead>();
    read->file=CreateFileW(xe::to_path(path).c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,
      FILE_FLAG_OPEN_REPARSE_POINT|FILE_FLAG_SEQUENTIAL_SCAN|FILE_FLAG_OVERLAPPED,nullptr);
    const auto file=read->file;
    guest_run::Require(file!=INVALID_HANDLE_VALUE,"run-file-open-failed");
    BY_HANDLE_FILE_INFORMATION before{},after{};
    guest_run::Require(GetFileInformationByHandle(file,&before)&&
      !(before.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)),"run-file-type-invalid");
    const uint64_t size=(uint64_t(before.nFileSizeHigh)<<32)|before.nFileSizeLow;
    guest_run::Require(size<=maximum&&(!retain||size<=32*1024*1024),"run-file-size-limit");
    std::wstring actual(32768,L'\0');const auto length=GetFinalPathNameByHandleW(file,actual.data(),DWORD(actual.size()),FILE_NAME_NORMALIZED|VOLUME_NAME_DOS);
    guest_run::Require(length&&length<actual.size(),"run-file-identity-unavailable");actual.resize(length);
    if(actual.starts_with(L"\\\\?\\"))actual.erase(0,4);
    const auto canonical=SecureRunPath(xe::path_to_utf8(std::filesystem::path(actual)));
    guest_run::Require(guest_run::Same(path,canonical),"run-file-route-changed");
    RunHash hash;guest_run::Require(BCryptOpenAlgorithmProvider(&hash.algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0&&
      BCryptCreateHash(hash.algorithm,&hash.hash,nullptr,0,nullptr,0,0)>=0,"run-file-hash-unavailable");
    guest_run::File result;result.path=canonical;result.size=size;
    if(retain)result.bytes.reserve(size_t(size));
    uint64_t total=0;
    while(total<size) {
      check();const DWORD wanted=DWORD(std::min(uint64_t(read->buffer.size()),size-total));
      const auto count=guest_run::ReadChunk(read,total,wanted,check);
      guest_run::Require(count==wanted&&BCryptHashData(hash.hash,read->buffer.data(),count,0)>=0,"run-file-read-failed");
      if(retain)result.bytes.append(reinterpret_cast<char*>(read->buffer.data()),count);total+=count;
    }
    std::array<uint8_t,32> digest{};
    guest_run::Require(BCryptFinishHash(hash.hash,digest.data(),DWORD(digest.size()),0)>=0&&
      GetFileInformationByHandle(file,&after)&&SameRunFile(before,after)&&
      guest_run::Same(SecureRunPath(input),canonical),"run-file-changed-during-read");
    result.sha256=Hex(digest.data(),digest.size());result.write_time=(uint64_t(after.ftLastWriteTime.dwHighDateTime)<<32)|after.ftLastWriteTime.dwLowDateTime;
    result.index=(uint64_t(after.nFileIndexHigh)<<32)|after.nFileIndexLow;result.volume=after.dwVolumeSerialNumber;return result;
  };
  io.scan=[check](const std::string& input,bool writable) {
    const auto root=SecureRunPath(input);guest_run::Inventory result;
    const auto root_attributes=GetFileAttributesW(xe::to_path(root).c_str());
    guest_run::Require(root_attributes!=INVALID_FILE_ATTRIBUTES&&(root_attributes&FILE_ATTRIBUTE_DIRECTORY),"run-root-not-directory");
    for(const auto& entry:std::filesystem::recursive_directory_iterator(xe::to_path(root))) {
      check();const auto path=SecureRunPath(xe::path_to_utf8(entry.path()));
      guest_run::Require(guest_run::Within(root,path),"run-scan-escape");
      const auto attributes=GetFileAttributesW(entry.path().c_str());
      guest_run::Require(attributes!=INVALID_FILE_ATTRIBUTES&&!(attributes&FILE_ATTRIBUTE_REPARSE_POINT),"run-scan-route-changed");
      if(attributes&FILE_ATTRIBUTE_DIRECTORY)result.directories.push_back(path);
      else {
        if(writable) {
          RunHandle file;file.value=CreateFileW(entry.path().c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,
            nullptr,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,nullptr);BY_HANDLE_FILE_INFORMATION info{};
          guest_run::Require(file.value!=INVALID_HANDLE_VALUE&&GetFileInformationByHandle(file.value,&info)&&
            !(info.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)&&info.nNumberOfLinks==1,"run-writable-file-alias");
        }
        result.files.push_back(path);
      }
      guest_run::Require(result.files.size()+result.directories.size()<=100000,"run-scan-count-limit");
    }
    return result;
  };
  return io;
}
// Read the same opened handle that the guest will use. All reads are bounded,
// offset-based, and performed without the capture or backing-map locks.
std::string OpenedFileHash(xe::filesystem::FileHandle& file,uint64_t length) {
  if(length>1024ULL*1024*1024) return {};
  BCRYPT_ALG_HANDLE algorithm=nullptr; BCRYPT_HASH_HANDLE hash=nullptr;
  if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0) return {};
  if(BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)<0) {
    BCryptCloseAlgorithmProvider(algorithm,0);return {};
  }
  std::array<uint8_t,65536> buffer{};bool valid=true;
  for(uint64_t offset=0;offset<length;) {
    const auto wanted=size_t(std::min(uint64_t(buffer.size()),length-offset));size_t actual=0;
    if(!file.Read(size_t(offset),buffer.data(),wanted,&actual)||actual!=wanted||
       BCryptHashData(hash,buffer.data(),ULONG(actual),0)<0) {valid=false;break;}
    offset+=actual;
  }
  std::array<uint8_t,32> digest{};
  valid=valid && BCryptFinishHash(hash,digest.data(),ULONG(digest.size()),0)>=0;
  BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);
  return valid?Hex(digest.data(),digest.size()):std::string{};
}
PSECURITY_DESCRIPTOR UserSecurity() {
  HANDLE token=nullptr; if (!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token)) return nullptr;
  DWORD size=0; GetTokenInformation(token,TokenUser,nullptr,0,&size);
  std::vector<uint8_t> storage(size);
  if (!GetTokenInformation(token,TokenUser,storage.data(),size,&size)) { CloseHandle(token); return nullptr; }
  CloseHandle(token); wchar_t* sid=nullptr;
  if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(storage.data())->User.Sid,&sid)) return nullptr;
  std::wstring sddl=L"D:P(A;;GA;;;"; sddl+=sid; sddl+=L")"; LocalFree(sid);
  PSECURITY_DESCRIPTOR result=nullptr;
  ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(),SDDL_REVISION_1,&result,nullptr);
  return result;
}
#endif
}  // namespace

struct RuntimeBridge::Impl {
  explicit Impl(Processor* value) : processor(value) {}
  struct Request { uint16_t kind; uint64_t id; std::string payload; uint64_t generation; uint64_t epoch; };
  struct Event { uint64_t id; std::string text; bool control; };
  struct Probe { Impl* owner; const bmt::ProbeProfile* profile; Function* function; };
  Processor* processor;
  const bmt::RuntimeProfile* profile=nullptr;
  std::array<Probe,3> probes{};
  std::array<std::atomic<uint64_t>,3> probe_hits{};
  std::array<std::atomic<bool>,3> probe_compiled{};
  static std::atomic<Impl*> active;
  static std::shared_mutex file_observer_gate;
  struct BackingObservation {
    xe::filesystem::FileHandle::BackingIdentity identity;
    std::string guest_path,name,sha256;
    const char* status="pending";
    bool read_only=false;
  };
  mutable std::mutex backing_mutex;
  std::vector<BackingObservation> backings;
  bool backing_limit=false;
  std::filesystem::path guest_path;
  struct Environment {
    std::string storage,content,cache,game,config,global_read,game_config;
    std::string global_status="unavailable",game_status="unavailable";
    bool game_read_only=false,allow_plugins=false,allow_game_writes=false,window_valid=false;
    uint32_t process=0,window_process=0,window_thread=0;
    uint64_t process_created=0,window=0;
    bool complete=false;
  } environment;
  std::atomic<bool> running{false}, connected{false}, capture{false};
  std::atomic<bool> disconnect{false};
  std::atomic<uint64_t> generation{0}, capture_epoch{0};
  std::atomic<uint64_t> ticks{0};
  std::string capture_session;
  std::mutex mutex;
  std::deque<Event> events;
  std::deque<Request> conditions;
  std::optional<Request> condition_inflight;
  std::atomic_flag condition_busy=ATOMIC_FLAG_INIT;
  uint32_t condition_result_va=0;
  const bmt::ActorProfile* actor_profile=nullptr;
  bool actor_routines_verified=false;
  const char* actor_profile_status="UnsupportedBuild";
  uint64_t sequence=0, dropped=0;
  std::shared_ptr<gamepad::Control> input_gamepad;
  uint64_t gamepad_press_sequence=0,gamepad_release_sequence=0;
  std::thread server;
  std::thread binding_worker;
  std::atomic<bool> binding_busy{false};
  std::optional<Request> binding_inflight;
#if XE_PLATFORM_WIN32
  HANDLE pipe=INVALID_HANDLE_VALUE;
#endif

  bool Read(uint32_t va, uint8_t* destination, size_t length) const {
#if XE_PLATFORM_WIN32
    if (!va || length > 64 || uint64_t(va)+length > 0x100000000ULL) return false;
    SIZE_T read=0;
    return ReadProcessMemory(GetCurrentProcess(),processor->memory()->TranslateVirtual(va),destination,length,&read) && read==length;
#else
    return false;
#endif
  }
  // Caller holds mutex. Controls cannot disappear behind high-volume probes.
  void EmitLocked(const char* kind, uint64_t id, const std::string& extra={}) {
#if XE_PLATFORM_WIN32
    const bool control=std::string_view(kind)!="guest-probe" && std::string_view(kind)!="heartbeat";
    if (events.size()>=4096) {
      auto discard=std::find_if(events.begin(),events.end(),[](const Event& event){return !event.control;});
      ++dropped;
      if (discard!=events.end()) events.erase(discard);
      else { if (control) disconnect=true; return; }
    }
    LARGE_INTEGER qpc{}; QueryPerformanceCounter(&qpc); // Enqueue time, ordered with sequence.
    events.push_back({id,"{\"protocol\":1,\"kind\":"+Quote(kind)+",\"sequence\":"+std::to_string(++sequence)+
      ",\"requestId\":"+std::to_string(id)+",\"frame\":"+std::to_string(ticks.load())+
      ",\"qpc\":"+std::to_string(qpc.QuadPart)+",\"dropped\":"+std::to_string(dropped)+extra+"}",control});
    if(events.back().text.size()>65536) {
      // Keep the wire valid. The original event was not representable; expose
      // that loss rather than sending a partial JSON document or a large frame.
      ++dropped;auto& event=events.back();event.control=true;
      event.text="{\"protocol\":1,\"kind\":\"error\",\"sequence\":"+std::to_string(sequence)+
        ",\"requestId\":"+std::to_string(id)+",\"frame\":"+std::to_string(ticks.load())+
        ",\"qpc\":"+std::to_string(qpc.QuadPart)+",\"dropped\":"+std::to_string(dropped)+
        ",\"error\":\"event-payload-limit\"}";
    }
#endif
  }
  void Emit(const char* kind, uint64_t id, const std::string& extra={}, uint64_t expected_generation=UINT64_MAX,
            uint64_t expected_epoch=UINT64_MAX) {
    std::lock_guard lock(mutex);
    if (!connected || (expected_generation!=UINT64_MAX && expected_generation!=generation)) return;
    if (expected_epoch!=UINT64_MAX && expected_epoch!=capture_epoch) return;
    if ((std::string_view(kind)=="guest-probe" || std::string_view(kind)=="heartbeat") && !capture) return;
    EmitLocked(kind,id,extra);
  }
  void DrainGamepadLocked() {
    if(!input_gamepad) return;
    if(input_gamepad->EventsLost()) disconnect=true;
    for(const auto& event:input_gamepad->Drain()) {
      const auto& value=event.snapshot;
      if(value.request.token.connection!=generation || value.request.token.capture!=capture_epoch) continue;
      const bool guest_return=event.kind==gamepad::EventKind::Press||event.kind==gamepad::EventKind::Release||event.kind==gamepad::EventKind::OutputAdjusted;
      std::string extra=",\"origin\":"+Quote(guest_return?"guest-xam-input":"bridge-control")+
        ",\"session\":"+Quote(capture_session)+
        ",\"connectionGeneration\":"+std::to_string(value.request.token.connection)+
        ",\"captureEpoch\":"+std::to_string(value.request.token.capture)+",\"bindingSerial\":"+std::to_string(value.request.token.profile)+
        ",\"windowIdentity\":"+std::to_string(value.request.token.window)+
        ",\"runManifestSha256\":"+Quote(event.manifest)+",\"slot\":"+std::to_string(value.request.slot)+
        ",\"requestedButtons\":"+std::to_string(value.request.buttons)+",\"holdMilliseconds\":"+std::to_string(value.request.holdMilliseconds)+
        ",\"deadlineMilliseconds\":"+std::to_string(value.request.deadlineMilliseconds)+
        ",\"admittedMonotonicMilliseconds\":"+std::to_string(value.admittedMilliseconds)+
        ",\"hostButtonsCleared\":"+(value.hostButtonsCleared?"true":"false")+
        ",\"releaseObserved\":"+(value.releaseObserved?"true":"false");
      if(event.kind==gamepad::EventKind::Queued) {
        gamepad_press_sequence=gamepad_release_sequence=0;
        EmitLocked("gamepad-queued",value.request.requestId,extra+",\"status\":\"queued\"");
      } else if(event.kind==gamepad::EventKind::Press || event.kind==gamepad::EventKind::Release) {
        const auto& observed=event.kind==gamepad::EventKind::Press?value.press:value.release;
        const auto& state=observed.returned.state;
        extra+=std::string(",\"phase\":\"")+(event.kind==gamepad::EventKind::Press?"pressed":"released")+"\""+
          ",\"observationOrdinal\":"+std::to_string(observed.returned.ordinal)+
          ",\"observedMonotonicMilliseconds\":"+std::to_string(observed.observedMilliseconds)+
          ",\"result\":"+std::to_string(observed.returned.result)+",\"flags\":"+std::to_string(event.flags)+
          ",\"packet\":"+std::to_string(state.packet)+",\"buttons\":"+std::to_string(state.buttons)+
          ",\"leftTrigger\":"+std::to_string(state.leftTrigger)+",\"rightTrigger\":"+std::to_string(state.rightTrigger)+
          ",\"leftX\":"+std::to_string(state.leftX)+",\"leftY\":"+std::to_string(state.leftY)+
          ",\"rightX\":"+std::to_string(state.rightX)+",\"rightY\":"+std::to_string(state.rightY);
        EmitLocked("gamepad-state",value.request.requestId,extra);
        if(event.kind==gamepad::EventKind::Press) gamepad_press_sequence=sequence;
        else gamepad_release_sequence=sequence;
      } else if(event.kind==gamepad::EventKind::OutputAdjusted) {
        const auto render=[](const ::bmt::gamepad::State& state) {
          return "{\"packet\":"+std::to_string(state.packet)+",\"buttons\":"+std::to_string(state.buttons)+
            ",\"leftTrigger\":"+std::to_string(state.leftTrigger)+",\"rightTrigger\":"+std::to_string(state.rightTrigger)+
            ",\"leftX\":"+std::to_string(state.leftX)+",\"leftY\":"+std::to_string(state.leftY)+
            ",\"rightX\":"+std::to_string(state.rightX)+",\"rightY\":"+std::to_string(state.rightY)+"}";
        };
        EmitLocked("gamepad-output",value.request.requestId,extra+",\"status\":\"neutralized\",\"originalOutput\":"+
          render(event.originalOutput)+",\"finalOutput\":"+render(event.finalOutput));
      } else if(event.kind==gamepad::EventKind::Completed) {
        EmitLocked("gamepad-result",value.request.requestId,extra+",\"status\":\"observed\",\"pressSequence\":"+
          std::to_string(gamepad_press_sequence)+",\"releaseSequence\":"+std::to_string(gamepad_release_sequence));
      } else EmitLocked("error",value.request.requestId,extra+",\"error\":\"gamepad-pulse-failed\",\"reason\":"+
        Quote(gamepad::FailureName(value.failure)));
    }
  }
  void Control(const Request& request) {
    std::lock_guard lock(mutex);
    if (!connected || request.generation!=generation) return;
    if (request.kind==2) {
      // The shared controller may append candidate filenames after the session.
      // This backend enumerates the actual guest table rather than trusting them.
      const auto session=request.payload.substr(0,request.payload.find('\n'));
      if (session.empty() || session.size()>128 ||
          !std::all_of(request.payload.begin(),request.payload.end(),[](unsigned char value){return (value>=32 && value<127)||value=='\n';})) {
        EmitLocked("error",request.id,",\"error\":\"invalid-session-id\""); return;
      }
      if (capture) { EmitLocked("error",request.id,",\"error\":\"capture-already-active\""); return; }
      ++capture_epoch;
      capture_session=session;
      if(input_gamepad) input_gamepad->Lifecycle(generation,capture_epoch,true,true,session,RuntimeGamepadMilliseconds());
      EmitLocked("capture-start",request.id,",\"session\":"+Quote(session)+EnvironmentJson()+NamespaceJson()); capture=true;
    } else if (request.kind==4 || request.kind==5) {
      if(input_gamepad) {input_gamepad->Lifecycle(generation,capture_epoch,true,false,{},RuntimeGamepadMilliseconds());DrainGamepadLocked();}
      capture=false; ++capture_epoch;
      for(const auto& pending:conditions) EmitLocked("error",pending.id,pending.kind==12?
        ",\"error\":\"capture-ended-before-actor-state\"":",\"error\":\"capture-ended-before-condition-query\"");
      conditions.clear();
      if(binding_inflight) {
        EmitLocked("error",binding_inflight->id,",\"origin\":\"bridge-control\",\"error\":\"capture-ended-during-guest-run-binding\"");
        binding_inflight.reset();
      }
      if(condition_inflight) {
        EmitLocked("error",condition_inflight->id,condition_inflight->kind==12?
          ",\"error\":\"capture-ended-during-actor-state\"":",\"error\":\"capture-ended-during-condition-query\"");
        condition_inflight.reset();
      }
      EmitLocked("capture-end",request.id,std::string(",\"status\":\"")+(request.kind==5?"cancelled":"completed")+"\"");
    } else if(request.kind==20) {
      if(!capture || request.epoch!=capture_epoch) {EmitLocked("error",request.id,",\"error\":\"active-capture-required\"");return;}
      const auto error=input_gamepad?input_gamepad->Submit(request.id,request.payload,RuntimeGamepadMilliseconds()):"gamepad-driver-disabled";
      DrainGamepadLocked();
      if(!error.empty()) EmitLocked("error",request.id,",\"error\":"+Quote(error));
    } else if((request.kind==9 && request.payload=="player-get-dead") || (request.kind==12 && request.payload=="player")) {
      if(!capture || request.epoch!=capture_epoch) {
        EmitLocked("error",request.id,",\"error\":\"active-capture-required\"");return;
      }
      if(conditions.size()>=8) { EmitLocked("error",request.id,",\"error\":\"condition-queue-full\"");return; }
      if(request.kind==12 && !actor_routines_verified) {
        EmitLocked("error",request.id,",\"error\":\"actor-state-profile-unavailable\"");return;
      }
      conditions.push_back(request);
      if(request.kind==12) EmitLocked("actor-state-queued",request.id,",\"target\":\"player\"");
      else EmitLocked("condition-probe-queued",request.id,",\"function\":\"GetDead\"");
    } else {
      EmitLocked("error",request.id,",\"error\":\"guest-action-or-state-evaluator-unavailable\"");
    }
  }
  void BindRun(const Request& request) {
#if XE_PLATFORM_WIN32
    {
      std::lock_guard lock(mutex);
      if(!connected||!capture||request.generation!=generation||request.epoch!=capture_epoch) {
        EmitLocked("error",request.id,",\"error\":\"active-capture-required\"");return;
      }
      if(!input_gamepad||!environment.complete) {EmitLocked("error",request.id,",\"error\":\"guest-run-environment-unavailable\"");return;}
      if(binding_busy.exchange(true)) {EmitLocked("error",request.id,",\"error\":\"guest-run-binding-busy\"");return;}
      binding_inflight=request;
    }
    if(binding_worker.joinable())binding_worker.join();
    try { binding_worker=std::thread([this,request] {
      try {
        std::array<std::string,4> fields;std::string_view rest=request.payload;
        for(size_t i=0;i<fields.size();++i) {
          const auto end=rest.find('\n');guest_run::Require((i+1==fields.size())==(end==std::string_view::npos),"guest-run-bind-payload");
          fields[i]=rest.substr(0,end);if(end!=std::string_view::npos)rest.remove_prefix(end+1);
        }
        guest_run::Require(fields[0]=="guest-run-bind/1"&&fields[1].size()<=32768&&guest_run::Hash(fields[2])&&
          !fields[3].empty()&&fields[3].size()<=128&&std::all_of(fields[3].begin(),fields[3].end(),[](unsigned char c){return c>=32&&c<127;}),"guest-run-bind-payload");
        {std::lock_guard lock(mutex);guest_run::Require(fields[3]==capture_session,"guest-run-bind-session-mismatch");}
        const auto start=RuntimeGamepadMilliseconds();uint64_t last_progress=0,files=0,bytes=0;
        std::string phase="profile";
        const auto check=[&] {
          guest_run::Require(running&&connected&&capture&&!disconnect&&request.generation==generation&&request.epoch==capture_epoch,"guest-run-binding-cancelled");
          const auto now=RuntimeGamepadMilliseconds();guest_run::Require(now>=start&&now-start<120000,"guest-run-binding-deadline");
          if(!last_progress||now-last_progress>=1000) {
            Emit("guest-run-bind-progress",request.id,",\"origin\":\"bridge-control\",\"phase\":"+Quote(phase)+
              ",\"filesVerified\":"+std::to_string(files)+",\"bytesVerified\":"+std::to_string(bytes),request.generation,request.epoch);
            last_progress=now;
          }
        };
        auto io=RunIo(check);io.progress=[&](const char* current,uint64_t count,uint64_t total){phase=current;files=count;bytes=total;};
        wchar_t module[32768]{};const auto module_length=GetModuleFileNameW(nullptr,module,DWORD(std::size(module)));
        guest_run::Require(module_length&&module_length<std::size(module),"guest-run-process-image-unavailable");
        const auto executable=io.read(xe::path_to_utf8(std::filesystem::path(module)),1024ULL*1024*1024,false);
        const auto& e=environment;
        guest_run::Evidence evidence{e.complete,e.game_read_only,e.allow_plugins,e.allow_game_writes,e.storage,e.content,e.cache,e.game,
          e.config,e.global_read,e.global_status,e.game_config,e.game_status,executable.path,executable.sha256,
          xe::path_to_utf8(guest_path),profile->xex_sha256};
        const auto admitted=guest_run::Validate(fields[1],fields[2],evidence,io);check();
        std::lock_guard lock(mutex);
        guest_run::Require(running&&connected&&capture&&!disconnect&&request.generation==generation&&request.epoch==capture_epoch&&
          fields[3]==capture_session&&binding_inflight&&binding_inflight->id==request.id,"guest-run-binding-stale");
        // Only this proof path invokes the control's trusted binding seam.
        gamepad::VerifiedRunBinding proof{profile->xex_sha256,admitted.profile_sha256,capture_session,e.process,e.window,request.generation,request.epoch};
        guest_run::Require(input_gamepad->BindVerifiedRun(proof),"guest-run-window-or-binding-mismatch");
        const auto bound=input_gamepad->Begin(0,0,RuntimeGamepadMilliseconds());
        EmitLocked("guest-run-bound",request.id,",\"origin\":\"bridge-control\",\"status\":\"bound\",\"session\":"+Quote(capture_session)+
          ",\"runManifestSha256\":"+Quote(admitted.profile_sha256)+",\"connectionGeneration\":"+std::to_string(request.generation)+
          ",\"captureEpoch\":"+std::to_string(request.epoch)+",\"bindingSerial\":"+std::to_string(bound.token.profile)+
          ",\"windowIdentity\":"+std::to_string(e.window)+",\"processId\":"+std::to_string(e.process)+
          ",\"processStartedFileTime\":"+std::to_string(e.process_created)+",\"scenarioSha256\":"+Quote(admitted.scenario_sha256));
        binding_inflight.reset();
      } catch(const std::exception& error) {
        Emit("error",request.id,",\"origin\":\"bridge-control\",\"error\":\"guest-run-binding-failed\",\"reason\":"+Quote(error.what()),request.generation,request.epoch);
        std::lock_guard lock(mutex);if(binding_inflight&&binding_inflight->id==request.id&&binding_inflight->generation==request.generation)binding_inflight.reset();
      }
      binding_busy=false;
    }); } catch(const std::exception& error) {
      binding_busy=false;std::lock_guard lock(mutex);binding_inflight.reset();
      if(connected&&request.generation==generation&&request.epoch==capture_epoch)
        EmitLocked("error",request.id,",\"error\":\"guest-run-worker-unavailable\",\"reason\":"+Quote(error.what()));
    }
#endif
  }
  std::string EnvironmentJson() const {
    const auto& e=environment;
    std::ostringstream window;window<<"0x"<<std::hex<<e.window;
    return ",\"effectiveEnvironment\":{\"schemaVersion\":1,\"status\":"+Quote(e.complete?"observed":"partial")+
      ",\"evidence\":\"initialized-emulator-roots-vfs-and-config-loader\",\"phase\":\"before-guest-thread-launch\""+
      ",\"storageRoot\":"+Quote(e.storage)+",\"contentRoot\":"+Quote(e.content)+",\"cacheRoot\":"+Quote(e.cache)+
      ",\"gameRoot\":"+Quote(e.game)+",\"gameReadOnly\":"+(e.game_read_only?"true":"false")+
      ",\"gameMountEvidence\":\"resolved-game-vfs-host-path-entry\",\"allowPlugins\":"+(e.allow_plugins?"true":"false")+
      ",\"allowGameRelativeWrites\":"+(e.allow_game_writes?"true":"false")+
      ",\"configPath\":"+Quote(e.config)+",\"globalConfigReadPath\":"+Quote(e.global_read)+
      ",\"globalConfigStatus\":"+Quote(e.global_status)+",\"gameConfigPath\":"+Quote(e.game_config)+
      ",\"gameConfigStatus\":"+Quote(e.game_status)+",\"configEvidence\":\"actual-loader-and-route-callbacks\""+
      ",\"processId\":"+std::to_string(e.process)+",\"processCreationFileTime\":"+Quote(std::to_string(e.process_created))+
      ",\"windowHandle\":"+Quote(window.str())+",\"windowProcessId\":"+std::to_string(e.window_process)+
      ",\"windowThreadId\":"+std::to_string(e.window_thread)+",\"windowValid\":"+(e.window_valid?"true":"false")+"}";
  }
  std::string NamespaceJson() const {
    const auto observed=bmt::ReadPluginNamespace(
      [&](uint32_t va,uint8_t* data,size_t size){return Read(va,data,size);},profile->namespace_proof.layout);
    const auto over_limit=[&]{return ",\"pluginNamespace\":{\"status\":\"partial\",\"detail\":\"ProtocolPayloadLimit\",\"count\":"+
      std::to_string(observed.observed_count)+",\"evidence\":\"validated-guest-compiled-file-table\",\"entries\":[]}";};
    std::string result=",\"pluginNamespace\":{\"status\":"+Quote(observed.status)+
      ",\"detail\":"+Quote(observed.detail)+",\"count\":"+std::to_string(observed.observed_count)+
      ",\"evidence\":\"validated-guest-compiled-file-table\",\"consistency\":\"two-agreeing-reads-required\",\"handlerVa\":"+
      std::to_string(observed.handler_va)+",\"entries\":[";
    for(size_t i=0;i<observed.entries.size();++i) {
      const auto& entry=observed.entries[i];if(i) result+=",";
      result+="{\"index\":"+std::to_string(entry.index)+",\"name\":"+Quote(entry.name)+
        ",\"guestFileVa\":"+std::to_string(entry.file_va)+",\"backingFiles\":[";
      std::lock_guard backing_lock(backing_mutex);bool first=true;
      for(const auto& backing:backings) {
        if(backing.name!=bmt::AsciiLower(entry.name)) continue;
        if(!first) result+=",";first=false;
        result+="{\"status\":"+Quote(backing.status)+",\"path\":"+Quote(xe::path_to_utf8(backing.identity.path))+
          ",\"guestPath\":"+Quote(backing.guest_path)+",\"sha256\":"+Quote(backing.sha256)+
          ",\"length\":"+std::to_string(backing.identity.size)+",\"lastWriteFileTime\":"+std::to_string(backing.identity.write_time)+
          ",\"volumeSerial\":"+std::to_string(backing.identity.volume)+",\"fileIndex\":"+std::to_string(backing.identity.file_index)+
          ",\"readOnlyDevice\":"+(backing.read_only?"true":"false")+
          ",\"hashScope\":\"successful-host-open-handle\"}";
        if(result.size()>60*1024) return over_limit();
      }
      result+=std::string("],\"backingObservationLimitReached\":")+(backing_limit?"true":"false")+"}";
      if(result.size()>60*1024) return over_limit();
    }
    return result+"]}";
  }
  void RecordOpenedFile(std::string_view path,xe::filesystem::FileHandle& file,bool read_only) {
#if XE_PLATFORM_WIN32
    const auto before=file.QueryBackingIdentity();if(!before) return;
    size_t index=0;
    {
      std::lock_guard lock(backing_mutex);
      const auto previous=std::find_if(backings.begin(),backings.end(),[&](const BackingObservation& item){
        return item.identity==*before && item.read_only==read_only;
      });
      if(previous!=backings.end()) {
        if(std::string_view(previous->status)!="hash-unavailable") return;
        // A metadata-only open may lack read access; a later readable handle
        // can retry that same identity without creating a duplicate candidate.
        index=size_t(previous-backings.begin());previous->status="pending";
      } else {
        if(backings.size()>=512) {backing_limit=true;return;}
        index=backings.size();
        backings.push_back({*before,std::string(path),bmt::AsciiLower(xe::path_to_utf8(file.path().filename())),{},"pending",read_only});
      }
    }
    const auto digest=OpenedFileHash(file,before->size);
    const auto after=file.QueryBackingIdentity();
    std::lock_guard lock(backing_mutex);
    auto& item=backings[index];
    if(!after || *before!=*after) item.status="changed-during-read";
    else if(digest.empty()) item.status=before->size>1024ULL*1024*1024?"size-limit":"hash-unavailable";
    else {item.sha256=digest;item.status="verified";}
#endif
  }
  void FinishCondition(const Request& request,const char* kind,const std::string& extra) {
    std::lock_guard lock(mutex);
    if(!condition_inflight || condition_inflight->id!=request.id ||
       condition_inflight->generation!=request.generation || condition_inflight->epoch!=request.epoch) return;
    condition_inflight.reset();
    if(!connected || request.generation!=generation || request.epoch!=capture_epoch) return;
    EmitLocked(kind,request.id,extra);
  }
  bmt::ActorInputs ActorAdmission() const {
    if(!actor_routines_verified) {bmt::ActorInputs result;result.status=actor_profile_status;return result;}
    return bmt::ReadPlayerActorInputs(
      [&](uint32_t va,uint8_t* bytes,size_t count){return Read(va,bytes,count);},*actor_profile,profile->player_pointer,profile->player_vtable);
  }
  bool RequestActive(const Request& request) {
    std::lock_guard lock(mutex);
    return connected && capture && request.generation==generation && request.epoch==capture_epoch &&
      condition_inflight && condition_inflight->id==request.id;
  }
  struct ActorQuery {bool valid=false,returned=false;double value=0;std::array<uint8_t,8> raw{};const char* reason="result-unavailable";};
  ActorQuery InvokeActorQuery(ppc::PPCContext* context,uint32_t address,uint32_t actor,uint32_t code) {
    const std::array<uint8_t,8> unset{{0x7F,0xF8,0,0,0,0,0,0}};
    std::memcpy(processor->memory()->TranslateVirtual(condition_result_va),unset.data(),unset.size());
    const auto saved=*context;
    struct Restore {ppc::PPCContext* current;const ppc::PPCContext& saved;~Restore(){*current=saved;}} restore{context,saved};
    context->r[3]=actor;context->r[4]=code;context->r[5]=0;context->r[6]=condition_result_va;
    const bool executed=processor->Execute(context->thread_state,address);
    ActorQuery result;result.returned=executed && (context->r[3]&0xFF)!=0;
    if(!executed) result.reason="execution-failed";
    else if(!result.returned) result.reason="handler-returned-false";
    if(!Read(condition_result_va,result.raw.data(),result.raw.size())) return result;
    result.valid=bmt::ActorQueryValue(executed,result.returned,result.raw,result.value);
    return result;
  }
  void ObserveActor(ppc::PPCContext* context,const Request& request) {
    const auto identity=ActorAdmission();
    if(std::string_view(identity.status)!="Observed") {
      FinishCondition(request,"error",",\"error\":\"actor-state-inputs-unavailable\",\"reason\":"+Quote(identity.status));return;
    }
    std::string statistics;
    const auto target=",\"engineTargetFormId\":20,\"engineTargetBaseFormId\":"+std::to_string(identity.base_id)+
      ",\"engineTargetFormType\":"+std::to_string(identity.reference_type)+",\"targetKind\":\"actor\""+
      ",\"executableSha256\":"+Quote(profile->xex_sha256)+",\"guestExecutableSha256\":"+Quote(profile->xex_sha256)+
      ",\"guestThreadId\":"+std::to_string(context->thread_id);
    std::string metadata;
    for(size_t i=0;i<bmt::kActorValueCodes.size();++i) {
      if(i) metadata+=",";
      metadata+="{\"name\":"+Quote(std::string(bmt::kActorValueNames[i]))+",\"code\":"+std::to_string(bmt::kActorValueCodes[i])+
        ",\"status\":\"observed\",\"address\":"+std::to_string(identity.info[i])+
        ",\"scriptNameAddress\":"+std::to_string(identity.name_pointer[i])+"}";
    }
    bool complete=true;
    auto query=[&](std::string_view name,std::string_view component,uint32_t code,size_t routine) {
      if(!RequestActive(request)) return false;
      const auto value=InvokeActorQuery(context,actor_profile->routines[routine].address,identity.player,code);
      const auto after=ActorAdmission();
      if(std::string_view(after.status)!="Observed" || !identity.SameIdentity(after)) {
        FinishCondition(request,"error",",\"error\":\"actor-identity-changed-during-query\"");return false;
      }
      if(!RequestActive(request)) return false;
      std::ostringstream number;number<<std::setprecision(17)<<value.value;
      const auto numeric=value.valid?number.str():"null";
      const auto fields="\"statistic\":"+Quote(std::string(name))+",\"component\":"+Quote(std::string(component))+
        ",\"status\":"+Quote(value.valid?"observed":"unavailable")+",\"value\":"+numeric+
        ",\"reason\":"+(value.valid?"null":Quote(value.reason))+
        ",\"handlerReturned\":"+(value.returned?"true":"false")+
        ",\"guestFunctionAddress\":"+std::to_string(actor_profile->routines[routine].address)+
        ",\"query\":"+Quote(actor_profile->routines[routine].name)+
        ",\"numericSemantics\":"+Quote(routine==1?"integer-getter-converted-to-double":"handler-double-output")+
        ",\"rawResultHex\":"+Quote(Hex(value.raw.data(),value.raw.size()))+
        ",\"evidence\":\"requested-engine-eval-handler\",\"comparisonObserved\":false";
      if(!statistics.empty()) statistics+=",";statistics+="{"+fields+"}";
      // Per-field observations are kept separate from the actor-state terminal result.
      Emit("snapshot",request.id,target+","+fields,request.generation,request.epoch);
      complete=complete && value.valid;return true;
    };
    if(!query("Level","current",0,3)) return;
    for(size_t value=0;value<bmt::kActorValueCodes.size();++value)
      for(size_t component=0;component<bmt::kActorComponents.size();++component)
        if(!query(bmt::kActorValueNames[value],bmt::kActorComponents[component],bmt::kActorValueCodes[value],component)) return;
    FinishCondition(request,"actor-state",target+",\"status\":"+Quote(complete?"observed":"partial")+
      ",\"isPlayer\":true,\"isCreature\":false,\"statistics\":["+statistics+"]"+
      ",\"actorAddress\":"+std::to_string(identity.player)+",\"baseAddress\":"+std::to_string(identity.base)+
      ",\"actorValueInfo\":["+metadata+"]"+
      ",\"routineValidation\":\"full-loaded-byte-match\""+
      ",\"baseIdentityEvidence\":"+Quote(std::string(actor_profile->base_identity_evidence))+
      ",\"actorValueIndexEvidence\":"+Quote(std::string(actor_profile->actor_value_index_evidence))+
      ",\"effects\":{\"status\":\"unavailable\",\"reason\":\"guest-layout-not-validated\"}"+
      ",\"damageCalculation\":{\"status\":\"unavailable\",\"reason\":\"requires-separate-guest-calibration\"}");
  }
  void ObserveCondition(ppc::PPCContext* context) {
    if(condition_busy.test_and_set()) return;
    struct Release {std::atomic_flag& flag;~Release(){flag.clear();}} release{condition_busy};
    Request request{};
    {
      std::lock_guard lock(mutex);
      if(conditions.empty()) return;
      request=std::move(conditions.front());conditions.pop_front();
      if(!capture || request.generation!=generation || request.epoch!=capture_epoch) return;
      condition_inflight=request;
    }
    if(request.kind==12) {ObserveActor(context,request);return;}
    auto reject=[&](const char* reason){FinishCondition(request,"error",",\"error\":"+Quote(reason));};
    std::array<uint8_t,4> pointer{},recheck{};
    if(!Read(profile->player_pointer,pointer.data(),pointer.size())) {reject("player-pointer-unreadable");return;}
    const auto player=bmt::Be32(pointer.data());
    const auto identity=bmt::ReadPlayerIdentity(
      [&](uint32_t va,uint8_t* data,size_t size){return Read(va,data,size);},player,profile->player_vtable);
    if(identity.form_id!=0x14 || !Read(profile->player_pointer,recheck.data(),recheck.size()) || pointer!=recheck) {
      reject("player-identity-unavailable");return;
    }
    // Result storage is private to this requested guest handler execution.
    // Preserve all PPC state around nested guest execution, since
    // Processor::Execute intentionally leaves argument/result registers changed.
    const std::array<uint8_t,8> unset{{0x7F,0xF8,0,0,0,0,0,0}};
    std::memcpy(processor->memory()->TranslateVirtual(condition_result_va),unset.data(),unset.size());
    const auto saved=*context;
    struct Restore {ppc::PPCContext* current;const ppc::PPCContext& saved;~Restore(){*current=saved;}} restore{context,saved};
    context->r[3]=player;context->r[4]=0;context->r[5]=0;context->r[6]=condition_result_va;
    const bool executed=processor->Execute(context->thread_state,profile->get_dead_condition);
    const bool returned=executed && (context->r[3]&0xFF)!=0;
    std::array<uint8_t,8> raw{};
    if(!executed || !Read(condition_result_va,raw.data(),raw.size())) {reject("condition-execution-failed");return;}
    uint64_t bits=0;for(uint8_t byte:raw)bits=(bits<<8)|byte;
    double value=0;std::memcpy(&value,&bits,sizeof(value));
    if(!std::isfinite(value)) {reject("condition-result-unavailable");return;}
    std::ostringstream number;number<<std::setprecision(17)<<value;
    FinishCondition(request,"condition-probe-result",
      ",\"function\":\"GetDead\",\"engineTargetFormId\":20,\"value\":"+number.str()+
      ",\"handlerReturned\":"+(returned?"true":"false")+
      ",\"evidence\":\"requested-engine-eval-handler\",\"comparisonObserved\":false,\"guestThreadId\":"+
      std::to_string(context->thread_id)+",\"guestFunctionAddress\":"+std::to_string(profile->get_dead_condition));
  }
  static void Observe(ppc::PPCContext* context, void* arg0, void*) {
    auto& probe=*static_cast<Probe*>(arg0); auto& self=*probe.owner;
    const auto generation=self.generation.load();
    const auto epoch=self.capture_epoch.load();
    const size_t index=static_cast<size_t>(&probe-self.probes.data());
    if (self.probe_hits[index].fetch_add(1)==0)
      XELOGI("BMT runtime: observed {} at {:08X}, guest thread {:08X}",probe.profile->name,probe.profile->address,context->thread_id);
    if(index==1) self.ObserveCondition(context);
    if (&probe==&self.probes[0]) {
      ++self.ticks;
      if (self.capture && self.ticks%60==0) self.Emit("heartbeat",0,{},generation,epoch);
    } else if (self.capture) {
      std::string extra=",\"symbol\":"+Quote(probe.profile->name)+",\"guestAddress\":"+std::to_string(probe.profile->address)+
        ",\"guestThreadId\":"+std::to_string(context->thread_id)+",\"linkRegister\":"+std::to_string(context->lr)+",\"registers\":{";
      for (int reg=3;reg<=10;++reg) {
        if (reg!=3) extra+=",";
        extra+=Quote("r"+std::to_string(reg))+":"+std::to_string(context->r[reg]);
      }
      extra+="},\"evidence\":\"guest-instruction-entry\"";
      if (index==2) {
        // Matching July PDB parameter registers; identical entry/caller ABI in
        // the separately fingerprinted 2011 XEX. SCRIPT_OUTPUT includes flow
        // opcodes and function opcodes. This is dispatch entry, not success.
        extra+=",\"scriptOpcode\":"+std::to_string(static_cast<uint32_t>(context->r[5]))+
          ",\"dataOffset\":"+std::to_string(static_cast<uint32_t>(context->r[8]))+
          ",\"scriptLine\":"+std::to_string(static_cast<uint32_t>(context->r[10]))+
          ",\"functionOwnerArgumentVa\":"+std::to_string(static_cast<uint32_t>(context->r[6]));
      }
      // Retain raw bytes at the Script* argument; no unverified structure decoding.
      std::array<uint8_t,40> bytes{};
      const auto pointer=static_cast<uint32_t>(context->r[4]);
      extra+=",\"scriptArgumentVa\":"+std::to_string(pointer);
      if (self.Read(pointer,bytes.data(),bytes.size())) extra+=",\"scriptPrefixHex\":"+Quote(Hex(bytes.data(),bytes.size()));
      else extra+=",\"scriptPrefixStatus\":\"unreadable\"";
      const auto identity=bmt::ReadScriptIdentity(
        [&](uint32_t va,uint8_t* target,size_t size){return self.Read(va,target,size);},pointer,self.profile->script_vtable);
      extra+=",\"scriptIdentityStatus\":"+Quote(identity.status);
      if(identity.form_id) extra+=",\"engineScriptFormId\":"+std::to_string(identity.form_id);
      self.Emit("guest-probe",0,extra,generation,epoch);
    }
  }
  std::string Hello() const {
#if XE_PLATFORM_WIN32
    LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
    std::string observations=",\"probes\":[";
    for (size_t index=0;index<probes.size();++index) {
      if (index) observations+=",";
      observations+="{\"symbol\":"+Quote(probes[index].profile->name)+",\"guestAddress\":"+
        std::to_string(probes[index].profile->address)+",\"compiled\":"+(probe_compiled[index]?"true":"false")+
        ",\"hits\":"+std::to_string(probe_hits[index].load())+"}";
    }
    observations+="]";
    const auto actor=ActorAdmission();
    return ",\"processId\":"+std::to_string(GetCurrentProcessId())+",\"backend\":\"xenia-canary\",\"qpcFrequency\":"+std::to_string(frequency.QuadPart)+
      ",\"guestExecutableSha256\":"+Quote(profile->xex_sha256)+",\"guestBuildIdentity\":"+Quote(profile->identity)+
      ",\"guestExecutablePath\":"+Quote(xe::path_to_utf8(guest_path))+
      ",\"guestPdbGuid\":"+Quote(profile->pdb_guid)+",\"xeniaCommit\":\"c3cd8617b18ef018ea8d638c865e0cced7399846\""+
      ",\"symbolBasis\":"+Quote(profile->symbol_basis)+
      ",\"scriptIdentityBasis\":\"Script RTTI/TESForm base and executable-specific header instructions\""+
      ",\"qpcMeaning\":\"event enqueue time\",\"frameMeaning\":\"Main::RunScripts entry count, not rendered frames\",\"capabilities\":{\"guestProbe\":true,\"scriptEntry\":true,\"scriptIdentity\":true,\"controlledConditionProbe\":true,\"activePluginQueries\":true,\"console\":false,\"numericState\":false,\"opcodeTrace\":true,\"conditionTrace\":false,\"messageChoice\":false,\"combatAttribution\":false,\"actorState\":"+
      std::string(std::string_view(actor.status)=="Observed"?"true":"false")+",\"gamepadBinding\":true,\"gamepadPulse\":"+
      (input_gamepad&&input_gamepad->Capable()?"true":"false")+"},\"gamepadStatus\":"+
      Quote(input_gamepad?(input_gamepad->Capable()?"verified-run-bound":"verified-guest-run-binding-required"):"disabled")+
      ",\"gamepadRequestKind\":20,\"gamepadPayload\":\"gamepad-pulse/1: six LF-separated typed fields\",\"actorStateScope\":\"player\",\"actorStateStatus\":"+Quote(actor.status)+
      observations+EnvironmentJson()+",\"limitations\":[\"probe entries are not function outcomes\",\"Script FormIDs are runtime IDs; plugin resolution requires the run load order\",\"capture lifecycle is host-side; probe events require actual guest execution\"]";
#else
    return {};
#endif
  }
  void Serve();
};
std::atomic<RuntimeBridge::Impl*> RuntimeBridge::Impl::active{nullptr};
std::shared_mutex RuntimeBridge::Impl::file_observer_gate;

void RuntimeBridge::Impl::Serve() {
#if XE_PLATFORM_WIN32
  auto security=UserSecurity(); if (!security) return;
  SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES),security,FALSE};
  const auto name=L"\\\\.\\pipe\\BMT.Runtime."+std::to_wstring(GetCurrentProcessId());
  while (running) {
    pipe=CreateNamedPipeW(name.c_str(),PIPE_ACCESS_DUPLEX|FILE_FLAG_FIRST_PIPE_INSTANCE,
      PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_NOWAIT|PIPE_REJECT_REMOTE_CLIENTS,1,131072,131072,1000,&attributes);
    if (pipe==INVALID_HANDLE_VALUE) break;
    bool admitted=false;
    while (running) {
      // In PIPE_NOWAIT mode TRUE means "available", not "connected".
      // Microsoft documents ERROR_PIPE_CONNECTED as the actual admission signal.
      const bool available=ConnectNamedPipe(pipe,nullptr)!=FALSE;
      const auto error=available ? ERROR_PIPE_LISTENING : GetLastError();
      if (error==ERROR_PIPE_CONNECTED) { admitted=true; break; }
      if (error!=ERROR_PIPE_LISTENING) break;
      Sleep(2);
    }
    if (!admitted) { CloseHandle(pipe); pipe=INVALID_HANDLE_VALUE; continue; }
    { std::lock_guard lock(mutex); ++generation; events.clear(); conditions.clear(); condition_inflight.reset(); sequence=0; dropped=0;
      capture=false; disconnect=false; connected=true;
      if(input_gamepad) {input_gamepad->Lifecycle(generation,capture_epoch,true,false,{},RuntimeGamepadMilliseconds());input_gamepad->Drain();} }
    bool handshaken=false;
    std::vector<uint8_t> pending_packet;
    size_t pending_offset=0;
    while (running && !disconnect) {
      if(input_gamepad) {input_gamepad->Tick(RuntimeGamepadMilliseconds());std::lock_guard lock(mutex);DrainGamepadLocked();}
      DWORD available=0,peeked=0; uint8_t header[24]{};
      if (!PeekNamedPipe(pipe,header,24,&peeked,&available,nullptr)) break;
      if (available>=24) {
        uint32_t magic=0,length=0,reserved=0; uint16_t version=0,kind=0; uint64_t id=0;
        memcpy(&magic,header,4); memcpy(&version,header+4,2); memcpy(&kind,header+6,2);
        memcpy(&id,header+8,8); memcpy(&length,header+16,4); memcpy(&reserved,header+20,4);
        if (magic!=0x31544D42 || version!=1 || reserved || length>65536 || !id) break;
        if (available>=24+length) {
          std::vector<char> packet(24+length); DWORD count=0;
          if (!ReadFile(pipe,packet.data(),DWORD(packet.size()),&count,nullptr) || count!=packet.size()) break;
          Request request{kind,id,std::string(packet.data()+24,length),generation.load(),capture_epoch.load()};
          if (kind==1) { handshaken=true; Emit("hello",id,Hello()); }
          else if (!handshaken) Emit("error",id,",\"error\":\"handshake-required\"");
          else if (kind==6) Emit("pong",id);
          else if(kind==21)BindRun(request);
          else Control(request); // Lifecycle changes host capture state only; no guest function is called.
        }
      }
      if (pending_packet.empty()) {
        Event event{}; bool present=false;
        { std::lock_guard lock(mutex); if (!events.empty()) { event=std::move(events.front()); events.pop_front(); present=true; } }
        if (present) {
          pending_packet.assign(24+event.text.size(),0); pending_offset=0;
          const uint32_t magic=0x31544D42,length=uint32_t(event.text.size()); const uint16_t version=1,kind=256;
          memcpy(pending_packet.data(),&magic,4); memcpy(pending_packet.data()+4,&version,2); memcpy(pending_packet.data()+6,&kind,2);
          memcpy(pending_packet.data()+8,&event.id,8); memcpy(pending_packet.data()+16,&length,4);
          memcpy(pending_packet.data()+24,event.text.data(),length);
        }
      }
      if (!pending_packet.empty()) {
        const auto status=bmt::PumpPacket(pending_packet,pending_offset,[&](std::span<const uint8_t> remaining,size_t& written) {
          DWORD count=0;
          const bool success=WriteFile(pipe,remaining.data(),DWORD(remaining.size()),&count,nullptr)!=FALSE;
          written=count; return success;
        });
        if (status==bmt::PacketWriteStatus::Failed) {
          XELOGW("BMT runtime: pipe write failed (error {})",GetLastError()); break;
        }
        if (status==bmt::PacketWriteStatus::Complete) pending_packet.clear();
        else if (status==bmt::PacketWriteStatus::Blocked) Sleep(2);
      } else Sleep(2);
    }
    { std::lock_guard lock(mutex); connected=false; capture=false;
      if(input_gamepad) {input_gamepad->Lifecycle(generation,capture_epoch,false,false,{},RuntimeGamepadMilliseconds());input_gamepad->Drain();}
      ++generation; events.clear(); conditions.clear(); condition_inflight.reset(); }
    DisconnectNamedPipe(pipe); CloseHandle(pipe); pipe=INVALID_HANDLE_VALUE;
  }
  LocalFree(security);
#endif
}

RuntimeBridge::RuntimeBridge(Processor* processor) : impl_(std::make_unique<Impl>(processor)) {}
RuntimeBridge::~RuntimeBridge() {
  {std::unique_lock file_lock(Impl::file_observer_gate);Impl::active=nullptr;}
  impl_->running=false;
  if(impl_->input_gamepad) impl_->input_gamepad->Close();
#if XE_PLATFORM_WIN32
  if (impl_->server.joinable()) {
    // Every pipe operation is nonblocking; shutdown does not wait for a client.
    impl_->server.join();
  }
  if(impl_->binding_worker.joinable())impl_->binding_worker.join();
#endif
}
void RuntimeBridge::ObserveConfigRoute(std::string_view kind,const std::filesystem::path& path,std::string_view status) {
  std::lock_guard lock(config_routes_mutex);
  if(kind=="global-reset") {config_routes=ConfigRoutes{};return;}
  if(kind=="global-route") config_routes.global_path=path;
  else if(kind=="global-read") {config_routes.global_read_path=path;config_routes.global_status=status;}
  else if(kind=="game-route") {config_routes.game_path=path;config_routes.game_status=status;}
  else if(kind=="game-read") {config_routes.game_path=path;config_routes.game_status=status;}
}
bool RuntimeBridge::Initialize(const std::filesystem::path& guest_path,xe::Emulator* emulator) {
#if XE_PLATFORM_WIN32
  if (Impl::active.load() || !emulator) return false;
  impl_->profile=bmt::SelectProfile(FileHash(guest_path));
  if (!impl_->profile) { XELOGE("BMT runtime: guest executable hash is not an approved profile"); return false; }
  std::error_code guest_path_error;
  impl_->guest_path=std::filesystem::canonical(guest_path,guest_path_error);
  if(guest_path_error) {XELOGE("BMT runtime: guest backing path is unavailable");return false;}
  for (const auto& probe : impl_->profile->probes) {
    std::array<uint8_t,16> actual{};
    if (!impl_->Read(probe.address,actual.data(),actual.size()) || actual!=probe.bytes ||
        impl_->processor->QueryFunction(probe.address)) {
      XELOGE("BMT runtime: probe {} does not match or was already compiled",probe.name); return false;
    }
  }
  auto read=[&](uint32_t va,uint8_t* target,size_t size){return impl_->Read(va,target,size);};
  auto instruction=[&](uint32_t va,uint32_t expected){
    std::array<uint8_t,4> bytes{};return read(va,bytes.data(),bytes.size()) && bmt::Be32(bytes.data())==expected;
  };
  if(!bmt::ValidateScriptVtable(read,impl_->profile->script_vtable) ||
     !instruction(impl_->profile->script_type_value_instruction,0x38E00011) || // li r7,0x11
     !instruction(impl_->profile->script_type_store_instruction,0x98FF0004) || // stb r7,4(r31)
     !instruction(impl_->profile->form_id_store_instruction,0x93BE000C)) {     // stw r29,12(r30)
    XELOGE("BMT runtime: Script RTTI or FormID layout proof does not match");return false;
  }
  const auto& ns=impl_->profile->namespace_proof;
  // Exact per-executable field accesses accompany the PDB/correspondence proof.
  const std::array<uint32_t,10> compiled_words{{0x2F040000,0x4198001C,0x2B0400FE,0x41990014,
    0x39640000|(ns.layout.files_offset/4),0x556A103A,0x7C6A182E,0x4E800020,0x38600000,0x4E800020}};
  bool namespace_valid=true;
  for(size_t i=0;i<compiled_words.size();++i)
    namespace_valid=namespace_valid && instruction(ns.compiled_file_function+uint32_t(i)*4,compiled_words[i]);
  namespace_valid=namespace_valid &&
    instruction(ns.global_high_instruction,0x3D400000|((ns.layout.handler_global+0x8000)>>16)) &&
    instruction(ns.global_low_instruction,0x816A0000|(ns.layout.handler_global&0xFFFF)) &&
    instruction(ns.count_store_instruction,0x92630000|ns.layout.count_offset) &&
    instruction(ns.filename_instruction,0x389F0020) &&
    instruction(ns.file_index_function,0x8863040C) && instruction(ns.file_index_function+4,0x4E800020);
  if(!namespace_valid) {XELOGE("BMT runtime: compiled-file namespace layout proof does not match");return false;}
  const std::array<uint8_t,16> condition_entry{{0x7D,0x88,0x02,0xA6,0x91,0x81,0xFF,0xF8,
    0xFB,0xC1,0xFF,0xE8,0xFB,0xE1,0xFF,0xF0}};
  std::array<uint8_t,16> condition_actual{};
  if(!bmt::ValidateFormVtable(read,impl_->profile->player_vtable,".?AVPlayerCharacter@@") ||
     !read(impl_->profile->get_dead_condition,condition_actual.data(),condition_actual.size()) ||
     condition_actual!=condition_entry) {
    XELOGE("BMT runtime: Player RTTI or GetDead condition handler does not match");return false;
  }
  impl_->actor_profile=bmt::FindActorProfile(impl_->profile->xex_sha256);
  impl_->actor_routines_verified=impl_->actor_profile && bmt::ValidateActorRoutines(read,*impl_->actor_profile);
  if(impl_->actor_profile)
    impl_->actor_profile_status=impl_->actor_routines_verified?"Validated":"RoutineBytesMismatch";
  // One private system-heap allocation lasts until the emulator Memory instance
  // is destroyed. Keeping that lifetime avoids freeing a pending callback's data.
  impl_->condition_result_va=impl_->processor->memory()->SystemHeapAlloc(8);
  if(!impl_->condition_result_va) {XELOGE("BMT runtime: condition result allocation failed");return false;}
  for (size_t i=0;i<impl_->probes.size();++i) {
    auto& probe=impl_->probes[i]; probe={impl_.get(),&impl_->profile->probes[i],nullptr};
    probe.function=impl_->processor->DefineBuiltin(probe.profile->name,Impl::Observe,&probe,nullptr);
  }
  // Snapshot actual routes after per-title configuration and before guest threads.
  auto& environment=impl_->environment;
  const auto path_text=[](const std::filesystem::path& value) {
    if(value.empty()) return std::string{};
    std::error_code error;const auto absolute=std::filesystem::absolute(value,error);
    return error?std::string{}:xe::path_to_utf8(absolute.lexically_normal());
  };
  environment.storage=path_text(emulator->storage_root());
  environment.content=path_text(emulator->content_root());
  environment.cache=path_text(emulator->cache_root());
  if(auto* entry=dynamic_cast<xe::vfs::HostPathEntry*>(emulator->file_system()->ResolvePath("game:\\"))) {
    environment.game=path_text(entry->host_path());
    environment.game_read_only=entry->is_read_only();
  }
  environment.allow_plugins=cvars::allow_plugins;
  environment.allow_game_writes=cvars::allow_game_relative_writes;
  {
    std::lock_guard lock(config_routes_mutex);
    environment.config=path_text(config_routes.global_path);
    environment.global_read=path_text(config_routes.global_read_path);
    environment.global_status=config_routes.global_status;
    environment.game_config=path_text(config_routes.game_path);
    environment.game_status=config_routes.game_status;
  }
  environment.process=GetCurrentProcessId();
  FILETIME created{},exited{},kernel{},user{};
  if(GetProcessTimes(GetCurrentProcess(),&created,&exited,&kernel,&user))
    environment.process_created=(uint64_t(created.dwHighDateTime)<<32)|created.dwLowDateTime;
  if(auto* window=dynamic_cast<xe::ui::Win32Window*>(emulator->display_window())) {
    const auto hwnd=window->hwnd();DWORD owner=0;
    environment.window_thread=GetWindowThreadProcessId(hwnd,&owner);
    environment.window_process=owner;environment.window=uint64_t(reinterpret_cast<uintptr_t>(hwnd));
    environment.window_valid=hwnd && IsWindow(hwnd) && owner==environment.process && environment.window_thread;
  }
  environment.complete=!environment.storage.empty()&&!environment.content.empty()&&!environment.cache.empty()&&
    !environment.game.empty()&&!environment.config.empty()&&environment.global_status=="loaded"&&
    !environment.global_read.empty()&&!environment.game_config.empty()&&
    (environment.game_status=="loaded"||environment.game_status=="absent")&&
    environment.process_created&&environment.window_valid;
  impl_->input_gamepad=AcquireRuntimeGamepad();
  if(impl_->input_gamepad) impl_->input_gamepad->AttachProfile(impl_->profile->xex_sha256,GetCurrentProcessId());
  Impl::active=impl_.get(); impl_->running=true;
  impl_->server=std::thread([this]{impl_->Serve();});
  XELOGI("BMT runtime: validated profile {}",impl_->profile->identity);
  return true;
#else
  return false;
#endif
}
Function* RuntimeBridge::FindHook(Processor* processor,uint32_t address) {
  auto* self=Impl::active.load();
  if (!self || self->processor!=processor) return nullptr;
  for (size_t index=0;index<self->probes.size();++index) {
    const auto& probe=self->probes[index];
    if (probe.profile->address!=address) continue;
    if (!self->probe_compiled[index].exchange(true))
      XELOGI("BMT runtime: compiled probe {} at {:08X}",probe.profile->name,address);
    return probe.function;
  }
  return nullptr;
}
void RuntimeBridge::ObserveHostOpen(std::string_view guest_path,
    xe::filesystem::FileHandle& file,bool read_only) {
  if(!Impl::active.load()) return;
  const auto extension=bmt::AsciiLower(xe::path_to_utf8(file.path().extension()));
  if(extension!=".esm" && extension!=".esp") return;
  // Shared lifetime guard lets independent opens proceed without serializing
  // hashing; teardown waits for outstanding observers before freeing the owner.
  std::shared_lock file_lock(Impl::file_observer_gate);
  auto* self=Impl::active.load();if(self) self->RecordOpenedFile(guest_path,file,read_only);
}
}  // namespace xe::cpu
