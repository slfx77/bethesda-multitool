// Windows overlapped reads for guest-run admission. No bridge lifetime is retained.
#pragma once
#include <array>
#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <stdexcept>
#include <thread>

namespace xe::cpu::guest_run {
inline std::atomic<bool> read_drain_pending{false};
struct PendingRead {
  HANDLE file=INVALID_HANDLE_VALUE,event=nullptr;
  OVERLAPPED overlapped{};
  std::array<uint8_t,65536> buffer{};
  std::shared_ptr<PendingRead> retained;
  bool pending=false;
  PendingRead(){event=CreateEventW(nullptr,TRUE,FALSE,nullptr);if(!event)throw std::runtime_error("run-read-event-failed");}
  ~PendingRead(){if(file!=INVALID_HANDLE_VALUE)CloseHandle(file);if(event)CloseHandle(event);}
  PendingRead(const PendingRead&)=delete;
  PendingRead& operator=(const PendingRead&)=delete;
};
inline bool DrainRead(PendingRead& read,DWORD timeout) {
  if(!read.pending)return true;
  if(WaitForSingleObject(read.event,timeout)!=WAIT_OBJECT_0)return false;
  DWORD count=0;GetOverlappedResult(read.file,&read.overlapped,&count,FALSE);
  read.pending=false;return true;
}
inline void CancelRead(std::shared_ptr<PendingRead> read) noexcept {
  if(!read->pending)return;
  CancelIoEx(read->file,&read->overlapped);
  if(DrainRead(*read,1000))return;
  // A defective local driver may delay cancellation. Keep its entire operation
  // alive outside the bridge, and prevent another admission while it drains.
  read_drain_pending.store(true);
  // The self-owner needs no allocation. If thread creation also fails, it safely
  // preserves the live kernel operation instead of freeing its memory.
  read->retained=read;
  try {std::thread([read] {
      if(DrainRead(*read,INFINITE)){read->retained.reset();read_drain_pending.store(false);}
      // An invalid wait also retains the operation until process exit.
    }).detach();}
  catch(...) {/* Retain the operation and sealed admission until process exit. */}
}
inline DWORD ReadChunk(const std::shared_ptr<PendingRead>& read,uint64_t offset,
    DWORD wanted,const std::function<void()>& check) {
  if(read_drain_pending.load())throw std::runtime_error("run-read-drain-pending");
  check();ResetEvent(read->event);read->overlapped={};
  read->overlapped.hEvent=read->event;read->overlapped.Offset=DWORD(offset);
  read->overlapped.OffsetHigh=DWORD(offset>>32);
  DWORD count=0;
  if(ReadFile(read->file,read->buffer.data(),wanted,&count,&read->overlapped)) {check();return count;}
  if(GetLastError()!=ERROR_IO_PENDING)throw std::runtime_error("run-file-read-failed");
  read->pending=true;
  try {
    while(true) {
      check();const auto state=WaitForSingleObject(read->event,50);
      if(state==WAIT_TIMEOUT)continue;
      if(state!=WAIT_OBJECT_0)throw std::runtime_error("run-file-read-wait-failed");
      const auto success=GetOverlappedResult(read->file,&read->overlapped,&count,FALSE);
      read->pending=false;
      if(!success)throw std::runtime_error("run-file-read-failed");
      check();return count;
    }
  } catch(...) {CancelRead(read);throw;}
}
} // namespace xe::cpu::guest_run
