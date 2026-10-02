std::vector<std::string> g_creatureScalingFixtureRows;
void TestCreatureHealthScaling() {
    struct Case {const char* name;bool observed;};
    const Case cases[]={
        {"scaled",true},{"fixed",true},{"raw-extremes",true},{"use-stats-retained",true},
        {"profile",false},{"wrong-role",false},{"overflow",false},
        {"child-id",false},{"base-id",false},{"player-id",false},{"player-base-id",false},
        {"missing-player",false},{"form-map",false},{"owner-table",false},{"health-slot",false},
        {"foreign-mapping",false},{"short-read",false},{"code-bytes",false},{"divisor",false},
        {"changed-child",false},{"changed-player",false},{"changed-singleton",false},{"changed-code",false},{"retired-context",false}
    };
    for(const auto& item:cases) {
        const std::string name=item.name;
        const UInt32 actor=name=="overflow"?0xFFFFFFF0u:0x2000000,base=0x2100000,player=0x2200000,playerBase=0x2300000;
        const UInt32 id=0xA000810,baseId=0xA000800;
        std::map<std::uintptr_t,std::vector<std::uint8_t>> memory;
        auto word=[&](std::uintptr_t address,UInt32 value){auto& b=memory[address];b.resize(4);memcpy(b.data(),&value,4);};
        auto field=[&](UInt32 address,size_t offset,UInt32 value,size_t count=4){memcpy(memory[address].data()+offset,&value,count);};
        auto header=[&](UInt32 address,size_t size,UInt32 table,std::uint8_t type,UInt32 form){auto& b=memory[address];b.resize(size);
            memcpy(b.data(),&table,4);b[4]=type;memcpy(b.data()+12,&form,4);};
        auto code=[&](UInt32 address,const char* hex){auto& b=memory[address];const std::string h=hex;
            for(size_t i=0;i<h.size();i+=2)b.push_back(static_cast<std::uint8_t>(strtoul(h.substr(i,2).c_str(),nullptr,16)));};
        for(const auto& r:CreatureScaleCodes)code(r.address,r.hex);
        code(CreatureScaleDivisorAddress,CreatureScaleDivisorHex);
        header(actor,0x24,0x10870AC,0x3C,id);field(actor,0x20,base);
        header(base,0xC4,0x1048F5C,0x2B,baseId);field(base,0xB0,0x1048E6C);
        field(base,0x34,name=="fixed"?0x40:0xC0);field(base,0x3C,2500,2);
        field(base,0xB4,name=="raw-extremes"?UINT32_MAX:137);
        if(name=="use-stats-retained"){field(base,0x4A,0x8002,2);field(base,0x54,0x2400000);}
        word(base+0x100,0x1048DC8);word(0x1048E7C,0x5F8E90);word(CreatureScalePlayerSlot,player);
        header(player,0x24,0x1086A6C,0x3B,0x14);field(player,0x20,playerBase);
        header(playerBase,0xC4,0x1047A6C,0x2A,7);field(playerBase,0x3C,name=="raw-extremes"?0xFFFF:1,2);
        if(name=="child-id")field(actor,12,id+1);
        if(name=="base-id")field(base,12,baseId+1);
        if(name=="player-id")field(player,12,0x15);
        if(name=="player-base-id")field(playerBase,12,8);
        if(name=="missing-player")word(CreatureScalePlayerSlot,0);
        if(name=="owner-table")word(base+0x100,0x1048DCC);
        if(name=="health-slot")word(0x1048E7C,0x5F8E91);
        if(name=="short-read")memory[base].pop_back();
        if(name=="code-bytes")memory[0x47DED0][0]^=1;
        if(name=="divisor")memory[CreatureScaleDivisorAddress][0]^=1;
        bool alive=true,wrapped=false;unsigned proofRounds=0;
        auto read=[&](std::uintptr_t address,void* bytes,size_t length){
            if(address<0x10000)wrapped=true;
            auto found=memory.find(address);if(found==memory.end() || found->second.size()<length)return false;
            memcpy(bytes,found->second.data(),length);
            if(address==CreatureScaleDivisorAddress && ++proofRounds==1) {
                if(name=="changed-child")field(base,0x3C,2501,2);
                if(name=="changed-player")field(playerBase,0x3C,2,2);
                if(name=="changed-singleton")word(CreatureScalePlayerSlot,player+4);
                if(name=="changed-code")memory[0x461580][0]^=1;
                if(name=="retired-context")alive=false;
            }
            return true;
        };
        auto mapped=[&](std::uintptr_t address,size_t length,bool executable){
            return !(name=="foreign-mapping" && executable) && address>=0x400000 && address+length<=0x1200000;
        };
        auto lookup=[&](UInt32 form,UInt32 pointer){return name!="form-map" &&
            ((form==id && pointer==actor)||(form==baseId && pointer==base)||(form==0x14 && pointer==player)||(form==7 && pointer==playerBase));};
        const auto out=ReadCreatureHealthScaling(name!="profile",actor,name=="wrong-role"?0x14:id,baseId,read,mapped,lookup,[&](){return alive;});
        Check(out.observed==item.observed,item.name);Check(!wrapped,"wrapped creature scaling pointer read");
        if(out.observed) {
            Check(proofRounds==2 && SameCreatureScaleFields(out.before,out.after),"scaling input repeat missing");
            Check(ActorRaw<UInt32>(out.before.baseRaw,0xB4)==(name=="raw-extremes"?UINT32_MAX:137),"stored UInt32 altered");
        } else Check(out.reason && *out.reason,"scaling refusal reason missing");
        const auto json=CreatureScaleJson(out,actor,id,baseId,"fixture-executable",3,4,5);
        Check(json.size()<8192,"scaling serialization exceeded bounded allowance");
        g_creatureScalingFixtureRows.push_back("{\"case\":"+Quote(item.name)+",\"observation\":"+json+"}");
    }
    for(unsigned kind=0;kind<3;++kind) {
        std::string old=",\"legacy\":\""+std::string(kind==2?MaxPayload:kind==1?MaxPayload-1024:100,'x')+"\"";
        const auto before=old;const std::string addition="{\"synthetic\":\""+std::string(4096,'y')+"\"}";
        const bool accepted=AppendCreatureHealthScaling(old,addition);
        Check(accepted==(kind!=2),"creature scaling frame bound failed");
        Check(old.compare(0,before.size(),before)==0,"existing actor payload changed");
        if(kind==1)Check(old.find("actor-state-payload-limit")!=std::string::npos && old.find(addition)==std::string::npos,
            "oversized scaling object did not become explicit unavailable");
        if(accepted)Check(old.size()+512<=MaxPayload,"scaling frame escaped envelope budget");
        g_creatureScalingFixtureRows.push_back("{\"case\":\"frame-bound-"+std::to_string(kind)+"\",\"accepted\":"+(accepted?"true":"false")+
            ",\"serializedEvent\":"+Quote("{"+old.substr(1)+"}")+"}");
    }
}
