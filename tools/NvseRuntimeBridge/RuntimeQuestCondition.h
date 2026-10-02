#pragma once
// Bounded QUST v2 policy. No engine calls, hooks, file-source claims or formula evaluator.
#include "RuntimeConditionPilot.h"

namespace bmt::ctda::quest {
using pilot::ActorIdentity;
using pilot::FormIdentity;
constexpr std::size_t MaxRows = pilot::MaxItemCalls;
using Resolve = bool (*)(void*, Address, bool actor, ActorIdentity&);
enum class Rejection { None, Snapshot, Count, Actor, DuplicateItem, Padding, Flags,
    Literal, Function, Parameter, RunOn, Reference, TrailingOr };
inline const char* Reason(Rejection value) noexcept {
    switch(value) {
    case Rejection::None:return "supported";
    case Rejection::Snapshot:return "source-snapshot-unavailable";
    case Rejection::Count:return "source-row-count-unsupported";
    case Rejection::Actor:return "actor-layout-unsupported-or-unavailable";
    case Rejection::DuplicateItem:return "duplicate-source-item";
    case Rejection::Padding:return "source-padding-unsupported";
    case Rejection::Flags:return "condition-flags-or-operator-unsupported";
    case Rejection::Literal:return "comparison-literal-nonfinite";
    case Rejection::Function:return "condition-function-unsupported";
    case Rejection::Parameter:return "GetIsID-NPC-parameter-unavailable";
    case Rejection::RunOn:return "run-on-unsupported";
    case Rejection::Reference:return "run-on-reference-unavailable-or-unexpected";
    case Rejection::TrailingOr:return "trailing-open-OR-unsupported";
    }
    return "unsupported";
}
inline bool ValidForm(const FormIdentity& value, std::uint8_t type) noexcept {
    return value.address >= 0x10000 && !(value.address & 3) && value.address <= UINT32_MAX - 0x24 &&
        value.id && (value.id >> 24) != 0xFF && value.type == type;
}
inline bool ValidActor(const ActorIdentity& value) noexcept {
    return ValidForm(value.form,0x3B) && ValidForm(value.base,0x2A);
}
struct Row {
    Address node = 0, item = 0;
    Item source;
    ActorIdentity evaluatedActor;
    FormIdentity parameter;
};
struct Binding {
    ActorIdentity subject, target;
    std::array<Row,MaxRows> rows{};
    std::size_t count = 0;
    Rejection rejection = Rejection::None;
    std::uint32_t failedRow = UINT32_MAX;
};
inline Binding Admit(const Snapshot& snapshot, const ActorIdentity& subject,
    const ActorIdentity& target, Resolve resolve, void* context) {
    Binding out;out.subject=subject;out.target=target;
    const auto reject=[&](Rejection value) {out.rejection=value;return out;};
    if(!snapshot.stable || snapshot.failure!=Failure::None || snapshot.owner.kind!=OwnerKind::Quest)
        return reject(Rejection::Snapshot);
    if(!snapshot.ItemCount() || snapshot.ItemCount()>MaxRows)return reject(Rejection::Count);
    if(!ValidActor(subject) || !ValidActor(target))return reject(Rejection::Actor);
    for(const auto& node:snapshot.nodes)if(node.hasItem) {
        out.failedRow=static_cast<std::uint32_t>(out.count);
        for(std::size_t i=0;i<out.count;++i)if(out.rows[i].item==node.itemAddress)return reject(Rejection::DuplicateItem);
        const auto& item=node.item;
        if(item.raw[1] || item.raw[2] || item.raw[3])return reject(Rejection::Padding);
        if(item.flags>1)return reject(Rejection::Flags); // EQ, literal, optional OR only.
        if((item.comparisonBits&0x7F800000)==0x7F800000)return reject(Rejection::Literal);
        if(item.functionStorage!=72)return reject(Rejection::Function);
        ActorIdentity parameter;
        if(item.parameter2Raw || !resolve || !resolve(context,item.parameter1Raw,false,parameter) ||
            !ValidForm(parameter.form,0x2A) || parameter.form.address!=item.parameter1Raw)
            return reject(Rejection::Parameter);
        if(item.runOn>2)return reject(Rejection::RunOn);
        auto actor=item.runOn==1?target:subject;
        if(item.runOn==2) {
            if(!resolve(context,item.reference,true,actor) || !ValidActor(actor) || actor.form.address!=item.reference)
                return reject(Rejection::Reference);
        } else if(item.reference)return reject(Rejection::Reference);
        out.rows[out.count++]={node.address,node.itemAddress,item,actor,parameter.form};
    }
    if(out.rows[out.count-1].source.orWithNext)return reject(Rejection::TrailingOr);
    out.failedRow=UINT32_MAX;return out;
}
inline bool Complete(const pilot::ItemSample& sample,const Binding& binding) noexcept {
    if(binding.rejection!=Rejection::None || sample.sourceRow>=binding.count ||
        !sample.entered || !sample.returned || !sample.rawBeforeReadable || !sample.rawAfterReadable ||
        sample.subject!=binding.subject.form.address || sample.target!=binding.target.form.address)return false;
    const auto& row=binding.rows[sample.sourceRow];const auto& eval=sample.eval;
    const auto& actor=row.evaluatedActor;const auto& parameter=row.parameter;
    return sample.item==row.item && sample.before==row.source.raw && sample.after==row.source.raw &&
        eval.entered && eval.returned && eval.handlerReturned && eval.resultReadable &&
        (eval.resultBits&0x7FF0000000000000ull)!=0x7FF0000000000000ull && eval.identityBefore && eval.identityStable &&
        eval.subject==actor.form.address && eval.subjectId==actor.form.id && eval.subjectType==actor.form.type &&
        eval.subjectBaseAddress==actor.base.address && eval.subjectBaseId==actor.base.id && eval.subjectBaseType==actor.base.type &&
        eval.parameter1==parameter.address && eval.parameter1Id==parameter.id && eval.parameter1Type==parameter.type &&
        eval.parameter2==0 && sample.comparison.entered && sample.comparison.returned &&
        sample.comparison.operation==0 && sample.comparison.comparisonBits==row.source.comparisonBits;
}
} // namespace bmt::ctda::quest
