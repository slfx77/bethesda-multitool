#pragma once

// Unintegrated Native32 draft. Pure admission/association only; no engine APIs.
#include "RuntimeConditionModel.h"
#include <array>
#include <cstdint>

namespace bmt::ctda::pilot {
constexpr std::size_t RowCount = 3;
constexpr std::size_t MaxItemCalls = 8;
constexpr std::uint32_t GetIsIdFunction = 72;
constexpr std::uint32_t GetIsIdOpcode = 0x1048;

struct Epoch {
    std::uint64_t request = 0, capture = 0, connection = 0, load = 0;
    std::uint32_t thread = 0;
    bool operator==(const Epoch& other) const noexcept {
        return request == other.request && capture == other.capture &&
            connection == other.connection && load == other.load && thread == other.thread;
    }
};
struct FormIdentity { Address address = 0; std::uint32_t id = 0; std::uint8_t type = 0; };
struct ActorIdentity { FormIdentity form, base; };
struct SourceBindings {
    Address player = 0, target = 0, playerBase = 0, docBase = 0;
    ActorIdentity playerIdentity, targetIdentity;
    FormIdentity playerBaseIdentity, docBaseIdentity;
};
enum class Rejection {
    None, Snapshot, Count, DuplicateItem, Padding, Operator, Function,
    Parameter, RunOn, Reference, Changed
};
inline Rejection CheckFixture(const Snapshot& snapshot, const SourceBindings& bindings) noexcept {
    if (!snapshot.stable || snapshot.failure != Failure::None ||
        snapshot.owner.kind != OwnerKind::Quest || !bindings.player || !bindings.target ||
        !bindings.playerBase || !bindings.docBase) return Rejection::Snapshot;
    if (snapshot.ItemCount() != RowCount) return Rejection::Count;
    std::array<Address, RowCount> seen{};
    std::size_t index = 0;
    for (const auto& node : snapshot.nodes) {
        if (!node.hasItem) continue;
        for (std::size_t prior = 0; prior < index; ++prior)
            if (seen[prior] == node.itemAddress) return Rejection::DuplicateItem;
        seen[index] = node.itemAddress;
        const auto& item = node.item;
        if (item.raw[1] || item.raw[2] || item.raw[3]) return Rejection::Padding;
        if (item.flags != (index == 0 ? 1 : 0) || item.comparisonBits != 0x3F800000)
            return Rejection::Operator;
        if (item.functionStorage != GetIsIdFunction) return Rejection::Function;
        if (item.parameter1Raw != (index == 2 ? bindings.playerBase : bindings.docBase) ||
            item.parameter2Raw != 0) return Rejection::Parameter;
        if (item.runOn != index) return Rejection::RunOn;
        if (item.reference != (index == 2 ? bindings.player : 0)) return Rejection::Reference;
        ++index;
    }
    return Rejection::None;
}
inline bool SameSnapshot(const Snapshot& before, const Snapshot& after) noexcept {
    if (!before.stable || !after.stable || before.failure != Failure::None ||
        after.failure != Failure::None || before.owner.address != after.owner.address ||
        before.owner.formId != after.owner.formId || before.owner.kind != after.owner.kind ||
        before.ownerHeader != after.ownerHeader || before.headAddress != after.headAddress ||
        before.nodes.size() != after.nodes.size()) return false;
    for (std::size_t index = 0; index < before.nodes.size(); ++index) {
        const auto& a = before.nodes[index]; const auto& b = after.nodes[index];
        if (a.address != b.address || a.raw != b.raw || a.hasItem != b.hasItem ||
            a.itemAddress != b.itemAddress || a.next != b.next ||
            (a.hasItem && a.item.raw != b.item.raw)) return false;
    }
    return true;
}
inline Address ExpectedSubject(std::uint32_t runOn, const SourceBindings& bindings) noexcept {
    return runOn == 1 ? bindings.target : runOn <= 2 ? bindings.player : 0;
}
struct EvalSample {
    bool entered = false, returned = false, handlerReturned = false, resultReadable = false;
    Address subject = 0, subjectBaseAddress = 0, parameter1 = 0, parameter2 = 0;
    std::uint32_t subjectId = 0, subjectBaseId = 0, parameter1Id = 0;
    std::uint8_t subjectType = 0, subjectBaseType = 0, parameter1Type = 0;
    bool identityBefore = false, identityStable = false;
    std::uint64_t resultBits = 0;
};
struct ComparisonSample {
    bool entered = false, returned = false, value = false;
    std::uint32_t operation = 0, actualBits = 0, comparisonBits = 0;
};
struct ItemSample {
    std::uint32_t callId = 0, sourceRow = UINT32_MAX, site = 0;
    Address item = 0, subject = 0, target = 0, auxiliary = 0;
    bool entered = false, returned = false, value = false;
    bool auxiliaryBeforeReadable = false, auxiliaryAfterReadable = false;
    std::uint8_t auxiliaryBefore = 0, auxiliaryAfter = 0;
    bool rawBeforeReadable = false, rawAfterReadable = false;
    std::array<std::uint8_t, 28> before{}, after{};
    EvalSample eval;
    ComparisonSample comparison;
};
// Fixed storage: observers allocate nothing and never infer a skipped evaluation.
struct Buffer {
    std::array<ItemSample, MaxItemCalls> items{};
    std::size_t count = 0;
    bool overflow = false, unexpected = false;
    ItemSample* Begin(std::uint32_t sourceRow) noexcept {
        if (count == items.size()) { overflow = true; return nullptr; }
        auto& row = items[count++];
        row.callId = static_cast<std::uint32_t>(count); row.sourceRow = sourceRow;
        row.entered = true; return &row;
    }
};
inline bool CompleteObservedItem(const ItemSample& sample, const SourceBindings& bindings) noexcept {
    if (sample.sourceRow >= RowCount || !sample.entered || !sample.returned ||
        !sample.rawBeforeReadable || !sample.rawAfterReadable || sample.before != sample.after ||
        sample.subject != bindings.player || sample.target != bindings.target) return false;
    const auto& eval = sample.eval;
    const auto& expectedSubject = sample.sourceRow == 1 ? bindings.targetIdentity : bindings.playerIdentity;
    const auto& expectedParameter = sample.sourceRow == 2 ? bindings.playerBaseIdentity : bindings.docBaseIdentity;
    return eval.entered && eval.returned && eval.handlerReturned && eval.resultReadable &&
        (eval.resultBits & 0x7FF0000000000000ull) != 0x7FF0000000000000ull &&
        eval.identityBefore && eval.identityStable &&
        expectedSubject.form.address && expectedSubject.form.id && expectedSubject.base.address && expectedSubject.base.id &&
        expectedParameter.address && expectedParameter.id &&
        eval.subject == ExpectedSubject(sample.sourceRow, bindings) &&
        eval.subject == expectedSubject.form.address && eval.subjectId == expectedSubject.form.id && eval.subjectType == expectedSubject.form.type &&
        eval.subjectBaseAddress == expectedSubject.base.address && eval.subjectBaseId == expectedSubject.base.id && eval.subjectBaseType == expectedSubject.base.type &&
        eval.parameter1 == (sample.sourceRow == 2 ? bindings.playerBase : bindings.docBase) &&
        eval.parameter1 == expectedParameter.address && eval.parameter1Id == expectedParameter.id && eval.parameter1Type == expectedParameter.type &&
        eval.parameter2 == 0 && sample.comparison.entered && sample.comparison.returned &&
        sample.comparison.operation == 0 && sample.comparison.comparisonBits == 0x3F800000;
}
} // namespace bmt::ctda::pilot
