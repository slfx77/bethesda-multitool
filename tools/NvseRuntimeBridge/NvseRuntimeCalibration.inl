// Read-only PC 1.4.0.525 inputs. NPC fields/settings/callbacks were captured in
// session011; common actor-base layout and infoName are from the pinned xNVSE SDK.
template<class T> T ActorRaw(const std::uint8_t* bytes,size_t offset) {
    T value{};memcpy(&value,bytes+offset,sizeof(value));return value;
}
#include "NvseRuntimeBaseOverride.inl"
bool ActorAscii(std::uintptr_t address,std::string& value,size_t limit=127) {
    value.clear();
    for(size_t index=0;index<=limit;++index) {
        char byte=0;if(!ReadRuntime(address+index,byte))return false;
        if(!byte)return !value.empty();
        if(static_cast<unsigned char>(byte)<32 || static_cast<unsigned char>(byte)>=127)return false;
        value+=byte;
    }
    return false;
}
std::string ActorBaseData(std::uintptr_t actor,UInt32 expectedBase,std::uint8_t actorType) {
    UInt32 base=0;std::uint8_t raw[0xC4]{};
    if(!g_pcLayoutVerified || (actorType!=0x3B && actorType!=0x3C) ||
       !ReadRuntime(actor+0x20,base) || !base || !ReadRuntime(base,raw))return ActorUnavailable("actor-base-layout-unavailable");
    const auto formId=ActorRaw<UInt32>(raw,0xC);
    if(formId!=expectedBase || raw[4]!=(actorType==0x3B?0x2A:0x2B))return ActorUnavailable("actor-base-identity-mismatch");
    std::string special;
    for(size_t index=0;index<7;++index){if(index)special+=",";special+=std::to_string(raw[0xBC+index]);}
    return "{\"status\":\"observed\",\"address\":"+std::to_string(base)+",\"formId\":"+std::to_string(formId)+
        ",\"formType\":"+std::to_string(raw[4])+",\"flags\":"+std::to_string(ActorRaw<UInt32>(raw,0x34))+
        ",\"levelEncodedUnsigned\":"+std::to_string(ActorRaw<std::uint16_t>(raw,0x3C))+
        ",\"minimumLevel\":"+std::to_string(ActorRaw<std::uint16_t>(raw,0x3E))+
        ",\"maximumLevel\":"+std::to_string(ActorRaw<std::uint16_t>(raw,0x40))+
        ",\"storedHealth\":"+std::to_string(ActorRaw<UInt32>(raw,0xB4))+
        ",\"storedEndurance\":"+std::to_string(raw[0xBE])+",\"storedSpecial\":["+special+"]"+
        ",\"templateFlags\":"+std::to_string(ActorRaw<std::uint16_t>(raw,0x4A))+
        ",\"templatePointer\":"+std::to_string(ActorRaw<UInt32>(raw,0x54))+
        ",\"rawHex\":"+Quote(ActorHex(raw,sizeof(raw)))+
        ",\"layoutEvidence\":\"pinned-sdk-common-actor-base;pc011-npc-and-retained-routines\""+
        ",\"templateEvidence\":\"pinned-sdk\",\"inheritance\":\"not-evaluated\"}";
}
std::string ActorGameSetting(const char* expected,std::uintptr_t address) {
    std::uint8_t raw[12]{};std::string actual,reason;
    const bool have=g_pcLayoutVerified && ReadRuntime(address,raw);
    const auto name=have?ActorRaw<UInt32>(raw,8):0;
    const auto value=have?ActorRaw<float>(raw,4):0;
    if(!have)reason="setting-layout-unavailable";
    else if(!ActorAscii(name,actual) || actual!=expected)reason="setting-name-mismatch";
    else if(!std::isfinite(value))reason="nonfinite-setting";
    return "{\"name\":"+Quote(expected)+",\"status\":"+Quote(reason.empty()?"observed":"unavailable")+
        ",\"value\":"+(reason.empty()?NumberField(value):"null")+",\"address\":"+std::to_string(address)+
        ",\"nameAddress\":"+(have?std::to_string(name):"null")+",\"observedName\":"+(actual.empty()?"null":Quote(actual))+
        ",\"rawHex\":"+(have?Quote(ActorHex(raw,sizeof(raw))):"null")+
        ",\"evidence\":\"pc011-name-and-layout-verified\",\"reason\":"+(reason.empty()?"null":Quote(reason))+"}";
}
std::string ActorGameSettings() {
    const std::pair<const char*,std::uintptr_t> settings[]={
        {"fAVDHealthEnduranceOffset",0x11CD120},{"fAVDHealthEnduranceMult",0x11CD270},
        {"fAVDNPCHealthEnduranceOffset",0x11CD27C},{"fAVDNPCHealthEnduranceMult",0x11CD544},
        {"fAVDHealthLevelMult",0x11CDEA4},{"fAVDNPCHealthLevelMult",0x11CD908},
        {"fAVDCritLuckBase",0x11CDEE0},{"fAVDCritLuckMult",0x11CDB48}};
    std::string rows;
    for(const auto& setting:settings){if(!rows.empty())rows+=",";rows+=ActorGameSetting(setting.first,setting.second);}
    return "["+rows+"]";
}
bool ActorInfoRaw(UInt32 code,std::uintptr_t table,UInt32& address,std::uint8_t (&raw)[0x5C]) {
    return g_pcLayoutVerified && code<77 && ReadRuntime(table+4*code,address) && address && ReadRuntime(address,raw);
}
bool ActorValueToken(UInt32 code,std::string& token,std::uintptr_t table=0x11D61C8) {
    UInt32 address=0;std::uint8_t raw[0x5C]{};
    if(!ActorInfoRaw(code,table,address,raw) || !ActorAscii(ActorRaw<UInt32>(raw,0x38),token,63))return false;
    return std::all_of(token.begin(),token.end(),[](unsigned char byte){return std::isalnum(byte) || byte=='_';});
}
std::string ActorInfo(const char* name,UInt32 code,std::uintptr_t table=0x11D61C8) {
    UInt32 address=0;std::uint8_t raw[0x5C]{};std::string token;
    const bool have=ActorInfoRaw(code,table,address,raw);
    const auto prefix="{\"name\":"+Quote(name)+",\"code\":"+std::to_string(code);
    if(!have)return prefix+",\"status\":\"unavailable\",\"reason\":\"actor-value-info-unavailable\"}";
    const bool named=ActorValueToken(code,token,table);
    return prefix+",\"status\":\"observed\",\"address\":"+std::to_string(address)+
        ",\"commandToken\":"+(named?Quote(token):"null")+",\"flags\":"+std::to_string(ActorRaw<UInt32>(raw,0x44))+
        ",\"type\":"+std::to_string(ActorRaw<UInt32>(raw,0x48))+
        ",\"baseCallback\":"+std::to_string(ActorRaw<UInt32>(raw,0x4C))+
        ",\"otherCallback\":"+std::to_string(ActorRaw<UInt32>(raw,0x50))+
        ",\"changeCallback\":"+std::to_string(ActorRaw<UInt32>(raw,0x54))+
        ",\"rawHex\":"+Quote(ActorHex(raw,sizeof(raw)))+",\"evidence\":\"pinned-sdk;pc011-callback-layout\"}";
}
std::string ActorValueInformation() {
    return "["+ActorInfo("Endurance",7)+","+ActorInfo("Luck",11)+","+ActorInfo("CritChance",14)+","+
        ActorInfo("Health",16)+","+ActorInfo("DamageResistance",18)+","+ActorInfo("DamageThreshold",76)+"]";
}
