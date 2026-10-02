// Included after the shared SDK helpers. All queries execute on the game thread.
// Candidate filenames are confirmed by GetModIndex; directory membership is not load-order evidence.
std::unordered_map<std::string, Script*> g_pluginExpressions;
bool g_loadedGameObserved=false;
bool PluginNumber(const std::string& expression, UInt32& result) {
    if (!g_numericEnabled || !g_script) return false;
    auto found=g_pluginExpressions.find(expression);
    if (found==g_pluginExpressions.end()) {
        if (g_pluginExpressions.size()>=512) return false;
        auto compiled=g_script->CompileExpression(expression.c_str());
        if (!compiled) return false;
        found=g_pluginExpressions.emplace(expression,compiled).first;
    }
    NumericElement value{};
    if (!g_script->CallFunction(found->second,nullptr,nullptr,&value,0) || value.type!=1 ||
        !std::isfinite(value.number) || value.number<0 || value.number>255 || std::floor(value.number)!=value.number) {
        if (value.type==3) g_numericEnabled=false;
        return false;
    }
    result=static_cast<UInt32>(value.number); return true;
}
std::string CapturePluginNamespace(const std::string& payload) {
    const auto split=payload.find('\n');
    g_session=payload.substr(0,split);
    auto unavailable=[](const char* reason) { return std::string(",\"pluginNamespace\":{\"status\":\"unavailable\",\"reason\":")+Quote(reason)+"}"; };
    if (split==std::string::npos) return unavailable("no-verified-candidate-set");
    if (!g_loadedGameObserved) return unavailable("no-successful-game-load-observed");
    std::vector<std::string> candidates; std::istringstream input(payload.substr(split+1)); std::string name;
    while (std::getline(input,name)) {
        if (name.empty() || name.size()>255 || candidates.size()>=255 || name.find_first_of("\"\\/:\r")!=std::string::npos ||
            !std::all_of(name.begin(),name.end(),[](unsigned char c){return c>=32 && c<127;})) return unavailable("invalid-candidate-name");
        candidates.push_back(name);
    }
    UInt32 count=0;
    if (!PluginNumber("GetNumLoadedMods",count) || !count) return unavailable("engine-mod-count-unavailable");
    std::vector<std::pair<UInt32,std::string>> entries; std::unordered_set<UInt32> indices;
    bool complete=true;
    for (const auto& candidate:candidates) {
        UInt32 index=255;
        if (!PluginNumber("GetModIndex \""+candidate+"\"",index)) { complete=false; continue; }
        if (index==255) continue;
        if (index>=count || !indices.insert(index).second) complete=false;
        entries.emplace_back(index,candidate);
    }
    std::sort(entries.begin(),entries.end());
    if (entries.size()!=count) complete=false;
    std::string json=",\"pluginNamespace\":{\"status\":"+Quote(complete?"complete":"partial")+
        ",\"count\":"+std::to_string(count)+",\"evidence\":\"engine-get-mod-index-and-count\",\"entries\":[";
    bool first=true;
    for (const auto& entry:entries) {
        if (!first) json+=','; first=false;
        json+="{\"index\":"+std::to_string(entry.first)+",\"name\":"+Quote(entry.second)+"}";
    }
    return json+"]}";
}
