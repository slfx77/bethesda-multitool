// PC 1.4.0.525 layouts from the pinned xNVSE GameAPI/GameForms/GameScript/NiTypes sources.
// Reads are bounded and checked. Mutations use the public SDK's script calls, never raw storage writes.
bool g_pcLayoutVerified=false;
std::string g_executableSha256;
std::unordered_map<std::string, Script*> g_questFunctions;
template<class T> bool ReadRuntime(std::uintptr_t address, T& value) {
    SIZE_T read=0;
    return address && ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),&value,sizeof(value),&read) && read==sizeof(value);
}
std::string ExecutableHash() {
    wchar_t path[32768]{};
    if (!GetModuleFileNameW(nullptr,path,32768)) return {};
    HANDLE file=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if (file==INVALID_HANDLE_VALUE) return {};
    BCRYPT_ALG_HANDLE algorithm=nullptr; BCRYPT_HASH_HANDLE hash=nullptr;
    bool valid=BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0 &&
        BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)>=0;
    std::uint8_t block[16384]{}; DWORD count=0;
    while (valid) {
        if (!ReadFile(file,block,sizeof(block),&count,nullptr)) { valid=false; break; }
        if (!count) break;
        valid=BCryptHashData(hash,block,count,0)>=0;
    }
    std::uint8_t digest[32]{};
    if (valid) valid=BCryptFinishHash(hash,digest,sizeof(digest),0)>=0;
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm,0);
    CloseHandle(file);
    if (!valid) return {};
    std::ostringstream result;
    for (const auto byte:digest) result<<std::hex<<std::setw(2)<<std::setfill('0')<<unsigned(byte);
    return result.str();
}
void* LookupRuntimeFormAt(std::uintptr_t mapAddress, UInt32 formId) {
    // NiTPointerMap: vtable, bucket count, bucket pointer, item count.
    UInt32 map[4]{};
    if (!ReadRuntime(mapAddress,map) || !map[1] || map[1]>0x100000 || !map[2]) return nullptr;
    UInt32 node=0;
    if (!ReadRuntime(std::uintptr_t(map[2])+4*(formId%map[1]),node)) return nullptr;
    std::unordered_set<UInt32> seen;
    for (size_t count=0;node && count<4096;++count) {
        UInt32 entry[3]{};
        if (!seen.insert(node).second || !ReadRuntime(node,entry)) return nullptr;
        if (entry[1]==formId) {
            UInt32 actual=0; std::uint8_t type=0;
            auto form=reinterpret_cast<void*>(entry[2]);
            return ReadFormIdentity(form,actual,type) && actual==formId ? form : nullptr;
        }
        node=entry[0];
    }
    return nullptr;
}
void* LookupRuntimeForm(UInt32 formId) {
    UInt32 map=0;
    return g_pcLayoutVerified && ReadRuntime(0x011C54C0,map) ? LookupRuntimeFormAt(map,formId) : nullptr;
}
bool VerifyRuntimeFormMap() {
    if (!g_pcLayoutVerified || !g_script || !g_numericEnabled) return false;
    auto found=g_expressions.find("player");
    if (found==g_expressions.end()) {
        if (g_expressions.size()>=MaxExpressions) return false;
        auto compiled=g_script->CompileExpression("player");
        if (!compiled) return false;
        found=g_expressions.emplace("player",compiled).first;
    }
    NumericElement result{};
    return g_script->CallFunction(found->second,nullptr,nullptr,&result,0) && result.type==2 && result.form &&
        result.form==LookupRuntimeForm(0x14);
}
struct QuestVariableStorage { UInt32 scriptId=0,index=0; std::uintptr_t address=0; double value=0; };
bool FindQuestVariableStorage(void* quest, const std::string& name, QuestVariableStorage& result) {
    UInt32 script=0,eventList=0,eventScript=0;
    const auto base=reinterpret_cast<std::uintptr_t>(quest);
    if (!ReadRuntime(base+0x1C,script) || !script || !ReadRuntime(base+0x5C,eventList) || !eventList ||
        !ReadRuntime(eventList,eventScript) || eventScript!=script) return false;
    std::uint8_t scriptType=0;
    if (!ReadFormIdentity(reinterpret_cast<void*>(script),result.scriptId,scriptType) || scriptType!=0x11) return false;
    UInt32 node=script+0x4C; std::unordered_set<UInt32> seen; bool found=false;
    for (size_t count=0;node && count<4096;++count) {
        UInt32 link[2]{},meta[8]{};
        if (!seen.insert(node).second || !ReadRuntime(node,link)) return false;
        if (link[0]) {
            if (!ReadRuntime(link[0],meta)) return false;
            const auto length=meta[7]&0xFFFF;
            if (length==name.size() && length<=128 && meta[6]) {
                char buffer[129]{}; SIZE_T read=0;
                if (!ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(meta[6]),buffer,length,&read) || read!=length) return false;
                if (!_stricmp(buffer,name.c_str())) { result.index=meta[0]; found=true; break; }
            }
        }
        node=link[1];
    }
    if (!found) return false;
    // SLSD flag zero can also mean ref: SCRV-style runtime reference metadata is authoritative.
    node=script+0x44; seen.clear();
    for (size_t count=0;node && count<4096;++count) {
        UInt32 link[2]{},refIndex=0;
        if (!seen.insert(node).second || !ReadRuntime(node,link)) return false;
        if (link[0] && (!ReadRuntime(std::uintptr_t(link[0])+0xC,refIndex) || refIndex==result.index)) return false;
        node=link[1];
    }
    if (node) return false;
    if (!ReadRuntime(std::uintptr_t(eventList)+0xC,node)) return false;
    seen.clear();
    for (size_t count=0;node && count<4096;++count) {
        UInt32 link[2]{},index=0;
        if (!seen.insert(node).second || !ReadRuntime(node,link)) return false;
        if (link[0]) {
            if (!ReadRuntime(link[0],index)) return false;
            if (index==result.index) {
                result.address=std::uintptr_t(link[0])+8;
                return ReadRuntime(result.address,result.value) && std::isfinite(result.value);
            }
        }
        node=link[1];
    }
    return false;
}
bool ParseQuestPayload(const Request& request, std::vector<std::string>& parts, UInt32& localId, double& value) {
    std::istringstream input(request.payload); std::string part;
    while (std::getline(input,part,'\t')) parts.push_back(part);
    if (parts.size()!=(request.kind==11?4u:3u) || parts[0].size()<5 || parts[0].size()>255 ||
        parts[0].find_first_of("\"\\/:")!=std::string::npos ||
        !std::all_of(parts[0].begin(),parts[0].end(),[](unsigned char c){return c>=32 && c<127;}) ||
        (_stricmp(parts[0].c_str()+parts[0].size()-4,".esm") && _stricmp(parts[0].c_str()+parts[0].size()-4,".esp")) ||
        !std::regex_match(parts[1],std::regex("^[0-9a-fA-F]{1,6}$")) ||
        !std::regex_match(parts[2],std::regex("^[A-Za-z_][A-Za-z_0-9]{0,127}$"))) return false;
    localId=static_cast<UInt32>(strtoul(parts[1].c_str(),nullptr,16));
    if (!localId) return false;
    if (request.kind==11) {
        if (parts[3].empty() || parts[3].size()>64) return false;
        char* end=nullptr; value=strtod(parts[3].c_str(),&end);
        if (!end || *end || !std::isfinite(value) || std::abs(value)>std::numeric_limits<float>::max()) return false;
    }
    return true;
}
void QuestVariable(const Request& request) {
    auto fail=[&](const char* reason){Emit("error",request.id,",\"error\":"+Quote(reason));};
    if (!g_capture || !g_loadedGameObserved || !g_script || !g_script->CompileScript || !VerifyRuntimeFormMap()) {
        fail("quest-runtime-profile-unavailable"); return;
    }
    std::vector<std::string> parts; UInt32 localId=0; double requested=0;
    if (!ParseQuestPayload(request,parts,localId,requested)) { fail("invalid-quest-request"); return; }
    UInt32 pluginIndex=255;
    if (!PluginNumber("GetModIndex \""+parts[0]+"\"",pluginIndex) || pluginIndex>=255) { fail("quest-plugin-not-loaded"); return; }
    const UInt32 formId=(pluginIndex<<24)|localId;
    auto quest=LookupRuntimeForm(formId); UInt32 actual=0; std::uint8_t type=0;
    if (!ReadFormIdentity(quest,actual,type) || actual!=formId || type!=0x47) { fail("quest-form-unavailable-or-type-mismatch"); return; }
    QuestVariableStorage storage{};
    if (!FindQuestVariableStorage(quest,parts[2],storage)) { fail("quest-numeric-variable-storage-unavailable"); return; }
    const bool write=request.kind==11;
    const auto key=parts[2]+(write?":set":":get");
    auto function=g_questFunctions.find(key);
    if (function==g_questFunctions.end()) {
        if (g_questFunctions.size()>=128) { fail("quest-function-cache-limit"); return; }
        const std::string text="ref target\nfloat newValue\nbegin function { target newValue }\n"+
            std::string(write?"SetVariable \""+parts[2]+"\" newValue target\n":"")+
            "SetFunctionValue (GetVariable \""+parts[2]+"\" target)\nend\n";
        auto compiled=g_script->CompileScript(text.c_str());
        if (!compiled) { fail("quest-function-compile-failed"); return; }
        function=g_questFunctions.emplace(key,compiled).first;
    }
    float narrowed=static_cast<float>(requested); UInt32 bits=0; memcpy(&bits,&narrowed,sizeof(bits));
    NumericElement result{};
    const bool returned=g_script->CallFunction(function->second,nullptr,nullptr,&result,2,quest,bits);
    double after=0;
    if (!returned || result.type!=1 || !std::isfinite(result.number) || !ReadRuntime(storage.address,after) || !std::isfinite(after) || after!=result.number) {
        fail("quest-sdk-and-storage-results-unavailable-or-disagree"); return;
    }
    const std::string identity=",\"engineTargetFormId\":"+std::to_string(actual)+",\"engineTargetFormType\":71,\"scriptFormId\":"+
        std::to_string(storage.scriptId)+",\"variableIndex\":"+std::to_string(storage.index)+",\"plugin\":"+Quote(parts[0])+
        ",\"localFormId\":"+std::to_string(localId)+",\"statistic\":"+Quote(parts[2])+",\"targetKind\":\"quest\",\"component\":\"variable\"";
    Emit("snapshot",request.id,identity+",\"value\":"+NumberField(after)+",\"evidence\":\"sdk-variable-return-and-runtime-storage\"");
    if (write) Emit("action-result",request.id,identity+",\"accepted\":"+(after==double(narrowed)?std::string("true"):std::string("false"))+
        ",\"before\":"+NumberField(storage.value)+",\"after\":"+NumberField(after)+",\"requested\":"+NumberField(requested)+
        ",\"evidence\":\"sdk-variable-write-and-observed-readback\"");
}
