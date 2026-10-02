// Real x86 naked-probe forwarding in this fixture process; no game addresses executed.
void FrameWord(condition_frame::Sample& row,int offset,UInt32 value) {
    memcpy(row.raw.data()+condition_frame::Locals+offset,&value,4);
}
condition_frame::Sample ValidFrame(const condition_frame::Anchor& anchor) {
    condition_frame::Sample row;row.ebp=anchor.ebp;row.esp=anchor.ebp-0x3C;
    FrameWord(row,4,anchor.returnAddress);FrameWord(row,-0x20,anchor.head);
    FrameWord(row,8,anchor.subject);FrameWord(row,12,anchor.target);FrameWord(row,16,anchor.auxiliary);
    FrameWord(row,-8,0x1054);FrameWord(row,-0x18,0x2000);row.raw[0x3C+20]=anchor.mode;
    row.raw[0x3C-9]=1;return row;
}
void TestFrameAdmission() {
    const condition_frame::Anchor anchor{0x8000,0x1234,0x7000,0x9000,0x1054,0x5000,0x6000,0x8100,0};
    for(unsigned fault=0;fault<12;++fault) {
        auto a=anchor;auto row=ValidFrame(a);
        switch(fault) {
        case 1:a.stackLow=row.ebp;break;
        case 2:row.ebp+=4;break;
        case 3:row.esp-=4;break;
        case 4:FrameWord(row,4,0x4321);break;
        case 5:FrameWord(row,-0x20,a.head+8);break;
        case 6:FrameWord(row,8,a.target);break;
        case 7:FrameWord(row,12,a.subject);break; // Independently observed normalization is not relabeled.
        case 8:FrameWord(row,16,a.auxiliary+1);break;
        case 9:row.raw[0x50]=1;break;
        case 10:row.raw[0x33]=2;break;
        case 11:row.ebp=UINT32_MAX-4;break;
        }
        Require((condition_frame::Decode(a,row)==condition_frame::Failure::None)==(fault==0),"frame ownership gate failed");
    }
}
ConditionInvocation FrameScope() {
    g_capture=true;g_connected=true;g_currentRequest=801;g_captureGeneration=802;g_connectionGeneration=803;g_gameLoadEpoch=804;
    ConditionInvocation scope;scope.epoch=ConditionCurrentEpoch();scope.invocation=805;scope.before=Fixture();
    scope.frameAnchor={0,0,__readfsdword(8),__readfsdword(4),0x1054,0x5000,0x6000,0,0};return scope;
}
void TestFrameLifecycle() {
    for(unsigned fault=0;fault<10;++fault) {
        auto scope=FrameScope();alignas(16) std::array<std::uint8_t,0x70> storage{};bool auxiliary=false;
        scope.frameAnchor.ebp=reinterpret_cast<UInt32>(storage.data()+0x3C);scope.frameAnchor.returnAddress=0x1234;
        scope.frameAnchor.auxiliary=reinterpret_cast<UInt32>(&auxiliary);
        auto sample=ValidFrame(scope.frameAnchor);memcpy(storage.data(),sample.raw.data(),sample.raw.size());
        UInt32 saved[9]{};saved[2]=sample.ebp;saved[3]=sample.esp-4;
        g_conditionInvocation=&scope;
        switch(fault) {
        case 1:++g_captureGeneration;break;
        case 2:++g_connectionGeneration;break;
        case 3:++g_gameLoadEpoch;break;
        case 4:++g_currentRequest;break;
        case 5:g_capture=false;break;
        case 6:g_connected=false;break;
        case 7:++scope.epoch.thread;break;
        case 8:g_conditionInvocation=nullptr;break;
        case 9:scope.frames.count=scope.frames.rows.size();break;
        }
        ConditionFrameObserve(ConditionFrameSites[0],saved);
        if(!fault)Require(scope.frames.count==1 && scope.frames.rows[0].associated,"owned frame not observed");
        else if(fault==9)Require(scope.frames.overflow,"frame overflow omitted");
        else Require(scope.frames.count==0,"stale frame entered new request");
        g_conditionInvocation=nullptr;
    }
}
void TestFrameSequence() {
    for(unsigned fault=0;fault<9;++fault) {
        auto scope=FrameScope();scope.observations.count=3;
        for(UInt32 index=0;index<3;++index) {
            auto& item=scope.observations.items[index];item.sourceRow=index;item.item=scope.before.nodes[index].itemAddress;
            auto* row=scope.frames.Begin();row->site=ConditionFrameSites[0];row->sourceRow=index;
            row->node=scope.before.nodes[index].address;row->item=item.item;row->associated=true;
            row->itemCalls=index+1;row->aggregate=1;row->group=index?1:0;row->open=index?0:1;
        }
        auto* final=scope.frames.Begin();*final=scope.frames.rows[2];final->site=ConditionFrameSites[1];final->node=0;
        if(fault==1)scope.frames.rows[1].sourceRow=2;
        if(fault==2)scope.frames.count=3;
        if(fault==3)scope.frames.rows[1].associated=false;
        if(fault==4)scope.frames.rows[1].itemCalls=1; // Missing eval without native true-open group.
        if(fault==5)final->aggregate=0;
        if(fault==6)scope.frames.overflow=true;
        if(fault==7) { // Complete native true-open group permits exactly one observed skip.
            scope.frames.rows[0].group=1;scope.frames.rows[1].itemCalls=1;scope.frames.rows[2].itemCalls=2;final->itemCalls=2;
            scope.observations.items[1]=scope.observations.items[2];scope.observations.count=2;
        }
        if(fault==8) { // Actual false group terminates with the current iterator, before row2.
            scope.frames.rows[1].aggregate=0;scope.frames.rows[1].group=0;
            scope.frames.rows[2]=scope.frames.rows[1];scope.frames.rows[2].site=ConditionFrameSites[1];scope.frames.count=3;
            scope.observations.count=2;
        }
        Require(ConditionFrameCoverage(scope,fault!=8)==(fault==0 || fault==7 || fault==8),"native step coverage inferred from missing events");
    }
}

alignas(16) unsigned char FrameFxBefore[512]{},FrameFxAfter[512]{};
UInt32 FrameRegisters[9]{},FrameSavedNonvolatile[3]{},FrameTestEntry=0,FrameTestFlags=0;
std::array<std::uint8_t,0x3C> FrameLocalBytes{};
__declspec(naked) bool __fastcall FrameFakeList(void*,void*,TESObjectREFR*,TESObjectREFR*,bool*,bool) {
    __asm {
        push ebp
        mov ebp,esp
        sub esp,3Ch
        mov FrameSavedNonvolatile,ebx
        mov FrameSavedNonvolatile+4,esi
        mov FrameSavedNonvolatile+8,edi
        lea edi,[ebp-3Ch]
        lea esi,FrameLocalBytes
        mov ecx,15
        cld
        rep movsd
        fxrstor FrameFxBefore
        push FrameTestFlags
        popfd
        mov eax,11223344h
        mov ebx,22334455h
        mov ecx,33445566h
        mov edx,44556677h
        mov esi,55667788h
        mov edi,66778899h
        jmp dword ptr [FrameTestEntry]
    }
}
__declspec(naked) void FrameFakeResume() {
    __asm {
        pushfd
        pushad
        fxsave FrameFxAfter
        mov esi,esp
        lea edi,FrameRegisters
        mov ecx,9
        cld
        rep movsd
        popad
        popfd
        cld
        mov ebx,FrameSavedNonvolatile
        mov esi,FrameSavedNonvolatile+4
        mov edi,FrameSavedNonvolatile+8
        mov al,byte ptr [ebp-9]
        mov esp,ebp
        pop ebp
        ret 10h
    }
}
void TestFrameAbi() {
    CommandFloatingGuard outer;
    const auto old0=ConditionFrameResume0,old1=ConditionFrameResume1;
    ConditionFrameResume0=ConditionFrameResume1=reinterpret_cast<UInt32>(&FrameFakeResume);
    for(unsigned state=0;state<4;++state)for(unsigned site=0;site<2;++site) {
        CommandObserverFloatingEnvironment();auto scope=FrameScope();bool auxiliary=false;
        scope.frameAnchor.auxiliary=reinterpret_cast<UInt32>(&auxiliary);
        condition_frame::Sample row=ValidFrame(scope.frameAnchor);row.raw[0x3C-9]=state&1;
        memcpy(FrameLocalBytes.data(),row.raw.data(),FrameLocalBytes.size());
        FrameTestEntry=ConditionFrameWrapper(site);FrameTestFlags=0x647; // CF/DF set, ZF set; TEST must replace arithmetic flags.
        memset(FrameFxBefore,0,sizeof(FrameFxBefore));memset(FrameFxAfter,0,sizeof(FrameFxAfter));
        __asm { fld1 }
        __asm { fldpi }
        __asm { movups xmm0,FrameLocalBytes }
        __asm { movups xmm1,FrameLocalBytes+4 }
        __asm { movups xmm2,FrameLocalBytes+8 }
        __asm { movups xmm3,FrameLocalBytes+12 }
        __asm { movups xmm4,FrameLocalBytes+16 }
        __asm { movups xmm5,FrameLocalBytes+20 }
        __asm { movups xmm6,FrameLocalBytes+24 }
        __asm { movups xmm7,FrameLocalBytes+28 }
        __asm { fxsave FrameFxBefore }
        // Unmasked x87/SSE invalid exceptions and nondefault rounding; no unsafe arithmetic occurs.
        const std::uint16_t control=0x047E;const UInt32 mxcsr=0x5F00;
        memcpy(FrameFxBefore,&control,2);memcpy(FrameFxBefore+24,&mxcsr,4);
        g_conditionInvocation=state==2?nullptr:&scope;if(state==3)++g_captureGeneration;
        SetLastError(0x77665544);
        const bool value=ConditionInvokeList(reinterpret_cast<ConditionListFunction>(&FrameFakeList),reinterpret_cast<void*>(scope.frameAnchor.head),
            reinterpret_cast<TESObjectREFR*>(scope.frameAnchor.subject),reinterpret_cast<TESObjectREFR*>(scope.frameAnchor.target),&auxiliary,false,&scope.frameAnchor);
        const DWORD error=GetLastError();CommandObserverFloatingEnvironment();g_conditionInvocation=nullptr;
        Require(value==bool(state&1) && error==0x77665544,"probe changed native result/LastError");
        Require(FrameRegisters[0]==0x66778899 && FrameRegisters[1]==0x55667788 && FrameRegisters[2]==scope.frameAnchor.ebp &&
            FrameRegisters[3]+4==scope.frameAnchor.ebp-0x3C && FrameRegisters[4]==0x22334455 && FrameRegisters[5]==(state&1) &&
            FrameRegisters[6]==0x33445566 && FrameRegisters[7]==0x11223344,"probe clobbered GPR/stack or omitted MOVZX");
        constexpr UInt32 tested=0xCC5;const UInt32 expected=state&1?0x400:0x444;
        Require((FrameRegisters[8]&tested)==expected,"probe changed preserved flags or omitted TEST");
        Require(!memcmp(FrameFxBefore,FrameFxAfter,28) && !memcmp(FrameFxBefore+160,FrameFxAfter+160,128),"probe changed FP control/status or XMM registers");
        for(size_t index=0;index<8;++index)Require(!memcmp(FrameFxBefore+32+16*index,FrameFxAfter+32+16*index,10),"probe changed x87 registers");
        Require(scope.frames.count==(state<2?1u:0u),"probe stale/inactive observation leaked");
        if(state<2)Require(scope.frames.rows[0].associated,"real frame/return anchor rejected");
    }
    ConditionFrameResume0=old0;ConditionFrameResume1=old1;
}
