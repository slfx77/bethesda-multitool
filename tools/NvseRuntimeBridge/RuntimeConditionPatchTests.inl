// Included by the standalone draft fixture only. Fake byte IO; no live code writes.
enum class PatchFault { None, Ownership, Read, Overlap, WriteFirst, PartialSecond, PartialFourth,
    Verify, FlushOnce, RollbackWrite, ForeignWrite, Restore, ForeignRestore, LateOwnerChange, FlushAlways, PartialSixth, ForeignSixthByte, InvalidLength, UnusedTail, OverlapSixth };
struct PatchFixture {
    PatchFault fault=PatchFault::None;
    std::array<condition_patch::Span,condition_patch::Count> spans=ConditionPatchPlan(false);
    std::array<condition_patch::Bytes,condition_patch::Count> memory{};
    unsigned writes=0,flushes=0;bool injected=false,rollbackFailed=false;
    explicit PatchFixture(PatchFault value):fault(value) {
        if(value==PatchFault::Restore || value==PatchFault::ForeignRestore)spans=ConditionPatchPlan(true);
        for(size_t index=0;index<spans.size();++index)memory[index]=spans[index].before;
        if(value==PatchFault::Ownership || value==PatchFault::ForeignRestore)memory[2][1]=Foreign(2);
        if(value==PatchFault::Overlap)spans[1].address=spans[0].address+1;
        if(value==PatchFault::InvalidLength)spans[5].length=7;
        if(value==PatchFault::UnusedTail)spans[0].before[5]=1;
        if(value==PatchFault::OverlapSixth)spans[5].address=spans[4].address+5;
        if(value==PatchFault::ForeignSixthByte)memory[5][5]=0xCC;
    }
    size_t Index(UInt32 address) const noexcept {
        for(size_t index=0;index<spans.size();++index)if(spans[index].address==address)return index;
        return spans.size();
    }
    std::uint8_t Foreign(size_t index) const noexcept {
        std::uint8_t value=0;
        while(value==spans[index].before[1] || value==spans[index].after[1])++value;
        return value;
    }
    static bool Read(void* context,UInt32 address,condition_patch::Bytes& output,size_t length) noexcept {
        auto& self=*static_cast<PatchFixture*>(context);const auto index=self.Index(address);if(index==self.spans.size() || length!=self.spans[index].length)return false;
        if(self.fault==PatchFault::Read && !self.injected){self.injected=true;return false;}
        if(self.fault==PatchFault::Verify && self.writes==1 && !self.injected){self.injected=true;return false;}
        if(self.fault==PatchFault::LateOwnerChange && self.writes==1 && index==1 && !self.injected) {
            self.injected=true;self.memory[index][1]=self.Foreign(index);
        }
        output=self.memory[index];return true;
    }
    static bool Write(void* context,UInt32 address,const condition_patch::Bytes& bytes,size_t length) noexcept {
        auto& self=*static_cast<PatchFixture*>(context);const auto index=self.Index(address);if(index==self.spans.size() || length!=self.spans[index].length)return false;
        ++self.writes;
        const bool restoration=bytes==self.spans[index].before;
        if(self.fault==PatchFault::RollbackWrite && restoration && !self.rollbackFailed) {
            self.rollbackFailed=true;return false;
        }
        const bool failFirst=self.fault==PatchFault::WriteFirst && index==0;
        const bool failSecond=(self.fault==PatchFault::PartialSecond || self.fault==PatchFault::RollbackWrite ||
            self.fault==PatchFault::ForeignWrite) && index==1;
        const bool failFourth=self.fault==PatchFault::PartialFourth && index==3;
        const bool failSixth=self.fault==PatchFault::PartialSixth && index==5;
        if(!restoration && !self.injected && (failFirst || failSecond || failFourth || failSixth)) {
            self.injected=true;
            if(failSixth)self.memory[index][5]=bytes[5];
            else if(!failFirst)for(size_t byte=1;byte<bytes.size();++byte)if(bytes[byte]!=self.memory[index][byte]) {
                self.memory[index][byte]=bytes[byte];break;
            }
            if(self.fault==PatchFault::ForeignWrite)self.memory[index][1]=self.Foreign(index);
            return false;
        }
        self.memory[index]=bytes;return true;
    }
    static bool Flush(void* context,UInt32,size_t) noexcept {
        auto& self=*static_cast<PatchFixture*>(context);++self.flushes;
        return self.fault!=PatchFault::FlushAlways && !(self.fault==PatchFault::FlushOnce && self.flushes==1);
    }
    bool All(bool replacement) const noexcept {
        for(size_t index=0;index<spans.size();++index)if(memory[index]!=(replacement?spans[index].after:spans[index].before))return false;
        return true;
    }
};
void TestPatchTransaction() {
    for(const auto fault:{PatchFault::None,PatchFault::Ownership,PatchFault::Read,PatchFault::Overlap,
        PatchFault::WriteFirst,PatchFault::PartialSecond,PatchFault::PartialFourth,PatchFault::Verify,
        PatchFault::FlushOnce,PatchFault::RollbackWrite,PatchFault::ForeignWrite,PatchFault::Restore,
        PatchFault::ForeignRestore,PatchFault::LateOwnerChange,PatchFault::FlushAlways,
        PatchFault::PartialSixth,PatchFault::ForeignSixthByte,PatchFault::InvalidLength,PatchFault::UnusedTail,PatchFault::OverlapSixth}) {
        PatchFixture fixture(fault);const auto before=fixture.memory;
        const auto result=condition_patch::Apply(fixture.spans,{&fixture,PatchFixture::Read,PatchFixture::Write,PatchFixture::Flush});
        switch(fault) {
        case PatchFault::None:case PatchFault::Restore:
            Require(result.status==condition_patch::Status::Committed && fixture.All(true) && result.changedMask==63,"six-site commit incomplete");break;
        case PatchFault::Ownership:case PatchFault::ForeignRestore:case PatchFault::ForeignSixthByte:
            Require(result.status==condition_patch::Status::OwnershipMismatch && !fixture.writes && fixture.memory==before,"foreign hook overwritten");break;
        case PatchFault::Read:
            Require(result.status==condition_patch::Status::ReadFailed && !fixture.writes,"unreadable code was written");break;
        case PatchFault::Overlap:case PatchFault::InvalidLength:case PatchFault::UnusedTail:case PatchFault::OverlapSixth:
            Require(result.status==condition_patch::Status::InvalidPlan && !fixture.writes,"overlapping plan admitted");break;
        case PatchFault::WriteFirst:case PatchFault::PartialSecond:case PatchFault::PartialFourth:case PatchFault::PartialSixth:
        case PatchFault::Verify:case PatchFault::FlushOnce:
            Require(result.status==condition_patch::Status::RolledBack && result.bytesRestored && fixture.All(false),"failed transaction not rolled back");break;
        case PatchFault::RollbackWrite:
            Require(result.status==condition_patch::Status::RollbackFailed && !result.bytesRestored && !fixture.All(false),"failed rollback hidden");break;
        case PatchFault::ForeignWrite:case PatchFault::LateOwnerChange:
            Require(result.status==condition_patch::Status::RollbackFailed && fixture.memory[1][1]==fixture.Foreign(1),"later foreign bytes overwritten during rollback");break;
        case PatchFault::FlushAlways:
            Require(result.status==condition_patch::Status::RollbackFailed && result.bytesRestored && !result.cacheFlushed && fixture.All(false),"cache flush failure hidden");break;
        }
    }
}
void TestThreadInventory() {
    const auto spans=ConditionPatchPlan(false);
    Require(ConditionInstructionInPlan(ConditionFrameSites[0]+5,spans),"sixth displaced byte escaped thread admission");
    Require(!ConditionInstructionInPlan(ConditionFrameSites[1]+6,spans),"resume address counted inside span");
    Require(!ConditionInstructionInPlan(ConditionCallSites[0]+5,spans),"CALL neighbor counted inside span");
    ConditionThreadSet set;set.count=2;set.ids[0]=11;set.ids[1]=12;
    constexpr size_t Size=sizeof(SYSTEM_PROCESS_INFORMATION)+4*sizeof(SYSTEM_THREAD_INFORMATION);
    for(unsigned fault=0;fault<5;++fault) {
        alignas(SYSTEM_PROCESS_INFORMATION) std::array<std::uint8_t,Size> bytes{};
        auto* process=reinterpret_cast<SYSTEM_PROCESS_INFORMATION*>(bytes.data());
        auto* threads=reinterpret_cast<SYSTEM_THREAD_INFORMATION*>(bytes.data()+sizeof(*process));
        process->UniqueProcessId=reinterpret_cast<HANDLE>(100);process->NumberOfThreads=3;
        for(unsigned index=0;index<4;++index)threads[index].ClientId={reinterpret_cast<HANDLE>(100),reinterpret_cast<HANDLE>(10+index)};
        ULONG returned=Size;
        if(fault==1)process->NumberOfThreads=4; // Thread created after the pre-suspension inventory.
        if(fault==2)threads[2].ClientId.UniqueThread=reinterpret_cast<HANDLE>(99); // Same count, different thread.
        if(fault==3)threads[2].ClientId.UniqueThread=threads[1].ClientId.UniqueThread;
        if(fault==4)returned=sizeof(*process)+sizeof(*threads);
        Require(ConditionMatchNativeThreads(bytes.data(),returned,set,100,10)==(fault==0),"post-suspension thread-set mismatch admitted");
    }
    // One real OS read validates the installed SDK layout. This does not suspend,
    // write code, install hooks or make an engine call; it is not a quiescence test.
    ConditionNativeThreads native;ConditionThreadSet actual;
    const bool prepared=ConditionPrepareNativeThreads(native) && ConditionPrepareThreads(actual);
    const bool matched=prepared && ConditionVerifyFrozenThreads(native,actual);
    ConditionCloseThreads(actual);if(native.buffer)VirtualFree(native.buffer,0,MEM_RELEASE);
    Require(matched,"current-process native thread inventory/layout unavailable");
}
