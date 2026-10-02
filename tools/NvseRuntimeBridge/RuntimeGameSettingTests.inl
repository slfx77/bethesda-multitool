// Focused synthetic map/SDK result fixtures. No game or engine routine is called.
std::vector<std::string> g_gmstFixtureRows;
struct GmstTestMemory {
    std::array<std::uint8_t,0x2000> bytes{};
    UInt32 unreadable=0,unmapped=0,changing=0;size_t changeReads=0;
    void Word(UInt32 address,UInt32 value){memcpy(bytes.data()+address-0x1000,&value,4);}
    void Text(UInt32 address,const std::string& value){memcpy(bytes.data()+address-0x1000,value.c_str(),value.size()+1);}
    void Setting(UInt32 address,const char* name,UInt32 raw,UInt32 nameAddress) {
        Word(address,0x2000);Word(address+4,raw);Word(address+8,nameAddress);Text(nameAddress,name);
    }
    GmstTestMemory() {
        Word(0x1000,0x1100);Word(0x120C,0x2000);Word(0x1210,2);Word(0x1214,0x1400);Word(0x1218,1);
        Word(0x1400,0x1500);Word(0x1504,0x1700);Word(0x1508,0x1600);Text(0x1700,"fTest");
        Setting(0x1600,"fTest",0xBF800000,0x1800);
    }
    bool Read(UInt32 address,void* destination,size_t length) {
        if(address<0x1000||std::uint64_t(address)+length>0x3000||
           (unreadable&&address<=unreadable&&std::uint64_t(address)+length>unreadable))return false;
        memcpy(destination,bytes.data()+address-0x1000,length);
        if(changing&&address<=changing&&std::uint64_t(address)+length>changing&&++changeReads>1)
            static_cast<std::uint8_t*>(destination)[changing-address]^=1;
        return true;
    }
    bool Mapped(UInt32 address,size_t length,bool) {
        return address>=0x1000&&std::uint64_t(address)+length<=0x3000&&
            !(unmapped&&address<=unmapped&&std::uint64_t(address)+length>unmapped);
    }
    GmstLookup Lookup(const char* name="fTest",bool profile=true) {
        return ReadGmstLookup(profile,name,[&](UInt32 a,void* d,size_t n){return Read(a,d,n);},
            [&](UInt32 a,size_t n,bool image){return Mapped(a,n,image);},0x1000);
    }
};
void TestGameSettings() {
    g_gmstFixtureRows.clear();
    const auto record=[](const std::string& name,const GmstLookup& value){g_gmstFixtureRows.push_back("{\"case\":"+Quote(name)+",\"observation\":"+GmstJson(value)+"}");};
    for(const std::string mode:{"valid-negative-one","case-insensitive","missing","profile","unreadable-key","unmapped-setting",
        "overflow-map","oversized-buckets","oversized-items","cycle","count-mismatch","ambiguous","name-mismatch",
        "type-mismatch","nonfinite","changed-singleton","changed-raw","changed-key","changed-buckets","null-collection"}) {
        GmstTestMemory memory;const char* query=mode=="case-insensitive"?"ftEST":mode=="missing"?"fMissing":"fTest";
        if(mode=="unreadable-key")memory.unreadable=0x1700;
        if(mode=="unmapped-setting")memory.unmapped=0x1600;
        if(mode=="overflow-map")memory.Word(0x1000,0xFFFFFFF0);
        if(mode=="oversized-buckets")memory.Word(0x1210,static_cast<UInt32>(GmstBucketLimit+1));
        if(mode=="oversized-items")memory.Word(0x1218,static_cast<UInt32>(GmstEntryLimit+1));
        if(mode=="cycle")memory.Word(0x1500,0x1500);
        if(mode=="count-mismatch")memory.Word(0x1218,2);
        if(mode=="ambiguous") {
            memory.Word(0x1218,2);memory.Word(0x1404,0x1540);memory.Word(0x1544,0x1700);memory.Word(0x1548,0x1640);
            memory.Setting(0x1640,"fTest",0,0x1840);
        }
        if(mode=="name-mismatch")memory.Text(0x1800,"fOther");
        if(mode=="type-mismatch")memory.Text(0x1800,"FTest");
        if(mode=="nonfinite")memory.Word(0x1604,0x7F800000);
        if(mode=="changed-singleton")memory.changing=0x1000;
        if(mode=="changed-raw")memory.changing=0x1604;
        if(mode=="changed-key")memory.changing=0x1700;
        if(mode=="changed-buckets")memory.changing=0x1400;
        if(mode=="null-collection")memory.Word(0x1000,0);
        const auto value=memory.Lookup(query,mode!="profile");
        const std::string expected=mode=="valid-negative-one"||mode=="case-insensitive"?"observed":mode=="missing"?"missing":mode=="ambiguous"?"ambiguous":"unavailable";
        Check(value.status==expected,"GMST lookup promoted unavailable/ambiguous memory");
        if(expected=="observed")Check(value.candidates.size()==1&&value.candidates[0].value==-1&&value.identityStable,"GMST lost legitimate -1");
        if(expected=="missing")Check(value.candidates.empty()&&value.complete,"GMST inferred absence from incomplete scan");
        if(expected=="ambiguous")Check(value.candidates.size()==2&&value.complete,"GMST discarded competing entries");
        if(mode=="unmapped-setting")Check(GmstJson(value).find("\"rawHex\":null")!=std::string::npos,"unreadable setting invented zero raw bytes");
        record(mode,value);
    }
    for(const auto raw:{2u,UINT32_MAX}) {
        GmstTestMemory memory;memory.Text(0x1700,"bTest");memory.Setting(0x1600,"bTest",raw,0x1800);
        const auto value=memory.Lookup("bTest");
        Check(value.status=="observed"&&value.candidates[0].value==(raw==2?2:-1),"GMST bool read normalized stored numeric value");
        record(raw==2?"bool-raw-two":"bool-raw-negative",value);
    }
    struct ValueCase { const char* name;char type;double value;bool accepted; };
    const ValueCase values[]={
        {"float",'f',1800,true},{"float-rounding",'f',0.1,true},{"float-overflow",'f',1e100,false},
        {"nan",'f',std::numeric_limits<double>::quiet_NaN(),false},{"infinity",'f',std::numeric_limits<double>::infinity(),false},
        {"bool-zero",'b',0,true},{"bool-one",'b',1,true},{"bool-other",'b',2,false},
        {"int-negative",'i',-1,true},{"int-fraction",'i',1.5,false},{"int-min",'i',INT32_MIN,true},
        {"int-max-rounded",'i',INT32_MAX,false},{"int-precision-loss",'i',16777217,false},
        {"uint-negative",'u',-1,false},{"uint-max-rounded",'u',UINT32_MAX,false},{"uint-exact-high",'u',4294967040.0,true}};
    for(const auto& c:values) {
        float submitted=0;UInt32 raw=0;const bool accepted=GmstValue(c.type,c.value,submitted,raw);
        Check(accepted==c.accepted,"GMST setter changed or rounded integer input");
        g_gmstFixtureRows.push_back("{\"case\":"+Quote(c.name)+",\"accepted\":"+(accepted?"true":"false")+"}");
    }
    GmstTestMemory memory;const auto before=memory.Lookup();
    memory.Word(0x1604,0x44E10000);const auto after=memory.Lookup(); // float1800.
    for(const std::string mode:{"write-observed","handler-false","result-zero","result-nonnumeric","result-nonfinite","readback-mismatch","identity-changed","capture-changed"}) {
        NumericElement value{};value.type=1;value.number=1;auto current=after;
        if(mode=="result-zero")value.number=0;
        if(mode=="result-nonnumeric")value.type=0;
        if(mode=="result-nonfinite")value.number=std::numeric_limits<double>::infinity();
        if(mode=="readback-mismatch")current.candidates[0].raw=0;
        if(mode=="identity-changed")++current.candidates[0].setting;
        const auto error=GmstWriteFailure(before,current,mode!="capture-changed",mode!="handler-false",value,0x44E10000);
        Check((error==nullptr)==(mode=="write-observed"),"GMST write accepted without command result and matching identity/readback");
        g_gmstFixtureRows.push_back("{\"case\":"+Quote(mode)+",\"accepted\":"+(error?"false":"true")+",\"reason\":"+(error?Quote(error):"null")+"}");
    }
    struct PayloadCase { const char* name;UInt32 kind;const char* payload;bool valid; };
    const PayloadCase payloads[]={
        {"read-payload",7,"gmst/1\tread\tfVanityModeAutoDelay",true},
        {"write-payload",3,"gmst/1\twrite\tfVanityModeAutoDelay\t1800",true},
        {"wrong-kind",7,"gmst/1\twrite\tfTest\t1",false},{"unknown-version",7,"gmst/2\tread\tfTest",false},
        {"string-setting",7,"gmst/1\tread\tsTest",false},{"injected-name",3,"gmst/1\twrite\tfTest\"\t1",false},
        {"extra-field",7,"gmst/1\tread\tfTest\t1",false},{"trailing-field",7,"gmst/1\tread\tfTest\t",false},
        {"rounded-int-payload",3,"gmst/1\twrite\tiTest\t16777217",false}};
    for(const auto& c:payloads) {
        std::string name;double requested=0;float submitted=0;UInt32 raw=0;
        const auto valid=ParseGmstRequest({static_cast<std::uint16_t>(c.kind),1,c.payload},name,requested,submitted,raw);
        Check(valid==c.valid,"GMST wire payload validation failed");
        g_gmstFixtureRows.push_back("{\"case\":"+Quote(c.name)+",\"accepted\":"+(valid?"true":"false")+"}");
    }
}
