// Only the pinned NVSE scene notifications are counted instead of enqueued individually.
// All state below is protected by g_mutex, the same lock as the native event queue.
constexpr UInt32 NotificationTypes[]={24,26,27,28,29,30,31,32};
constexpr const char* NotificationNames[]={"OnFramePresent","OnRefSet3D","OnRefUnset3D","OnRefAttach",
    "OnCellStateChange","OnCellRefsLoaded","OnNonPersistentFormLoad","OnNonPersistentFormUnload"};
constexpr std::uint64_t NotificationCountLimit=UINT32_MAX;
struct NotificationCount {
    std::uint64_t count=0,firstFrame=0,lastFrame=0;
    LONGLONG firstQpc=0,lastQpc=0;
    bool saturated=false;
};
struct NotificationCapture {
    bool initialized=false;
    std::uint64_t generation=0,connection=0;
    std::string session;
    std::array<NotificationCount,std::size(NotificationTypes)> counts{};
};
NotificationCapture g_notifications;
struct NotificationContext { std::uint64_t generation=0,connection=0; };
enum class NotificationRoute { Ignored,Aggregated,Raw };
size_t NotificationIndex(UInt32 type) {
    for(size_t i=0;i<std::size(NotificationTypes);++i)if(NotificationTypes[i]==type)return i;
    return std::size(NotificationTypes);
}
std::string NotificationPolicyJson() {
    return "{\"schemaVersion\":1,\"policy\":\"lifecycle-raw-scene-counts-v1\","
        "\"aggregatedTypes\":[24,26,27,28,29,30,31,32],\"rawScope\":\"all-other-types-except-main-game-loop\",\"mainGameLoop\":\"existing-dispatch-and-heartbeat\","
        "\"perReferencePayloads\":\"not-collected\",\"queueLossAccounting\":\"unchanged\"}";
}
bool NotificationCaptureMatchesLocked(std::uint64_t generation,std::uint64_t connection) {
    return g_notifications.initialized && g_notifications.generation==generation && g_notifications.connection==connection;
}
std::string NotificationCoverageLocked(const char* boundary,std::uint64_t generation,std::uint64_t connection) {
    const bool initialized=NotificationCaptureMatchesLocked(generation,connection);
    std::uint64_t total=0;bool saturated=false;
    std::string counts;
    for(size_t i=0;i<std::size(NotificationTypes);++i) {
        const NotificationCount empty{};const auto& count=initialized?g_notifications.counts[i]:empty;
        total+=count.count;saturated=saturated || count.saturated;
        if(i)counts+=',';
        counts+="{\"messageType\":"+std::to_string(NotificationTypes[i])+",\"sdkName\":"+Quote(NotificationNames[i])+
            ",\"count\":"+(initialized?std::to_string(count.count):"null")+
            ",\"saturated\":"+(count.saturated?"true":"false")+
            ",\"firstFrame\":"+(count.count?std::to_string(count.firstFrame):"null")+
            ",\"lastFrame\":"+(count.count?std::to_string(count.lastFrame):"null")+
            ",\"firstQpc\":"+(count.count?std::to_string(count.firstQpc):"null")+
            ",\"lastQpc\":"+(count.count?std::to_string(count.lastQpc):"null")+"}";
    }
    return ",\"runtimeNotificationCoverage\":{\"schemaVersion\":1,\"boundary\":"+Quote(boundary)+
        ",\"policy\":"+NotificationPolicyJson()+",\"status\":"+Quote(!initialized?"unavailable":saturated?"count-saturated":"observed")+
        ",\"session\":"+(initialized?Quote(g_notifications.session):"null")+
        ",\"captureGeneration\":"+std::to_string(generation)+",\"connectionGeneration\":"+std::to_string(connection)+
        ",\"countsComplete\":"+(initialized && !saturated?"true":"false")+
        ",\"countLimitPerType\":"+std::to_string(NotificationCountLimit)+
        ",\"aggregatedTotal\":"+(initialized?std::to_string(total):"null")+
        ",\"aggregationIsEventLoss\":false,\"counts\":["+counts+"]}";
}
// The start row and zero counters precede every admitted callback under one lock.
bool BeginNotificationCapture(const Request& request,std::uint64_t generation,std::uint64_t connection,const std::string& fields) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if(!g_connected || g_capture || g_captureGeneration.load()!=generation || g_connectionGeneration.load()!=connection ||
        (request.originConnectionGeneration && request.originConnectionGeneration!=connection))return false;
    g_notifications={};g_notifications.initialized=true;g_notifications.generation=generation;
    g_notifications.connection=connection;g_notifications.session=g_session;
    g_capture=true;
    LARGE_INTEGER now{};QueryPerformanceCounter(&now);
    EmitLocked("capture-start",request.id,fields+NotificationCoverageLocked("start",generation,connection),generation,now.QuadPart);
    return true;
}
// Capture tokens before waiting for the queue lock. A stale callback cannot enter
// a later capture/connection. Raw formatting and enqueue then share the same lock;
// capture-end cannot overtake a raw callback which has actually been admitted.
NotificationRoute CaptureRuntimeNotification(const NVSEMessagingInterface::Message& message,const NotificationContext& context) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if(!g_connected || !g_capture || g_captureGeneration.load()!=context.generation ||
        g_connectionGeneration.load()!=context.connection)return NotificationRoute::Ignored;
    const auto index=NotificationIndex(message.type);
    if(index<std::size(NotificationTypes) && NotificationCaptureMatchesLocked(context.generation,context.connection)) {
        auto& count=g_notifications.counts[index];
        LARGE_INTEGER now{};QueryPerformanceCounter(&now);
        if(!count.count){count.firstFrame=g_frame;count.firstQpc=now.QuadPart;}
        count.lastFrame=g_frame;count.lastQpc=now.QuadPart;
        if(count.count<NotificationCountLimit)++count.count;else count.saturated=true;
        return NotificationRoute::Aggregated;
    }
    // Legacy synthetic callers without a real capture start retain raw behavior;
    // their end evidence explicitly reports unavailable counters, never zero counts.
    std::string extra=",\"messageType\":"+std::to_string(message.type);
    if(message.type==8)extra+=",\"loadSucceeded\":"+std::string(message.data?"true":"false");
    if((message.type==3 || message.type==6) && message.data && message.dataLen) {
        const char* path=static_cast<const char*>(message.data);size_t length=0;
        while(length<std::min(message.dataLen,4096u) && path[length])++length;
        extra+=",\"savePath\":"+Quote(std::string(path,length));
    }
    if(message.type==10 && message.data && message.dataLen) {
        const auto length=std::min(message.dataLen,8192u);const char* text=static_cast<const char*>(message.data);
        size_t count=0;while(count<length && text[count])++count;
        extra+=",\"text\":"+Quote(std::string(text,count));
    }
    LARGE_INTEGER now{};QueryPerformanceCounter(&now);
    EmitLocked(message.type==10?"script-error":"runtime-message",0,extra,context.generation,now.QuadPart);
    return NotificationRoute::Raw;
}
NotificationRoute CaptureRuntimeNotification(const NVSEMessagingInterface::Message& message) {
    return CaptureRuntimeNotification(message,{g_captureGeneration.load(),g_connectionGeneration.load()});
}
// Called only when deferred combat cleanup has finished. The final counts, end
// row, capture retirement and pending-request clear form one queue transaction.
bool EndNotificationCapture(const Request& request,std::uint64_t generation,std::uint64_t connection,const std::string& fields) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if(!g_connected || !g_capture || g_captureGeneration.load()!=generation || g_connectionGeneration.load()!=connection)return false;
    LARGE_INTEGER now{};QueryPerformanceCounter(&now);
    EmitLocked("capture-end",request.id,fields+NotificationCoverageLocked("end",generation,connection),generation,now.QuadPart);
    g_capture=false;++g_captureGeneration;
    g_notifications.initialized=false;
    ClearRequestsLocked(request.kind==5?"capture-cancelled":"capture-stopped",GetTickCount64(),connection);
    return true;
}
