#pragma once
#include <array>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <limits>

namespace bmt::damage {
constexpr unsigned Version=1, MaximumDepth=8;
constexpr std::uint32_t HitAddress=0x009B5170, StageAddress=0x00644CE0;
enum Site : unsigned { HitEntry, HitReturn, StageEntry, MeleeAv, Limb, StageReturn, SiteCount };
constexpr std::array<std::uint32_t,SiteCount> Sites{{HitAddress,0x009B5642,StageAddress,0x00644DA8,0x00644E3F,0x0064508E}};
constexpr std::array<unsigned,SiteCount> Lengths{{6,5,6,6,5,6}};
constexpr std::array<std::array<std::uint8_t,6>,SiteCount> Original{{
    {{0x55,0x8B,0xEC,0x83,0xEC,0x44}},{{0x5E,0x8B,0xE5,0x5D,0xC3,0}},
    {{0x55,0x8B,0xEC,0x83,0xEC,0x74}},{{0xD8,0x45,0xD8,0xD9,0x5D,0xD8}},
    {{0xD9,0x00,0xDC,0x4D,0xB0,0}},{{0xD9,0x5D,0x8C,0xD9,0x45,0x8C}}
}};
inline std::uint32_t U32(const void* p) noexcept {std::uint32_t v=0;std::memcpy(&v,p,4);return v;}
inline std::uint16_t U16(const void* p) noexcept {std::uint16_t v=0;std::memcpy(&v,p,2);return v;}
inline float F32(std::uint32_t raw) noexcept {float v=0;std::memcpy(&v,&raw,4);return v;}
inline std::uint32_t Bits(float v) noexcept {std::uint32_t raw=0;std::memcpy(&raw,&v,4);return raw;}
inline bool Finite(std::uint32_t raw) noexcept {return (raw&0x7F800000u)!=0x7F800000u;}
// x87 extended operands are admitted only when exactly representable as binary32.
// FXSAVE stores ST0 at +32 regardless of physical TOP; FTW indexes physical TOP.
inline bool Float80(const std::uint8_t* raw,std::uint32_t& bits) noexcept {
    std::uint64_t mantissa=0;std::memcpy(&mantissa,raw,8);
    const auto signExponent=U16(raw+8);
    const auto exponent=signExponent&0x7FFF;
    const std::uint32_t sign=(signExponent&0x8000)?0x80000000u:0;
    if(!mantissa && !exponent){bits=sign;return true;}
    if(!exponent || exponent==0x7FFF || !(mantissa&0x8000000000000000ull))return false;
    const int binaryExponent=static_cast<int>(exponent)-16383;
    if(binaryExponent>127 || binaryExponent< -149)return false;
    const unsigned shift=binaryExponent>= -126?40u:static_cast<unsigned>(-binaryExponent-86);
    if(shift>=64 || (mantissa&((std::uint64_t(1)<<shift)-1)))return false;
    const auto significand=static_cast<std::uint32_t>(mantissa>>shift);
    bits=sign|(binaryExponent>= -126?((static_cast<std::uint32_t>(binaryExponent+127)<<23)|(significand&0x7FFFFF)):significand);
    return Finite(bits);
}
inline bool St0(const std::uint8_t* fx,std::array<std::uint8_t,10>& raw,std::uint32_t& bits) noexcept {
    const unsigned top=(U16(fx+2)>>11)&7;
    if(!(fx[4]&(1u<<top)))return false;
    std::memcpy(raw.data(),fx+32,raw.size());return Float80(raw.data(),bits);
}
struct Frame {
    // EBP-74 through EBP+27, including all eight arguments and saved EBP/return.
    std::array<std::uint8_t,0x9C> raw{};
    std::uint32_t local(unsigned offset) const noexcept {return U32(raw.data()+0x74-offset);}
    std::uint32_t argument(unsigned index) const noexcept {return U32(raw.data()+0x7C+index*4);}
};
struct Stage {
    bool entered=false,returned=false,invalid=false,settingsStable=false,weaponStable=false;
    unsigned meleeCalls=0,limbCalls=0;
    std::uint32_t entryEsp=0,frame=0,returnAddress=0,callerFrame=0,extra=0,av17=0;
    std::array<std::uint32_t,8> arguments{};
    std::array<std::uint8_t,10> av17Raw{};
    std::array<std::uint8_t,0x160> weaponBefore{},weaponAfter{};
    std::array<std::uint8_t,8> limbRaw{};
    std::uint16_t entryControl=0,returnControl=0;
    Frame after;
};
inline const char* Complete(const Stage& s) noexcept {
    if(!s.entered || !s.returned || s.invalid)return "stage-entry-return-unavailable";
    if(s.entryControl!=0x007F || s.returnControl!=s.entryControl)return "x87-control-unavailable";
    if(s.returnAddress!=0x004BDF76 || !s.arguments[0] || !s.arguments[1] || !s.arguments[6])return "call-route-unavailable";
    if(s.meleeCalls!=1 || s.limbCalls!=1)return "type1-operand-path-unavailable";
    if(!s.weaponStable || s.weaponBefore!=s.weaponAfter || s.weaponBefore[4]!=0x28 || s.weaponBefore[0xF4]!=1)return "weapon-changed-or-unsupported";
    if(!s.settingsStable)return "settings-changed-or-unavailable";
    if(U32(s.after.raw.data()+0x74)!=s.callerFrame || U32(s.after.raw.data()+0x78)!=s.returnAddress)return "caller-frame-changed";
    for(unsigned i=0;i<8;++i)if(s.arguments[i]!=s.after.argument(i))return "arguments-changed";
    if(s.after.local(0x3C)!=U16(s.weaponBefore.data()+0xA0) || s.after.local(0x30)!=U32(s.weaponBefore.data()+0x15C))return "weapon-getter-mismatch";
    if(s.after.local(0x48)!=1)return "limb-type-mismatch";
    if(std::memcmp(s.limbRaw.data(),s.after.raw.data()+0x24,8))return "limb-local-changed";
    double limb=0;std::memcpy(&limb,s.limbRaw.data(),8);
    if(!std::isfinite(limb) || static_cast<double>(static_cast<float>(limb))!=limb)return "limb-value-unrepresentable";
    for(const auto bits:{s.av17,s.extra,s.arguments[2],s.arguments[3],s.after.local(0x20),s.after.local(0x24),s.after.local(8),s.after.local(4),s.after.local(0x74)})
        if(!Finite(bits))return "nonfinite-operand";
    return nullptr;
}
}


namespace bmt::damage {
// Caller serializes access. Keys prevent delayed completions from contributing
// to a later lease or capture. Select is called only for the current lease.
struct CoverageKey {
    std::uint64_t lease=0,capture=0,connection=0,load=0;
    bool valid() const noexcept {return lease && capture && connection && load;}
    bool operator==(const CoverageKey& other) const noexcept {
        return lease==other.lease && capture==other.capture && connection==other.connection && load==other.load;
    }
};
enum CoverageCounter : unsigned {
    RouteEntries,ParticipantsMatched,AdmittedInvocations,StageEventAttempts,
    WrongThread,InactiveCapture,StackOrArguments,MissingParticipants,OtherParticipants,
    LeaseChanged,Budget,InvalidEntry,InvalidReturn,Depth,Exception,CoverageCounterCount
};
constexpr std::array<const char*,CoverageCounterCount> CoverageNames{{
    "routeEntries","participantsMatched","admittedInvocations","stageEventAttempts",
    "wrongThread","inactiveCapture","stackOrArguments","missingParticipants","otherParticipants",
    "leaseChanged","budget","invalidEntry","invalidReturn","depth","exception"
}};
struct Coverage {
    CoverageKey key;
    std::array<std::uint32_t,CoverageCounterCount> counts{};
    bool saturated=false;
    bool select(const CoverageKey& current) noexcept {
        if(!current.valid())return false;
        if(!(key==current)){key=current;counts={};saturated=false;}
        return true;
    }
    bool add(const CoverageKey& owner,CoverageCounter counter) noexcept {
        if(!owner.valid() || !(key==owner) || counter>=CoverageCounterCount)return false;
        auto& count=counts[counter];
        if(count==UINT32_MAX)saturated=true;else ++count;
        return true;
    }
};
}
