// Independent byte-addressed fixtures use pinned SDK structure layouts, not production structs.
// No engine call, executable image or game process is involved.
std::vector<std::string> g_scriptLocalFixtureRows;
struct ScriptLocalTestMemory {
    std::array<std::uint8_t,0x3000> data{};
    UInt32 unreadable=0,changing=0;unsigned changes=0;UInt32 rebind=0;std::unordered_map<UInt32,unsigned> lookups;
    void Word(UInt32 a,UInt32 v){memcpy(data.data()+a-0x1000,&v,4);}
    void Value(double v){memcpy(data.data()+0x2008-0x1000,&v,8);}
    void Meta(UInt32 a,UInt32 index,UInt32 text,const char* name) {
        Word(a,index);Word(a+0x18,text);Word(a+0x1C,static_cast<UInt32>(strlen(name))|(128u<<16));
        memcpy(data.data()+text-0x1000,name,strlen(name));
    }
    ScriptLocalTestMemory() {
        // REFR persistent 0x01000123 -> base 0x01000456, one ExtraScript.
        Word(0x1000,0x3800);Word(0x1004,0x3A);Word(0x1008,0x400);Word(0x100C,0x01000123);Word(0x1020,0x1100);
        Word(0x1100,0x3800);Word(0x1104,0x1B);Word(0x110C,0x01000456);
        Word(0x1048,0x1200);data[0x104D-0x1000]=0x20;
        Word(0x1200,0x3800);Word(0x1204,0x0D);Word(0x120C,0x1400);Word(0x1210,0x1800);
        Word(0x1400,0x3800);Word(0x1404,0x11);Word(0x140C,0x01000789);
        Word(0x144C,0x1500);Meta(0x1500,7,0x1600,"counter");
        Word(0x1800,0x1400);Word(0x180C,0x1900);Word(0x1900,0x2000);Word(0x2000,7);Value(3.25);
    }
    bool Read(UInt32 a,void* destination,size_t n) {
        if(a<0x1000||std::uint64_t(a)+n>0x4000||(unreadable&&a<=unreadable&&std::uint64_t(a)+n>unreadable))return false;
        memcpy(destination,data.data()+a-0x1000,n);
        if(changing&&a<=changing&&std::uint64_t(a)+n>changing&&++changes>1)static_cast<std::uint8_t*>(destination)[changing-a]^=1;
        return true;
    }
    UInt32 Lookup(UInt32 id) {
        if(id==rebind&&++lookups[id]>1)return 0x3000;
        if(id==0x01000123)return 0x1000;
        return id==0x01000456?0x1100:id==0x01000789?0x1400:0;
    }
    ReferenceLocalObservation Observe(const std::string& name="counter",bool profile=true) {
        return ReadReferenceLocal(profile,0x01000123,name,[&](UInt32 a,void* d,size_t n){return Read(a,d,n);},
            [](UInt32 a,size_t n,bool image){return image?a==0x3800&&n==4:a>=0x1000&&std::uint64_t(a)+n<=0x4000;},
            [&](UInt32 id){return Lookup(id);});
    }
};
void TestScriptLocals() {
    g_scriptLocalFixtureRows.clear();
    struct Case {const char* name;const char* status;const char* reason;};
    const Case cases[]={
        {"finite","observed","numeric-local-storage"},{"positive-zero","observed","numeric-local-storage"},
        {"negative-zero","observed","numeric-local-storage"},{"negative","observed","numeric-local-storage"},
        {"case-insensitive","observed","numeric-local-storage"},{"actor","observed","numeric-local-storage"},
        {"creature","observed","numeric-local-storage"},{"empty-tail","observed","numeric-local-storage"},
        {"no-extra","absent","extra-script-absent"},{"no-event","absent","event-list-absent"},
        {"no-name","absent","variable-name-absent"},{"no-slot","absent","variable-slot-absent"},
        {"reference","unsupported","reference-local"},{"unsupported-type","unsupported","non-numeric-metadata-type"},
        {"duplicate-extra","ambiguous","duplicate-extra-script"},
        {"duplicate-name","ambiguous","duplicate-metadata-name-or-index"},
        {"duplicate-index","ambiguous","duplicate-metadata-name-or-index"},
        {"duplicate-slot","ambiguous","duplicate-local-index"},
        {"extra-cycle","unavailable","extra-list-unreadable-cycle-or-limit"},
        {"metadata-cycle","unavailable","metadata-list-unreadable-cycle-or-limit"},
        {"reference-cycle","unavailable","reference-list-unreadable-cycle-or-limit"},
        {"local-cycle","unavailable","local-list-unreadable-cycle-or-limit"},
        {"unreadable-name","unavailable","metadata-name-unreadable-or-invalid"},
        {"unreadable-value","unavailable","local-unreadable"},
        {"event-script","unavailable","event-script-mismatch-or-unreadable"},
        {"nonfinite","unavailable","nonfinite-local-value"},
        {"changed-owner","unavailable","identity-or-storage-changed"},
        {"changed-base","unavailable","identity-or-storage-changed"},
        {"changed-script","unavailable","identity-or-storage-changed"},
        {"changed-event","unavailable","identity-or-storage-changed"},
        {"changed-name","unavailable","identity-or-storage-changed"},
        {"changed-value","unavailable","identity-or-storage-changed"},
        {"changed-list","unavailable","identity-or-storage-changed"},
        {"rebound-owner","unavailable","identity-or-storage-changed"},
        {"rebound-base","unavailable","identity-or-storage-changed"},
        {"rebound-script","unavailable","identity-or-storage-changed"},
        {"nonpersistent","unavailable","owner-not-supported-persistent-reference"},
        {"temporary","unavailable","owner-not-supported-persistent-reference"},
        {"container","unavailable","container-reference-route-unsupported"},
        {"presence-mismatch","unavailable","extra-script-presence-mismatch"},
        {"pointer-overflow","unavailable","metadata-unreadable"},
        {"profile","unavailable","reference-local-profile-unavailable"}
    };
    for(const auto& test:cases) {
        ScriptLocalTestMemory memory;const std::string name=test.name;
        if(name=="positive-zero")memory.Value(0);
        if(name=="negative-zero")memory.Value(-0.0);
        if(name=="negative")memory.Value(-17.5);
        if(name=="actor"||name=="creature"){memory.Word(0x1004,name=="actor"?0x3B:0x3C);memory.Word(0x1104,name=="actor"?0x2A:0x2B);}
        if(name=="empty-tail")memory.Word(0x1904,0x2100);
        if(name=="no-extra"){memory.Word(0x1048,0);memory.data[0x104D-0x1000]=0;}
        if(name=="no-event")memory.Word(0x1210,0);
        if(name=="no-slot")memory.Word(0x2000,8);
        if(name=="reference"){memory.Word(0x1444,0x2200);memory.Word(0x220C,7);}
        if(name=="unsupported-type")memory.Word(0x1510,2);
        if(name=="duplicate-extra"){memory.Word(0x1208,0x1300);memory.Word(0x1304,0x0D);}
        if(name=="duplicate-name"||name=="duplicate-index"){
            memory.Word(0x1450,0x2100);memory.Word(0x2100,0x2300);
            memory.Meta(0x2300,name=="duplicate-name"?8:7,0x2400,name=="duplicate-name"?"COUNTER":"other");
        }
        if(name=="duplicate-slot"){memory.Word(0x1904,0x2100);memory.Word(0x2100,0x2300);memory.Word(0x2300,7);}
        if(name=="extra-cycle")memory.Word(0x1208,0x1200);
        if(name=="metadata-cycle")memory.Word(0x1450,0x144C);
        if(name=="reference-cycle")memory.Word(0x1448,0x1444);
        if(name=="local-cycle")memory.Word(0x1904,0x1900);
        if(name=="unreadable-name")memory.unreadable=0x1600;
        if(name=="unreadable-value")memory.unreadable=0x2008;
        if(name=="event-script")memory.Word(0x1800,0x1404);
        if(name=="nonfinite")memory.Value(std::numeric_limits<double>::infinity());
        if(name=="changed-owner")memory.changing=0x100C;
        if(name=="changed-base")memory.changing=0x110C;
        if(name=="changed-script")memory.changing=0x140C;
        if(name=="changed-event")memory.changing=0x1800;
        if(name=="changed-name")memory.changing=0x1600;
        if(name=="changed-value")memory.changing=0x2008;
        if(name=="changed-list")memory.changing=0x1904;
        if(name=="rebound-owner")memory.rebind=0x01000123;
        if(name=="rebound-base")memory.rebind=0x01000456;
        if(name=="rebound-script")memory.rebind=0x01000789;
        if(name=="nonpersistent")memory.Word(0x1008,0);
        if(name=="temporary")memory.Word(0x1008,0x4400);
        if(name=="container")memory.Word(0x1204,0x1C);
        if(name=="presence-mismatch")memory.data[0x104D-0x1000]=0;
        if(name=="pointer-overflow")memory.Word(0x144C,0xFFFFFFF0);
        const auto value=memory.Observe(name=="case-insensitive"?"COUNTER":name=="no-name"?"missing":"counter",name!="profile");
        Check(value.storage.status==test.status&&value.storage.reason==test.reason,("script-local case: "+name).c_str());
        const auto json="{"+ReferenceLocalJson(value).substr(1)+"}";
        if(value.storage.status=="observed")Check(value.identityStable&&value.storage.rawObserved&&value.storage.complete&&value.storage.presence=="present","observed local lacks stable complete evidence");
        else Check(json.find("\"value\":null")!=std::string::npos,"unobserved local invented numeric value");
        if(name=="finite"||name=="negative")Check(value.storage.value==(name=="finite"?3.25:-17.5),"finite local value changed");
        if(name=="positive-zero"||name=="negative-zero")Check(json.find(name=="positive-zero"?"0000000000000000":"0000000000000080")!=std::string::npos,"signed zero raw bytes were lost");
        if(value.storage.status=="absent")Check(value.identityStable&&!value.storage.rawObserved&&json.find("\"rawValueHex\":null")!=std::string::npos,"absence became a numeric zero");
        g_scriptLocalFixtureRows.push_back("{\"case\":"+Quote(name)+",\"observation\":"+json+"}");
    }
    // Invalid typed payloads terminate as errors; they cannot fall back to expression compilation.
    for(const std::string payload:{"reference-local/2\tA.esp\t000123\tx","reference-local/1\t@player\t000014\tx",
        "reference-local/1\tA.esp\t000123\tx\t","reference-local/1\tA.esp\t000123\tx.y"}) {
        Request request{};request.kind=7;request.id=91;request.payload=payload;g_events.clear();
        const auto active=g_capture.load(),connected=g_connected.load();g_capture=true;g_connected=true;
        Check(ReferenceLocalRequest(request)&&g_events.size()==1&&g_events[0].json.find("invalid-reference-local-request")!=std::string::npos,"invalid direct read escaped typed rejection");
        g_capture=active;g_connected=connected;
        g_scriptLocalFixtureRows.push_back("{\"case\":\"invalid-payload\",\"event\":"+g_events[0].json+"}");
    }
}
