// Observation only: no patching, engine calls, or offset normalization here.
// The retained PC expression dispatcher and PC046's loaded window agree on these bounds.
constexpr UInt32 DispatchWindowAddress=0x005AC7A0,DispatchWindowLength=1161;
struct DispatchWindowSample {
    const char* status="unavailable";
    const char* reason="not-read";
    std::string bytesHex,sha256;
};
bool DispatchWindowMapped() {
    if(reinterpret_cast<std::uintptr_t>(GetModuleHandleW(nullptr))!=0x00400000)return false;
    MEMORY_BASIC_INFORMATION region{};
    if(VirtualQuery(reinterpret_cast<void*>(DispatchWindowAddress),&region,sizeof(region))!=sizeof(region))return false;
    const auto begin=reinterpret_cast<std::uintptr_t>(region.BaseAddress);
    const auto protection=region.Protect&0xFF;
    return region.State==MEM_COMMIT && region.Type==MEM_IMAGE &&
        reinterpret_cast<std::uintptr_t>(region.AllocationBase)==0x00400000 &&
        !(region.Protect&(PAGE_GUARD|PAGE_NOACCESS)) &&
        (protection==PAGE_EXECUTE_READ || protection==PAGE_EXECUTE_READWRITE || protection==PAGE_EXECUTE_WRITECOPY) &&
        begin<=DispatchWindowAddress && region.RegionSize>=DispatchWindowLength &&
        DispatchWindowAddress-begin<=region.RegionSize-DispatchWindowLength;
}
template<class Mapped,class Read> DispatchWindowSample ReadDispatchWindow(Mapped mapped,Read read) {
    DispatchWindowSample result;
    std::array<std::uint8_t,DispatchWindowLength> first{},second{};
    if(!mapped()){result.reason="main-image-region-unverified";return result;}
    if(!read(first.data(),first.size())){result.reason="first-read-unavailable";return result;}
    if(!read(second.data(),second.size())){result.reason="second-read-unavailable";return result;}
    if(!mapped()){result.reason="main-image-region-changed";return result;}
    if(first!=second){result.reason="bytes-changed-between-reads";return result;}
    result.sha256=CommandBytecodeHash(first.data(),DispatchWindowLength);
    if(result.sha256.empty()){result.reason="hash-unavailable";return result;}
    result.bytesHex=CommandHex(first.data(),first.size());
    result.status="observed";result.reason="repeated-main-image-read";return result;
}
// Hash the actual mapped bridge backing file, not the usvfs-visible loader path.
// This small module-only read is bounded; the executable hash remains the startup identity.
std::string DispatchBridgeHash(HMODULE module) {
    wchar_t mapped[32768]{};
    const DWORD length=K32GetMappedFileNameW(GetCurrentProcess(),module,mapped,32768);
    if(!length || length>=32768 || wcsncmp(mapped,L"\\Device\\",8))return {};
    const auto path=std::wstring(L"\\\\?\\GLOBALROOT")+mapped;
    HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return {};
    LARGE_INTEGER size{};
    bool valid=GetFileSizeEx(file,&size) && size.QuadPart>0 && size.QuadPart<=8*1024*1024;
    BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;
    valid=valid && BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0 &&
        BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)>=0;
    std::array<std::uint8_t,16384> bytes{};LONGLONG total=0;
    while(valid && total<size.QuadPart) {
        DWORD received=0;const auto count=static_cast<DWORD>(std::min<LONGLONG>(bytes.size(),size.QuadPart-total));
        valid=ReadFile(file,bytes.data(),count,&received,nullptr) && received==count;
        if(valid){valid=BCryptHashData(hash,bytes.data(),received,0)>=0;total+=received;}
    }
    std::uint8_t digest[32]{};
    if(valid)valid=BCryptFinishHash(hash,digest,sizeof(digest),0)>=0;
    if(hash)BCryptDestroyHash(hash);
    if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);
    CloseHandle(file);return valid?CommandHex(digest,sizeof(digest)):std::string{};
}
std::string DispatchFingerprintFields(const char* boundary,std::uint64_t generation,std::uint64_t connection) {
    const DWORD incomingError=GetLastError();
    CommandFloatingState floating;floating.Save();CommandObserverFloatingEnvironment();
    FILETIME created{},exited{},kernel{},user{};HMODULE bridge=nullptr;
    const bool identity=g_pcLayoutVerified && GetProcessTimes(GetCurrentProcess(),&created,&exited,&kernel,&user) &&
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&DispatchFingerprintFields),&bridge);
    const auto nativeHash=identity?DispatchBridgeHash(bridge):std::string{};
    const auto sample=identity && !nativeHash.empty()?ReadDispatchWindow(DispatchWindowMapped,[](void* data,size_t count) {
        return CommandReadBytes(DispatchWindowAddress,data,count);
    }):DispatchWindowSample{};
    const auto creation=(std::uint64_t(created.dwHighDateTime)<<32)|created.dwLowDateTime;
    const auto fields=",\"dispatchWindow\":{\"schemaVersion\":1,\"route\":\"pc-expression-X-command\",\"boundary\":"+Quote(boundary)+
        ",\"status\":"+Quote(sample.status)+",\"reason\":"+Quote(sample.reason)+
        ",\"processId\":"+std::to_string(GetCurrentProcessId())+",\"processCreationFileTime\":"+std::to_string(creation)+
        ",\"executableFileSha256\":"+Quote(g_executableSha256)+",\"nativeImageBase\":"+std::to_string(reinterpret_cast<std::uintptr_t>(bridge))+
        ",\"nativeSha256\":"+Quote(nativeHash)+",\"session\":"+Quote(g_session)+
        ",\"captureGeneration\":"+std::to_string(generation)+",\"connectionGeneration\":"+std::to_string(connection)+
        ",\"imageBase\":4194304,\"address\":"+std::to_string(DispatchWindowAddress)+",\"length\":"+std::to_string(DispatchWindowLength)+
        ",\"sha256\":"+Quote(sample.sha256)+",\"bytesHex\":"+Quote(sample.bytesHex)+"}";
    floating.Restore();SetLastError(incomingError);return fields;
}
