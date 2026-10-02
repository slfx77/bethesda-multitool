#include "RuntimeConditionModel.h"
#include <iostream>
#include <map>
#include <stdexcept>
#include <string>
#include <utility>

using namespace bmt::ctda;
namespace {
void Check(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}
struct Region { Address address; std::vector<std::uint8_t> bytes; };
struct Memory {
    std::vector<Region> regions;
    std::map<Address, unsigned> reads;
    Address changeAddress = 0;
    std::size_t changeOffset = 0;
    Address unreadable = 0;
    static bool Read(void* context, Address address, void* output, std::size_t length) {
        auto& self = *static_cast<Memory*>(context);
        const auto count = ++self.reads[address];
        if (address == self.unreadable) return false;
        for (auto& region : self.regions) {
            if (address >= region.address && std::uint64_t(address) + length <= std::uint64_t(region.address) + region.bytes.size()) {
                const auto offset = static_cast<std::size_t>(address - region.address);
                if (address == self.changeAddress && count == 2) region.bytes.at(offset + self.changeOffset) ^= 1;
                std::memcpy(output, region.bytes.data() + offset, length); return true;
            }
        }
        return false;
    }
    void Word(Address address, std::uint32_t value) {
        for (auto& region : regions) if (address >= region.address && std::uint64_t(address) + 4 <= std::uint64_t(region.address) + region.bytes.size()) {
            auto* dest = region.bytes.data() + (address - region.address);
            for (unsigned n = 0; n != 4; ++n) dest[n] = static_cast<std::uint8_t>(value >> (8 * n));
            return;
        }
        throw std::runtime_error("bad fixture address");
    }
};
Memory Fixture(OwnerKind kind) {
    Memory value;
    value.regions = {{0x1000, std::vector<std::uint8_t>(0x60)}, {0x2000, std::vector<std::uint8_t>(8)},
                     {0x3000, std::vector<std::uint8_t>(28)}, {0x3100, std::vector<std::uint8_t>(28)}};
    value.regions[0].bytes[4] = kind == OwnerKind::Info ? 0x46 : 0x47;
    value.Word(0x100C, 0x01000800);
    const auto head = kind == OwnerKind::Info ? 0x1018u : 0x1054u;
    value.Word(head, 0x3000); value.Word(head + 4, 0x2000);
    value.Word(0x2000, 0x3100);
    value.Word(0x3000, 1); // OR with the next condition.
    value.Word(0x3004, 0x80000000); // Preserve literal negative zero.
    value.Word(0x3008, 0xABCD002E); // Runtime dispatch uses the low ushort; retain storage.
    value.Word(0x300C, 0xDEADBEEF); value.Word(0x3010, 0x12345678);
    value.Word(0x3100, 0x60); // greater-or-equal; closes the OR group.
    value.Word(0x3104, 0x3F800000); value.Word(0x3108, 0x2E);
    value.Word(0x3114, 2); value.Word(0x3118, 0x11223344);
    return value;
}
AdmissionEvidence Proof() { return {true,true,true,true,true,true,true,true,false}; }
}

int main() {
    try {
        unsigned cases = 0;
        for (auto kind : {OwnerKind::Info, OwnerKind::Quest}) {
            auto memory = Fixture(kind);
            const auto original = memory.regions;
            auto snapshot = ReadOwnerList(Memory::Read, &memory, {kind,0x1000,0x01000800});
            Check(snapshot.stable && snapshot.failure == Failure::None && snapshot.ItemCount() == 2, "stable owner list");
            Check(snapshot.nodes[0].address == snapshot.headAddress && snapshot.nodes[1].address == 0x2000, "physical node order");
            const auto& first = snapshot.nodes[0].item;
            Check(first.comparisonBits == 0x80000000 && first.functionStorage == 0xABCD002E && first.functionIndex == 0x2E, "raw values preserved");
            Check(first.orWithNext && !snapshot.nodes[1].item.orWithNext && snapshot.nodes[1].item.runOn == 2, "group and run-on storage");
            Check(first.parameter1Raw == 0xDEADBEEF && first.parameter2Raw == 0x12345678, "parameters preserved");
            Check(CheckAdmission(snapshot, Proof()) == Failure::None, "complete prerequisite evidence");
            for (std::size_t i = 0; i != original.size(); ++i) Check(memory.regions[i].bytes == original[i].bytes, "reader mutated fixture");
            ++cases;
        }
        struct BadCase { const char* name; Failure expected; };
        for (auto test : {BadCase{"owner",Failure::OwnerIdentity}, {"deleted",Failure::OwnerTemporaryOrDeleted},
            {"temporary",Failure::OwnerTemporaryOrDeleted}, {"dynamic-id",Failure::OwnerTemporaryOrDeleted},
            {"cycle",Failure::Cycle}, {"null-item",Failure::NullItem}, {"unreadable",Failure::Unreadable},
            {"unaligned",Failure::AddressRange}, {"overflow",Failure::AddressRange},
            {"owner-change",Failure::OwnerChanged}, {"node-change",Failure::NodeChanged}, {"item-change",Failure::ItemChanged},
            {"node-limit",Failure::NodeLimit}, {"budget",Failure::ReadBudget}, {"invalid-limit",Failure::InvalidLimits}}) {
            auto m = Fixture(OwnerKind::Info); Limits limits; OwnerRequest request{OwnerKind::Info,0x1000,0x01000800};
            const std::string name(test.name);
            if (name == "owner") request.formId = 0x01000801;
            if (name == "deleted") m.Word(0x1008, 0x20);
            if (name == "temporary") m.Word(0x1008, 0x4000);
            if (name == "dynamic-id") { request.formId = 0xFF000800; m.Word(0x100C, request.formId); }
            if (name == "cycle") m.Word(0x2004, 0x1018);
            if (name == "null-item") m.Word(0x1018, 0);
            if (name == "unreadable") m.unreadable = 0x3000;
            if (name == "unaligned") m.Word(0x1018, 0x3001);
            if (name == "overflow") m.Word(0x1018, 0xFFFFFFF0);
            if (name == "owner-change") { m.changeAddress = 0x1000; m.changeOffset = 12; }
            if (name == "node-change") { m.changeAddress = 0x1018; m.changeOffset = 4; }
            if (name == "item-change") { m.changeAddress = 0x3000; m.changeOffset = 4; }
            if (name == "node-limit") limits.maxNodes = 1;
            if (name == "budget") limits.maxReadBytes = 80;
            if (name == "invalid-limit") limits.maxNodes = 257;
            auto result = ReadOwnerList(Memory::Read, &m, request, limits);
            Check(!result.stable && result.failure == test.expected, test.name);
            Check(CheckAdmission(result, Proof()) == Failure::SnapshotIncomplete, "failed snapshot admitted");
            ++cases;
        }
        for (bool tail : {false,true}) {
            auto m = Fixture(OwnerKind::Info);
            const auto address = tail ? 0x2000u : 0x1018u;
            m.Word(address, 0); m.Word(address + 4, 0);
            auto result = ReadOwnerList(Memory::Read, &m, {OwnerKind::Info,0x1000,0x01000800});
            Check(result.stable && result.ItemCount() == (tail ? 1u : 0u), "empty sentinel"); ++cases;
        }
        { auto m = Fixture(OwnerKind::Info); m.Word(0x2000,0x3000);
          auto result = ReadOwnerList(Memory::Read,&m,{OwnerKind::Info,0x1000,0x01000800});
          Check(result.stable && result.ItemCount()==2 && result.nodes[0].address!=result.nodes[1].address &&
                result.nodes[0].itemAddress==result.nodes[1].itemAddress,"shared item preserves node occurrences"); ++cases; }
        using EvidenceMember = bool AdmissionEvidence::*;
        const std::pair<EvidenceMember,Failure> gates[] = {
            {&AdmissionEvidence::executableProfile,Failure::ProfileProof},
            {&AdmissionEvidence::loadedFunctionsAndJumpTables,Failure::CodeProof},
            {&AdmissionEvidence::independentCommandTable,Failure::TableProof},
            {&AdmissionEvidence::ownerLayout,Failure::OwnerLayoutProof},
            {&AdmissionEvidence::pluginNamespace,Failure::NamespaceProof},
            {&AdmissionEvidence::subjectAndTarget,Failure::SubjectProof},
            {&AdmissionEvidence::commandParameters,Failure::ParameterProof}};
        for (const auto& gate : gates) {
            auto m = Fixture(OwnerKind::Info); auto snapshot = ReadOwnerList(Memory::Read,&m,{OwnerKind::Info,0x1000,0x01000800});
            auto evidence = Proof(); evidence.*gate.first = false;
            Check(CheckAdmission(snapshot,evidence)==gate.second,"missing admission proof"); ++cases;
        }
        for (unsigned variant=0; variant!=7; ++variant) {
            auto m=Fixture(OwnerKind::Info); auto evidence=Proof(); Failure expected=Failure::None;
            if(variant==0){m.Word(0x3000,0xC0);expected=Failure::UnsupportedOperator;}
            if(variant==1){m.Word(0x3014,3);expected=Failure::UnsupportedRunOn;}
            if(variant==2){m.Word(0x3004,0x7FC00001);expected=Failure::NonfiniteLiteral;}
            if(variant==3){m.Word(0x3004,0x7F800000);expected=Failure::NonfiniteLiteral;}
            if(variant==4){m.Word(0x3000,4);m.Word(0x3004,0x4000);evidence.globalIdentities=false;expected=Failure::GlobalProof;}
            if(variant==5){m.Word(0x3000,4);m.Word(0x3004,0);evidence.globalIdentities=false;}
            if(variant==6){evidence.mode=true;expected=Failure::NonDefaultMode;}
            auto result=ReadOwnerList(Memory::Read,&m,{OwnerKind::Info,0x1000,0x01000800});
            Check(result.stable && CheckAdmission(result,evidence)==expected,"item admission variant");
            if(variant==5)Check(result.nodes[0].item.globalFlag && !result.nodes[0].item.usesGlobal,"null global uses literal zero");
            ++cases;
        }
        for (const auto& span : RetainedSpans) {
            Memory m; m.regions.push_back({span.address,{span.bytes.begin(),span.bytes.begin()+span.length}});
            Check(MatchesSpan(Memory::Read,&m,span),"retained span equality");
            m.regions[0].bytes[span.length-1]^=1;
            Check(!MatchesSpan(Memory::Read,&m,span),"changed span accepted");
            m.unreadable=span.address; Check(!MatchesSpan(Memory::Read,&m,span),"unreadable span accepted"); ++cases;
        }
        std::cout << "{\"status\":\"passed\",\"cases\":" << cases
                  << ",\"engineCalled\":false,\"hooksEnabled\":false}" << std::endl;
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << std::endl; return 1;
    }
}
