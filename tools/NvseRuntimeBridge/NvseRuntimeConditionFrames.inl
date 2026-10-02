// Only the two PC029-loaded six-byte spans. No engine calls or allocations in probes.
// Establish the exact impending engine frame and return address without an entry hook.
// cdecl(function,head,subject,target,aux,mode,anchor); engine thiscall pops its 16 bytes.
static_assert(offsetof(condition_frame::Anchor,ebp)==0 && offsetof(condition_frame::Anchor,returnAddress)==4);
__declspec(naked) bool __cdecl ConditionInvokeList(ConditionListFunction,void*,TESObjectREFR*,TESObjectREFR*,bool*,bool,
    condition_frame::Anchor*) {
    __asm {
        push ebp
        mov ebp,esp
        push dword ptr [ebp+28]
        push dword ptr [ebp+24]
        push dword ptr [ebp+20]
        push dword ptr [ebp+16]
        mov ecx,[ebp+12]
        mov eax,[ebp+32]
        lea edx,[esp-8]
        mov [eax],edx
        mov edx,offset returned
        mov [eax+4],edx
        call dword ptr [ebp+8]
    returned:
        mov esp,ebp
        pop ebp
        ret
    }
}

// pushad layout: EDI,ESI,EBP,ESP-before-pushad,EBX,EDX,ECX,EAX,EFLAGS.
// The original ESP is saved ESP + 4, accounting for the initial PUSHFD.
void __cdecl ConditionFrameObserve(UInt32 site,const UInt32* saved) noexcept {
    auto* scope=g_conditionInvocation;
    if(!scope || !ConditionAlive(*scope))return;
    auto* sample=scope->frames.Begin();if(!sample)return;
    sample->site=site;sample->ebp=saved[2];sample->esp=saved[3]+4;sample->flags=saved[8];
    sample->itemCalls=static_cast<UInt32>(scope->observations.count);
    sample->failure=condition_frame::Bounds(scope->frameAnchor,sample->ebp,sample->esp);
    if(sample->failure!=condition_frame::Failure::None)return;
    if(__readfsdword(4)!=scope->frameAnchor.stackHigh || __readfsdword(8)>sample->esp) {
        sample->failure=condition_frame::Failure::Bounds;return;
    }
    std::array<std::uint8_t,condition_frame::Bytes> again{};
    if(!ConditionRead(nullptr,sample->ebp-condition_frame::Locals,sample->raw.data(),sample->raw.size()) ||
        !ConditionRead(nullptr,scope->frameAnchor.auxiliary,&sample->outerAuxiliary,1) ||
        !ConditionRead(nullptr,sample->ebp-condition_frame::Locals,again.data(),again.size())) {
        sample->failure=condition_frame::Failure::Unreadable;return;
    }
    if(again!=sample->raw){sample->failure=condition_frame::Failure::Changed;return;}
    sample->failure=condition_frame::Decode(scope->frameAnchor,*sample);
    if(sample->failure!=condition_frame::Failure::None)return;
    if(scope->item || (site!=ConditionFrameSites[0] && site!=ConditionFrameSites[1])) {
        sample->failure=condition_frame::Failure::Sequence;return;
    }
    UInt32 row=0;bool found=false;
    for(const auto& node:scope->before.nodes)if(node.hasItem) {
        if(node.itemAddress==sample->item && (site==ConditionFrameSites[1] || node.address==sample->node)) {
            sample->sourceRow=row;found=true;break;
        }
        ++row;
    }
    // Final exit may retain a null iterator, but must retain the last physical item.
    if(!found){sample->failure=condition_frame::Failure::Node;return;}
    if(!ConditionAlive(*scope)){sample->failure=condition_frame::Failure::Changed;return;}
    sample->associated=true;
}

// Preserve original GPRs/EFLAGS first, then FXSAVE all x87/MMX/XMM/control state.
// Observation uses masked nearest rounding and clear DF; saved LastError is restored.
// The two displaced instructions run once AFTER complete restoration. TEST produces
// the actual outgoing flags; an indirect JMP preserves those flags and all GPRs.
#define CONDITION_FRAME_PROBE(Name,Site,Resume) \
__declspec(naked) void Name() { __asm { pushfd } __asm { pushad } \
    __asm { mov ebx,esp } __asm { sub esp,544 } __asm { and esp,0FFFFFFF0h } \
    __asm { fxsave [esp] } __asm { fninit } __asm { ldmxcsr [ConditionFrameNeutralMxcsr] } __asm { cld } \
    __asm { call GetLastError } __asm { mov [esp+512],eax } \
    __asm { push ebx } __asm { push Site } __asm { call ConditionFrameObserve } __asm { add esp,8 } \
    __asm { push dword ptr [esp+512] } __asm { call SetLastError } __asm { fxrstor [esp] } \
    __asm { mov esp,ebx } __asm { popad } __asm { popfd } \
    __asm { movzx edx,byte ptr [ebp-9] } __asm { test edx,edx } __asm { jmp dword ptr [Resume] } }
CONDITION_FRAME_PROBE(ConditionFrameHook0,00680EC4h,ConditionFrameResume0)
CONDITION_FRAME_PROBE(ConditionFrameHook1,00680F42h,ConditionFrameResume1)
#undef CONDITION_FRAME_PROBE

UInt32 ConditionFrameWrapper(size_t index) noexcept {
    return index?reinterpret_cast<UInt32>(&ConditionFrameHook1):reinterpret_cast<UInt32>(&ConditionFrameHook0);
}
std::array<std::uint8_t,6> ConditionOwnedFrame(size_t index) noexcept {
    std::array<std::uint8_t,6> bytes{{0xE9,0,0,0,0,0x90}};
    const UInt32 displacement=ConditionFrameWrapper(index)-(ConditionFrameSites[index]+5);
    memcpy(bytes.data()+1,&displacement,4);return bytes;
}
bool ConditionOwnsFrames() noexcept {
    for(size_t index=0;index<2;++index) {
        std::array<std::uint8_t,6> actual{};
        if(!ConditionRead(nullptr,ConditionFrameSites[index],actual.data(),actual.size()) || actual!=ConditionOwnedFrame(index))return false;
    }
    return true;
}

// Completeness is derived only after the owner/code snapshots and loss checks.
// Every encountered physical iteration has a step, followed by exactly one final.
bool ConditionFrameCoverage(const ConditionInvocation& scope,bool returned) noexcept {
    if(!ConditionFramesReviewed || scope.frames.overflow || scope.frames.unexpected || scope.frames.count<2 ||
        scope.observations.overflow || scope.observations.unexpected)return false;
    UInt32 calls=0,expectedRow=0;const condition_frame::Sample* previous=nullptr;
    for(size_t index=0;index<scope.frames.count;++index) {
        const auto& sample=scope.frames.rows[index];
        if(!sample.associated || sample.failure!=condition_frame::Failure::None)return false;
        const bool final=index+1==scope.frames.count;
        if(sample.site!=(final?ConditionFrameSites[1]:ConditionFrameSites[0]))return false;
        if(final) {
            if(!previous || sample.sourceRow!=previous->sourceRow || sample.item!=previous->item || sample.itemCalls!=calls ||
                bool(sample.aggregate)!=returned)return false;
            // A terminated iteration remains on its node; an exhausted list has null next.
            const bmt::ctda::Node* node=nullptr;
            for(const auto& candidate:scope.before.nodes)if(candidate.address==previous->node && candidate.itemAddress==previous->item)node=&candidate;
            if(!node)return false;
            const bool canContinue=previous->aggregate || previous->open || previous->outerAuxiliary;
            if(canContinue ? (node->next!=0 || sample.node!=0) : sample.node!=previous->node)return false;
            continue;
        }
        if(sample.sourceRow!=expectedRow++ || (previous && !(previous->aggregate || previous->open || previous->outerAuxiliary)))return false;
        if(sample.itemCalls==calls+1) {
            if(calls>=scope.observations.count || scope.observations.items[calls].sourceRow!=sample.sourceRow ||
                scope.observations.items[calls].item!=sample.item)return false;
            ++calls;
        } else if(sample.itemCalls==calls) {
            // In mode=false a complete prior open, true group proves the skip branch.
            if(!previous || !previous->open || !previous->group)return false;
        } else return false;
        previous=&sample;
    }
    return calls==scope.observations.count;
}
void ConditionEmitFrames(const ConditionInvocation& scope,const std::string& common,bool complete) {
    UInt32 calls=0;
    for(size_t index=0;index<scope.frames.count;++index) {
        const auto& row=scope.frames.rows[index];const bool final=row.site==ConditionFrameSites[1];
        const bool skipped=complete && !final && row.itemCalls==calls;
        Emit(final?"condition-list-final-state":"condition-list-step",scope.epoch.request,common+
            ",\"stepIndex\":"+std::to_string(index)+",\"siteAddress\":"+std::to_string(row.site)+
            ",\"frameAddress\":"+std::to_string(row.ebp)+",\"stackPointer\":"+std::to_string(row.esp)+
            ",\"sourceRow\":"+std::to_string(row.sourceRow)+",\"nodeAddress\":"+std::to_string(row.node)+
            ",\"itemAddress\":"+std::to_string(row.item)+",\"observedItemCalls\":"+std::to_string(row.itemCalls)+
            ",\"frameRawHex\":"+Quote(ActorHex(row.raw.data(),row.raw.size()))+
            ",\"nativeAggregateRaw\":"+std::to_string(row.aggregate)+",\"nativeGroupRaw\":"+std::to_string(row.group)+
            ",\"nativeGroupOpenRaw\":"+std::to_string(row.open)+",\"nativeItemAuxiliaryRaw\":"+std::to_string(row.itemAuxiliary)+
            ",\"nativeOuterAuxiliaryRaw\":"+std::to_string(row.outerAuxiliary)+
            ",\"frameAssociation\":"+Quote(row.associated?"observed":"unavailable")+
            ",\"frameFailureCode\":"+std::to_string(static_cast<unsigned>(row.failure))+
            ",\"stepCoverageComplete\":"+(complete?"true":"false")+
            ",\"evaluationStatus\":"+Quote(final?"final-state":!complete?"unavailable":skipped?"observed-or-skip":"observed-evaluation"),scope.epoch.capture);
        calls=row.itemCalls;
    }
}
