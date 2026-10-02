// Focused synthetic callback/queue controls. No executable/game mapping is read.
std::vector<std::string> g_notificationFixtureRows;
void SaveNotificationCase(const std::string& name) {
    g_notificationFixtureRows.push_back("{\"case\":"+Quote(name)+",\"lastEvent\":"+
        (g_events.empty()?"null":g_events.back().json)+",\"queueDrops\":"+std::to_string(g_dropped)+"}");
}
NotificationContext BeginNotificationFixture(const std::string& session="notification-fixture") {
    std::uint64_t generation=0,connection=0;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_connected=true;g_capture=false;generation=++g_captureGeneration;connection=++g_connectionGeneration;
        g_session=session;g_events.clear();g_requests.clear();g_sequence=0;g_dropped=0;g_frame=100;
    }
    Check(BeginNotificationCapture({2,11,""},generation,connection,",\"session\":"+Quote(session)),"fixture capture did not begin");
    return {generation,connection};
}
NVSEMessagingInterface::Message NotificationFixture(UInt32 type,void* data=nullptr,UInt32 length=0) {
    return {"NVSE",type,length,data};
}
void EndNotificationFixture(NotificationContext context) {
    Check(EndNotificationCapture({4,12,""},context.generation,context.connection,",\"status\":\"completed\""),"fixture capture did not end");
}
void TestNotifications() {
    const bool savedLayout=g_pcLayoutVerified;g_pcLayoutVerified=false;
    {
        const auto context=BeginNotificationFixture();
        Check(g_events.size()==1 && g_notifications.initialized,"start coverage missing");
        for(const auto& count:g_notifications.counts)Check(count.count==0 && !count.saturated,"start counts not zero");
        Check(g_events.front().json.find("\"boundary\":\"start\"")!=std::string::npos &&
            g_events.front().json.find("\"countsComplete\":true")!=std::string::npos,"zero coverage not explicit");
        EndNotificationFixture(context);
        Check(g_events.back().json.find("\"aggregatedTotal\":0")!=std::string::npos,"zero end counts missing");
        SaveNotificationCase("zero-start-and-end-counts");
    }
    for(const auto type:NotificationTypes) {
        const auto context=BeginNotificationFixture();const auto message=NotificationFixture(type);
        Check(CaptureRuntimeNotification(message,context)==NotificationRoute::Aggregated,"known type was not counted");
        ++g_frame;CaptureRuntimeNotification(message,context);
        const auto& count=g_notifications.counts[NotificationIndex(type)];
        Check(count.count==2 && count.firstFrame==100 && count.lastFrame==101 &&
            count.firstQpc>0 && count.lastQpc>=count.firstQpc && g_events.size()==1 && g_dropped==0,
            "known type count/frame/queue behavior differs");
        EndNotificationFixture(context);
        Check(g_events.back().json.find("\"aggregatedTotal\":2")!=std::string::npos,"count missing at end");
        SaveNotificationCase("aggregate-"+std::to_string(type));
    }
    // Existing load/path/error payloads and unknown notifications keep their raw shapes.
    struct RawCase {UInt32 type;const char* data;const char* expected;};
    const RawCase rawCases[]={
        {1,nullptr,"\"messageType\":1"},
        {3,"BMT_PC016_MenuFree.fos","\"savePath\":\"BMT_PC016_MenuFree.fos\""},
        {6,"save/path.fos","\"savePath\":\"save/path.fos\""},
        {8,"success","\"loadSucceeded\":true"},
        {8,nullptr,"\"loadSucceeded\":false"},
        {10,"fixture error","\"kind\":\"script-error\""},
        {25,nullptr,"\"messageType\":25"},
        {99,nullptr,"\"messageType\":99"}};
    for(size_t i=0;i<std::size(rawCases);++i) {
        const auto& test=rawCases[i];const auto context=BeginNotificationFixture();
        auto message=NotificationFixture(test.type,const_cast<char*>(test.data),test.data?static_cast<UInt32>(strlen(test.data)+1):0);
        Check(CaptureRuntimeNotification(message,context)==NotificationRoute::Raw && g_events.size()==2 &&
            g_events.back().json.find(test.expected)!=std::string::npos,"raw notification payload changed");
        for(const auto& count:g_notifications.counts)Check(count.count==0,"raw type entered aggregate counts");
        SaveNotificationCase("raw-"+std::to_string(test.type)+"-"+std::to_string(i));EndNotificationFixture(context);
    }
    {
        const auto old=BeginNotificationFixture("old-session");CaptureRuntimeNotification(NotificationFixture(26),old);EndNotificationFixture(old);
        const auto current=BeginNotificationFixture("new-session");
        Check(g_notifications.session=="new-session" && g_notifications.generation==current.generation &&
            g_notifications.connection==current.connection && g_notifications.counts[1].count==0,"new session inherited counters");
        Check(CaptureRuntimeNotification(NotificationFixture(26),old)==NotificationRoute::Ignored,"stale capture entered new counts");
        EndNotificationFixture(current);SaveNotificationCase("reset-and-old-capture-refusal");
    }
    for(const auto fault:{"generation","connection","inactive","disconnected"}) {
        const auto context=BeginNotificationFixture();
        if(!strcmp(fault,"generation"))++g_captureGeneration;
        if(!strcmp(fault,"connection"))++g_connectionGeneration;
        if(!strcmp(fault,"inactive"))g_capture=false;
        if(!strcmp(fault,"disconnected"))g_connected=false;
        for(const auto type:{26u,99u})Check(CaptureRuntimeNotification(NotificationFixture(type),context)==NotificationRoute::Ignored,
            "stale callback was observed in a new/inactive connection");
        Check(g_events.size()==1 && g_notifications.counts[1].count==0,"stale context changed evidence");
        Check(!EndNotificationCapture({4,12,""},context.generation,context.connection,""),"stale end was emitted");
        SaveNotificationCase(std::string("refuse-")+fault);
    }
    {
        const auto context=BeginNotificationFixture();auto& count=g_notifications.counts[1];
        count.count=NotificationCountLimit;count.firstFrame=99;count.firstQpc=1;
        CaptureRuntimeNotification(NotificationFixture(26),context);EndNotificationFixture(context);
        Check(count.count==NotificationCountLimit && count.saturated && g_dropped==0 &&
            g_events.back().json.find("\"status\":\"count-saturated\"")!=std::string::npos &&
            g_events.back().json.find("\"countsComplete\":false")!=std::string::npos,"saturated count claimed completeness or event loss");
        SaveNotificationCase("counter-saturation-is-explicit");
    }
    {
        const auto context=BeginNotificationFixture();
        while(g_events.size()<MaxEvents)Emit("fixture",0,"",context.generation);
        CaptureRuntimeNotification(NotificationFixture(26),context);
        Check(g_events.size()==MaxEvents && g_dropped==0,"aggregation consumed event queue capacity");
        EndNotificationFixture(context);
        Check(g_dropped==1 && g_events.back().json.find("\"dropped\":1")!=std::string::npos,
            "real queue overflow was hidden by aggregation");
        SaveNotificationCase("real-queue-loss-preserved");
    }
    {
        const auto context=BeginNotificationFixture();std::atomic<bool> bad{false};
        auto worker=[&](){for(unsigned i=0;i<250;++i)if(CaptureRuntimeNotification(NotificationFixture(26),context)!=NotificationRoute::Aggregated)bad=true;};
        std::thread a(worker),b(worker),c(worker),d(worker);a.join();b.join();c.join();d.join();
        Check(!bad && g_notifications.counts[1].count==1000 && g_events.size()==1,"concurrent callback counts lost or enqueued");
        EndNotificationFixture(context);SaveNotificationCase("concurrent-exact-counts");
    }
    // Either contender may acquire the queue lock first. In both orders the end
    // row is last and its counts exactly reflect callbacks admitted before it.
    for(const auto type:{26u,99u}) {
        const auto context=BeginNotificationFixture();NotificationRoute route=NotificationRoute::Ignored;bool ended=false;
        std::atomic<bool> go{false};
        std::thread callback([&](){while(!go.load())std::this_thread::yield();route=CaptureRuntimeNotification(NotificationFixture(type),context);});
        std::thread end([&](){while(!go.load())std::this_thread::yield();ended=EndNotificationCapture({4,12,""},context.generation,context.connection,",\"status\":\"completed\"");});
        go=true;callback.join();end.join();
        Check(ended && !g_capture && g_events.back().json.find("\"kind\":\"capture-end\"")!=std::string::npos,"callback crossed final capture boundary");
        const auto count=route==NotificationRoute::Aggregated?1u:0u;
        Check(g_events.back().json.find("\"aggregatedTotal\":"+std::to_string(count))!=std::string::npos &&
            g_events.size()==(route==NotificationRoute::Raw?3u:2u),"end boundary lost count/raw ordering");
        SaveNotificationCase("callback-end-race-"+std::to_string(type));
    }
    for(const auto kind:{4u,5u}) {
        LeaseFixture f;ResetLease(f);g_capture=false;
        const NotificationContext context{g_captureGeneration.load(),g_connectionGeneration.load()};
        Check(BeginNotificationCapture({2,11,""},context.generation,context.connection,",\"session\":"+Quote(g_session)),"lease coverage start failed");
        g_events.clear();std::string error;f.fault="deferred-combat";Check(ArmLease(f,error),"lease arm failed");
        CaptureRuntimeNotification(NotificationFixture(26),context);
        CombatLeaseEndCapture({static_cast<std::uint16_t>(kind),402,""},LeaseIo());
        Check(g_capture && CombatLeaseActive() && g_events.empty(),"deferred cleanup emitted an early end");
        CaptureRuntimeNotification(NotificationFixture(26),context);
        f.combat[0]=f.combat[1]=false;++g_frame;ServiceCombatLease(g_combatLease.cleanupStarted+30,LeaseIo());
        Check(!g_capture && !CombatLeaseActive() && g_events.size()==2 &&
            g_events.front().json.find("\"kind\":\"combat-cleanup\"")!=std::string::npos &&
            g_events.back().json.find("\"kind\":\"capture-end\"")!=std::string::npos &&
            g_events.back().json.find("\"aggregatedTotal\":2")!=std::string::npos,"deferred end lost coverage/terminal ordering");
        SaveNotificationCase(kind==4?"deferred-stop-counts":"deferred-cancel-counts");
    }
    {
        const auto context=BeginNotificationFixture();g_notifications={};
        Check(CaptureRuntimeNotification(NotificationFixture(26),context)==NotificationRoute::Raw,"uninitialized coverage silently filtered a message");
        EndNotificationFixture(context);
        Check(g_events.back().json.find("\"status\":\"unavailable\"")!=std::string::npos &&
            g_events.back().json.find("\"aggregatedTotal\":null")!=std::string::npos,"uninitialized counters became observed zero");
        SaveNotificationCase("uninitialized-is-unavailable");
    }
    {
        const auto context=BeginNotificationFixture();EndNotificationFixture(context);g_captureGeneration=1001;g_connectionGeneration=1002;
        Request stale{2,21,""};stale.originConnectionGeneration=1000;
        Check(!BeginNotificationCapture(stale,1001,1002,"") && !g_capture,"old Start crossed a new connection");
        SaveNotificationCase("old-start-connection-refused");
    }
    g_pcLayoutVerified=savedLayout;
}
