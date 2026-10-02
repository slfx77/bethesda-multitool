// Read-only PC membership observations. Pinned SDK: TESTopic (+2C list),
// Info (+04 NiTLargeArray), TESObjectCELL (+26 load state, +4C LAND).
// Preserve sparse slots and the actual pointer path; no response priority is inferred.
constexpr UInt32 MembershipMaxGroups=16, MembershipMaxSlots=256;
using MembershipReader=bool(*)(std::uintptr_t,void*,size_t);
bool MembershipRead(std::uintptr_t address,void* value,size_t length) {
    SIZE_T read=0;
    return address && length && address<=UINT32_MAX && length-1<=UINT32_MAX-address &&
        ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),value,length,&read) && read==length;
}
template<class T> bool MembershipReadAt(MembershipReader reader,std::uintptr_t address,T& value) {
    return address && address<=UINT32_MAX && sizeof(T)-1<=UINT32_MAX-address && reader(address,&value,sizeof(T));
}
bool MembershipIdentity(MembershipReader reader,UInt32 address,std::uint8_t type,UInt32& id,UInt32& flags) {
    std::uint8_t raw[16]{};
    if(!MembershipReadAt(reader,address,raw) || raw[4]!=type)return false;
    id=ActorRaw<UInt32>(raw,12);flags=ActorRaw<UInt32>(raw,8);return id!=0;
}
struct MembershipGroup {
    UInt32 address=0,questId=0,questFlags=0,slots=0,count=0,capacity=0,data=0;
    std::uint8_t raw[0x1C]{};
    bool valid=false,readable=false;
    std::string reason="unreadable-group";
};
MembershipGroup MembershipInspectGroup(MembershipReader reader,UInt32 address) {
    MembershipGroup result;result.address=address;
    if(!MembershipReadAt(reader,address,result.raw))return result;
    result.readable=true;
    if(!MembershipIdentity(reader,ActorRaw<UInt32>(result.raw,0),0x47,result.questId,result.questFlags)) {
        result.reason="quest-identity-unavailable";return result;
    }
    result.data=ActorRaw<UInt32>(result.raw,8);result.capacity=ActorRaw<UInt32>(result.raw,12);
    result.slots=ActorRaw<UInt32>(result.raw,16);result.count=ActorRaw<UInt32>(result.raw,20);
    if(result.count>result.slots || result.slots>result.capacity || (result.slots && !result.data)) {
        result.reason="invalid-array-header";return result;
    }
    if(result.slots>MembershipMaxSlots) {result.reason="slot-limit";return result;}
    result.valid=true;result.reason.clear();return result;
}
std::string MembershipCandidate(const char* path,const MembershipGroup& group) {
    return "{\"path\":"+Quote(path)+",\"address\":"+std::to_string(group.address)+
        ",\"status\":"+Quote(group.valid?"validated":"unavailable")+",\"reason\":"+
        (group.valid?"null":Quote(group.reason))+",\"rawHex\":"+
        (group.readable?Quote(ActorHex(group.raw,sizeof(group.raw))):"null")+"}";
}
std::string MembershipDialogue(UInt32 topic,MembershipReader reader=MembershipRead,UInt32 expectedId=0) {
    UInt32 topicId=0,flags=0;
    if(!MembershipIdentity(reader,topic,0x45,topicId,flags))return ActorUnavailable("topic-identity-unavailable");
    if(expectedId && topicId!=expectedId)return ActorUnavailable("topic-identity-changed-before-read");
    const auto first=std::uint64_t(topic)+0x2C;
    if(first>UINT32_MAX)return ActorUnavailable("topic-address-overflow");
    UInt32 node=static_cast<UInt32>(first),groupIndex=0,totalSlots=0;bool complete=true;
    std::string groups,reason;std::unordered_set<UInt32> seen;
    std::vector<std::pair<UInt32,std::pair<UInt32,UInt32>>> observedNodes;
    while(node) {
        if(groupIndex>=MembershipMaxGroups){complete=false;reason="group-limit";break;}
        if(!seen.insert(node).second){complete=false;reason="cyclic-group-list";break;}
        UInt32 link[2]{};
        if(!MembershipReadAt(reader,node,link)){complete=false;reason="group-list-unreadable";break;}
        observedNodes.push_back({node,{link[0],link[1]}});
        // The empty embedded head is a list sentinel. Preserve later null nodes as holes.
        if(!link[0] && !link[1] && groupIndex==0)break;
        if(!groups.empty())groups+=",";
        groups+="{\"index\":"+std::to_string(groupIndex++)+",\"nodeAddress\":"+std::to_string(node);
        if(!link[0])groups+=",\"status\":\"empty\",\"slots\":[]}";
        else {
            const auto direct=MembershipInspectGroup(reader,link[0]);
            UInt32 indirectAddress=0;MembershipReadAt(reader,link[0],indirectAddress);
            const auto indirect=MembershipInspectGroup(reader,indirectAddress);
            groups+=",\"candidates\":["+MembershipCandidate("node.data",direct)+","+
                MembershipCandidate("node.data->pointer",indirect)+"]";
            if(direct.valid==indirect.valid) {
                complete=false;
                groups+=",\"status\":"+Quote(direct.valid?"ambiguous":"unavailable")+",\"slots\":[]}";
            } else {
                const auto& group=direct.valid?direct:indirect;
                groups+=",\"pointerPath\":"+Quote(direct.valid?"node.data":"node.data->pointer")+
                    ",\"questFormId\":"+std::to_string(group.questId)+",\"capacity\":"+std::to_string(group.capacity)+
                    ",\"firstFreeEntry\":"+std::to_string(group.slots)+",\"reportedCount\":"+std::to_string(group.count)+",\"slots\":[";
                UInt32 nonNull=0;bool valid=true;std::string groupReason;
                std::vector<std::pair<UInt32,UInt32>> consumedSlots;
                struct InfoIdentity {UInt32 address,id,flags;};
                std::vector<InfoIdentity> consumedInfos;
                for(UInt32 slot=0;slot<group.slots;++slot) {
                    if(totalSlots>=MembershipMaxSlots){valid=false;groupReason="total-slot-limit";break;}
                    if(slot)groups+=",";
                    ++totalSlots;
                    UInt32 entry=0,info=0,infoFlags=0;
                    const auto at=std::uint64_t(group.data)+4ULL*slot;
                    groups+="{\"index\":"+std::to_string(slot);
                    if(at>UINT32_MAX || !MembershipReadAt(reader,static_cast<UInt32>(at),entry)) {
                        valid=false;groups+=",\"status\":\"unavailable\",\"reason\":\"slot-unreadable\"}";continue;
                    }
                    consumedSlots.push_back({static_cast<UInt32>(at),entry});
                    groups+=",\"address\":"+std::to_string(entry);
                    if(!entry){groups+=",\"status\":\"empty\",\"formId\":null}";continue;}
                    ++nonNull;
                    if(!MembershipIdentity(reader,entry,0x46,info,infoFlags)) {
                        valid=false;groups+=",\"status\":\"unavailable\",\"reason\":\"info-identity-unavailable\"}";continue;
                    }
                    consumedInfos.push_back({entry,info,infoFlags});
                    groups+=",\"status\":\"observed\",\"formId\":"+std::to_string(info)+",\"flags\":"+std::to_string(infoFlags)+"}";
                }
                std::uint8_t after[0x1C]{};
                if(!MembershipReadAt(reader,group.address,after) || memcmp(after,group.raw,sizeof(after))) {
                    valid=false;groupReason="group-changed-during-read";
                }
                if(!direct.valid) {
                    UInt32 current=0;
                    if(!MembershipReadAt(reader,link[0],current) || current!=group.address) {
                        valid=false;groupReason="indirect-path-changed-during-read";
                    }
                }
                UInt32 questId=0,questFlags=0;
                if(!MembershipIdentity(reader,ActorRaw<UInt32>(group.raw,0),0x47,questId,questFlags) ||
                    questId!=group.questId || questFlags!=group.questFlags) {
                    valid=false;groupReason="quest-identity-changed-during-read";
                }
                for(const auto& slot:consumedSlots) {
                    UInt32 current=0;
                    if(!MembershipReadAt(reader,slot.first,current) || current!=slot.second) {
                        valid=false;groupReason="slot-changed-during-read";break;
                    }
                }
                for(const auto& info:consumedInfos) {
                    UInt32 id=0,currentFlags=0;
                    if(!MembershipIdentity(reader,info.address,0x46,id,currentFlags) || id!=info.id || currentFlags!=info.flags) {
                        valid=false;groupReason="info-identity-changed-during-read";break;
                    }
                }
                if(nonNull!=group.count && groupReason.empty()){valid=false;groupReason="array-count-mismatch";}
                complete=complete && valid;
                groups+="],\"status\":"+Quote(valid?"observed":"partial")+",\"reason\":"+
                    (groupReason.empty()?"null":Quote(groupReason))+"}";
            }
        }
        node=link[1];
    }
    for(const auto& entry:observedNodes) {
        UInt32 current[2]{};
        if(!MembershipReadAt(reader,entry.first,current) || current[0]!=entry.second.first || current[1]!=entry.second.second) {
            complete=false;reason="group-list-changed-during-read";break;
        }
    }
    UInt32 afterId=0,afterFlags=0;
    if(!MembershipIdentity(reader,topic,0x45,afterId,afterFlags) || afterId!=topicId || afterFlags!=flags) {
        complete=false;reason="topic-changed-during-read";
    }
    return "{\"status\":"+Quote(complete?(groupIndex?"observed":"empty"):"partial")+
        ",\"topicFormId\":"+std::to_string(topicId)+",\"flags\":"+std::to_string(flags)+
        ",\"reason\":"+(reason.empty()?"null":Quote(reason))+",\"groups\":["+groups+
        "],\"groupLimit\":16,\"slotLimit\":256,\"evidence\":\"sdk-layout-validated-pointer-paths\"}";
}
std::string MembershipTerrain(UInt32 cell,MembershipReader reader=MembershipRead,UInt32 expectedId=0) {
    std::uint8_t raw[0x50]{};
    if(!MembershipReadAt(reader,cell,raw) || raw[4]!=0x39 || !ActorRaw<UInt32>(raw,12))return ActorUnavailable("cell-identity-unavailable");
    if(expectedId && ActorRaw<UInt32>(raw,12)!=expectedId)return ActorUnavailable("cell-identity-changed-before-read");
    const auto state=raw[0x26];const auto land=ActorRaw<UInt32>(raw,0x4C);
    if(state>6)return ActorUnavailable("cell-load-state-invalid");
    UInt32 id=0,flags=0;bool complete=true;
    std::string value;
    if(!land) value=state==3 || state==6 ? "{\"status\":\"empty\",\"formId\":null}" : ActorUnavailable("cell-not-loaded");
    else if(MembershipIdentity(reader,land,0x42,id,flags))value="{\"status\":\"observed\",\"formId\":"+std::to_string(id)+",\"flags\":"+std::to_string(flags)+"}";
    else {value=ActorUnavailable("land-identity-unavailable");complete=false;}
    std::uint8_t after[0x50]{};
    UInt32 afterLandId=0,afterLandFlags=0;
    const bool landStable=!id || (MembershipIdentity(reader,land,0x42,afterLandId,afterLandFlags) && id==afterLandId && flags==afterLandFlags);
    const bool stable=MembershipReadAt(reader,cell,after) && raw[4]==after[4] &&
        ActorRaw<UInt32>(raw,12)==ActorRaw<UInt32>(after,12) && ActorRaw<UInt32>(raw,8)==ActorRaw<UInt32>(after,8) &&
        raw[0x24]==after[0x24] && state==after[0x26] && land==ActorRaw<UInt32>(after,0x4C) && landStable;
    return "{\"status\":"+Quote(complete && stable?"observed":"partial")+",\"cellFormId\":"+std::to_string(ActorRaw<UInt32>(raw,12))+
        ",\"loadState\":"+std::to_string(state)+",\"cellFlags\":"+std::to_string(raw[0x24])+
        ",\"pointerPath\":\"CELL+0x4C\",\"landAddress\":"+std::to_string(land)+",\"land\":"+value+
        ",\"stableRead\":"+(stable?"true":"false")+",\"rawHex\":"+Quote(ActorHex(raw,sizeof(raw)))+
        ",\"evidence\":\"sdk-layout-cell-land-identity\"}";
}
void MembershipRequest(const Request& request) {
    const auto generation=g_captureGeneration.load();
    auto error=[&](const char* reason){Emit("error",request.id,",\"error\":"+Quote(reason),g_capture?generation:0);};
    if(!g_capture || !g_loadedGameObserved || !VerifyRuntimeFormMap()){error("membership-api-unavailable");return;}
    const auto delimiter=request.payload.find('\t');
    if(delimiter==std::string::npos){error("invalid-membership-request");return;}
    const auto operation=request.payload.substr(0,delimiter);
    if(operation!="read-dialogue-state" && operation!="read-cell-terrain"){error("invalid-membership-operation");return;}
    TypedOperation parsed;
    if(!ParseTypedOperation({15,request.id,request.payload.substr(delimiter+1)},parsed) || parsed.subject.plugin=="@player") {
        error("invalid-membership-identity");return;
    }
    TypedForm subject;std::string reason;
    if(!TypedResolve(parsed.subject,subject,reason)){error(reason.c_str());return;}
    const bool dialogue=operation=="read-dialogue-state";
    if(subject.type!=(dialogue?0x45:0x39)){error("membership-form-type-mismatch");return;}
    const auto address=reinterpret_cast<UInt32>(subject.pointer);
    auto state=dialogue?MembershipDialogue(address,MembershipRead,subject.id):MembershipTerrain(address,MembershipRead,subject.id);
    if(state.size()>MaxPayload-4096)state=ActorUnavailable("membership-output-limit");
    Emit("record-membership",request.id,",\"operation\":"+Quote(operation)+",\"engineTargetFormId\":"+
        std::to_string(subject.id)+",\"engineTargetFormType\":"+std::to_string(subject.type)+",\"plugin\":"+
        Quote(subject.requested.plugin)+",\"localFormId\":"+std::to_string(subject.requested.localId)+
        ",\"state\":"+state,generation);
}
