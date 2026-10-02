// Parameterized flow/decision controls and real x86 wrapper forwarding, no game.
void TestCriticalInvocationFlow() {
    using namespace bmt::critical;
    for(unsigned fault=0;fault<12;++fault) {
        Sample sample;sample.controlWord=0x007F;
        for(unsigned point=0;point<=8;++point) {
            if(fault==point+1)continue;
            Next(sample,static_cast<Point>(point));
        }
        sample.randomObserved=true;sample.thresholdObserved=true;sample.random=123456;sample.remainder=456;
        if(fault==10)++sample.remainder;
        if(fault==11)sample.controlWord=0x027F;
        Check(Complete(sample)==(fault==0),"critical missing/reordered stage was accepted");
    }
    Sample early;early.controlWord=0x007F;Next(early,Point::Begin);Next(early,Point::End);
    Check(Complete(early)&&early.early,"critical early exit was not distinguished");
    Next(early,Point::End);Check(!Complete(early),"duplicate critical terminal accepted");
    struct Case {std::int32_t threshold;std::uint32_t remainder;bool expected;};
    for(const auto& row:std::array<Case,8>{{{-1,0,false},{0,0,false},{1,0,true},{1,1,false},
        {999,998,true},{999,999,false},{1000,999,true},{INT32_MIN,0,false}}}) {
        Sample sample;memcpy(&sample.threshold,&row.threshold,4);sample.remainder=row.remainder;
        Check(Comparison(sample)==row.expected,"signed critical threshold comparison changed");
    }
    Sample anchor;anchor.frame=0x8000;anchor.stackLow=0x7000;anchor.stackHigh=0x9000;
    for(unsigned fault=0;fault<6;++fault) {
        auto sample=anchor;UInt32 frame=0x8000,esp=0x7FC0;
        if(fault==1)++frame;if(fault==2)--esp;if(fault==3)esp=0x7FC5;
        if(fault==4)sample.stackLow=0x7FC1;if(fault==5)sample.stackHigh=0x8007;
        Check(Frame(sample,frame,esp)==(fault==0),"critical frame bounds mismatch");
    }
    // Validate the actual two six-span plans, including zero unused bytes and inverse restoration.
    for(bool restoring:{false,true}) {
        const auto damagePlan=DamagePlan(restoring),criticalPlan=CriticalPatchPlan(restoring);
        Check(condition_patch::Valid(damagePlan),"actual damage patch plan invalid");
        Check(condition_patch::Valid(criticalPlan),"actual critical patch plan invalid");
        const auto reverseDamage=DamagePlan(!restoring),reverseCritical=CriticalPatchPlan(!restoring);
        for(size_t i=0;i<damagePlan.size();++i) {
            Check(damagePlan[i].address==reverseDamage[i].address && damagePlan[i].length==reverseDamage[i].length &&
                damagePlan[i].before==reverseDamage[i].after && damagePlan[i].after==reverseDamage[i].before,
                "damage restoration is not the inverse owned plan");
            Check(criticalPlan[i].address==reverseCritical[i].address && criticalPlan[i].length==reverseCritical[i].length &&
                criticalPlan[i].before==reverseCritical[i].after && criticalPlan[i].after==reverseCritical[i].before,
                "critical restoration is not the inverse owned plan");
        }
    }
    const auto wasInstalled=g_criticalInvocationInstalled.load();
    for(unsigned mode=0;mode<9;++mode) {
        constexpr UInt32 call=0x009B5619,base=call-6;
        std::array<std::uint8_t,17> canonical{};canonical.fill(0xA5);
        memcpy(canonical.data()+6,critical_invocation::Spans[0].bytes.data(),5);
        auto actual=canonical;
        g_criticalInvocationInstalled=mode>=1 && mode<=4;
        if(mode==1 || mode==3 || mode==4 || mode==5) {
            const auto owned=CriticalOwned(0);memcpy(actual.data()+6,owned.data(),5);
        }
        if(mode==3)actual[6]=0xE9; // Same displacement, foreign instruction kind.
        if(mode==4 || mode==6)actual[7]^=1; // A one-byte foreign operand is not normalized.
        const auto before=actual;
        const auto address=mode==8?call+10:base;
        const auto length=mode==7?10u:static_cast<unsigned>(actual.size());
        const bool expected=mode==0 || mode==1 || mode==8;
        Check(CriticalNormalizeDamageProof(address,actual.data(),length)==expected,"critical ownership normalization mismatch");
        Check(actual==(expected && mode!=8?canonical:before),"critical normalization changed unrelated or rejected bytes");
    }
    g_criticalInvocationInstalled=wasInstalled;
    const auto oldSlots=g_criticalSlots;const auto oldCapture=g_criticalCaptureSeen,oldConnection=g_criticalConnectionSeen;
    const auto oldAbandoned=g_criticalInvocationAbandoned.load(),oldTotal=g_criticalInvocationAbandonedTotal.load();
    g_criticalSlots={};g_criticalCaptureSeen=0;g_criticalConnectionSeen=0;
    DamageHitScope owner;owner.capture=1;owner.connection=1;owner.invocation=1;
    Check(CriticalFind(owner,true)!=nullptr,"critical initial slot unavailable");
    ++owner.invocation;Check(CriticalFind(owner,true)!=nullptr && g_criticalInvocationAbandoned==1,"same capture omission hidden");
    ++owner.capture;++owner.invocation;
    Check(CriticalFind(owner,true)!=nullptr && g_criticalInvocationAbandoned==0,"old capture omission poisoned next capture");
    g_criticalSlots=oldSlots;g_criticalCaptureSeen=oldCapture;g_criticalConnectionSeen=oldConnection;
    g_criticalInvocationAbandoned=oldAbandoned;g_criticalInvocationAbandonedTotal=oldTotal;
}

alignas(16) std::uint8_t CriticalTestFxBefore[512]{},CriticalTestFxEngine[512]{},CriticalTestFxAfter[512]{};
UInt32 CriticalTestInput[9]{},CriticalTestExpected[9]{},CriticalTestActual[9]{};
UInt32 CriticalTestEntry=0,CriticalTestCalls=0,CriticalTestNonvolatile[3]{};
#define CRITICAL_TEST_SNAPSHOT(Registers,Fx) \
    __asm { pushfd } __asm { pushad } __asm { fxsave Fx } \
    __asm { mov esi,esp } __asm { lea edi,Registers } __asm { mov ecx,9 } __asm { cld } \
    __asm { rep movsd } __asm { popad } __asm { popfd }
__declspec(naked) void CriticalTestEngine() {
    CRITICAL_TEST_SNAPSHOT(CriticalTestInput,CriticalTestFxEngine)
    __asm { inc CriticalTestCalls }
    __asm { push 77665544h } __asm { call SetLastError }
    __asm { fstp st(0) } // An original floating-point side effect must occur once.
    __asm { mov eax,0FEDCBA98h } __asm { mov edx,87654321h } __asm { mov ecx,76543210h }
    __asm { push 647h } __asm { popfd }
    CRITICAL_TEST_SNAPSHOT(CriticalTestExpected,CriticalTestFxEngine)
    __asm { ret }
}
__declspec(naked) void CriticalTestInvoke() {
    __asm { push ebp } __asm { mov ebp,esp }
    __asm { mov CriticalTestNonvolatile,ebx } __asm { mov CriticalTestNonvolatile+4,esi } __asm { mov CriticalTestNonvolatile+8,edi }
    __asm { fxrstor CriticalTestFxBefore }
    __asm { mov eax,11223344h } __asm { mov ebx,22334455h } __asm { mov ecx,33445566h }
    __asm { mov edx,44556677h } __asm { mov esi,55667788h } __asm { mov edi,66778899h }
    __asm { push 647h } __asm { popfd }
    __asm { call dword ptr [CriticalTestEntry] }
    CRITICAL_TEST_SNAPSHOT(CriticalTestActual,CriticalTestFxAfter)
    __asm { cld }
    __asm { mov ebx,CriticalTestNonvolatile } __asm { mov esi,CriticalTestNonvolatile+4 } __asm { mov edi,CriticalTestNonvolatile+8 }
    __asm { mov esp,ebp } __asm { pop ebp } __asm { ret }
}
#undef CRITICAL_TEST_SNAPSHOT
void TestCriticalCallAbi() {
    CommandFloatingGuard floating;
    const auto a=CriticalOriginalCaller,b=CriticalOriginalRandom,c=CriticalOriginalThreshold;
    CriticalOriginalCaller=CriticalOriginalRandom=CriticalOriginalThreshold=reinterpret_cast<UInt32>(&CriticalTestEngine);
    const auto priorDepth=g_damageDepth;g_damageDepth=0;
    for(const auto index:{0u,4u,5u}) {
        CommandObserverFloatingEnvironment();
        __asm { fld1 } __asm { fldpi } __asm { fxsave CriticalTestFxBefore }
        const std::uint16_t control=0x047E;const UInt32 mxcsr=0x5F00;
        memcpy(CriticalTestFxBefore,&control,2);memcpy(CriticalTestFxBefore+24,&mxcsr,4);
        CriticalTestEntry=CriticalWrapper(index);CriticalTestCalls=0;
        CriticalTestInvoke();const auto error=GetLastError();CommandObserverFloatingEnvironment();
        Check(CriticalTestCalls==1 && error==0x77665544,"critical wrapper repeated original or changed LastError");
        Check(CriticalTestInput[7]==0x11223344 && CriticalTestInput[6]==0x33445566 && CriticalTestInput[5]==0x44556677,
            "critical wrapper changed original incoming volatile registers");
        for(const auto reg:{0u,1u,2u,4u,5u,6u,7u})Check(CriticalTestActual[reg]==CriticalTestExpected[reg],"critical return GPR changed");
        Check((CriticalTestActual[8]&0xCD5)==(CriticalTestExpected[8]&0xCD5),"critical return flags changed");
        Check(!memcmp(CriticalTestFxEngine,CriticalTestFxAfter,28) &&
            !memcmp(CriticalTestFxEngine+160,CriticalTestFxAfter+160,128),"critical return FP/XMM state changed");
        for(unsigned reg=0;reg<8;++reg)Check(!memcmp(CriticalTestFxEngine+32+16*reg,CriticalTestFxAfter+32+16*reg,10),"critical return x87 data changed");
    }
    CriticalOriginalCaller=a;CriticalOriginalRandom=b;CriticalOriginalThreshold=c;g_damageDepth=priorDepth;
}

UInt32 CriticalProbeTestSite=0,CriticalProbeTestEntry=0,CriticalProbeTestEnabled=0,CriticalProbeTestLocal=0;
__declspec(naked) void CriticalProbeTestResume() {
    __asm { pushfd } __asm { pushad } __asm { fxsave CriticalTestFxAfter }
    __asm { mov esi,esp } __asm { lea edi,CriticalTestActual } __asm { mov ecx,9 } __asm { cld } __asm { rep movsd }
    __asm { popad } __asm { popfd }
    __asm { mov eax,[ebp-0Ch] } __asm { mov CriticalProbeTestLocal,eax }
    __asm { cld }
    __asm { mov ebx,CriticalTestNonvolatile } __asm { mov esi,CriticalTestNonvolatile+4 } __asm { mov edi,CriticalTestNonvolatile+8 }
    __asm { mov esp,ebp } __asm { pop ebp } __asm { ret }
}
__declspec(naked) void CriticalProbeTestInvoke() {
    __asm { push ebp } __asm { mov ebp,esp } __asm { sub esp,3Ch }
    __asm { mov CriticalTestNonvolatile,ebx } __asm { mov CriticalTestNonvolatile+4,esi } __asm { mov CriticalTestNonvolatile+8,edi }
    __asm { mov dword ptr [ebp-1Ch],12345678h } __asm { mov dword ptr [ebp-0Ch],87654321h }
    __asm { cmp CriticalProbeTestSite,1 } __asm { je weaponArgs }
    __asm { push 0 } __asm { push 0 } __asm { push 0 }
    __asm { weaponArgs: } __asm { push 0 } __asm { push 0 }
    __asm { fxrstor CriticalTestFxBefore }
    __asm { mov eax,11223344h } __asm { mov ebx,22334455h } __asm { mov ecx,33445566h }
    __asm { mov edx,44556677h } __asm { mov esi,55667788h } __asm { mov edi,66778899h }
    __asm { push 647h } __asm { popfd }
    __asm { cmp CriticalProbeTestEnabled,0 } __asm { je original }
    __asm { jmp dword ptr [CriticalProbeTestEntry] }
    __asm { original: } __asm { cmp CriticalProbeTestSite,1 } __asm { je originalWeapon }
    __asm { cmp CriticalProbeTestSite,2 } __asm { je originalSource }
    __asm { add esp,14h } __asm { mov edx,[ebp-1Ch] } __asm { jmp CriticalProbeTestResume }
    __asm { originalSource: } __asm { add esp,14h } __asm { mov ecx,[ebp-1Ch] } __asm { jmp CriticalProbeTestResume }
    __asm { originalWeapon: } __asm { add esp,8 } __asm { fstp dword ptr [ebp-0Ch] } __asm { jmp CriticalProbeTestResume }
}
void TestCriticalProbeAbi() {
    CommandFloatingGuard floating;
    const auto a=CriticalResumeWeapon,b=CriticalResumeSource,c=CriticalResumeTarget;
    CriticalResumeWeapon=CriticalResumeSource=CriticalResumeTarget=reinterpret_cast<UInt32>(&CriticalProbeTestResume);
    const auto priorDepth=g_damageDepth;g_damageDepth=0;
    for(unsigned site=1;site<=3;++site) {
        CommandObserverFloatingEnvironment();
        __asm { fld1 } __asm { fldpi } __asm { fxsave CriticalTestFxBefore }
        CriticalProbeTestSite=site;CriticalProbeTestEntry=CriticalWrapper(site);CriticalProbeTestEnabled=0;
        SetLastError(0x19384756);CriticalProbeTestInvoke();CommandObserverFloatingEnvironment();
        std::array<UInt32,9> expected{};memcpy(expected.data(),CriticalTestActual,sizeof(CriticalTestActual));
        std::array<std::uint8_t,512> fx{};memcpy(fx.data(),CriticalTestFxAfter,fx.size());const auto local=CriticalProbeTestLocal;
        CriticalProbeTestEnabled=1;SetLastError(0x19384756);CriticalProbeTestInvoke();
        const auto error=GetLastError();CommandObserverFloatingEnvironment();
        Check(error==0x19384756 && CriticalProbeTestLocal==local,"critical probe changed local store/LastError");
        for(const auto reg:{0u,1u,2u,3u,4u,5u,6u,7u})Check(CriticalTestActual[reg]==expected[reg],"critical probe displaced GPR/stack mismatch");
        Check((CriticalTestActual[8]&0xCD5)==(expected[8]&0xCD5),"critical probe displaced flags mismatch");
        // FIP differs for the displaced FSTP; control/status/tag/XMM and data must match.
        Check(!memcmp(fx.data(),CriticalTestFxAfter,6) && !memcmp(fx.data()+24,CriticalTestFxAfter+24,4) &&
            !memcmp(fx.data()+160,CriticalTestFxAfter+160,128),"critical probe FP state mismatch");
        for(unsigned reg=0;reg<8;++reg)Check(!memcmp(fx.data()+32+16*reg,CriticalTestFxAfter+32+16*reg,10),"critical probe x87 data mismatch");
    }
    CriticalResumeWeapon=a;CriticalResumeSource=b;CriticalResumeTarget=c;g_damageDepth=priorDepth;
}
