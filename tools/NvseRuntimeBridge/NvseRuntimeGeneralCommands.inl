// Published xNVSE script-command table: PluginAPI.h 527-531, CommandTable.h 0x28 ABI.
// No private dispatch offsets, argument extraction, generated machine code or SCDA normalization.
constexpr size_t GeneralCommandCapacity=4096,GeneralCommandTableLimit=65536;
struct GeneralCommandHook {
    CommandInfo* entry=nullptr;CommandExecute original=nullptr;
    UInt32 opcode=0,returnType=UINT32_MAX;
    std::string name;
};
struct GeneralCommandCoverageRow {
    UInt32 entryAddress=0,opcode=0;std::string name;
    const char* status="unavailable";
};
std::array<GeneralCommandHook,GeneralCommandCapacity> g_generalCommands;
std::vector<GeneralCommandCoverageRow> g_generalCoverageRows;
std::atomic<size_t> g_generalInstalled{0};
std::atomic<bool> g_generalCoverageReady{false};
bool g_generalAttempted=false;
UInt32 g_generalTableStart=0,g_generalTableEnd=0;
std::uint64_t g_generalPublishedEntries=UINT64_MAX;
const char* g_generalTableStatus="not-installed";

bool GeneralCommandExecute(size_t slot,void* caller,ParamInfo* parameters,void* data,TESObjectREFR* reference,
    TESObjectREFR* containing,Script* script,ScriptEventList* events,double* result,UInt32* offset) {
    const auto& hook=g_generalCommands[slot];
    const auto incomingError=GetLastError();
    struct RestoreError {DWORD value;~RestoreError(){SetLastError(value);}} restoreError{incomingError};
    CommandFloatingGuard floating;
    const auto admission=AdmitCommand();
    bool admitted=admission.generation!=0;
    CommandLocationSample before;std::string context;
    if(admitted)try {
        admitted=TraceSelectedScript(script);
        if(admitted) {
            before=SampleCommandLocation(data,script,offset);
            UInt32 owner=0;bool ownerKnown=false;
            context=CallContext(reference,script,caller,owner,ownerKnown,false);
            context+=",\"eventListAddress\":"+std::to_string(reinterpret_cast<UInt32>(events));
            UInt32 id=0;std::uint8_t type=0;
            if(ReadFormIdentity(containing,id,type))context+=",\"containerFormId\":"+std::to_string(id);
        }
    } catch(...) {admitted=false;DropCommandObservation(admission.generation);}
    SetLastError(incomingError);
    floating.BeforeOriginal();
    const bool returned=hook.original(parameters,data,reference,containing,script,events,result,offset);
    floating.AfterOriginal();
    restoreError.value=GetLastError();
    if(!admitted || !g_capture || !g_connected || admission.generation!=g_captureGeneration.load())return returned;
    try {
        const auto after=SampleCommandLocation(data,script,offset);
        Emit("command-execute",admission.request,",\"command\":"+Quote(hook.name)+",\"opcode\":"+std::to_string(hook.opcode)+
            ",\"handlerReturned\":"+(returned?"true":"false")+context+CommandAdmissionFields(admission)+
            CommandResultFields(result,hook.returnType)+CommandLocationFields(before,after)+
            ",\"evidence\":\"sdk-table-execute-return\"",admission.generation);
    } catch(...) {DropCommandObservation(admission.generation);}
    return returned;
}
template<size_t Slot> __declspec(noinline) bool GeneralCommandWrapper(ParamInfo* parameters,void* data,
    TESObjectREFR* reference,TESObjectREFR* containing,Script* script,ScriptEventList* events,double* result,UInt32* offset) {
    return GeneralCommandExecute(Slot,_ReturnAddress(),parameters,data,reference,containing,script,events,result,offset);
}
template<size_t... Slots> constexpr auto GeneralCommandWrappers(std::index_sequence<Slots...>) {
    return std::array<CommandExecute,sizeof...(Slots)>{GeneralCommandWrapper<Slots>...};
}
const auto g_generalWrappers=GeneralCommandWrappers(std::make_index_sequence<GeneralCommandCapacity>{});

bool ReadCommandName(const char* pointer,std::string& result) {
    result.clear();
    const auto address=reinterpret_cast<UInt32>(pointer);
    for(UInt32 index=0;index<=128;++index) {
        char value=0;
        if(index>UINT32_MAX-address || !CommandReadBytes(address+index,&value,1))return false;
        if(!value)return true;
        if(index==128)return false;
        result.push_back(value);
    }
    return false;
}
bool GeneralSpecialEntry(const CommandInfo* entry) {
    for(size_t index=0;index<3;++index)
        if(g_commandHooks[index].entry==entry && (g_messageHookMask.load()&(1u<<index)))return true;
    return false;
}
void InstallGeneralCommandHooks() {
    if(g_generalAttempted)return;
    g_generalAttempted=true;
    try {
        if(!g_commands || !g_commands->Start || !g_commands->End || !g_commands->GetByOpcode) {
            g_generalTableStatus="sdk-table-unavailable";g_generalCoverageReady=true;return;
        }
        g_generalTableStart=reinterpret_cast<UInt32>(g_commands->Start());
        g_generalTableEnd=reinterpret_cast<UInt32>(g_commands->End());
        const auto bytes=std::uint64_t(g_generalTableEnd)-g_generalTableStart;
        if(g_generalTableStart && g_generalTableEnd>=g_generalTableStart && bytes%sizeof(CommandInfo)==0)
            g_generalPublishedEntries=bytes/sizeof(CommandInfo);
        if(!g_generalTableStart || g_generalTableEnd<g_generalTableStart || bytes%sizeof(CommandInfo) ||
            bytes/sizeof(CommandInfo)>GeneralCommandTableLimit) {
            g_generalTableStatus="invalid-or-oversized-table";g_generalCoverageReady=true;return;
        }
        const auto count=static_cast<size_t>(bytes/sizeof(CommandInfo));
        std::vector<CommandInfo> snapshots(count);
        std::unordered_map<UInt32,size_t> opcodeCounts;
        g_generalCoverageRows.reserve(count);
        for(size_t index=0;index<count;++index) {
            const auto address=g_generalTableStart+static_cast<UInt32>(index*sizeof(CommandInfo));
            auto& snapshot=snapshots[index];
            GeneralCommandCoverageRow row;row.entryAddress=address;
            if(!CommandReadBytes(address,&snapshot,sizeof(snapshot)))row.status="unreadable-entry";
            else {
                row.opcode=snapshot.opcode;++opcodeCounts[row.opcode];
                row.status=!ReadCommandName(snapshot.longName,row.name)?"unreadable-name":row.name.empty()?"unnamed-padding":"candidate";
            }
            g_generalCoverageRows.push_back(std::move(row));
        }
        bool partial=false;
        for(size_t index=0;index<count;++index) {
            auto& row=g_generalCoverageRows[index];const auto& snapshot=snapshots[index];
            if(strcmp(row.status,"candidate")) {if(strcmp(row.status,"unnamed-padding"))partial=true;continue;}
            auto entry=reinterpret_cast<CommandInfo*>(row.entryAddress);
            if(opcodeCounts[row.opcode]!=1)row.status="duplicate-opcode";
            else if(g_commands->GetByOpcode(row.opcode)!=entry)row.status="opcode-entry-mismatch";
            else if(GeneralSpecialEntry(entry))row.status="special-message-hook";
            else if(!snapshot.execute)row.status="no-execute-handler";
            else if(!ExecutableAddress(reinterpret_cast<void*>(snapshot.execute)))row.status="nonexecutable-handler";
            else if(g_generalInstalled>=GeneralCommandCapacity)row.status="wrapper-capacity";
            else {
                CommandInfo current{};
                if(!CommandReadBytes(row.entryAddress,&current,sizeof(current)) || current.opcode!=snapshot.opcode ||
                    current.longName!=snapshot.longName || current.execute!=snapshot.execute)row.status="entry-changed";
                else {
                    const auto slot=g_generalInstalled.load();auto& hook=g_generalCommands[slot];
                    hook.entry=entry;hook.original=snapshot.execute;hook.opcode=row.opcode;hook.name=row.name;
                    hook.returnType=g_commands->GetReturnType?g_commands->GetReturnType(entry):UINT32_MAX;
                    if(ReplacePointer(reinterpret_cast<void**>(&entry->execute),reinterpret_cast<void*>(hook.original),
                        reinterpret_cast<void*>(g_generalWrappers[slot]))) {++g_generalInstalled;row.status="installed";}
                    else row.status="replace-failed";
                }
            }
            if(strcmp(row.status,"installed") && strcmp(row.status,"special-message-hook") && strcmp(row.status,"no-execute-handler"))partial=true;
        }
        g_generalTableStatus=partial?"partial":"complete";
    } catch(...) {g_generalTableStatus="installation-failed";}
    // Readers only access the immutable table/coverage after this release publication.
    g_generalCoverageReady=true;
}
void RestoreGeneralCommandHooks() {
    if(!g_generalCoverageReady)return;
    for(size_t slot=0;slot<g_generalInstalled;++slot) {
        const auto& hook=g_generalCommands[slot];
        ReplacePointer(reinterpret_cast<void**>(&hook.entry->execute),reinterpret_cast<void*>(g_generalWrappers[slot]),
            reinterpret_cast<void*>(hook.original));
    }
    // Metadata and wrapper targets stay alive for already-entered calls until process exit.
}
std::string GeneralCommandCoverage() {
    if(!g_generalCoverageReady)return ",\"commandTraceCoverage\":{\"schema\":\"bmt/command-hooks\",\"version\":1,\"status\":\"not-installed\"}";
    std::map<std::string,size_t> reasons;size_t special=0,skipped=0;
    for(const auto& row:g_generalCoverageRows) {
        if(!strcmp(row.status,"special-message-hook"))++special;
        else if(strcmp(row.status,"installed")){++reasons[row.status];++skipped;}
    }
    std::string counts;for(const auto& item:reasons){if(!counts.empty())counts+=',';counts+=Quote(item.first)+':'+std::to_string(item.second);}
    return ",\"commandTraceCoverage\":{\"schema\":\"bmt/command-hooks\",\"version\":1,\"status\":"+Quote(g_generalTableStatus)+
        ",\"basis\":\"sdk-Start-End-GetByOpcode\",\"tableStartAddress\":"+std::to_string(g_generalTableStart)+
        ",\"tableEndAddress\":"+std::to_string(g_generalTableEnd)+",\"tableEntries\":"+std::to_string(g_generalCoverageRows.size())+
        ",\"publishedTableEntries\":"+(g_generalPublishedEntries==UINT64_MAX?"null":std::to_string(g_generalPublishedEntries))+
        ",\"installed\":"+std::to_string(g_generalInstalled.load())+",\"special\":"+std::to_string(special)+
        ",\"skipped\":"+std::to_string(skipped)+",\"reasonCounts\":{"+counts+"},\"capacity\":"+std::to_string(GeneralCommandCapacity)+
        ",\"scanLimit\":"+std::to_string(GeneralCommandTableLimit)+",\"detailsKind\":\"command-hook-coverage\",\"detailsTruncated\":false,\"scope\":\"scriptTraceScope\",\"snapshot\":\"installation\",\"consoleTableIncluded\":false}";
}
void EmitGeneralCommandCoverage(std::uint64_t request,std::uint64_t generation) {
    if(!g_generalCoverageReady)return;
    constexpr size_t perPart=64;
    const auto parts=(g_generalCoverageRows.size()+perPart-1)/perPart;
    for(size_t start=0;start<g_generalCoverageRows.size();start+=perPart) {
        std::string rows;
        for(size_t index=start;index<std::min(start+perPart,g_generalCoverageRows.size());++index) {
            const auto& row=g_generalCoverageRows[index];if(!rows.empty())rows+=',';
            rows+="{\"entryAddress\":"+std::to_string(row.entryAddress)+",\"opcode\":"+std::to_string(row.opcode)+
                ",\"command\":"+Quote(row.name)+",\"status\":"+Quote(row.status)+"}";
        }
        Emit("command-hook-coverage",request,",\"schema\":\"bmt/command-hooks\",\"version\":1,\"part\":"+
            std::to_string(start/perPart)+",\"parts\":"+std::to_string(parts)+",\"truncated\":false,\"entries\":["+rows+"]",generation);
    }
}
