// Behavioral v2 controls share production admission/association and serializers.
condition_pilot::ActorIdentity QuestSubject{{0x15000,0x02001234,0x3B},{0x17000,0x02005678,0x2A}};
condition_pilot::ActorIdentity QuestTarget{{0x16000,0x03001235,0x3B},{0x18000,0x03005679,0x2A}};
bool QuestResolve(void*,UInt32 address,bool actor,condition_pilot::ActorIdentity& value) {
    if(actor && address==QuestSubject.form.address){value=QuestSubject;return true;}
    if(actor && address==QuestTarget.form.address){value=QuestTarget;return true;}
    if(!actor && address==QuestSubject.base.address){value={QuestSubject.base,{}};return true;}
    if(!actor && address==QuestTarget.base.address){value={QuestTarget.base,{}};return true;}
    return false;
}
bmt::ctda::Snapshot QuestFixture(bool positiveFirst=false) {
    auto snapshot=Fixture();snapshot.owner.address=0x11000;snapshot.owner.formId=0x04000800;snapshot.headAddress=0x11054;
    for(UInt32 i=0;i<3;++i) {
        auto& n=snapshot.nodes[i];n.address=i?0x11100+i*8:snapshot.headAddress;
        n.itemAddress=0x12000+i*32;n.next=i==2?0:0x11100+(i+1)*8;
        memcpy(n.raw.data(),&n.itemAddress,4);memcpy(n.raw.data()+4,&n.next,4);
        auto raw=n.item.raw;Put32(raw,12,i==2 || positiveFirst?QuestSubject.base.address:QuestTarget.base.address);
        Put32(raw,24,i==2?QuestSubject.form.address:0);n.item=bmt::ctda::ParseItem(raw);
    }
    return snapshot;
}
condition_quest::Binding QuestAdmit(const bmt::ctda::Snapshot& snapshot) {
    return condition_quest::Admit(snapshot,QuestSubject,QuestTarget,QuestResolve,nullptr);
}
condition_pilot::ItemSample QuestObserved(const condition_quest::Binding& b,UInt32 index) {
    condition_pilot::ItemSample sample;const auto& row=b.rows[index];const auto& a=row.evaluatedActor;const auto& p=row.parameter;
    sample.sourceRow=index;sample.item=row.item;sample.subject=b.subject.form.address;sample.target=b.target.form.address;
    sample.entered=sample.returned=sample.rawBeforeReadable=sample.rawAfterReadable=true;
    sample.before=sample.after=row.source.raw;
    auto& e=sample.eval;e.entered=e.returned=e.handlerReturned=e.resultReadable=e.identityBefore=e.identityStable=true;
    e.resultBits=0x3FF0000000000000ull;e.subject=a.form.address;e.subjectId=a.form.id;e.subjectType=a.form.type;
    e.subjectBaseAddress=a.base.address;e.subjectBaseId=a.base.id;e.subjectBaseType=a.base.type;
    e.parameter1=p.address;e.parameter1Id=p.id;e.parameter1Type=p.type;
    sample.comparison.entered=sample.comparison.returned=true;sample.comparison.comparisonBits=row.source.comparisonBits;
    return sample;
}
UInt32 QuestMemoryActor[12]{},QuestMemoryBase[4]{};
void* QuestLookup(UInt32 id) {
    return id==0x02000001?QuestMemoryActor:id==0x02000002?QuestMemoryBase:nullptr;
}
unsigned TestQuestV2() {
    unsigned cases=0;
    for(const auto& spec:std::array<std::pair<const char*,bool>,7>{{
        {"quest-conditions-v2\tResearch.esm\t1234\tActors.esp\tABC\tOther.esp\tDEF",true},
        {"quest-conditions-v2\tResearch.esm\t1234\t@player\t14\tOther.esp\tDEF",true},
        {"quest-conditions-v2\t@player\t14\t@player\t14\tOther.esp\tDEF",false},
        {"quest-conditions-v2\tResearch.esm\t01001234\t@player\t14\tOther.esp\tDEF",false},
        {"quest-conditions-v2\tResearch.esm\t1234\t@player\t15\tOther.esp\tDEF",false},
        {"quest-conditions-v2\tC:\\Research.esm\t1234\t@player\t14\tOther.esp\tDEF",false},
        {"quest-conditions-v2\tResearch.esm\t1234\t@player\t14\tOther.esp\tDEF\textra",false}}}) {
        TypedIdentity owner,subject,target;bool v2=false;
        const bool parsed=ConditionParse({22,1,spec.first},owner,subject,target,&v2);
        Require(parsed==spec.second && (!parsed || v2),"v2 payload identity boundary");++cases;
    }
    // Shape variants distinguish every admitted input dimension; v1 remains covered separately.
    for(unsigned fault=0;fault<21;++fault) {
        auto snapshot=QuestFixture(fault==1);auto raw=snapshot.nodes[0].item.raw;
        switch(fault) {
        case 2:snapshot.stable=false;break;
        case 3:snapshot.nodes.clear();break;
        case 4:while(snapshot.nodes.size()<9)snapshot.nodes.push_back(snapshot.nodes.back());break;
        case 5:snapshot.owner.kind=bmt::ctda::OwnerKind::Info;break;
        case 6:snapshot.nodes[1].itemAddress=snapshot.nodes[0].itemAddress;break;
        case 7:raw[1]=1;break;
        case 8:raw[0]=0x20;break; // Other operators are not promoted by generic pure model support.
        case 9:raw[0]=4;break;
        case 10:Put32(raw,4,0x7F800000);break;
        case 11:Put32(raw,8,0x10048);break;
        case 12:Put32(raw,12,0x104C0C);break; // Disk FormID is not a runtime pointer.
        case 13:Put32(raw,16,1);break;
        case 14:Put32(raw,20,3);break;
        case 15:Put32(raw,24,QuestSubject.form.address);break;
        case 16:Put32(raw,20,2);Put32(raw,24,0x14);break;
        case 17:snapshot.nodes.back().item=bmt::ctda::ParseItem([](auto b){b[0]=1;return b;}(snapshot.nodes.back().item.raw));break;
        case 19:{auto explicitRow=snapshot.nodes[2].item.raw;Put32(explicitRow,24,QuestTarget.form.address);snapshot.nodes[2].item=bmt::ctda::ParseItem(explicitRow);break;}
        case 20:raw[0]=0;snapshot.nodes.resize(1);break;
        case 18:Put32(raw,4,0);break; // Finite zero comparison is present, not absent.
        }
        if(!snapshot.nodes.empty())snapshot.nodes[0].item=bmt::ctda::ParseItem(raw);
        const auto binding=QuestAdmit(snapshot);
        Require((binding.rejection==condition_quest::Rejection::None)==(fault<2 || fault>=18),"v2 source policy mismatch");++cases;
    }
    for(const auto type:std::array<std::uint8_t,3>{{0x3B,0x3C,0x3A}}) {
        auto subject=QuestSubject;subject.form.type=type;
        Require((condition_quest::Admit(QuestFixture(),subject,QuestTarget,QuestResolve,nullptr).rejection==condition_quest::Rejection::None)==(type==0x3B),"actor layout widened");++cases;
    }
    const auto binding=QuestAdmit(QuestFixture(true));
    for(UInt32 row=0;row<3;++row){Require(condition_quest::Complete(QuestObserved(binding,row),binding),"dynamic source row identity rejected");++cases;}
    for(unsigned fault=0;fault<10;++fault) {
        auto sample=QuestObserved(binding,0);
        switch(fault) {
        case 0:sample.sourceRow=3;break;
        case 1:sample.item+=4;break;
        case 2:sample.after[12]^=1;break;
        case 3:++sample.eval.subjectBaseAddress;break;
        case 4:++sample.eval.subjectId;break;
        case 5:++sample.eval.parameter1Id;break;
        case 6:sample.eval.identityStable=false;break;
        case 7:sample.eval.resultBits=0x7FF8000000000000ull;break;
        case 8:sample.comparison.comparisonBits=0;break;
        case 9:sample.target=sample.subject;break;
        }
        Require(!condition_quest::Complete(sample,binding),"wrong row/eval identity accepted");++cases;
    }
    for(unsigned fault=0;fault<7;++fault) {
        memset(QuestMemoryActor,0,sizeof(QuestMemoryActor));memset(QuestMemoryBase,0,sizeof(QuestMemoryBase));
        QuestMemoryActor[1]=0x3B;QuestMemoryActor[3]=0x02000001;QuestMemoryBase[1]=0x2A;QuestMemoryBase[3]=0x02000002;
        QuestMemoryActor[8]=reinterpret_cast<UInt32>(QuestMemoryBase);
        UInt32 address=reinterpret_cast<UInt32>(QuestMemoryActor);
        switch(fault) {
        case 1:QuestMemoryActor[2]=0x20;break;
        case 2:QuestMemoryActor[2]=0x4000;break;
        case 3:QuestMemoryBase[1]=0x2B;break;
        case 4:QuestMemoryActor[8]=0;break;
        case 5:QuestMemoryActor[3]=0x02000003;break;
        case 6:address=UINT32_MAX-3;break;
        }
        condition_pilot::ActorIdentity observed;
        Require(ConditionQuestResolveUsing(address,true,observed,QuestLookup)==(fault==0),"map-backed actor identity mismatch");++cases;
    }
    // Native true-open frames prove a skip; the stored skipped row is still emitted.
    auto scope=FrameScope();scope.questV2=true;scope.before=QuestFixture(true);scope.questBindings=binding;
    scope.owner.requested={"Research.esm",0x800};scope.owner.id=0x04000800;scope.owner.pointer=reinterpret_cast<void*>(0x11000);
    scope.subject.requested={"Actors.esp",0x1234};scope.subject.id=QuestSubject.form.id;
    scope.target.requested={"Other.esp",0x1235};scope.target.id=QuestTarget.form.id;
    scope.observations.count=2;scope.observations.items[0]=QuestObserved(binding,0);scope.observations.items[1]=QuestObserved(binding,2);
    for(UInt32 i=0;i<3;++i) {
        auto* f=scope.frames.Begin();f->site=ConditionFrameSites[0];f->sourceRow=i;f->node=scope.before.nodes[i].address;
        f->item=scope.before.nodes[i].itemAddress;f->associated=true;f->itemCalls=i==2?2:1;
        f->aggregate=f->group=1;f->open=i==0?1:0;
    }
    auto* final=scope.frames.Begin();*final=scope.frames.rows[2];final->site=ConditionFrameSites[1];final->node=0;
    Require(ConditionFrameCoverage(scope,true),"native true-open skip association rejected");++cases;
    g_events.clear();const auto common=ConditionCommon(scope);ConditionEmitSourceRows(scope,scope.before,common,true);
    Require(g_events.size()==3,"uncalled row was omitted from source snapshot");
    for(const auto& event:g_events)std::cout<<event.json<<'\n';
    Require(common.find("requestedOwnerPlugin")!=std::string::npos && common.find("sourceOwnerPlugin")==std::string::npos &&
        common.find("unavailable-requires-offline-hash-and-row-mapping")!=std::string::npos,"requested owner relabeled physical winner");++cases;
    return cases;
}
