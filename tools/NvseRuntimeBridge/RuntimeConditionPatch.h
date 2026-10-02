#pragma once

// Pure bounded exact-span transaction. Caller owns executable-page/thread admission.
// No allocation, locks, OS APIs or exception handling in the frozen region.
#include <array>
#include <cstddef>
#include <cstdint>

namespace bmt::ctda::patch {
constexpr std::size_t Count = 6, Width = 6;
using Bytes = std::array<std::uint8_t, Width>;
struct Span { std::uint32_t address = 0; std::size_t length=0; Bytes before{}, after{}; };
enum class Status { NotRun, InvalidPlan, OwnershipMismatch, ReadFailed, WriteFailed,
    VerifyFailed, FlushFailed, Committed, RolledBack, RollbackFailed };
struct Result {
    Status status = Status::NotRun, failure = Status::NotRun;
    std::uint32_t attemptedMask = 0, changedMask = 0, restoredMask = 0, conflictMask = 0;
    bool bytesRestored = false, cacheFlushed = false;
};
using Read = bool (*)(void*,std::uint32_t,Bytes&,std::size_t) noexcept;
using Write = bool (*)(void*,std::uint32_t,const Bytes&,std::size_t) noexcept;
using Flush = bool (*)(void*,std::uint32_t,std::size_t) noexcept;
struct Io { void* context = nullptr; Read read = nullptr; Write write = nullptr; Flush flush = nullptr; };

inline bool Valid(const std::array<Span,Count>& spans) noexcept {
    for(std::size_t index=0;index<Count;++index) {
        const auto& span=spans[index];
        if(!span.address || !span.length || span.length>Width || span.address>UINT32_MAX-span.length || span.before==span.after)
            return false;
        for(std::size_t other=0;other<index;++other)
            if(span.address<spans[other].address+spans[other].length && spans[other].address<span.address+span.length)return false;
        for(std::size_t byte=span.length;byte<Width;++byte)if(span.before[byte] || span.after[byte])return false;
    }
    return true;
}
inline bool FlushAll(const std::array<Span,Count>& spans,const Io& io) noexcept {
    bool result=true;
    for(const auto& span:spans)if(!io.flush(io.context,span.address,span.length))result=false;
    return result;
}
inline Result Rollback(const std::array<Span,Count>& spans,const Io& io,Result result) noexcept {
    bool restored=true;
    for(std::size_t reverse=Count;reverse>0;--reverse) {
        const auto index=reverse-1,bit=std::uint32_t(1)<<index;
        if(!(result.attemptedMask&bit))continue;
        Bytes actual{};
        if(!io.read(io.context,spans[index].address,actual,spans[index].length)) {restored=false;continue;}
        if(actual==spans[index].before) {result.restoredMask|=bit;continue;}
        // A failed Write may have changed only a prefix. Restore only combinations
        // of the exact admitted old/new bytes; never overwrite a later foreign hook.
        bool owned=true;
        for(std::size_t byte=0;byte<spans[index].length;++byte)
            if(actual[byte]!=spans[index].before[byte] && actual[byte]!=spans[index].after[byte])owned=false;
        if(!owned) {result.conflictMask|=bit;restored=false;continue;}
        if(!io.write(io.context,spans[index].address,spans[index].before,spans[index].length) ||
            !io.read(io.context,spans[index].address,actual,spans[index].length) || actual!=spans[index].before) {restored=false;continue;}
        result.restoredMask|=bit;
    }
    // Also recheck untouched spans; a changed neighbor invalidates all-or-none proof.
    for(std::size_t index=0;index<Count;++index) {
        Bytes actual{};
        if(!io.read(io.context,spans[index].address,actual,spans[index].length) || actual!=spans[index].before)restored=false;
    }
    result.bytesRestored=restored;result.cacheFlushed=FlushAll(spans,io);
    result.status=restored && result.cacheFlushed?Status::RolledBack:Status::RollbackFailed;
    return result;
}
inline Result Apply(const std::array<Span,Count>& spans,const Io& io) noexcept {
    Result result;
    if(!Valid(spans) || !io.read || !io.write || !io.flush) {result.status=Status::InvalidPlan;return result;}
    for(const auto& span:spans) {
        Bytes actual{};
        if(!io.read(io.context,span.address,actual,span.length)) {result.status=Status::ReadFailed;return result;}
        if(actual!=span.before) {result.status=Status::OwnershipMismatch;return result;}
    }
    for(std::size_t index=0;index<Count;++index) {
        const auto& span=spans[index];Bytes actual{};
        if(!io.read(io.context,span.address,actual,span.length) || actual!=span.before) {
            result.failure=Status::OwnershipMismatch;return Rollback(spans,io,result);
        }
        result.attemptedMask|=std::uint32_t(1)<<index;
        if(!io.write(io.context,span.address,span.after,span.length)) {result.failure=Status::WriteFailed;return Rollback(spans,io,result);}
        result.changedMask|=std::uint32_t(1)<<index;
        if(!io.read(io.context,span.address,actual,span.length) || actual!=span.after) {
            result.failure=Status::VerifyFailed;return Rollback(spans,io,result);
        }
    }
    for(const auto& span:spans) {
        Bytes actual{};
        if(!io.read(io.context,span.address,actual,span.length) || actual!=span.after) {
            result.failure=Status::VerifyFailed;return Rollback(spans,io,result);
        }
    }
    if(!FlushAll(spans,io)) {result.failure=Status::FlushFailed;return Rollback(spans,io,result);}
    result.cacheFlushed=true;result.status=Status::Committed;return result;
}
} // namespace bmt::ctda::patch
