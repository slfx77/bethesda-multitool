// Bounded nonblocking byte-pipe output. BSD-3-Clause.
#pragma once
#include <cstddef>
#include <cstdint>
#include <span>

namespace xe::cpu::bmt {
enum class PacketWriteStatus { Failed, Blocked, Progress, Complete };

// The caller retains the packet until Complete. Successful short writes, including
// zero bytes, are backpressure; restarting at byte zero would corrupt framing.
template <typename Writer>
PacketWriteStatus PumpPacket(std::span<const uint8_t> packet, size_t& offset,
                             Writer&& write) {
  if (offset > packet.size()) return PacketWriteStatus::Failed;
  if (offset == packet.size()) return PacketWriteStatus::Complete;
  size_t written = 0;
  const auto remaining = packet.subspan(offset);
  if (!write(remaining, written) || written > remaining.size())
    return PacketWriteStatus::Failed;
  offset += written;
  if (offset == packet.size()) return PacketWriteStatus::Complete;
  return written ? PacketWriteStatus::Progress : PacketWriteStatus::Blocked;
}
}  // namespace xe::cpu::bmt
