// Typed game-thread operations. Script bodies are fixed here; payloads never contain code.
// ABI: pinned xNVSE Script interface. Command signatures: FNV command table plus pinned SDK.
constexpr size_t MaxInventoryRows=128, MaxOperationFunctions=96;
std::unordered_map<std::string,Script*> g_operationFunctions;
struct TypedIdentity { std::string plugin; UInt32 localId=0; };
struct TypedForm { void* pointer=nullptr; UInt32 id=0,baseId=0; std::uint8_t type=0,baseType=0; TypedIdentity requested; };
struct TypedOperation {
    std::string op,axis,objectiveComponent; TypedIdentity subject,other;
    bool hasOther=false,hasIndex=false; double value=0; UInt32 index=0;
};
void CombatLeaseStart(const Request&,const TypedOperation&,const TypedForm&,const TypedForm*,std::uint64_t);
bool CombatLeaseStop(const Request&,const TypedOperation&,const TypedForm&,std::uint64_t);
bool TypedPair(const std::string& plugin,const std::string& local,TypedIdentity& identity) {
    if (!std::regex_match(local,std::regex("^[0-9a-fA-F]{1,6}$"))) return false;
    const auto id=static_cast<UInt32>(strtoul(local.c_str(),nullptr,16));
    if (!id) return false;
    if (plugin=="@player") { if(id!=0x14) return false; }
    else if (plugin.size()<5 || plugin.size()>255 || plugin.find_first_of("\"\\/:")!=std::string::npos ||
        !std::all_of(plugin.begin(),plugin.end(),[](unsigned char c){return c>=32 && c<127;}) ||
        (_stricmp(plugin.c_str()+plugin.size()-4,".esm") && _stricmp(plugin.c_str()+plugin.size()-4,".esp"))) return false;
    identity={plugin,id};return true;
}
bool TypedInteger(const std::string& text,UInt32 min,UInt32 max,UInt32& value) {
    if(text.empty() || text.size()>10 || !std::all_of(text.begin(),text.end(),[](unsigned char c){return c>='0' && c<='9';})) return false;
    const auto number=strtoull(text.c_str(),nullptr,10);
    if(number<min || number>max) return false;
    value=static_cast<UInt32>(number);return true;
}
bool ParseTypedOperation(const Request& request,TypedOperation& result) {
    if(request.payload.empty() || request.payload.size()>2048 || request.payload.back()=='\t' ||
       !std::all_of(request.payload.begin(),request.payload.end(),[](unsigned char c){return c=='\t' || (c>=32 && c<127);})) return false;
    std::vector<std::string> p;std::istringstream input(request.payload);std::string part;
    while(std::getline(input,part,'\t'))p.push_back(part);
    result={};
    if(request.kind==15) {
        result.op="read-reference-state";
        if(p.size()==1 && p[0]=="player") {result.subject={"@player",0x14};return true;}
        return p.size()==2 && TypedPair(p[0],p[1],result.subject);
    }
    if(p.size()<3 || !TypedPair(p[1],p[2],result.subject)) return false;
    result.op=p[0];
    auto other=[&](){return p.size()>=5 && (result.hasOther=TypedPair(p[3],p[4],result.other));};
    if(request.kind==16) {
        if(result.op=="start-combat-leased")return p.size()==6 && other() && TypedInteger(p[5],1,5000,result.index);
        if(result.op=="stop-combat" || result.op=="enable-reference" || result.op=="disable-reference") return p.size()==3;
        if(result.op=="start-combat" || result.op=="move-reference") return p.size()==5 && other();
        if(result.op!="set-position" && result.op!="set-rotation") return false;
        if(p.size()!=5 || (p[3]!="X" && p[3]!="Y" && p[3]!="Z") || p[4].empty() || p[4].size()>32) return false;
        char* end=nullptr;result.value=strtod(p[4].c_str(),&end);result.axis=p[3];
        return end && !*end && std::isfinite(result.value) && std::abs(result.value)<=1e7;
    }
    if(request.kind==17) {
        if(result.op=="read-inventory") return p.size()==3 || (p.size()==5 && other());
        if(result.op=="equip-item" || result.op=="unequip-item")return p.size()==5 && other();
        if(result.op!="add-item" && result.op!="remove-item")return false;
        return p.size()==6 && other() && TypedInteger(p[5],1,10000,result.index);
    }
    if(request.kind==18) {
        if(result.subject.plugin=="@player")return false;
        if(result.op=="read-quest-state") {
            result.hasIndex=p.size()==4;
            return p.size()==3 || (p.size()==4 && TypedInteger(p[3],0,65535,result.index));
        }
        if(result.op=="set-quest-stage")return p.size()==4 && TypedInteger(p[3],0,65535,result.index);
        if(result.op!="set-quest-objective" || p.size()!=6 || !TypedInteger(p[3],0,65535,result.index) ||
           (p[4]!="completed" && p[4]!="displayed") || (p[5]!="0" && p[5]!="1"))return false;
        result.hasIndex=true;result.objectiveComponent=p[4];result.value=p[5]=="1"?1:0;return true;
    }
    return false;
}
bool TypedIsReference(const TypedForm& form){return form.type>=0x3A && form.type<=0x3C;}
bool TypedIsActor(const TypedForm& form){return form.type==0x3B || form.type==0x3C;}
bool TypedValidateForm(void* pointer,UInt32 requested,TypedForm& form) {
    if(!ReadFormIdentity(pointer,form.id,form.type) || form.id!=requested)return false;
    form.pointer=pointer;
    if(TypedIsReference(form)) {
        UInt32 base=0;
        if(!ReadRuntime(reinterpret_cast<std::uintptr_t>(pointer)+0x20,base) ||
           !ReadFormIdentity(reinterpret_cast<void*>(base),form.baseId,form.baseType))return false;
        if((form.type==0x3B && form.baseType!=0x2A) || (form.type==0x3C && form.baseType!=0x2B))return false;
    }
    return true;
}
bool TypedResolve(const TypedIdentity& identity,TypedForm& form,std::string& reason) {
    UInt32 slot=0;
    if(identity.plugin!="@player" && (!PluginNumber("GetModIndex \""+identity.plugin+"\"",slot) || slot>=255)) {
        reason="requested-plugin-not-loaded";return false;
    }
    const auto id=(slot<<24)|identity.localId;form={};form.requested=identity;
    if(!TypedValidateForm(LookupRuntimeForm(id),id,form)){reason="requested-form-not-resident-or-identity-mismatch";return false;}
    return true;
}
bool TypedRefresh(const TypedForm& prior,TypedForm& current,void* (*lookup)(UInt32)=LookupRuntimeForm) {
    current={};current.requested=prior.requested;
    return TypedValidateForm(lookup(prior.id),prior.id,current) && current.type==prior.type && current.baseId==prior.baseId;
}
std::string TypedIdentityJson(const TypedForm& form) {
    return ",\"engineTargetFormId\":"+std::to_string(form.id)+",\"engineTargetBaseFormId\":"+
        (form.baseId?std::to_string(form.baseId):"null")+",\"engineTargetFormType\":"+std::to_string(form.type)+
        ",\"plugin\":"+Quote(form.requested.plugin)+",\"localFormId\":"+std::to_string(form.requested.localId)+
        ",\"targetKind\":"+Quote(form.type==0x47?"quest":TypedIsActor(form)?"actor":"reference");
}
// A compiled function is bounded and cached by fixed body, not by payload identities or numeric values.
bool TypedCall(const std::string& body,const TypedForm& subject,const TypedForm* other,float number,
               NumericElement& result,std::string& reason) {
    if(!g_numericEnabled || !g_script || !g_script->CompileScript || !g_script->CallFunction){reason="sdk-function-api-unavailable";return false;}
    auto found=g_operationFunctions.find(body);
    if(found==g_operationFunctions.end()) {
        if(g_operationFunctions.size()>=MaxOperationFunctions){reason="operation-function-cache-limit";return false;}
        const auto text="ref owner\nref other\nfloat number\nbegin function { owner other number }\n"+body+"\nend\n";
        auto script=g_script->CompileScript(text.c_str());
        if(!script){reason="typed-function-compile-failed";return false;}
        found=g_operationFunctions.emplace(body,script).first;
    }
    UInt32 bits=0;memcpy(&bits,&number,sizeof(bits));result={};
    auto callingReference=TypedIsReference(subject)?static_cast<TESObjectREFR*>(subject.pointer):nullptr;
    if(!g_script->CallFunction(found->second,callingReference,nullptr,&result,3,subject.pointer,other?other->pointer:nullptr,bits)) {
        reason="typed-function-return-unavailable";return false;
    }
    if(result.type==3){g_numericEnabled=false;reason="unexpected-string-return";return false;}
    return true;
}
bool TypedNumber(const std::string& expression,const TypedForm& subject,const TypedForm* other,float argument,
                 double& value,std::string& reason) {
    NumericElement result{};
    if(!TypedCall("SetFunctionValue ("+expression+")",subject,other,argument,result,reason))return false;
    if(result.type!=1 || !std::isfinite(result.number)){reason="typed-number-unavailable";return false;}
    value=result.number;return true;
}
std::string TypedNumberJson(const std::string& expression,const TypedForm& subject,const TypedForm* other=nullptr,float argument=0) {
    double value=0;std::string reason;
    if(!TypedNumber(expression,subject,other,argument,value,reason))return ActorUnavailable(reason.c_str());
    return "{\"status\":\"observed\",\"value\":"+NumberField(value)+"}";
}
bool TypedFormValue(const std::string& expression,const TypedForm& subject,const TypedForm* other,float argument,
                    void*& pointer,UInt32& id,std::uint8_t& type,std::string& reason) {
    NumericElement result{};
    if(!TypedCall("SetFunctionValue ("+expression+")",subject,other,argument,result,reason))return false;
    if(result.type!=2){reason="typed-form-return-unavailable";return false;}
    pointer=result.form;id=0;type=0;
    if(pointer && !ReadFormIdentity(pointer,id,type)){reason="returned-form-identity-unreadable";return false;}
    return true;
}
std::string TypedFormJson(const std::string& expression,const TypedForm& subject,float argument=0) {
    void* pointer=nullptr;UInt32 id=0;std::uint8_t type=0;std::string reason;
    if(!TypedFormValue(expression,subject,nullptr,argument,pointer,id,type,reason))return ActorUnavailable(reason.c_str());
    return "{\"status\":"+Quote(pointer?"observed":"empty")+",\"formId\":"+(pointer?std::to_string(id):"null")+
        ",\"formType\":"+(pointer?std::to_string(type):"null")+"}";
}
struct TypedSafetyCommand {
    const CommandInfo* entry=nullptr;
    CommandInfo metadata{};
    UInt32 returnType=UINT32_MAX;
};
bool TypedReadSafetyCommand(const char* name,TypedSafetyCommand& result,std::string& reason) {
    // These names are fixed locally. Observe the active PC table; no Xbox opcode or
    // stored actor flag is evidence of the effective PC command result.
    if(!g_commands || g_commands->version<1 || !g_commands->GetByName ||
       !g_commands->GetByOpcode || !g_commands->GetReturnType) {
        reason="safety-command-interface-unavailable";return false;
    }
    result={};result.entry=g_commands->GetByName(name);
    if(!result.entry || !CommandReadBytes(reinterpret_cast<UInt32>(result.entry),&result.metadata,sizeof(result.metadata))) {
        reason="safety-command-metadata-unavailable";return false;
    }
    std::string observedName;
    if(!ReadCommandName(result.metadata.longName,observedName) || observedName!=name ||
       !result.metadata.opcode || g_commands->GetByOpcode(result.metadata.opcode)!=result.entry ||
       result.metadata.needsParent!=1 || result.metadata.numParams!=0 || !result.metadata.execute ||
       !ExecutableAddress(reinterpret_cast<void*>(result.metadata.execute))) {
        reason="safety-command-metadata-mismatch";return false;
    }
    result.returnType=g_commands->GetReturnType(result.entry);
    if(result.returnType!=0) {reason="safety-command-return-type-unavailable";return false;} // SDK kRetnType_Default.
    return true;
}
std::string TypedActorSafety(const char* name,const TypedForm& subject,void* (*lookup)(UInt32)=LookupRuntimeForm) {
    if(strcmp(name,"GetDead") && strcmp(name,"IsEssential"))return ActorUnavailable("unsupported-safety-command");
    if(!TypedIsActor(subject))return ActorUnavailable("safety-subject-is-not-an-actor");
    const auto generation=g_captureGeneration.load(),connection=g_connectionGeneration.load();
    if(!g_capture || !g_connected)return ActorUnavailable("safety-capture-unavailable");
    TypedSafetyCommand command;std::string reason;
    if(!TypedReadSafetyCommand(name,command,reason))return ActorUnavailable(reason.c_str());
    TypedForm before;UInt32 baseAddress=0;
    if(!TypedRefresh(subject,before,lookup) || before.pointer!=subject.pointer || before.baseType!=subject.baseType ||
       !ReadRuntime(reinterpret_cast<std::uintptr_t>(before.pointer)+0x20,baseAddress) || !baseAddress)
        return ActorUnavailable("safety-subject-identity-unavailable-before-read");
    NumericElement result{};
    // Opcode/handler are part of the fixed-body cache key: a later remapping must
    // not reuse bytecode compiled against the previous table entry.
    const bool returned=TypedCall(std::string("SetFunctionValue (owner.")+name+")\n; active PC opcode "+
        std::to_string(command.metadata.opcode)+" handler "+std::to_string(reinterpret_cast<UInt32>(command.metadata.execute)),
        before,nullptr,0,result,reason);
    if(!g_capture || !g_connected || generation!=g_captureGeneration.load() || connection!=g_connectionGeneration.load())
        return ActorUnavailable("safety-capture-changed");
    TypedForm after;UInt32 afterBase=0;
    if(!TypedRefresh(subject,after,lookup) || after.pointer!=before.pointer || after.baseType!=before.baseType ||
       !ReadRuntime(reinterpret_cast<std::uintptr_t>(after.pointer)+0x20,afterBase) || afterBase!=baseAddress)
        return ActorUnavailable("safety-subject-identity-changed");
    TypedSafetyCommand current;std::string metadataReason;
    if(!TypedReadSafetyCommand(name,current,metadataReason) || current.entry!=command.entry ||
       memcmp(&current.metadata,&command.metadata,sizeof(command.metadata)) || current.returnType!=command.returnType)
        return ActorUnavailable("safety-command-metadata-changed");
    if(!returned)return ActorUnavailable(reason.c_str());
    if(result.type!=1)return ActorUnavailable("safety-command-numeric-return-unavailable");
    const auto value=result.number;
    if(!std::isfinite(value) || (value!=0 && value!=1))return ActorUnavailable("safety-command-boolean-unavailable");
    return "{\"status\":\"observed\",\"value\":"+NumberField(value)+
        ",\"engineTargetFormId\":"+std::to_string(before.id)+",\"engineTargetBaseFormId\":"+std::to_string(before.baseId)+
        ",\"referenceAddress\":"+std::to_string(reinterpret_cast<UInt32>(before.pointer))+",\"baseAddress\":"+std::to_string(baseAddress)+
        ",\"command\":{\"name\":"+Quote(name)+",\"opcode\":"+std::to_string(command.metadata.opcode)+
        ",\"tableEntryAddress\":"+std::to_string(reinterpret_cast<UInt32>(command.entry))+
        ",\"executeAddress\":"+std::to_string(reinterpret_cast<UInt32>(command.metadata.execute))+
        ",\"needsParent\":1,\"numParams\":0,\"returnType\":0},\"evidence\":\"active-pc-command-metadata-and-explicit-owner-return\"}";
}
std::string TypedReferenceState(const TypedForm& subject) {
    std::string fields="\"Disabled\":"+TypedNumberJson("owner.GetDisabled",subject);
    for(const auto* axis:{"X","Y","Z"}) {
        fields+=",\"Position"+std::string(axis)+"\":"+TypedNumberJson("owner.GetPos "+std::string(axis),subject);
        fields+=",\"Rotation"+std::string(axis)+"\":"+TypedNumberJson("owner.GetAngle "+std::string(axis),subject);
    }
    std::string forms="\"parentCell\":"+TypedFormJson("owner.GetParentCell",subject);
    if(TypedIsActor(subject)) {
        fields+=",\"GetDead\":"+TypedActorSafety("GetDead",subject)+",\"IsEssential\":"+TypedActorSafety("IsEssential",subject);
        fields+=",\"IsInCombat\":"+TypedNumberJson("owner.IsInCombat",subject)+",\"Health\":"+TypedNumberJson("owner.GetAV Health",subject);
        // Pinned TESBipedModelForm::ePart_Weapon == 5; getter returns inventory base form, not equipped instance.
        fields+=",\"EquippedWeaponCurrentHealth\":"+TypedNumberJson("owner.GetEquippedCurrentHealth number",subject,nullptr,5);
        fields+=",\"EquippedWeaponModFlags\":"+TypedNumberJson("owner.GetEquippedWeaponModFlags",subject);
        forms+=",\"combatTarget\":"+TypedFormJson("owner.GetCombatTarget",subject)+",\"equippedWeapon\":"+TypedFormJson("owner.GetEquippedObject number",subject,5);
    }
    return "{\"statistics\":{"+fields+"},\"forms\":{"+forms+"},\"rotationUnits\":\"degrees\"}";
}
bool TypedMutate(const std::string& statement,const TypedForm& subject,const TypedForm* other,float value,std::string& reason) {
    NumericElement result{};
    // Use the same NVSE expression/ref-variable path as the verified reads. A vanilla
    // command's numeric return is diagnostic only; before/after observations decide outcome.
    if(!TypedCall("SetFunctionValue ("+statement+")",subject,other,value,result,reason))return false;
    if(result.type!=1 || !std::isfinite(result.number)){reason="mutation-command-return-unavailable";return false;}
    return true;
}
bool TypedGenerationActive(std::uint64_t generation){return g_capture && g_connected && generation==g_captureGeneration.load();}
void TypedReferenceAction(const Request& request,const TypedOperation& op,const TypedForm& subject,const TypedForm* other,std::uint64_t generation,
                          void* (*lookup)(UInt32)=LookupRuntimeForm) {
    if(op.op=="start-combat-leased"){CombatLeaseStart(request,op,subject,other,generation);return;}
    if(op.op=="stop-combat" && CombatLeaseStop(request,op,subject,generation))return;
    std::string statement,reason;
    auto fail=[&](const char* value){Emit("error",request.id,",\"error\":"+Quote(value),generation);};
    if(!TypedIsReference(subject)){fail("subject-is-not-a-reference");return;}
    if((op.op=="start-combat" || op.op=="stop-combat") && !TypedIsActor(subject)){fail("combat-subject-is-not-an-actor");return;}
    if(other && (!TypedIsReference(*other) || subject.id==other->id)){fail("secondary-reference-invalid-or-self");return;}
    if(op.op=="start-combat" && (!other || !TypedIsActor(*other))){fail("combat-target-is-not-an-actor");return;}
    if(op.op=="disable-reference" && subject.id==0x14){fail("player-disable-unsupported");return;}
    if(op.op=="start-combat")statement="owner.StartCombat other";
    else if(op.op=="stop-combat")statement="owner.StopCombat";
    else if(op.op=="enable-reference")statement="owner.Enable";
    else if(op.op=="disable-reference")statement="owner.Disable";
    else if(op.op=="move-reference")statement="owner.MoveTo other";
    else if(op.op=="set-position")statement="owner.SetPos "+op.axis+" number";
    else if(op.op=="set-rotation")statement="owner.SetAngle "+op.axis+" number";
    else {fail("unsupported-reference-operation");return;}
    const auto before=TypedReferenceState(subject);
    const auto argumentEcho=op.axis.empty()?"null":TypedNumberJson("number",subject,other,static_cast<float>(op.value));
    if(!TypedGenerationActive(generation))return;
    const bool accepted=TypedMutate(statement,subject,other,static_cast<float>(op.value),reason);
    TypedForm current;const bool resolvedAfter=TypedRefresh(subject,current,lookup);
    const auto after=resolvedAfter?TypedReferenceState(current):ActorUnavailable("subject-identity-unavailable-after-action");
    Emit("action-result",request.id,TypedIdentityJson(subject)+",\"operation\":"+Quote(op.op)+
        ",\"engineOtherFormId\":"+(other?std::to_string(other->id):"null")+",\"accepted\":"+(accepted?"true":"false")+
        ",\"requestedAxis\":"+(op.axis.empty()?"null":Quote(op.axis))+",\"requestedValue\":"+
        (op.axis.empty()?"null":NumberField(op.value))+
        ",\"argumentEcho\":"+argumentEcho+
        ",\"before\":"+before+",\"after\":"+after+",\"afterIdentityResolved\":"+(resolvedAfter?"true":"false")+
        ",\"evidence\":\"sdk-explicit-reference-function-and-readback\",\"error\":"+
        (accepted?"null":Quote(reason)),generation);
}
bool TypedInventoryItem(const TypedForm& form) {
    // Exact inventory base types annotated in the pinned GameForms.h enum; lists are deliberately excluded.
    switch(form.type){case 0x18:case 0x19:case 0x1A:case 0x1D:case 0x1E:case 0x1F:case 0x28:case 0x29:case 0x2E:case 0x2F:case 0x31:case 0x32:case 0x6C:case 0x73:case 0x74:return true;default:return false;}
}
std::string TypedInventoryItemState(const TypedForm& owner,const TypedForm& item) {
    return "{\"formId\":"+std::to_string(item.id)+",\"formType\":"+std::to_string(item.type)+
        ",\"count\":"+TypedNumberJson("owner.GetItemCount other",owner,&item)+
        ",\"equipped\":"+(TypedIsActor(owner)?TypedNumberJson("owner.GetEquipped other",owner,&item):ActorUnavailable("owner-is-not-an-actor"))+"}";
}
void TypedInventory(const Request& request,const TypedOperation& op,const TypedForm& owner,const TypedForm* item,std::uint64_t generation) {
    auto fail=[&](const char* reason){Emit("error",request.id,",\"error\":"+Quote(reason),generation);};
    if(!TypedIsReference(owner) || (!TypedIsActor(owner) && owner.baseType!=0x1B)){fail("inventory-owner-is-not-actor-or-container");return;}
    if(item && !TypedInventoryItem(*item)){fail("inventory-item-type-unsupported");return;}
    if(op.op=="read-inventory") {
        if(item){Emit("inventory-state",request.id,TypedIdentityJson(owner)+",\"status\":\"selected-item\",\"items\":["+TypedInventoryItemState(owner,*item)+"]",generation);return;}
        double count=0;std::string reason,items;UInt32 observed=0;
        if(!TypedNumber("owner.GetNumItems",owner,nullptr,0,count,reason) || count<0 || count>1000000 || std::floor(count)!=count){fail("inventory-count-unavailable");return;}
        const auto bounded=std::min(static_cast<size_t>(count),MaxInventoryRows);bool complete=count<=MaxInventoryRows;
        for(size_t i=0;i<bounded;++i) {
            if(!TypedGenerationActive(generation))return;
            TypedForm entry;
            if(!TypedFormValue("owner.GetInventoryObject number",owner,nullptr,static_cast<float>(i),entry.pointer,entry.id,entry.type,reason) || !entry.pointer || !TypedInventoryItem(entry)) {
                complete=false;if(!items.empty())items+=",";items+="{\"index\":"+std::to_string(i)+",\"status\":\"unavailable\",\"reason\":"+Quote(reason.empty()?"inventory-item-missing-or-unsupported":reason)+"}";continue;
            }
            if(!items.empty())items+=",";items+=TypedInventoryItemState(owner,entry);++observed;
        }
        double after=0;if(!TypedNumber("owner.GetNumItems",owner,nullptr,0,after,reason) || after!=count)complete=false;
        Emit("inventory-state",request.id,TypedIdentityJson(owner)+",\"status\":"+Quote(complete?"complete":"partial")+
            ",\"reportedItemTypes\":"+NumberField(count)+",\"observedItems\":"+std::to_string(observed)+",\"limit\":"+std::to_string(MaxInventoryRows)+",\"items\":["+items+"]",generation);return;
    }
    if(!item){fail("inventory-item-required");return;}
    if((op.op=="equip-item" || op.op=="unequip-item") && (!TypedIsActor(owner) || (item->type!=0x18 && item->type!=0x28 && item->type!=0x29))){fail("equip-requires-actor-and-equippable-item");return;}
    const auto before=TypedInventoryItemState(owner,*item);std::string statement;
    if(op.op=="add-item")statement="owner.AddItem other number 1";
    else if(op.op=="remove-item")statement="owner.RemoveItem other number 1";
    else if(op.op=="equip-item")statement="owner.EquipItem other 0 1";
    else if(op.op=="unequip-item")statement="owner.UnequipItem other 0 1";
    else {fail("unsupported-inventory-operation");return;}
    if(!TypedGenerationActive(generation))return;
    std::string reason;const bool accepted=TypedMutate(statement,owner,item,static_cast<float>(op.index),reason);
    TypedForm currentOwner,currentItem;const bool resolvedAfter=TypedRefresh(owner,currentOwner) && TypedRefresh(*item,currentItem);
    const auto after=resolvedAfter?TypedInventoryItemState(currentOwner,currentItem):ActorUnavailable("inventory-identities-unavailable-after-action");
    Emit("action-result",request.id,TypedIdentityJson(owner)+",\"operation\":"+Quote(op.op)+",\"engineOtherFormId\":"+std::to_string(item->id)+
        ",\"accepted\":"+(accepted?"true":"false")+",\"before\":"+before+",\"after\":"+after+",\"afterIdentityResolved\":"+(resolvedAfter?"true":"false")+
        ",\"evidence\":\"sdk-explicit-inventory-function-and-readback\",\"error\":"+(accepted?"null":Quote(reason)),generation);
}
std::string TypedQuestState(const TypedForm& quest,const TypedOperation& op) {
    std::string fields="\"Stage\":"+TypedNumberJson("GetStage owner",quest)+",\"Running\":"+TypedNumberJson("GetQuestRunning owner",quest);
    if(op.hasIndex)fields+=",\"ObjectiveCompleted\":"+TypedNumberJson("GetObjectiveCompleted owner number",quest,nullptr,static_cast<float>(op.index))+
        ",\"ObjectiveDisplayed\":"+TypedNumberJson("GetObjectiveDisplayed owner number",quest,nullptr,static_cast<float>(op.index));
    if(op.op=="set-quest-stage")fields+=",\"RequestedStageDone\":"+TypedNumberJson("GetStageDone owner number",quest,nullptr,static_cast<float>(op.index));
    return "{\"statistics\":{"+fields+"},\"objectiveIndex\":"+(op.hasIndex?std::to_string(op.index):"null")+"}";
}
void TypedQuest(const Request& request,const TypedOperation& op,const TypedForm& quest,std::uint64_t generation) {
    if(quest.type!=0x47){Emit("error",request.id,",\"error\":\"subject-is-not-a-quest\"",generation);return;}
    const auto before=TypedQuestState(quest,op);
    if(op.op=="read-quest-state"){Emit("quest-state",request.id,TypedIdentityJson(quest)+",\"state\":"+before,generation);return;}
    std::string statement,reason;
    if(op.op=="set-quest-stage")statement="SetStage owner number";
    else if(op.op=="set-quest-objective")statement=std::string(op.objectiveComponent=="completed"?"SetObjectiveCompleted":"SetObjectiveDisplayed")+" owner number "+(op.value==1?"1":"0");
    else {Emit("error",request.id,",\"error\":\"unsupported-quest-operation\"",generation);return;}
    if(!TypedGenerationActive(generation))return;
    const bool accepted=TypedMutate(statement,quest,nullptr,static_cast<float>(op.index),reason);
    TypedForm current;const bool resolvedAfter=TypedRefresh(quest,current);
    const auto after=resolvedAfter?TypedQuestState(current,op):ActorUnavailable("quest-identity-unavailable-after-action");
    Emit("action-result",request.id,TypedIdentityJson(quest)+",\"operation\":"+Quote(op.op)+",\"accepted\":"+(accepted?"true":"false")+
        ",\"before\":"+before+",\"after\":"+after+",\"afterIdentityResolved\":"+(resolvedAfter?"true":"false")+
        ",\"evidence\":\"sdk-explicit-quest-function-and-readback\",\"error\":"+(accepted?"null":Quote(reason)),generation);
}
void TypedOperationRequest(const Request& request) {
    const auto generation=g_captureGeneration.load();
    auto fail=[&](const std::string& reason){Emit("error",request.id,",\"error\":"+Quote(reason),g_capture?generation:0);};
    if(!g_capture || !g_loadedGameObserved || !VerifyRuntimeFormMap()){fail("typed-runtime-profile-unavailable");return;}
    TypedOperation op;TypedForm subject,other;std::string reason;
    if(!ParseTypedOperation(request,op)){fail("invalid-typed-operation-payload");return;}
    if(!TypedResolve(op.subject,subject,reason) || (op.hasOther && !TypedResolve(op.other,other,reason))){fail(reason);return;}
    if(TypedIsActor(subject)){std::lock_guard<std::mutex> lock(g_mutex);g_watchedActors.insert(subject.id);if(op.hasOther && TypedIsActor(other))g_watchedActors.insert(other.id);}
    if(request.kind==15) {
        if(!TypedIsReference(subject)){fail("subject-is-not-a-reference");return;}
        Emit("reference-state",request.id,TypedIdentityJson(subject)+",\"state\":"+TypedReferenceState(subject)+",\"evidence\":\"sdk-explicit-reference-readback\"",generation);
    } else if(request.kind==16)TypedReferenceAction(request,op,subject,op.hasOther?&other:nullptr,generation);
    else if(request.kind==17)TypedInventory(request,op,subject,op.hasOther?&other:nullptr,generation);
    else if(request.kind==18)TypedQuest(request,op,subject,generation);
}
