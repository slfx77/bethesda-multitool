// DRAFT game-thread installer; never invoked while ConditionPilotReviewed is false.
// Follows the existing ScriptTrace thread-handle/context admission, generalized to
// four exact CALLs plus two six-byte OR probes and unique executable pages.
namespace condition_patch = bmt::ctda::patch;
enum class ConditionPatchPhase { NotAttempted, ProfileRejected, ThreadRejected, PageRejected,
    TransactionFailed, ProtectionRestoreFailed, ResumeFailed, Installed, Restored };
struct ConditionPatchReport {
    ConditionPatchPhase phase=ConditionPatchPhase::NotAttempted;
    condition_patch::Result transaction;
    UInt32 threads=0,suspended=0,resumeFailures=0,protectionRestoreFailures=0;
    UInt32 nativeThreadCheck=0,nativeQueryStatus=0;
    bool restoring=false;
};
std::atomic<UInt32> g_conditionPatchReportSequence{0};
std::array<std::atomic<UInt32>,16> g_conditionPatchReportWords{};
bool g_conditionPatchAttempted=false;
std::atomic<bool> g_conditionPatchMayBeOwned{false};
struct ConditionThreadSet {
    std::array<HANDLE,256> handles{};
    std::array<DWORD,256> ids{};
    size_t count=0,suspended=0;
};
struct ConditionPage {
    UInt32 address=0;SIZE_T length=0;DWORD protection=0;bool writable=false;
};
struct ConditionPages {std::array<ConditionPage,8> entries{};size_t count=0;};
using ConditionQuerySystemInformation=NTSTATUS (NTAPI*)(SYSTEM_INFORMATION_CLASS,PVOID,ULONG,PULONG);
static_assert(sizeof(SYSTEM_PROCESS_INFORMATION)==0xB8 && offsetof(SYSTEM_PROCESS_INFORMATION,UniqueProcessId)==0x44);
static_assert(sizeof(SYSTEM_THREAD_INFORMATION)==0x40 && offsetof(SYSTEM_THREAD_INFORMATION,ClientId)==0x20);
struct ConditionNativeThreads {
    ConditionQuerySystemInformation query=nullptr;
    void* buffer=nullptr;
    ULONG capacity=4*1024*1024;
    DWORD process=0,current=0;
    UInt32 check=0,status=0;
};
bool ConditionPrepareNativeThreads(ConditionNativeThreads& native) noexcept {
    const auto module=GetModuleHandleW(L"ntdll.dll");
    native.query=module?reinterpret_cast<ConditionQuerySystemInformation>(GetProcAddress(module,"NtQuerySystemInformation")):nullptr;
    native.process=GetCurrentProcessId();native.current=GetCurrentThreadId();
    if(!native.query){native.check=1;return false;}
    native.buffer=VirtualAlloc(nullptr,native.capacity,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE);
    if(!native.buffer){native.check=1;return false;}
    return true;
}
bool ConditionMatchNativeThreads(const void* buffer,ULONG returned,const ConditionThreadSet& set,DWORD process,DWORD current) noexcept {
    if(!buffer || returned<sizeof(SYSTEM_PROCESS_INFORMATION))return false;
    const auto* bytes=static_cast<const std::uint8_t*>(buffer);size_t offset=0;unsigned records=0;
    while(offset<=returned-sizeof(SYSTEM_PROCESS_INFORMATION) && ++records<=65536) {
        const auto* info=reinterpret_cast<const SYSTEM_PROCESS_INFORMATION*>(bytes+offset);
        const size_t length=info->NextEntryOffset?info->NextEntryOffset:returned-offset;
        if(length<sizeof(*info) || length>returned-offset ||
            (info->NextEntryOffset && (info->NextEntryOffset&(alignof(SYSTEM_PROCESS_INFORMATION)-1))))return false;
        if(reinterpret_cast<UInt32>(info->UniqueProcessId)==process) {
            if(info->NumberOfThreads!=set.count+1 || info->NumberOfThreads>257 ||
                info->NumberOfThreads>(length-sizeof(*info))/sizeof(SYSTEM_THREAD_INFORMATION))return false;
            const auto* threads=reinterpret_cast<const SYSTEM_THREAD_INFORMATION*>(bytes+offset+sizeof(*info));
            std::array<bool,256> seen{};bool haveCurrent=false;
            for(ULONG index=0;index<info->NumberOfThreads;++index) {
                if(reinterpret_cast<UInt32>(threads[index].ClientId.UniqueProcess)!=process)return false;
                const DWORD id=reinterpret_cast<UInt32>(threads[index].ClientId.UniqueThread);
                if(id==current){if(haveCurrent)return false;haveCurrent=true;continue;}
                size_t match=0;while(match<set.count && set.ids[match]!=id)++match;
                if(match==set.count || seen[match])return false;seen[match]=true;
            }
            return haveCurrent; // Exact count + unique IDs proves every retained thread was present.
        }
        if(!info->NextEntryOffset)return false;
        offset+=info->NextEntryOffset;
    }
    return false;
}
bool ConditionVerifyFrozenThreads(ConditionNativeThreads& native,const ConditionThreadSet& set) noexcept {
    ULONG returned=0;
    const auto status=native.query(SystemProcessInformation,native.buffer,native.capacity,&returned);
    native.status=static_cast<UInt32>(status);
    if(status<0 || !returned || returned>native.capacity){native.check=2;return false;}
    const bool matched=ConditionMatchNativeThreads(native.buffer,returned,set,native.process,native.current);
    native.check=matched?4u:3u;return matched;
}

bool ConditionPatchRead(void*,UInt32 address,condition_patch::Bytes& bytes,size_t length) noexcept {
    bytes={};return ConditionRead(nullptr,address,bytes.data(),length);
}
bool ConditionPatchWrite(void*,UInt32 address,const condition_patch::Bytes& bytes,size_t length) noexcept {
    SIZE_T count=0;
    return WriteProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),bytes.data(),length,&count) && count==length;
}
bool ConditionPatchFlush(void*,UInt32 address,size_t length) noexcept {
    return FlushInstructionCache(GetCurrentProcess(),reinterpret_cast<void*>(address),length)!=FALSE;
}
std::array<condition_patch::Span,condition_patch::Count> ConditionPatchPlan(bool restoring) noexcept {
    std::array<condition_patch::Span,condition_patch::Count> result{};
    for(size_t index=0;index<result.size();++index) {
        auto& span=result[index];
        if(index<4) {
            span.address=ConditionCallSites[index];span.length=5;
            memcpy(span.before.data(),bmt::ctda::RetainedSpans[index].bytes.data(),span.length);
            const auto bytes=ConditionOwnedCall(index);memcpy(span.after.data(),bytes.data(),bytes.size());
        } else {
            span.address=ConditionFrameSites[index-4];span.length=6;span.before=ConditionFrameOriginal;
            span.after=ConditionOwnedFrame(index-4);
        }
        if(restoring)std::swap(span.before,span.after);
    }
    return result;
}
void ConditionCloseThreads(ConditionThreadSet& set) noexcept {
    for(size_t index=0;index<set.count;++index)if(set.handles[index])CloseHandle(set.handles[index]);
    set={};
}
bool ConditionPrepareThreads(ConditionThreadSet& set) noexcept {
    // Open all handles and obtain two matching bounded inventories before suspension.
    // There is no heap allocation or snapshot API after the first SuspendThread.
    const DWORD process=GetCurrentProcessId(),current=GetCurrentThreadId();
    auto snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
    if(snapshot==INVALID_HANDLE_VALUE)return false;
    THREADENTRY32 entry{};entry.dwSize=sizeof(entry);bool valid=Thread32First(snapshot,&entry)!=FALSE;
    if(valid)do {
        if(entry.th32OwnerProcessID!=process || entry.th32ThreadID==current)continue;
        if(set.count==set.handles.size()){valid=false;break;}
        const auto handle=OpenThread(THREAD_SUSPEND_RESUME|THREAD_GET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,entry.th32ThreadID);
        if(!handle){valid=false;break;}
        set.ids[set.count]=entry.th32ThreadID;set.handles[set.count++]=handle;
    }while(Thread32Next(snapshot,&entry));
    if(valid && GetLastError()!=ERROR_NO_MORE_FILES)valid=false;
    CloseHandle(snapshot);
    if(!valid){ConditionCloseThreads(set);return false;}
    snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
    if(snapshot==INVALID_HANDLE_VALUE){ConditionCloseThreads(set);return false;}
    std::array<bool,256> seen{};size_t observed=0;entry={};entry.dwSize=sizeof(entry);
    valid=Thread32First(snapshot,&entry)!=FALSE;
    if(valid)do {
        if(entry.th32OwnerProcessID!=process || entry.th32ThreadID==current)continue;
        size_t index=0;while(index<set.count && set.ids[index]!=entry.th32ThreadID)++index;
        if(index==set.count || seen[index]){valid=false;break;}
        seen[index]=true;++observed;
    }while(Thread32Next(snapshot,&entry));
    if(valid && GetLastError()!=ERROR_NO_MORE_FILES)valid=false;
    CloseHandle(snapshot);
    valid=valid && observed==set.count;
    for(size_t index=0;valid && index<set.count;++index) {
        DWORD code=0;valid=GetExitCodeThread(set.handles[index],&code)!=FALSE && code==STILL_ACTIVE;
    }
    if(!valid)ConditionCloseThreads(set);
    return valid;
}
bool ConditionInstructionInPlan(UInt32 ip,const std::array<condition_patch::Span,condition_patch::Count>& spans) noexcept {
    for(const auto& span:spans)if(ip>=span.address && ip-span.address<span.length)return true;
    return false;
}
bool ConditionSuspendThreads(ConditionThreadSet& set,const std::array<condition_patch::Span,condition_patch::Count>& spans) noexcept {
    for(size_t index=0;index<set.count;++index) {
        if(SuspendThread(set.handles[index])==DWORD(-1))return false;
        ++set.suspended; // Exactly one matching ResumeThread even if context read fails.
        CONTEXT context{};context.ContextFlags=CONTEXT_CONTROL;
        if(!GetThreadContext(set.handles[index],&context))return false;
        if(ConditionInstructionInPlan(context.Eip,spans))return false;
    }
    return true;
}
UInt32 ConditionResumeThreads(ConditionThreadSet& set) noexcept {
    UInt32 failures=0;
    for(size_t reverse=set.suspended;reverse>0;--reverse)
        if(ResumeThread(set.handles[reverse-1])==DWORD(-1))++failures;
    set.suspended=0;return failures;
}
bool ConditionPreparePages(ConditionPages& pages,const std::array<condition_patch::Span,condition_patch::Count>& spans) noexcept {
    SYSTEM_INFO info{};GetSystemInfo(&info);
    const UInt32 size=info.dwPageSize;
    if(!size || (size&(size-1))!=0)return false;
    const auto image=GetModuleHandleW(nullptr);
    for(const auto& span:spans)for(UInt32 address=span.address&~(size-1);address< span.address+span.length;address+=size) {
        bool present=false;for(size_t index=0;index<pages.count;++index)if(pages.entries[index].address==address)present=true;
        if(present)continue;
        if(pages.count==pages.entries.size())return false;
        MEMORY_BASIC_INFORMATION memory{};
        if(VirtualQuery(reinterpret_cast<void*>(address),&memory,sizeof(memory))!=sizeof(memory) ||
            memory.State!=MEM_COMMIT || memory.Type!=MEM_IMAGE || memory.AllocationBase!=image ||
            (memory.Protect&(PAGE_GUARD|PAGE_NOACCESS)) ||
            !(memory.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY)))return false;
        pages.entries[pages.count++]={address,size,memory.Protect,false};
    }
    return true;
}
bool ConditionOpenPages(ConditionPages& pages) noexcept {
    for(size_t index=0;index<pages.count;++index) {
        auto& page=pages.entries[index];DWORD previous=0;
        if(!VirtualProtect(reinterpret_cast<void*>(page.address),page.length,PAGE_EXECUTE_READWRITE,&previous))return false;
        page.writable=true;
        // Admission remembered the actual original protection. A change before this
        // call refuses the transaction; cleanup restores the immediately prior value.
        if(previous!=page.protection){page.protection=previous;return false;}
    }
    return true;
}
UInt32 ConditionClosePages(ConditionPages& pages) noexcept {
    UInt32 failures=0;
    for(size_t reverse=pages.count;reverse>0;--reverse) {
        auto& page=pages.entries[reverse-1];if(!page.writable)continue;
        DWORD previous=0;
        if(!VirtualProtect(reinterpret_cast<void*>(page.address),page.length,page.protection,&previous))++failures;
        else page.writable=false;
    }
    return failures;
}
ConditionPatchReport ConditionTransactCalls(bool restoring) noexcept {
    ConditionPatchReport report;report.restoring=restoring;
    const auto spans=ConditionPatchPlan(restoring);
    ConditionThreadSet threads;ConditionPages pages;ConditionNativeThreads native;
    if(!ConditionPrepareNativeThreads(native)){report.phase=ConditionPatchPhase::ThreadRejected;report.nativeThreadCheck=native.check;return report;}
    if(!ConditionPrepareThreads(threads)){report.phase=ConditionPatchPhase::ThreadRejected;VirtualFree(native.buffer,0,MEM_RELEASE);return report;}
    report.threads=static_cast<UInt32>(threads.count);
    if(!ConditionPreparePages(pages,spans) || !ConditionOpenPages(pages)) {
        report.phase=ConditionPatchPhase::PageRejected;
        report.protectionRestoreFailures=ConditionClosePages(pages);ConditionCloseThreads(threads);VirtualFree(native.buffer,0,MEM_RELEASE);return report;
    }
    // Frozen section: fixed stack storage and Win32 context/memory/protection calls
    // only. No SDK call, JSON formatting, Emit, heap work or bridge lock is allowed.
    const bool admitted=ConditionSuspendThreads(threads,spans) && ConditionVerifyFrozenThreads(native,threads);
    report.suspended=static_cast<UInt32>(threads.suspended);
    report.nativeThreadCheck=native.check;report.nativeQueryStatus=native.status;
    if(admitted) {
        report.transaction=condition_patch::Apply(spans,{nullptr,ConditionPatchRead,ConditionPatchWrite,ConditionPatchFlush});
        report.phase=report.transaction.status==condition_patch::Status::Committed?
            (restoring?ConditionPatchPhase::Restored:ConditionPatchPhase::Installed):ConditionPatchPhase::TransactionFailed;
    } else report.phase=ConditionPatchPhase::ThreadRejected;
    report.protectionRestoreFailures=ConditionClosePages(pages);
    report.resumeFailures=ConditionResumeThreads(threads);
    ConditionCloseThreads(threads);
    VirtualFree(native.buffer,0,MEM_RELEASE);
    if(report.protectionRestoreFailures)report.phase=ConditionPatchPhase::ProtectionRestoreFailed;
    if(report.resumeFailures)report.phase=ConditionPatchPhase::ResumeFailed;
    return report;
}
void ConditionPublishPatchReport(const ConditionPatchReport& report) {
    // Even ResumeThread failure must not take a mutex a stranded thread may own.
    const std::array<UInt32,16> words{{static_cast<UInt32>(report.phase),static_cast<UInt32>(report.transaction.status),
        static_cast<UInt32>(report.transaction.failure),report.threads,report.suspended,report.resumeFailures,
        report.protectionRestoreFailures,report.restoring?1u:0u,report.transaction.attemptedMask,
        report.transaction.changedMask,report.transaction.restoredMask,report.transaction.conflictMask,
        report.transaction.bytesRestored?1u:0u,report.transaction.cacheFlushed?1u:0u,report.nativeThreadCheck,report.nativeQueryStatus}};
    ++g_conditionPatchReportSequence;
    for(size_t index=0;index<words.size();++index)g_conditionPatchReportWords[index].store(words[index]);
    ++g_conditionPatchReportSequence;
}
void InstallConditionPilotCalls() {
    if(!ConditionPilotReviewed || !ConditionFramesReviewed || g_conditionPatchAttempted || !g_loadedGameObserved || !g_pcLayoutVerified || g_conditionInvocation)return;
    g_conditionPatchAttempted=true;
    ConditionLastError error{GetLastError()};CommandFloatingGuard floating;
    UInt32 table=0;ConditionOriginalEval original;ConditionPatchReport report;
    if(!ConditionReadTable(table) || !ConditionOriginalEvalRead(original) || !ConditionCodeMatches(table,false)) {
        report.phase=ConditionPatchPhase::ProfileRejected;ConditionPublishPatchReport(report);return;
    }
    // Wrappers are never freed after patching, including rollback/restoration failure.
    HMODULE pinned=nullptr;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(ConditionWrapper(0)),&pinned)) {
        report.phase=ConditionPatchPhase::ProfileRejected;ConditionPublishPatchReport(report);return;
    }
    report=ConditionTransactCalls(false);
    g_conditionPatchMayBeOwned=report.transaction.attemptedMask!=0 && !report.transaction.bytesRestored;
    const bool installed=report.phase==ConditionPatchPhase::Installed && ConditionSameOriginalEval(original) && ConditionConfirmInstallation();
    g_conditionCallsInstalled=installed;
    if(report.phase==ConditionPatchPhase::Installed && !installed)report.phase=ConditionPatchPhase::ProfileRejected;
    ConditionPublishPatchReport(report);
}
void RestoreConditionPilotCalls() {
    g_conditionCallsInstalled=false;
    if(!g_conditionPatchMayBeOwned || g_conditionInvocation)return;
    ConditionLastError error{GetLastError()};CommandFloatingGuard floating;
    // Requires every installed span still be ours. Never overwrite a later hook.
    if(!ConditionOwnsCalls()) {
        ConditionPatchReport report;report.restoring=true;report.phase=ConditionPatchPhase::ProfileRejected;
        report.transaction.status=condition_patch::Status::OwnershipMismatch;ConditionPublishPatchReport(report);return;
    }
    const auto report=ConditionTransactCalls(true);
    if(report.phase==ConditionPatchPhase::Restored)g_conditionPatchMayBeOwned=false;
    ConditionPublishPatchReport(report);
}
std::string ConditionPatchDiagnostics() {
    std::array<UInt32,16> words{};bool observed=false;
    for(unsigned attempt=0;attempt<3 && !observed;++attempt) {
        const auto sequence=g_conditionPatchReportSequence.load();if(sequence&1)continue;
        for(size_t index=0;index<words.size();++index)words[index]=g_conditionPatchReportWords[index].load();
        observed=g_conditionPatchReportSequence.load()==sequence;
    }
    if(!observed)return ",\"conditionPilotHookState\":{\"schemaVersion\":1,\"status\":\"unavailable-changing\"}";
    ConditionPatchReport report;report.phase=static_cast<ConditionPatchPhase>(words[0]);
    report.transaction.status=static_cast<condition_patch::Status>(words[1]);report.transaction.failure=static_cast<condition_patch::Status>(words[2]);
    report.threads=words[3];report.suspended=words[4];report.resumeFailures=words[5];report.protectionRestoreFailures=words[6];
    report.restoring=words[7]!=0;report.transaction.attemptedMask=words[8];report.transaction.changedMask=words[9];
    report.transaction.restoredMask=words[10];report.transaction.conflictMask=words[11];
    report.transaction.bytesRestored=words[12]!=0;report.transaction.cacheFlushed=words[13]!=0;
    report.nativeThreadCheck=words[14];report.nativeQueryStatus=words[15];
    return ",\"conditionPilotHookState\":{\"schemaVersion\":1,\"phaseCode\":"+std::to_string(static_cast<unsigned>(report.phase))+
        ",\"transactionCode\":"+std::to_string(static_cast<unsigned>(report.transaction.status))+
        ",\"failureCode\":"+std::to_string(static_cast<unsigned>(report.transaction.failure))+
        ",\"restoring\":"+(report.restoring?"true":"false")+",\"threadCount\":"+std::to_string(report.threads)+
        ",\"suspendedCount\":"+std::to_string(report.suspended)+",\"resumeFailures\":"+std::to_string(report.resumeFailures)+
        ",\"protectionRestoreFailures\":"+std::to_string(report.protectionRestoreFailures)+
        ",\"nativeThreadCheck\":"+std::to_string(report.nativeThreadCheck)+",\"nativeQueryStatus\":"+std::to_string(report.nativeQueryStatus)+
        ",\"attemptedMask\":"+std::to_string(report.transaction.attemptedMask)+",\"changedMask\":"+std::to_string(report.transaction.changedMask)+
        ",\"restoredMask\":"+std::to_string(report.transaction.restoredMask)+",\"conflictMask\":"+std::to_string(report.transaction.conflictMask)+
        ",\"bytesRestored\":"+(report.transaction.bytesRestored?"true":"false")+",\"cacheFlushed\":"+(report.transaction.cacheFlushed?"true":"false")+
        ",\"installed\":"+(g_conditionCallsInstalled?"true":"false")+
        ",\"retainedOwnedBytesPossible\":"+(g_conditionPatchMayBeOwned?"true":"false")+",\"scope\":\"four-call-sites-two-or-probes\"}";
}
