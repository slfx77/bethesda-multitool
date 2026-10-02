std::vector<std::string> g_weaponCriticalFixtureRows;
float __cdecl CriticalFixtureStage(void* owner,void* weapon) {
    Check(owner==reinterpret_cast<void*>(0x20000A4) && weapon==reinterpret_cast<void*>(0x2200000),"stage ABI arguments changed");
    return 42.25f;
}
float __fastcall CriticalFixtureCurrent(void* owner,void*,UInt32 code) {
    Check(owner==reinterpret_cast<void*>(0x20000A4) && code==14,"current getter ABI arguments changed");
    return 9.0f;
}
void TestWeaponCriticalStage() {
    struct Case {const char* name;bool observed;unsigned stageCalls;};
    const Case cases[]={
        {"equipped",true,1},{"explicit-no-weapon",true,1},{"automatic",true,1},
        {"negative-rate",true,1},{"negative-multiplier",true,1},{"signed-zero-rate",true,1},
        {"nonautomatic-nonfinite-rate",true,1},
        {"profile",false,0},{"npc",false,0},{"creature",false,0},{"overflow",false,0},
        {"wrong-actor",false,0},{"wrong-base",false,0},{"wrong-type",false,0},{"owner-table",false,0},{"current-target",false,0},{"base-target",false,0},
        {"foreign-code",false,0},{"partial-code",false,0},{"tampered-stage",false,0},{"tampered-current",false,0},
        {"tampered-type-getter",false,0},{"tampered-flags-getter",false,0},{"tampered-rate-getter",false,0},
        {"tampered-multiplier-getter",false,0},{"tampered-zero-constant",false,0},
        {"sdk-unavailable",false,0},{"sdk-not-weapon",false,0},{"weapon-id",false,0},{"weapon-field-unreadable",false,0},
        {"nonfinite-rate",false,0},{"nonfinite-multiplier",false,0},{"floating-profile",false,0},{"current-nonfinite",false,0},
        {"before-stage-owner-changed",false,0},{"before-stage-weapon-changed",false,0},
        {"capture-retired",false,0},{"disconnect",false,1},{"load-epoch",false,1},
        {"changed-base-after-stage",false,1},{"changed-code-after-stage",false,1},
        {"changed-equipped-after-stage",false,1},{"changed-fields-after-stage",false,1},
        {"changed-cw-after-stage",false,1},{"changed-current",false,1},{"nonfinite-stage",false,1}
    };
    for(const auto& item:cases) {
        const std::string name=item.name;
        const UInt32 actor=name=="overflow"?0xFFFFFFF0u:0x2000000,base=0x2100000,weapon=0x2200000;
        std::map<std::uintptr_t,std::vector<std::uint8_t>> memory;
        auto word=[&](std::uintptr_t address,UInt32 value){auto& b=memory[address];b.resize(4);memcpy(b.data(),&value,4);};
        auto code=[&](const CriticalCode& r){auto& b=memory[r.address];
            const std::string h=r.hex;for(size_t n=0;n<h.size();n+=2)b.push_back(static_cast<std::uint8_t>(strtoul(h.substr(n,2).c_str(),nullptr,16)));};
        code(CriticalStageCode);code(CriticalCurrentCode);for(const auto& r:CriticalHelpers)code(r);
        auto header=[&](UInt32 address,size_t size,UInt32 table,std::uint8_t type,UInt32 id){auto& b=memory[address];b.resize(size);
            memcpy(b.data(),&table,4);b[4]=type;memcpy(b.data()+12,&id,4);};
        header(actor,0x24,0x1086A6C,0x3B,0x14);memcpy(memory[actor].data()+0x20,&base,4);
        header(base,16,0x1020000,0x2A,7);word(std::uintptr_t(actor)+0xA4,0x108A974);word(0x108A980,0x93ACB0);word(0x108A978,0x8803A0);
        header(weapon,16,0x1040000,0x28,0x4334);memory[weapon+0xF4]={4};
        const bool automatic=name=="automatic" || name=="negative-rate" || name=="signed-zero-rate" || name=="nonfinite-rate";
        memory[weapon+0x100]={static_cast<std::uint8_t>(automatic?2:0)};
        word(weapon+0x134,name=="negative-rate"?0xC0E00000:name=="signed-zero-rate"?0x80000000:
            name=="nonfinite-rate" || name=="nonautomatic-nonfinite-rate"?0x7F800000:0x40E00000);
        word(weapon+0x1C4,name=="negative-multiplier"?0xBF800000:name=="nonfinite-multiplier"?0x7FC00000:0x40400000);
        if(name=="wrong-actor")memory[actor][12]=0x15;
        if(name=="wrong-base")memory[base][12]=8;
        if(name=="wrong-type")memory[actor][4]=0x3C;
        if(name=="owner-table")word(std::uintptr_t(actor)+0xA4,0x108A978);
        if(name=="current-target")word(0x108A980,0x93ACB4);
        if(name=="base-target")word(0x108A978,0x8803A4);
        if(name=="partial-code")memory[CriticalStageCode.address].pop_back();
        const std::pair<const char*,UInt32> corrupt[]={
            {"tampered-stage",0x646D80},{"tampered-current",0x93ACB0},{"tampered-type-getter",0x446390},
            {"tampered-flags-getter",0x524B40},{"tampered-rate-getter",0x821640},
            {"tampered-multiplier-getter",0x8D1EB0},{"tampered-zero-constant",0x1012060}};
        for(const auto& r:corrupt)if(name==r.first)memory[r.second][0]^=1;
        if(name=="weapon-id")memory[weapon][12]^=1;
        if(name=="weapon-field-unreadable")memory.erase(weapon+0x134);
        unsigned currentCalls=0,stageCalls=0,equippedCalls=0;bool alive=true,wrappedRead=false;
        auto read=[&](std::uintptr_t address,void* bytes,size_t size){
            if(address<0x10000)wrappedRead=true;
            const auto p=memory.find(address);if(p==memory.end() || p->second.size()<size)return false;
            memcpy(bytes,p->second.data(),size);return true;
        };
        auto mapped=[&](std::uintptr_t address,size_t length,bool executable){
            if(name=="foreign-code" && executable && address==0x646D80)return false;
            return address>=0x400000 && address+length<=0x1100000;
        };
        auto equipped=[&](UInt32& address,UInt32& id,std::uint8_t& type){++equippedCalls;
            if(name=="sdk-unavailable")return false;
            if(name=="explicit-no-weapon" || (name=="changed-equipped-after-stage" && stageCalls)) {address=0;id=0;type=0;return true;}
            if(name=="before-stage-owner-changed" && equippedCalls==2)word(std::uintptr_t(actor)+0xA4,0x108A978);
            if(name=="before-stage-weapon-changed" && equippedCalls==2)memory[weapon+0x100][0]^=2;
            address=weapon;id=0x4334;type=name=="sdk-not-weapon"?0x29:0x28;return true;
        };
        auto current=[&](UInt32 address,UInt32 owner,UInt32 av){++currentCalls;
            Check(address==0x93ACB0 && owner==actor+0xA4 && av==14,"current owner/slot/code was not admitted");
            if(name=="capture-retired")alive=false;
            if(name=="current-nonfinite") {const UInt32 nan=0x7FC00000;float value=0;memcpy(&value,&nan,4);return value;}
            return name=="changed-current" && currentCalls==2?10.0f:9.0f;
        };
        auto stage=[&](UInt32 address,UInt32 owner,UInt32 actualWeapon){++stageCalls;
            Check(address==0x646D80 && owner==actor+0xA4 && actualWeapon==(name=="explicit-no-weapon"?0u:weapon),"stage arguments did not match admitted equipped owner");
            if(name=="disconnect" || name=="load-epoch")alive=false;
            if(name=="changed-base-after-stage")memory[base][12]=8;
            if(name=="changed-code-after-stage")memory[0x446390][0]^=1;
            if(name=="changed-fields-after-stage")memory[weapon+0x1C4][3]^=1;
            if(name=="nonfinite-stage") {const UInt32 nan=0x7FC00000;float value=0;memcpy(&value,&nan,4);return value;}
            return 42.25f; // Deliberately unrelated to inputs; the observer must preserve the engine return.
        };
        auto cw=[&](){return static_cast<std::uint16_t>(name=="floating-profile" || (name=="changed-cw-after-stage" && stageCalls)?0x37F:0x7F);};
        const auto out=ReadWeaponCriticalStage(name!="profile",actor,name=="npc" || name=="creature"?0x1234u:0x14u,7,
            read,mapped,equipped,current,stage,[&](){return alive;},cw);
        Check(out.observed==item.observed && stageCalls==item.stageCalls,item.name);
        Check(!wrappedRead,"wrapped critical pointer was read");
        const auto json=CriticalObservationJson(out,actor,0x14,7,"fixture-executable",1,2,3);
        Check(json.size()<8192,"critical serialization grew beyond bounded allowance");
        if(item.observed) {
            Check(out.identityStable && out.stage==42.25f && currentCalls==2 && equippedCalls==3,"observed stage omitted repeated admissions");
            Check(json.find("\"readConsistency\":\"bracketed-equal\"")!=std::string::npos,"bracketed evidence was mislabeled");
            if(name=="explicit-no-weapon")Check(json.find("\"weaponPresent\":false,\"weaponAddress\":null")!=std::string::npos &&
                json.find("\"weaponFieldsBeforeHex\":null")!=std::string::npos,"explicit absence became a fallback weapon");
        } else Check(out.reason && *out.reason && json.find("\"status\":\"unavailable\"")!=std::string::npos,"failure lost reason");
        g_weaponCriticalFixtureRows.push_back("{\"case\":"+Quote(item.name)+",\"currentCalls\":"+std::to_string(currentCalls)+
            ",\"stageCalls\":"+std::to_string(stageCalls)+",\"observation\":"+json+"}");
    }
    for(unsigned kind=0;kind<3;++kind) {
        std::string old=",\"legacy\":\""+std::string(kind==2?MaxPayload:kind==1?MaxPayload-1024:100,'x')+"\"";
        const auto before=old;const std::string addition="{\"synthetic\":\""+std::string(4096,'y')+"\"}";
        const bool ok=AppendWeaponCriticalStage(old,addition);
        Check(ok==(kind!=2),"actor-state frame bound failed");
        Check(old.compare(0,before.size(),before)==0,"legacy actor fields were truncated");
        if(kind==1)Check(old.find("actor-state-payload-limit")!=std::string::npos && old.find(addition)==std::string::npos,"oversized addition was not explicit unavailable");
        if(ok)Check(old.size()+512<=MaxPayload,"actor-state escaped envelope budget");
        g_weaponCriticalFixtureRows.push_back("{\"case\":\"frame-bound-"+std::to_string(kind)+"\",\"accepted\":"+(ok?"true":"false")+",\"serializedEvent\":"+Quote("{"+old.substr(1)+"}")+"}");
    }
    Check(InvokeCriticalCurrent(reinterpret_cast<UInt32>(&CriticalFixtureCurrent),0x20000A4,14)==9,"current thiscall return mismatch");
    Check(InvokeCriticalStage(reinterpret_cast<UInt32>(&CriticalFixtureStage),0x20000A4,0x2200000)==42.25f,"stage cdecl return mismatch");
    g_weaponCriticalFixtureRows.push_back("{\"case\":\"native-current-thiscall-and-stage-cdecl\",\"passed\":true}");
    const auto before=ReadFloatingFields();SetLastError(0x6319);
    {CriticalEnvironmentGuard guard;auto altered=before;altered.control^=0x200;altered.mxcsr^=0x2000;
        WriteFloatingFields(altered);SetLastError(17);}
    const auto error=GetLastError();const auto after=ReadFloatingFields();
    Check(error==0x6319 && EqualFloatingFields(before,after),"critical observation changed FP environment or LastError");
    g_weaponCriticalFixtureRows.push_back("{\"case\":\"observer-environment-restored\",\"passed\":true}");
}
