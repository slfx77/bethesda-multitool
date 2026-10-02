#pragma once
#include <array>
#include <cstdint>
#include <cstring>

namespace bmt::critical {
enum class Point : std::uint32_t { Begin, Weapon, SourceModifiers, TargetModifiers,
    BeforeRandom, AfterRandom, BeforeThreshold, AfterThreshold, End };
enum class Failure : std::uint32_t { None, Sequence, Frame, Bounds, Read, Changed,
    Identity, Lifetime, FloatingPoint, Reentry, Capacity, Code };
struct Sample {
    std::uint64_t invocation=0,capture=0,connection=0,load=0;
    std::uint32_t hit=0,frame=0,returnAddress=0,stackLow=0,stackHigh=0,point=0;
    std::uint32_t source=0,target=0,weapon=0,fallback=0,selectedWeapon=0,selectedWeaponId=0;
    std::uint32_t context=0,flagsBefore=0,flagsAfter=0,random=0,remainder=0,threshold=0;
    std::uint32_t weaponBits=0,sourceBits=0,targetBits=0,finalBits=0;
    std::uint32_t vatsBonusBits=0,sneakMultiplierBits=0;
    std::uint16_t controlWord=0;
    std::uint32_t count=0;
    Failure failure=Failure::None;
    bool began=false,ended=false,early=false,randomObserved=false,thresholdObserved=false;
    std::array<std::uint8_t,0x64> hitBefore{},hitAfter{};
    std::array<std::uint8_t,16> selectedWeaponHeader{};
};
inline std::uint32_t Word(const std::uint8_t* data,std::size_t offset) noexcept {
    std::uint32_t value=0;std::memcpy(&value,data+offset,4);return value;
}
inline void Fail(Sample& sample,Failure failure) noexcept {
    if(sample.failure==Failure::None)sample.failure=failure;
}
inline bool Next(Sample& sample,Point point) noexcept {
    const auto next=static_cast<std::uint32_t>(point);
    if(point==Point::Begin) {
        if(sample.began){Fail(sample,Failure::Reentry);return false;}
        sample.began=true;sample.point=next;sample.count=1;return true;
    }
    if(!sample.began || sample.ended){Fail(sample,Failure::Sequence);return false;}
    if(point==Point::End && sample.point==static_cast<std::uint32_t>(Point::Begin)) {
        sample.early=true;sample.ended=true;sample.point=next;++sample.count;return true;
    }
    if(next!=sample.point+1){Fail(sample,Failure::Sequence);return false;}
    sample.point=next;++sample.count;if(point==Point::End)sample.ended=true;return true;
}
inline bool Frame(const Sample& sample,std::uint32_t frame,std::uint32_t esp) noexcept {
    return frame==sample.frame && frame>=0x40 && frame<=UINT32_MAX-8 &&
        sample.stackLow && sample.stackLow<=frame-0x40 && frame+8<=sample.stackHigh &&
        esp>=frame-0x40 && esp<=frame-0x3C;
}
inline bool Complete(const Sample& sample) noexcept {
    if(sample.failure!=Failure::None || !sample.began || !sample.ended || sample.controlWord!=0x007F)return false;
    if(sample.early)return sample.count==2 && !sample.randomObserved && !sample.thresholdObserved;
    return sample.count==9 && sample.randomObserved && sample.thresholdObserved && sample.remainder==sample.random%1000;
}
inline bool Comparison(const Sample& sample) noexcept {
    // 009B72C9: signed CMP/JGE. Remainder is in 0..999.
    std::int32_t threshold=0;std::memcpy(&threshold,&sample.threshold,4);
    return static_cast<std::int32_t>(sample.remainder)<threshold;
}
} // namespace bmt::critical
