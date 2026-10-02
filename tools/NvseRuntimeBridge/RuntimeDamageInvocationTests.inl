// Synthetic native fixtures only; no process patch or game launch.
damage::Stage DamageValidStage() {
    damage::Stage s;s.entered=s.returned=s.settingsStable=s.weaponStable=true;
    s.entryControl=s.returnControl=0x007F;s.returnAddress=0x004BDF76;s.meleeCalls=s.limbCalls=1;
    memcpy(s.after.raw.data()+0x78,&s.returnAddress,4);
    s.arguments={{0x1000,0x2000,damage::Bits(1),damage::Bits(1),0,0,0x3000,1}};
    s.weaponBefore[4]=0x28;s.weaponBefore[0xF4]=1;
    const std::uint16_t base=8;const UInt32 skill=33;
    memcpy(s.weaponBefore.data()+0xA0,&base,2);memcpy(s.weaponBefore.data()+0x15C,&skill,4);
    s.weaponAfter=s.weaponBefore;
    auto local=[&](unsigned offset,UInt32 value){memcpy(s.after.raw.data()+0x74-offset,&value,4);};
    for(unsigned i=0;i<s.arguments.size();++i)memcpy(s.after.raw.data()+0x7C+i*4,&s.arguments[i],4);
    local(0x3C,8);local(0x30,skill);local(0x48,1);local(0x20,damage::Bits(12));
    local(8,damage::Bits(1));local(4,damage::Bits(1));local(0x74,damage::Bits(6.48f));
    const double limb=1;memcpy(s.limbRaw.data(),&limb,8);memcpy(s.after.raw.data()+0x24,&limb,8);
    return s;
}
void TestDamageStageAdmission() {
    Check(damage::Complete(DamageValidStage())==nullptr,"valid damage stage refused");
    enum Fault { MissingEntry,MissingReturn,Invalid,WrongCw,ChangedCw,WrongCaller,MissingOwner,MissingItem,
        MissingAv,DuplicateAv,MissingLimb,DuplicateLimb,ChangedWeapon,WrongType,WrongMode,ChangedSetting,
        ChangedArgument,DamageGetter,SkillGetter,LimbType,LimbLocal,InfiniteAv,InfiniteExtra,InfiniteReturn,Count };
    for(unsigned fault=0;fault<Count;++fault) {
        auto s=DamageValidStage();
        switch(fault) {
        case MissingEntry:s.entered=false;break;case MissingReturn:s.returned=false;break;case Invalid:s.invalid=true;break;
        case WrongCw:s.entryControl=0x037F;break;case ChangedCw:s.returnControl=0x0F7F;break;
        case WrongCaller:s.returnAddress++;break;case MissingOwner:s.arguments[0]=0;break;case MissingItem:s.arguments[6]=0;break;
        case MissingAv:s.meleeCalls=0;break;case DuplicateAv:s.meleeCalls=2;break;case MissingLimb:s.limbCalls=0;break;case DuplicateLimb:s.limbCalls=2;break;
        case ChangedWeapon:s.weaponStable=false;break;case WrongType:s.weaponBefore[4]=0x2A;break;case WrongMode:s.weaponBefore[0xF4]=2;break;
        case ChangedSetting:s.settingsStable=false;break;case ChangedArgument:s.arguments[4]++;break;
        case DamageGetter:s.after.raw[0x74-0x3C]++;break;case SkillGetter:s.after.raw[0x74-0x30]++;break;
        case LimbType:s.after.raw[0x74-0x48]=2;break;case LimbLocal:s.limbRaw[0]=1;break;
        case InfiniteAv:s.av17=0x7F800000;break;case InfiniteExtra:s.extra=0x7FC00000;break;
        case InfiniteReturn:{const UInt32 inf=0x7F800000;memcpy(s.after.raw.data(),&inf,4);break;}
        }
        Check(damage::Complete(s)!=nullptr,"invalid damage operand admission accepted");
    }
}
void TestDamageExtendedOperands() {
    struct Case {std::uint64_t mantissa;std::uint16_t exponent;bool valid;UInt32 bits;};
    const Case cases[]{
        {0,0,true,0},{0,0x8000,true,0x80000000},{0x8000000000000000ull,0x3FFF,true,0x3F800000},
        {0xC000000000000000ull,0xC000,true,0xC0400000},{0x8000000000000000ull,0x3F81,true,0x00800000},
        {0x8000000000000000ull,0x3F6A,true,1},{0x8000000000000000ull,0x3F69,false,0},
        {0x8000000000000001ull,0x3FFF,false,0},{0x8000000000000000ull,0x7FFF,false,0},
        {0x4000000000000000ull,0x3FFF,false,0},{0,0x3FFF,false,0}
    };
    for(const auto& c:cases) {
        std::array<std::uint8_t,10> raw{};memcpy(raw.data(),&c.mantissa,8);memcpy(raw.data()+8,&c.exponent,2);
        UInt32 bits=0;Check(damage::Float80(raw.data(),bits)==c.valid,"extended damage admission mismatch");
        if(c.valid)Check(bits==c.bits,"extended damage value rounded");
    }
    for(unsigned top=0;top<8;++top) {
        std::array<std::uint8_t,512> fx{};std::array<std::uint8_t,10> raw{};UInt32 bits=0;
        const auto status=static_cast<std::uint16_t>(top<<11);memcpy(fx.data()+2,&status,2);fx[4]=static_cast<std::uint8_t>(1u<<top);
        const std::uint64_t mantissa=0x8000000000000000ull;const std::uint16_t exponent=0x3FFF;
        memcpy(fx.data()+32,&mantissa,8);memcpy(fx.data()+40,&exponent,2);
        Check(damage::St0(fx.data(),raw,bits) && bits==0x3F800000,"physical x87 TOP confused with logical ST0");
        fx[4]=0;Check(!damage::St0(fx.data(),raw,bits),"empty x87 ST0 accepted");
    }
}
void TestDamageEpochIsolation() {
    const auto capture=g_captureGeneration.load(),connection=g_connectionGeneration.load(),load=g_gameLoadEpoch.load(),frame=g_frame;
    const bool active=g_capture,connected=g_connected,loaded=g_loadedGameObserved,installed=g_damageInstalled;
    const auto thread=g_damageThread;const auto depth=g_damageDepth,overflow=g_damageOverflow;
    auto priorScopes=g_damageScopes;
    DamageHitScope s;s.selected=true;s.capture=123;s.connection=456;s.load=789;s.frame=100;s.thread=GetCurrentThreadId();
    for(unsigned fault=0;fault<11;++fault) {
        g_capture=true;g_connected=true;g_loadedGameObserved=true;g_damageInstalled=true;g_damageThread=s.thread;
        g_captureGeneration=s.capture;g_connectionGeneration=s.connection;g_gameLoadEpoch=s.load;g_frame=s.frame;s.invalid=false;
        if(fault==1)g_capture=false;if(fault==2)g_connected=false;if(fault==3)g_loadedGameObserved=false;
        if(fault==4)++g_captureGeneration;if(fault==5)++g_connectionGeneration;if(fault==6)++g_gameLoadEpoch;
        if(fault==7)++g_frame;if(fault==8)g_damageThread=0;if(fault==9)g_damageInstalled=false;if(fault==10)s.invalid=true;
        Check(DamageAlive(s)==(fault==0),"damage event crossed capture/load/thread/frame boundary");
    }
    s.invalid=false;g_capture=true;g_connected=true;g_loadedGameObserved=true;g_damageInstalled=true;g_damageThread=s.thread;
    g_captureGeneration=s.capture;g_connectionGeneration=s.connection;g_gameLoadEpoch=s.load;g_frame=s.frame;
    g_damageDepth=2;g_damageOverflow=0;g_damageScopes[0]=s;g_damageScopes[1]={};
    Check(!DamageActiveScope(),"unselected nested hit borrowed outer damage identity");
    --g_damageDepth;Check(DamageActiveScope()==&g_damageScopes[0],"outer hit scope not restored");
    g_damageOverflow=1;Check(!DamageActiveScope(),"overflow inherited a damage scope");
    g_damageScopes=priorScopes;g_damageDepth=depth;g_damageOverflow=overflow;
    g_captureGeneration=capture;g_connectionGeneration=connection;g_gameLoadEpoch=load;g_frame=frame;
    g_capture=active;g_connected=connected;g_loadedGameObserved=loaded;g_damageInstalled=installed;g_damageThread=thread;
}
alignas(16) std::uint8_t g_damageFixtureBeforeFx[512]{},g_damageFixtureAfterFx[512]{};
UInt32 g_damageFixtureBefore[9]{},g_damageFixtureAfter[9]{},g_damageFixtureEsp=0,g_damageFixtureHook=0;
const double DamageFixtureValue=1.25;
UInt32 g_damageFixtureMxcsr=0x1F80;
std::uint16_t g_damageFixtureControl=0x007F;
alignas(16) const UInt32 DamageFixtureXmm[4]{0xDEADBEEF,0x12345678,0x87654321,0x13579BDF};
__declspec(naked) void DamageFixtureReturn(){__asm {ret}}
#define DAMAGE_FIXTURE_REGS(Name) \
    __asm {mov [Name],eax} __asm {mov [Name+4],ecx} __asm {mov [Name+8],edx} \
    __asm {mov [Name+12],ebx} __asm {mov [Name+16],esp} __asm {mov [Name+20],ebp} \
    __asm {mov [Name+24],esi} __asm {mov [Name+28],edi} __asm {pushfd} __asm {pop dword ptr [Name+32]}
__declspec(naked) void DamageFixtureRun() {
    __asm {pushfd} __asm {pushad} __asm {mov [g_damageFixtureEsp],esp} __asm {sub esp,160} __asm {lea ebp,[esp+128]}
    __asm {fninit} __asm {fldcw [g_damageFixtureControl]} __asm {ldmxcsr [g_damageFixtureMxcsr]}
    __asm {fld qword ptr [DamageFixtureValue]} __asm {fld qword ptr [DamageFixtureValue]}
    __asm {movaps xmm0,[DamageFixtureXmm]} __asm {movaps xmm1,[DamageFixtureXmm]} __asm {movaps xmm2,[DamageFixtureXmm]} __asm {movaps xmm3,[DamageFixtureXmm]}
    __asm {movaps xmm4,[DamageFixtureXmm]} __asm {movaps xmm5,[DamageFixtureXmm]} __asm {movaps xmm6,[DamageFixtureXmm]} __asm {movaps xmm7,[DamageFixtureXmm]}
    __asm {mov eax,11223344h} __asm {mov ecx,55667788h} __asm {mov edx,12344321h} __asm {mov ebx,87654321h}
    __asm {mov esi,0AABBCCDDh} __asm {mov edi,0EEFF0011h} __asm {std} __asm {stc}
    DAMAGE_FIXTURE_REGS(g_damageFixtureBefore)
    __asm {fxsave [g_damageFixtureBeforeFx]} __asm {call dword ptr [g_damageFixtureHook]}
    DAMAGE_FIXTURE_REGS(g_damageFixtureAfter)
    __asm {fxsave [g_damageFixtureAfterFx]} __asm {cld} __asm {fninit}
    __asm {mov esp,[g_damageFixtureEsp]} __asm {popad} __asm {popfd} __asm {ret}
}
#undef DAMAGE_FIXTURE_REGS
void TestDamageProbeTransparency() {
    CommandFloatingGuard restoreFp;const auto savedThread=g_damageThread;const auto trampolines=g_damageTrampolines;
    g_damageThread=0; // Actual wrapper executes; observation exits before touching game state.
    for(unsigned mode=0;mode<2;++mode)for(unsigned i=0;i<damage::SiteCount;++i) {
        g_damageFixtureControl=mode?0x0F7F:0x007F;g_damageFixtureMxcsr=mode?0x7F80:0x1F80;
        g_damageTrampolines[i]=reinterpret_cast<UInt32>(&DamageFixtureReturn);g_damageFixtureHook=DamageWrapper(i);
        memset(g_damageFixtureBeforeFx,0,sizeof(g_damageFixtureBeforeFx));memset(g_damageFixtureAfterFx,0,sizeof(g_damageFixtureAfterFx));
        SetLastError(0x4567);DamageFixtureRun();const auto error=GetLastError();
        Check(error==0x4567,"damage probe changed LastError");
        Check(!memcmp(g_damageFixtureBefore,g_damageFixtureAfter,sizeof(g_damageFixtureBefore)),"damage probe changed GPR/flags/stack");
        Check(!memcmp(g_damageFixtureBeforeFx,g_damageFixtureAfterFx,6),"damage probe changed x87 control/status/tags");
        Check(!memcmp(g_damageFixtureBeforeFx+24,g_damageFixtureAfterFx+24,8),"damage probe changed MXCSR");
        Check(!memcmp(g_damageFixtureBeforeFx+32,g_damageFixtureAfterFx+32,128+128),"damage probe changed x87/MMX/XMM registers");
    }
    g_damageThread=savedThread;g_damageTrampolines=trampolines;
}

void TestDamageCoverage() {
    damage::Coverage c;const damage::CoverageKey key{1,2,3,4};
    Check(!c.select({}) && !c.add(key,damage::RouteEntries),"empty coverage key admitted");
    Check(c.select(key),"current coverage key refused");
    for(unsigned i=0;i<damage::CoverageCounterCount;++i)
        Check(c.add(key,static_cast<damage::CoverageCounter>(i)) && c.counts[i]==1,"coverage counter omitted");
    Check(c.select(key) && c.counts[damage::RouteEntries]==1,"same-key snapshot reset counters");
    for(unsigned field=0;field<4;++field) {
        auto changed=key;
        if(field==0)++changed.lease;if(field==1)++changed.capture;if(field==2)++changed.connection;if(field==3)++changed.load;
        Check(!c.add(changed,damage::RouteEntries) && c.counts[damage::RouteEntries]==1,"stale coverage key counted");
    }
    c.counts[damage::RouteEntries]=UINT32_MAX;
    Check(c.add(key,damage::RouteEntries) && c.saturated && c.counts[damage::RouteEntries]==UINT32_MAX,"coverage count overflowed");
    const damage::CoverageKey next{2,3,4,5};
    Check(c.select(next) && !c.saturated && c.counts==std::array<std::uint32_t,damage::CoverageCounterCount>{},"new lease retained old coverage");
    Check(!c.add(key,damage::RouteEntries) && c.add(next,damage::RouteEntries),"delayed completion crossed lease");
    const auto previousLease=g_combatLease;const auto previousCoverage=g_damageCoverage;
    const bool installed=g_damageInstalled;g_damageInstalled=true;g_combatLease={};
    g_combatLease.id=key.lease;g_combatLease.captureGeneration=key.capture;
    g_combatLease.connectionGeneration=key.connection;g_combatLease.loadEpoch=key.load;
    g_damageCoverage={};
    auto json=DamageInvocationCoverage(g_combatLease);
    Check(json.find("\"status\":\"observed\"")!=std::string::npos && json.find("\"routeEntries\":0")!=std::string::npos,"zero-route snapshot missing");
    DamageCount(key,damage::RouteEntries);DamageCount(key,damage::WrongThread);
    json=DamageInvocationCoverage(g_combatLease);
    Check(json.find("\"routeEntries\":1")!=std::string::npos && json.find("\"wrongThread\":1")!=std::string::npos,"coverage serialization lost rejection");
    auto stale=g_combatLease;++stale.id;
    Check(DamageInvocationCoverage(stale).find("lease-changed")!=std::string::npos && g_damageCoverage.counts[0]==1,"stale snapshot reset current counters");
    const auto oldThread=g_damageThread,oldDepth=g_damageDepth,oldOverflow=g_damageOverflow;
    const auto oldScopes=g_damageScopes;
    const bool oldCapture=g_capture,oldConnected=g_connected,oldLoaded=g_loadedGameObserved;
    g_combatLease.active=true;g_combatLease.deadline=GetTickCount64()+10000;
    g_damageThread=0;g_damageDepth=0;g_damageOverflow=0;
    UInt32 saved[8]{};
    DamageObserve(damage::HitEntry,saved,nullptr);
    Check(g_damageCoverage.counts[damage::RouteEntries]==2 && g_damageCoverage.counts[damage::WrongThread]==2 && !g_damageDepth,
        "wrong-thread route was not independently counted");
    g_damageThread=GetCurrentThreadId();g_capture=false;
    DamageObserve(damage::HitEntry,saved,nullptr);
    Check(g_damageCoverage.counts[damage::InactiveCapture]==1,"inactive capture rejection missing");
    g_damageDepth=0;g_capture=true;g_connected=true;g_loadedGameObserved=true;
    DamageObserve(damage::HitEntry,saved,nullptr);
    Check(g_damageCoverage.counts[damage::StackOrArguments]==1,"invalid stack rejection missing");
    g_damageDepth=0;UInt32 arguments[6]{};saved[3]=reinterpret_cast<UInt32>(arguments)-4;
    DamageObserve(damage::HitEntry,saved,nullptr);
    Check(g_damageCoverage.counts[damage::MissingParticipants]==1,"missing participant rejection missing");
    g_combatLease.cleanupIssued=true;const auto entries=g_damageCoverage.counts[damage::RouteEntries];
    DamageObserve(damage::HitEntry,saved,nullptr);
    Check(g_damageCoverage.counts[damage::RouteEntries]==entries,"cleanup route leaked into active-lease counters");
    g_damageThread=oldThread;g_damageDepth=oldDepth;g_damageOverflow=oldOverflow;g_damageScopes=oldScopes;
    g_capture=oldCapture;g_connected=oldConnected;g_loadedGameObserved=oldLoaded;
    g_damageInstalled=false;
    Check(DamageInvocationCoverage(g_combatLease).find("\"status\":\"unavailable\"")!=std::string::npos,"uninstalled hook advertised observed coverage");
    g_combatLease=previousLease;g_damageCoverage=previousCoverage;g_damageInstalled=installed;
}

void TestDamageInvocation() {
    TestDamageCoverage();
    TestDamageStageAdmission();TestDamageExtendedOperands();TestDamageEpochIsolation();TestDamageProbeTransparency();
}
