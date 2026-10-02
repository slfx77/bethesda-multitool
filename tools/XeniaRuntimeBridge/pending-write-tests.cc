// Exercise the actual Windows PIPE_NOWAIT short/zero-write contract.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include "overlay/runtime_pending_write.h"
#include <algorithm>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

using xe::cpu::bmt::PacketWriteStatus;
void require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
int main() try {
  for(const size_t size:{size_t(65560),size_t(32791)}) {
    const auto name=L"\\\\.\\pipe\\BMT.Backpressure.Test."+std::to_wstring(GetCurrentProcessId())+L"."+std::to_wstring(size);
    const auto server=CreateNamedPipeW(name.c_str(),PIPE_ACCESS_DUPLEX|FILE_FLAG_FIRST_PIPE_INSTANCE,
      PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_NOWAIT,1,131072,131072,0,nullptr);
    require(server!=INVALID_HANDLE_VALUE,"create server");
    const auto client=CreateFileW(name.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr);
    require(client!=INVALID_HANDLE_VALUE,"connect client");
    require(ConnectNamedPipe(server,nullptr)||GetLastError()==ERROR_PIPE_CONNECTED,"admit client");
    std::vector<uint8_t> packet(size); for(size_t i=0;i<size;++i) packet[i]=uint8_t((i*31+i/251)%256);
    size_t offset=0; int blocked=0,shortWrites=0;
    const auto pump=[&] {
      return xe::cpu::bmt::PumpPacket(packet,offset,[&](std::span<const uint8_t> remaining,size_t& written) {
        DWORD count=0;const bool ok=WriteFile(server,remaining.data(),DWORD(remaining.size()),&count,nullptr)!=FALSE;
        written=count;if(ok && count<remaining.size()) ++shortWrites;return ok;
      });
    };
    std::vector<uint8_t> expected;
    // Frames fit within production's 128 KiB pipe capacity. Fill it without a
    // reader, rather than assuming Windows must choose a positive short write.
    for(int frame=0;frame<10 && !blocked;++frame) {
      offset=0;packet[0]=uint8_t(frame);expected.insert(expected.end(),packet.begin(),packet.end());
      for(int step=0;step<1000;++step) {
        const auto state=pump();require(state!=PacketWriteStatus::Failed,"fill pipe");
        if(state==PacketWriteStatus::Complete) break;
        if(state==PacketWriteStatus::Blocked) {++blocked;break;}
      }
    }
    require(blocked>0,"must exercise real full-pipe zero write");
    std::vector<uint8_t> received;
    for(int step=0;step<2000 && received.size()<expected.size();++step) {
      DWORD available=0;require(PeekNamedPipe(client,nullptr,0,nullptr,&available,nullptr)!=FALSE,"peek output");
      if(available) {
        uint8_t buffer[997];DWORD count=0;
        require(ReadFile(client,buffer,std::min<DWORD>(available,sizeof(buffer)),&count,nullptr)!=FALSE,"drain output");
        received.insert(received.end(),buffer,buffer+count);
      }
      const auto state=pump();require(state!=PacketWriteStatus::Failed,"retry write");
      if(state==PacketWriteStatus::Blocked) ++blocked;
    }
    require(received==expected,"retry duplicated or lost ordered packet bytes");
    require(offset==packet.size() && blocked && shortWrites,"backpressure was not exercised");
    CloseHandle(client);offset=0;
    require(pump()==PacketWriteStatus::Failed,"closed client must fail rather than retry forever");
    CloseHandle(server);

    // Positive short writes are permitted by PIPE_NOWAIT's byte-pipe contract,
    // though the local kernel can choose zero instead. Force that second branch.
    offset=0;received.clear();bool pause=false;
    while(offset<packet.size()) {
      const auto state=xe::cpu::bmt::PumpPacket(packet,offset,[&](std::span<const uint8_t> remaining,size_t& written) {
        pause=!pause;written=pause?0:std::min<size_t>(997,remaining.size());
        received.insert(received.end(),remaining.begin(),remaining.begin()+written);return true;
      });
      require(state!=PacketWriteStatus::Failed,"injected short write");
    }
    require(received==packet,"positive short-write offsets corrupted framing");
  }
  std::cout<<"Passed 2 real-pipe backpressure cases (partial, blocked, exact drain, disconnect)\n";
} catch(const std::exception& error) {
  std::cerr<<"FAILED: "<<error.what()<<" (Windows error "<<GetLastError()<<")\n";
  return 1;
}
