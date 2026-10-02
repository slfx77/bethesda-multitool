#pragma once
#include <array>
#include <cstdint>
#include <cstring>

namespace bmt::ctda::frame {
constexpr std::size_t Locals=0x3C, Bytes=0x54, MaxSteps=9;
struct Anchor {
    std::uint32_t ebp=0,returnAddress=0,stackLow=0,stackHigh=0;
    std::uint32_t head=0,subject=0,target=0,auxiliary=0;
    std::uint8_t mode=0;
};
enum class Failure { None, Bounds, StackPointer, Frame, ReturnAddress, Head, Subject,
    TargetNormalizedOrChanged, Auxiliary, Mode, Unreadable, Changed, Node, Boolean, Sequence, Overflow };
struct Sample {
    std::uint32_t site=0,ebp=0,esp=0,flags=0,node=0,item=0,sourceRow=UINT32_MAX,itemCalls=0;
    std::uint32_t head=0,subject=0,target=0,auxiliary=0;
    std::uint8_t aggregate=0,group=0,open=0,itemAuxiliary=0,outerAuxiliary=0,mode=0;
    bool associated=false;
    Failure failure=Failure::None;
    std::array<std::uint8_t,Bytes> raw{};
};
inline std::uint32_t Word(const Sample& sample,int offset) noexcept {
    std::uint32_t value=0;std::memcpy(&value,sample.raw.data()+Locals+offset,4);return value;
}
inline Failure Bounds(const Anchor& anchor,std::uint32_t ebp,std::uint32_t esp) noexcept {
    if(!anchor.stackLow || anchor.stackHigh<=anchor.stackLow || ebp<Locals || ebp>UINT32_MAX-0x18 ||
        ebp-Locals<anchor.stackLow || ebp+0x18>anchor.stackHigh)return Failure::Bounds;
    if(ebp!=anchor.ebp)return Failure::Frame;
    if(esp!=ebp-Locals)return Failure::StackPointer;
    return Failure::None;
}
inline Failure Decode(const Anchor& anchor,Sample& sample) noexcept {
    const auto bounds=Bounds(anchor,sample.ebp,sample.esp);if(bounds!=Failure::None)return bounds;
    sample.head=Word(sample,-0x20);sample.subject=Word(sample,8);sample.target=Word(sample,12);
    sample.auxiliary=Word(sample,16);sample.mode=sample.raw[Locals+20];
    sample.node=Word(sample,-8);sample.item=Word(sample,-0x18);
    sample.aggregate=sample.raw[Locals-9];sample.group=sample.raw[Locals-4];
    sample.open=sample.raw[Locals-10];sample.itemAuxiliary=sample.raw[Locals-0x19];
    if(Word(sample,4)!=anchor.returnAddress)return Failure::ReturnAddress;
    if(sample.head!=anchor.head)return Failure::Head;
    if(sample.subject!=anchor.subject)return Failure::Subject;
    // 680CB7 may normalize and replace +0C. This pilot admits unchanged actors only.
    if(sample.target!=anchor.target)return Failure::TargetNormalizedOrChanged;
    if(sample.auxiliary!=anchor.auxiliary)return Failure::Auxiliary;
    if(sample.mode!=anchor.mode)return Failure::Mode;
    if(sample.aggregate>1 || sample.group>1 || sample.open>1 || sample.itemAuxiliary>1 || sample.outerAuxiliary>1)
        return Failure::Boolean;
    return Failure::None;
}
struct Buffer {
    std::array<Sample,MaxSteps> rows{};std::size_t count=0;
    bool overflow=false,unexpected=false;
    Sample* Begin() noexcept {
        if(count==rows.size()){overflow=true;return nullptr;}
        return &rows[count++];
    }
};
} // namespace bmt::ctda::frame
