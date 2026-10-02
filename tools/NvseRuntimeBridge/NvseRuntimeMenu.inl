// One-button menu actions, through the actual engine HandleClick virtual method.
// PC008 menu captures pin the full handler/current-message getter and tile ownership.
// This is an engine UI action; it does not activate a Windows window or call a callback directly.
struct RuntimeMenuState {
    UInt32 menu=0, tile=0, button=0, queued=0, callback=0, handler=0;
    std::string text,label,indexRoutineHex;
    bool operator==(const RuntimeMenuState& other) const {
        return menu==other.menu && tile==other.tile && button==other.button && queued==other.queued &&
            callback==other.callback && handler==other.handler && text==other.text && label==other.label && indexRoutineHex==other.indexRoutineHex;
    }
};
enum class MenuAvailability { Ready, Unavailable, Unreadable, Invalid, Unverified };
struct MenuReadField {
    const char* name;
    std::uint64_t address;
    bool readable;
    std::vector<UInt32> values;
};
struct MenuInspection {
    MenuAvailability availability=MenuAvailability::Ready;
    std::string failedCheck;
    std::vector<MenuReadField> fields;
    bool queuedObserved=false;
    UInt32 queued=0;
    bool Fail(const char* check,MenuAvailability kind) {
        failedCheck=check;availability=kind;return false;
    }
};
template<class Reader,class T> bool ReadMenuValue(Reader& read,const char* name,std::uint64_t address,T& value,MenuInspection& inspection) {
    const bool readable=address && address<=UINT32_MAX-(sizeof(value)-1) && read(static_cast<std::uintptr_t>(address),value);
    inspection.fields.push_back({name,address,readable,{}});
    if(readable)inspection.fields.back().values.push_back(static_cast<UInt32>(value));
    return readable;
}
template<class Reader,size_t Count> bool ReadMenuWords(Reader& read,const char* name,std::uint64_t address,UInt32 (&value)[Count],MenuInspection& inspection) {
    const bool readable=address && address<=UINT32_MAX-(sizeof(value)-1) && read(static_cast<std::uintptr_t>(address),value);
    inspection.fields.push_back({name,address,readable,{}});
    if(readable)inspection.fields.back().values.assign(value,value+Count);
    return readable;
}
// Shared by the actual engine reader and synthetic memory fixtures. Absence is observed;
// it is not labeled a transition and never admits a choice.
template<class Reader> bool ReadMenuLayout(Reader& read,RuntimeMenuState& state,MenuInspection& inspection) {
    UInt32 ui=0,array[4]{},menu[16]{},stack[10]{};unsigned char visible=0;
    if(!ReadMenuValue(read,"ui",0x11D8A80,ui,inspection))return inspection.Fail("message-ui-unreadable",MenuAvailability::Unreadable);
    if(!ReadMenuValue(read,"visible",0x11F3478,visible,inspection))return inspection.Fail("message-visibility-unreadable",MenuAvailability::Unreadable);
    if(visible>1)return inspection.Fail("message-visibility-invalid",MenuAvailability::Invalid);
    if(!visible)return inspection.Fail("message-not-visible",MenuAvailability::Unavailable);
    if(!ui)return inspection.Fail("message-ui-absent",MenuAvailability::Unavailable);
    if(!ReadMenuWords(read,"stack",std::uint64_t(ui)+0x114,stack,inspection))return inspection.Fail("message-stack-unreadable",MenuAvailability::Unreadable);
    const bool stackEmpty=std::all_of(stack,stack+10,[](UInt32 id){return id==0;});
    if(!stackEmpty && stack[0]!=1001)return inspection.Fail("message-stack-owner-mismatch",MenuAvailability::Invalid);
    if(!stackEmpty && std::any_of(stack+1,stack+10,[](UInt32 id){return id!=0;}))return inspection.Fail("message-stack-not-exclusive",MenuAvailability::Invalid);
    if(!ReadMenuWords(read,"registry",0x11F3508,array,inspection))return inspection.Fail("message-registry-unreadable",MenuAvailability::Unreadable);
    // Pinned xNVSE 0ccd23ad, NiTypes.h:113-120: NiTArray has capacity at +8,
    // firstFreeEntry at +0xA and numObjs at +0xC. numObjs==0 ignores inactive slots.
    // Validate dimensions first; a corrupt zero count is not absence.
    if((array[2]&0xFFFF)>4096 || (array[2]>>16)>(array[2]&0xFFFF) || (array[3]&0xFFFF)>(array[2]>>16))
        return inspection.Fail("message-registry-bounds-invalid",MenuAvailability::Invalid);
    if(!(array[3]&0xFFFF))return inspection.Fail("message-registry-empty",MenuAvailability::Unavailable);
    if(!array[1])return inspection.Fail("message-registry-data-missing",MenuAvailability::Invalid);
    if(!ReadMenuValue(read,"rootTile",array[1],state.tile,inspection))return inspection.Fail("message-root-unreadable",MenuAvailability::Unreadable);
    if(!state.tile)return inspection.Fail("message-root-absent",MenuAvailability::Unavailable);
    if(!ReadMenuValue(read,"menuOwner",std::uint64_t(state.tile)+0x3C,state.menu,inspection))return inspection.Fail("message-menu-owner-unreadable",MenuAvailability::Unreadable);
    if(!state.menu)return inspection.Fail("message-menu-owner-missing",MenuAvailability::Invalid);
    if(!ReadMenuWords(read,"menu",state.menu,menu,inspection))return inspection.Fail("message-menu-unreadable",MenuAvailability::Unreadable);
    if(menu[1]!=state.tile)return inspection.Fail("message-tile-owner-mismatch",MenuAvailability::Invalid);
    if(menu[8]!=1001)return inspection.Fail("message-menu-type-mismatch",MenuAvailability::Invalid);
    if(!menu[0])return inspection.Fail("message-menu-vtable-missing",MenuAvailability::Invalid);
    if(!ReadMenuValue(read,"handler",std::uint64_t(menu[0])+12,state.handler,inspection))return inspection.Fail("message-handler-unreadable",MenuAvailability::Unreadable);
    if(state.handler!=0x7AA070)return inspection.Fail("message-handler-mismatch",MenuAvailability::Invalid);
    // An empty active stack must not hide a malformed registry or a present invalid owner.
    if(stackEmpty)return inspection.Fail("message-stack-empty",MenuAvailability::Unavailable);
    return true;
}
template<class Reader> bool ReadMenuOwner(Reader& read,UInt32 queued,RuntimeMenuState& state,MenuInspection& inspection) {
    inspection.queuedObserved=true;inspection.queued=state.queued=queued;
    if(!queued)return inspection.Fail("message-queue-empty",MenuAvailability::Unavailable);
    if(!ReadMenuValue(read,"callback",std::uint64_t(queued)+0x28,state.callback,inspection))return inspection.Fail("message-callback-unreadable",MenuAvailability::Unreadable);
    if(!state.callback)return inspection.Fail("message-callback-missing",MenuAvailability::Invalid);
    return true;
}
std::string MenuInspectionFields(const MenuInspection& inspection) {
    const char* availability=inspection.availability==MenuAvailability::Ready?"ready":
        inspection.availability==MenuAvailability::Unavailable?"unavailable":
        inspection.availability==MenuAvailability::Unreadable?"unreadable":
        inspection.availability==MenuAvailability::Invalid?"invalid":"unverified";
    std::string json=",\"menuInspection\":{\"availability\":"+Quote(availability)+",\"failedCheck\":"+
        (inspection.failedCheck.empty()?"null":Quote(inspection.failedCheck))+",\"fields\":[";
    for(size_t index=0;index<inspection.fields.size();++index) {
        const auto& field=inspection.fields[index];if(index)json+=',';
        json+="{\"name\":"+Quote(field.name)+",\"address\":"+std::to_string(field.address)+",\"readable\":"+(field.readable?"true":"false");
        if(field.readable) {
            json+=",\"values\":[";
            for(size_t value=0;value<field.values.size();++value) {if(value)json+=',';json+=std::to_string(field.values[value]);}
            json+=']';
        }
        json+='}';
    }
    json+=']';
    if(inspection.queuedObserved)json+=",\"queuedMessageAddress\":"+std::to_string(inspection.queued);
    return json+'}';
}
std::string MenuLegacyError(const MenuInspection& inspection) {
    const auto& check=inspection.failedCheck;
    if(check=="message-not-visible" || check=="message-visibility-invalid")return "visible-message-menu-unavailable";
    if(check=="message-ui-unreadable" || check=="message-ui-absent" || check=="message-visibility-unreadable")return "message-visibility-unreadable";
    if(check=="message-queue-empty" || check=="message-callback-unreadable" || check=="message-callback-missing")return "message-owner-unavailable";
    for(const auto* prefix:{"message-stack-","message-registry-","message-root-","message-menu-","message-tile-","message-handler-"})
        if(check.compare(0,strlen(prefix),prefix)==0)return "visible-message-menu-layout-or-owner-mismatch";
    return check;
}
void EmitMenuReadFailure(const Request& request,const MenuInspection& inspection,std::uint64_t generation,const char* error=nullptr) {
    if(request.payload=="inspect" && inspection.availability==MenuAvailability::Unavailable) {
        const bool hidden=inspection.failedCheck=="message-not-visible";
        Emit("message-state",request.id,",\"status\":\"unavailable\",\"reason\":"+Quote(hidden?"menu-not-visible":"menu-not-ready")+
            ",\"evidence\":"+Quote(hidden?"engine-menu-visibility":"engine-menu-availability")+MenuInspectionFields(inspection),generation);
    } else Emit("error",request.id,",\"error\":"+Quote(error?error:MenuLegacyError(inspection))+MenuInspectionFields(inspection),generation);
}
bool g_menuApiVerified=false;
bool MenuRoutineMatches(UInt32 address,const char* hex) {
    const size_t length=strlen(hex)/2;
    if (!length || length>192 || !ActorRoutine(address)) return false;
    std::uint8_t actual[192]{};SIZE_T count=0;
    if (!ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),actual,length,&count) || count!=length) return false;
    for(size_t index=0;index<length;++index) {
        char pair[]={hex[index*2],hex[index*2+1],0};
        if(actual[index]!=strtoul(pair,nullptr,16)) return false;
    }
    return true;
}
bool VerifyMenuApi() {
    // Filled from the independently captured PC008 bytes, not guessed prologues.
    return g_menuApiVerified=g_pcLayoutVerified &&
        MenuRoutineMatches(0x7AA070,"558bec5153894dfc837d0c007519837d08007c13837d08067d0d8b45088b4dfc8b54812889550c837d08077567837d0c0074618b450c508b4dfc83c140e85e7003008bd8e857d1d0ff8898e4000000c605eda41d0101e8d5f1ffff85c07415e8ccf1ffff83782800740ae8c1f1ffff8b4828ffd1c605eda41d01000fb615eea41d0185d27409c605eea41d0100eb05e82c0400005b8be55dc20800") &&
        MenuRoutineMatches(0x7A92A0,"558bec51c745fc00000000b93ca51d01e80b4bcaff3905f4a41d017315a1f4a41d0150b93ca51d01e863e70c008b08894dfc8b45fc8be55dc3") &&
        MenuRoutineMatches(0x7A8AA0,"558bec51894dfcb8e90300008be55dc3");
}
bool MenuString(UInt32 address,std::string& text,size_t limit) {
    text.clear();
    if (!address) return false;
    for(size_t index=0;index<=limit;++index) {
        unsigned char byte=0;
        if(!ReadRuntime(std::uintptr_t(address)+index,byte)) return false;
        if(!byte) return !text.empty();
        if(byte<32 || byte>=127) return false; // Initial action contract is exact ASCII, no normalization.
        text.push_back(static_cast<char>(byte));
    }
    return false;
}
bool MenuTrait(UInt32 tile,UInt32 wanted,std::string* text,float* number) {
    UInt32 array[4]{};
    if(!ReadRuntime(std::uintptr_t(tile)+0x10,array) || array[2]>array[3] || array[2]>128 || array[3]>4096) return false;
    bool found=false;
    for(UInt32 index=0;index<array[2];++index) {
        UInt32 value=0,data[5]{};
        if(!ReadRuntime(std::uintptr_t(array[1])+index*4,value) || !value || !ReadRuntime(value,data)) return false;
        if(data[0]!=wanted) continue;
        if(found || data[1]!=tile) return false;
        found=true;
        if(text && !MenuString(data[3],*text,1023)) return false;
        if(number) { memcpy(number,data+2,4);if(!std::isfinite(*number))return false; }
    }
    return found;
}
bool VisitMenuTiles(UInt32 tile,UInt32 parent,size_t depth,std::unordered_set<UInt32>& visited,
    RuntimeMenuState& state,unsigned& textCount,unsigned& buttonCount) {
    if(depth>10 || visited.size()>=96 || !visited.insert(tile).second) return false;
    UInt32 name=0,actualParent=0;std::string tileName;
    if(!ReadRuntime(std::uintptr_t(tile)+0x20,name) || !MenuString(name,tileName,127) ||
        !ReadRuntime(std::uintptr_t(tile)+0x28,actualParent) || (parent && actualParent!=parent)) return false;
    if(tileName=="MM_MessageText") {
        if(++textCount!=1 || !MenuTrait(tile,4036,&state.text,nullptr)) return false;
    }
    if(tileName=="MM_Button") {
        float buttonId=0,visible=0;
        if(++buttonCount!=1 || !MenuTrait(tile,4036,&state.label,nullptr) ||
            !MenuTrait(tile,4010,nullptr,&buttonId) || buttonId!=7 ||
            !MenuTrait(tile,4003,nullptr,&visible) || visible!=1) return false;
        state.button=tile;
    }
    UInt32 node=tile+4;std::unordered_set<UInt32> nodes;
    for(size_t index=0;node && index<96;++index) {
        UInt32 link[2]{},child[3]{};
        if(!nodes.insert(node).second || !ReadRuntime(node,link)) return false;
        if(link[0] && (!ReadRuntime(link[0],child) || !child[2] ||
            !VisitMenuTiles(child[2],tile,depth+1,visited,state,textCount,buttonCount))) return false;
        node=link[1];
    }
    return node==0;
}
bool ReadMenuState(RuntimeMenuState& state,MenuInspection& inspection) {
    state={};inspection={};
    if(!VerifyMenuApi())return inspection.Fail("menu-profile-unavailable",MenuAvailability::Unverified);
    auto read=[](std::uintptr_t address,auto& value){return ReadRuntime(address,value);};
    if(!ReadMenuLayout(read,state,inspection))return false;
    using CurrentMessage=void* (__cdecl*)();
    const auto queued=reinterpret_cast<UInt32>(reinterpret_cast<CurrentMessage>(0x7A92A0)());
    if(!ReadMenuOwner(read,queued,state,inspection))return false;
    std::unordered_set<UInt32> visited;unsigned texts=0,buttons=0;
    if(!VisitMenuTiles(state.tile,0,0,visited,state,texts,buttons) || texts!=1 || buttons!=1) {
        return inspection.Fail("single-button-menu-fields-unavailable",MenuAvailability::Invalid);
    }
    // Exact live HandleClick call site pushes Tile*, sets ECX=menu+0x40, and uses EAX as the button index.
    // Record this transitive routine's live bytes; packed on-disk bytes are not runtime evidence.
    std::uint8_t indexBytes[32]{};
    if(!ActorRoutine(0x7E1110) || !ReadRuntime(0x7E1110,indexBytes))return inspection.Fail("button-index-routine-unavailable",MenuAvailability::Unverified);
    using ButtonIndex=UInt32 (__thiscall*)(void*,void*);
    if(reinterpret_cast<ButtonIndex>(0x7E1110)(reinterpret_cast<void*>(state.menu+0x40),reinterpret_cast<void*>(state.button))!=0) {
        return inspection.Fail("single-button-index-mismatch",MenuAvailability::Invalid);
    }
    state.indexRoutineHex=ActorHex(indexBytes,sizeof(indexBytes));
    return true;
}
struct MenuChoiceRequest { bool probe=false;std::string text,label; };
bool ParseMenuChoice(const std::string& payload,MenuChoiceRequest& choice) {
    std::vector<std::string> parts;size_t start=0;
    for(size_t end=0;(end=payload.find('\t',start))!=std::string::npos;start=end+1) parts.push_back(payload.substr(start,end-start));
    parts.push_back(payload.substr(start));
    if(parts.size()!=5 || parts[0]!="choose" || (parts[1]!="probe" && parts[1]!="visible") || parts[2]!="0" ||
        parts[3].empty() || parts[3].size()>1023 || parts[4].empty() || parts[4].size()>127) return false;
    for(size_t index=3;index<5;++index)
        if(!std::all_of(parts[index].begin(),parts[index].end(),[](unsigned char c){return c>=32 && c<127;})) return false;
    choice.probe=parts[1]=="probe";choice.text=parts[3];choice.label=parts[4];return true;
}
bool MatchMenuChoice(const MenuChoiceRequest& choice,const RuntimeMenuState& state,std::uint64_t generation,std::string& reason) {
    if(state.text!=choice.text || state.label!=choice.label) { reason="message-text-or-button-mismatch";return false; }
    const bool owner=state.callback==reinterpret_cast<UInt32>(MessageProbeCallback);
    if(choice.probe) {
        if(!owner || !g_pendingMessageProbe.pending || g_pendingMessageProbe.generation!=generation ||
            generation!=g_captureGeneration.load()) { reason="probe-message-owner-or-generation-mismatch";return false; }
    } else if(owner) { reason="probe-requires-owned-choice";return false; }
    return true;
}
std::string MenuFields(const RuntimeMenuState& state) {
    return ",\"menuAddress\":"+std::to_string(state.menu)+",\"queuedMessageAddress\":"+std::to_string(state.queued)+
        ",\"callbackAddress\":"+std::to_string(state.callback)+",\"buttonTileAddress\":"+std::to_string(state.button)+
        ",\"text\":"+Quote(state.text)+",\"buttonLabel\":"+Quote(state.label)+",\"buttonIndex\":0"+
        ",\"indexRoutineAddress\":8261904,\"indexRoutinePrefixHex\":"+Quote(state.indexRoutineHex);
}
void MenuAction(const Request& request) {
    if(!g_capture || !g_loadedGameObserved) { Emit("error",request.id,",\"error\":\"loaded-capture-required\"");return; }
    const auto generation=g_captureGeneration.load();
    auto fail=[&](const std::string& reason){Emit("error",request.id,",\"error\":"+Quote(reason),generation);};
    MenuChoiceRequest choice;
    if(request.payload!="inspect" && !ParseMenuChoice(request.payload,choice)) { fail("invalid-menu-request");return; }
    RuntimeMenuState before,again;MenuInspection inspection;std::string reason;
    if(!ReadMenuState(before,inspection)) {EmitMenuReadFailure(request,inspection,generation);return;}
    if(request.payload=="inspect") {
        const bool probe=before.callback==reinterpret_cast<UInt32>(MessageProbeCallback);
        const bool owned=probe && g_pendingMessageProbe.pending && g_pendingMessageProbe.generation==generation;
        Emit("message-state",request.id,MenuFields(before)+",\"status\":\"visible\",\"owner\":"+
            Quote(probe?(owned?"dedicated-probe":"stale-probe"):"explicit-visible-text")+
            ",\"messageInstance\":"+(owned?std::to_string(g_pendingMessageProbe.instance):"null")+
            ",\"evidence\":\"engine-resident-menu\""+MenuInspectionFields(inspection),generation);return;
    }
    const auto serial=g_probeChoiceSerial;
    if(!MatchMenuChoice(choice,before,generation,reason)) { fail(reason);return; }
    if(!ReadMenuState(again,inspection)) {EmitMenuReadFailure(request,inspection,generation,"message-instance-changed");return;}
    if(!(before==again) || !MatchMenuChoice(choice,again,generation,reason) ||
        !g_capture || !g_connected || generation!=g_captureGeneration.load()) {
        fail("message-instance-changed");return;
    }
    Emit("message-choice-request",request.id,MenuFields(before)+",\"owner\":"+Quote(choice.probe?"dedicated-probe":"explicit-visible-text")+
        ",\"messageInstance\":"+(choice.probe?std::to_string(g_pendingMessageProbe.instance):"null"),generation);
    using Click=void (__thiscall*)(void*,UInt32,void*);
    reinterpret_cast<Click>(before.handler)(reinterpret_cast<void*>(before.menu),7,reinterpret_cast<void*>(before.button));
    const bool callback=choice.probe && g_probeChoiceSerial==serial+1;
    Emit("action-result",request.id,MenuFields(before)+",\"accepted\":true,\"evidence\":\"engine-menu-handler-return\",\"probeCallbackObserved\":"+
        std::string(callback?"true":"false")+",\"foregroundInputUsed\":false",generation);
    if(choice.probe && !callback) fail("probe-callback-not-observed");
}
