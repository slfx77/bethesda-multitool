// Dedicated message callback, independent of the global script owner/button pair.
// ShowMessageBox signature/address: pinned xNVSE GameAPI.h/.cpp. The button reader
// is the sole call in the pinned engine's ShowMessageBox_Callback (0x005B4A70).
// Both routines were read from the admitted PC process and are checked below.
using ProbeMessageCallback = void (__cdecl*)();
using ProbeShowMessage = bool (__cdecl*)(const char*,UInt32,UInt32,ProbeMessageCallback,UInt32,UInt32,float,float,...);
using ProbeReadButton = unsigned char (__cdecl*)();
ProbeShowMessage g_probeShowMessage=nullptr;
ProbeReadButton g_probeReadButton=nullptr;
bool g_messageApiAttempted=false, g_messageApiVerified=false;
struct PendingMessageProbe {
    bool pending=false;
    std::uint64_t request=0, generation=0, instance=0;
};
PendingMessageProbe g_pendingMessageProbe;
std::uint64_t g_probeInstance=0;
std::uint64_t g_probeChoiceSerial=0;

void VerifyMessageProbeApi() {
    if (g_messageApiAttempted) return;
    g_messageApiAttempted=true;
    if (!g_pcLayoutVerified) return;
    constexpr std::uint8_t showPrefix[]={0x55,0x8B,0xEC,0x83,0xEC,0x08,0xE8,0x85,0x33,0xDB,0xFF,0x85,0xC0,0x74,0x6C};
    constexpr std::uint8_t readBody[]={0x55,0x8B,0xEC,0x51,0xE8,0x67,0x32,0xDB,0xFF,0x8A,0x80,0xE4,0,0,0,0x88,0x45,0xFF,0xE8,0x59,0x32,0xDB,0xFF,0xC6,0x80,0xE4,0,0,0,0xFF,0x8A,0x45,0xFF,0x8B,0xE5,0x5D,0xC3};
    std::uint8_t show[sizeof(showPrefix)]{},read[sizeof(readBody)]{}; SIZE_T count=0;
    if (!ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(0x00703E80),show,sizeof(show),&count) || count!=sizeof(show) ||
        memcmp(show,showPrefix,sizeof(show))!=0 ||
        !ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(0x00703FA0),read,sizeof(read),&count) || count!=sizeof(read) ||
        memcmp(read,readBody,sizeof(read))!=0) return;
    g_probeShowMessage=reinterpret_cast<ProbeShowMessage>(0x00703E80);
    g_probeReadButton=reinterpret_cast<ProbeReadButton>(0x00703FA0);
    g_messageApiVerified=true;
}
void __cdecl MessageProbeCallback() {
    // The engine invokes this callback for the message instance we supplied.
    // Consume its button without writing GetButtonPressed's shared script globals.
    if (!g_pendingMessageProbe.pending || !g_probeReadButton) return;
    const auto pending=g_pendingMessageProbe;
    const auto button=g_probeReadButton();
    g_pendingMessageProbe={};
    if (!g_capture || !g_connected || pending.generation!=g_captureGeneration.load()) return;
    const auto fields=",\"messageInstance\":"+std::to_string(pending.instance)+
        ",\"value\":"+std::to_string(button)+",\"evidence\":\"dedicated-engine-message-callback\"";
    if (button!=0) Emit("error",pending.request,fields+",\"error\":\"unexpected-probe-button\"",pending.generation);
    else { ++g_probeChoiceSerial; Emit("message-probe-choice",pending.request,fields,pending.generation); }
}
void MessageProbe(const Request& request) {
    if (!g_capture || !g_messageApiVerified || !g_probeShowMessage || !g_probeReadButton) {
        Emit("error",request.id,",\"error\":\"message-probe-api-unavailable\""); return;
    }
    // A cancelled/disconnected queued message still owns its callback. Do not let
    // a later capture reuse the slot until that message has actually been closed.
    if (g_pendingMessageProbe.pending || request.payload!="show-ok") {
        Emit("error",request.id,",\"error\":\"message-probe-active-or-invalid\""); return;
    }
    g_pendingMessageProbe={true,request.id,g_captureGeneration.load(),++g_probeInstance};
    if (!g_probeShowMessage("BMT runtime observation: click OK.",0,0,MessageProbeCallback,0,0x17,0,0,"Ok",nullptr)) {
        g_pendingMessageProbe={};
        Emit("error",request.id,",\"error\":\"message-probe-call-failed\""); return;
    }
    Emit("message-probe-start",request.id,",\"evidence\":\"dedicated-engine-message-callback\",\"expectedButtonCount\":1,\"messageInstance\":"+
        std::to_string(g_pendingMessageProbe.instance));
}
