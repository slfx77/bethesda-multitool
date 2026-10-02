// Raw CommandExecute arguments and Script fields from pinned xNVSE CommandTable.h/GameScript.h.
// A raw opcodeOffset is not yet a normalized SCDA offset, including expression-backed calls.
extern bool g_pcLayoutVerified;
constexpr UInt32 CommandBytecodeLimit=65536;
struct CommandLocationSample {
    UInt32 scriptAddress=0,scriptDataAddress=0,offsetPointer=0,offset=0;
    UInt32 formId=0,flags=0,dataAddress=0,dataLength=0;
    std::uint8_t formType=0;
    const char* offsetStatus="unavailable";
    const char* scriptStatus="unavailable";
    const char* bytecodeStatus="unavailable";
    const char* pointerRangeStatus="unavailable";
    std::string headerHex,bytecodeSha256;
};
bool CommandReadBytes(UInt32 address,void* data,size_t length) {
    if(!address || !length || length>CommandBytecodeLimit || length-1>std::numeric_limits<UInt32>::max()-address)return false;
    SIZE_T received=0;
    return ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),data,length,&received) && received==length;
}
std::string CommandHex(const std::uint8_t* bytes,size_t length) {
    static constexpr char hex[]="0123456789abcdef";
    std::string result(length*2,'0');
    for(size_t i=0;i<length;++i){result[i*2]=hex[bytes[i]>>4];result[i*2+1]=hex[bytes[i]&15];}
    return result;
}
std::string CommandBytecodeHash(const std::uint8_t* bytes,UInt32 length) {
    BCRYPT_ALG_HANDLE algorithm=nullptr;
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return {};
    std::uint8_t digest[32]{};
    const bool valid=BCryptHash(algorithm,nullptr,0,const_cast<PUCHAR>(bytes),length,digest,sizeof(digest))>=0;
    BCryptCloseAlgorithmProvider(algorithm,0);
    return valid?CommandHex(digest,sizeof(digest)):std::string{};
}
bool CommandHeaderIdentityEqual(const std::uint8_t* a,const std::uint8_t* b) {
    return a[4]==b[4] && !memcmp(a+8,b+8,8) && !memcmp(a+0x20,b+0x20,4) && !memcmp(a+0x30,b+0x30,4);
}
CommandLocationSample SampleCommandLocation(void* scriptData,Script* script,UInt32* offset) {
    CommandLocationSample sample;
    sample.scriptAddress=reinterpret_cast<UInt32>(script);
    sample.scriptDataAddress=reinterpret_cast<UInt32>(scriptData);
    sample.offsetPointer=reinterpret_cast<UInt32>(offset);
    sample.offsetStatus=!offset?"null":CommandReadBytes(sample.offsetPointer,&sample.offset,sizeof(sample.offset))?"observed":"unreadable";
    if(!g_pcLayoutVerified){sample.scriptStatus="layout-unverified";return sample;}
    std::uint8_t header[0x34]{};
    if(!script){sample.scriptStatus="null";return sample;}
    if(sizeof(header)-1>std::numeric_limits<UInt32>::max()-sample.scriptAddress){sample.scriptStatus="address-overflow";return sample;}
    if(!CommandReadBytes(sample.scriptAddress,header,sizeof(header))){sample.scriptStatus="unreadable";return sample;}
    sample.headerHex=CommandHex(header,sizeof(header));sample.formType=header[4];
    memcpy(&sample.formId,header+0xC,4);memcpy(&sample.flags,header+8,4);
    memcpy(&sample.dataLength,header+0x20,4);memcpy(&sample.dataAddress,header+0x30,4);
    if(sample.formType!=0x11){sample.scriptStatus="type-mismatch";return sample;}
    sample.scriptStatus="observed";
    if(!sample.dataLength){sample.bytecodeStatus="empty";return sample;}
    if(!sample.dataAddress){sample.bytecodeStatus="null-data";return sample;}
    if(sample.dataLength-1>std::numeric_limits<UInt32>::max()-sample.dataAddress){sample.bytecodeStatus="address-overflow";return sample;}
    const auto end=std::uint64_t(sample.dataAddress)+sample.dataLength;
    sample.pointerRangeStatus=!sample.scriptDataAddress?"null":sample.scriptDataAddress==sample.dataAddress?"data-start":
        sample.scriptDataAddress>sample.dataAddress && sample.scriptDataAddress<end?"inside-data":
        sample.scriptDataAddress==end?"at-data-end":"outside-data";
    if(sample.dataLength>CommandBytecodeLimit){sample.bytecodeStatus="length-limit";return sample;}
    std::vector<std::uint8_t> bytes(sample.dataLength);
    if(!CommandReadBytes(sample.dataAddress,bytes.data(),bytes.size())){sample.bytecodeStatus="unreadable";return sample;}
    sample.bytecodeSha256=CommandBytecodeHash(bytes.data(),sample.dataLength);
    sample.bytecodeStatus=sample.bytecodeSha256.empty()?"hash-unavailable":"observed";
    std::uint8_t after[sizeof(header)]{};
    if(!CommandReadBytes(sample.scriptAddress,after,sizeof(after))){sample.scriptStatus="recheck-unreadable";return sample;}
    if(!CommandHeaderIdentityEqual(header,after))sample.scriptStatus="changed-during-read";
    return sample;
}
std::string CommandLocationJson(const CommandLocationSample& sample) {
    return "{\"scriptDataAddress\":"+std::to_string(sample.scriptDataAddress)+",\"opcodeOffsetPointer\":"+std::to_string(sample.offsetPointer)+
        ",\"opcodeOffsetStatus\":"+Quote(sample.offsetStatus)+",\"opcodeOffset\":"+
        (!strcmp(sample.offsetStatus,"observed")?std::to_string(sample.offset):"null")+
        ",\"script\":{\"status\":"+Quote(sample.scriptStatus)+",\"address\":"+std::to_string(sample.scriptAddress)+
        ",\"formId\":"+std::to_string(sample.formId)+",\"formType\":"+std::to_string(sample.formType)+
        ",\"flags\":"+std::to_string(sample.flags)+",\"dataAddress\":"+std::to_string(sample.dataAddress)+
        ",\"dataLength\":"+std::to_string(sample.dataLength)+",\"headerHex\":"+Quote(sample.headerHex)+"},\"bytecode\":{\"status\":"+
        Quote(sample.bytecodeStatus)+",\"sha256\":"+(sample.bytecodeSha256.empty()?"null":Quote(sample.bytecodeSha256))+
        ",\"length\":"+std::to_string(sample.dataLength)+",\"byteOrder\":\"little\",\"scope\":\"entire-Script-data\",\"limit\":"+
        std::to_string(CommandBytecodeLimit)+"},\"pointerRangeStatus\":"+Quote(sample.pointerRangeStatus)+"}";
}
std::string CommandLocationFields(const CommandLocationSample& before,const CommandLocationSample& after) {
    const bool identity=!strcmp(before.scriptStatus,"observed") && !strcmp(after.scriptStatus,"observed") &&
        before.scriptAddress==after.scriptAddress && before.formId==after.formId && before.formType==after.formType && before.flags==after.flags &&
        before.dataAddress==after.dataAddress && before.dataLength==after.dataLength;
    const bool stable=identity && !strcmp(before.bytecodeStatus,"observed") && !strcmp(after.bytecodeStatus,"observed") &&
        before.bytecodeSha256==after.bytecodeSha256;
    const bool changed=!strcmp(before.scriptStatus,"changed-during-read") || !strcmp(after.scriptStatus,"changed-during-read") ||
        (!strcmp(before.scriptStatus,"observed") && !strcmp(after.scriptStatus,"observed") && (!identity ||
         (!before.bytecodeSha256.empty() && !after.bytecodeSha256.empty() && before.bytecodeSha256!=after.bytecodeSha256)));
    return ",\"commandLocation\":{\"status\":"+Quote(changed?"changed":stable?"observed":"partial")+
        ",\"basis\":\"raw-sdk-command-arguments\",\"normalizedOffsetStatus\":\"unverified\",\"bytecodeStableAcrossCall\":"+(stable?"true":"false")+
        ",\"before\":"+CommandLocationJson(before)+",\"after\":"+CommandLocationJson(after)+"}";
}
