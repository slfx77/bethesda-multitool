void TestDispatchFingerprint() {
    const char* cases[]={"observed","first-unmapped","second-unmapped","first-unreadable","second-unreadable","changed"};
    for(size_t index=0;index<std::size(cases);++index) {
        unsigned maps=0,reads=0;
        const auto sample=ReadDispatchWindow([&](){++maps;return !(index==1 || (index==2 && maps==2));},
            [&](void* destination,size_t count){++reads;memset(destination,0x90,count);
                if(index==5 && reads==2)static_cast<std::uint8_t*>(destination)[count-1]=0xCC;
                return !((index==3 && reads==1) || (index==4 && reads==2));});
        Check((!strcmp(sample.status,"observed"))==(index==0),cases[index]);
        Check(index==0?sample.bytesHex.size()==DispatchWindowLength*2 && sample.sha256.size()==64:
            sample.bytesHex.empty() && sample.sha256.empty(),"unreadable or changing code acquired a fingerprint");
    }
    // The fixture executable is not the supported retail image. No engine address is read.
    const bool previous=g_pcLayoutVerified;g_pcLayoutVerified=false;
    SetLastError(12345);const auto fields=DispatchFingerprintFields("start",77,88);
    Check(GetLastError()==12345,"dispatch observation changed LastError");g_pcLayoutVerified=previous;
    Check(fields.find("\"status\":\"unavailable\"")!=std::string::npos && fields.find("\"bytesHex\":\"\"")!=std::string::npos,
        "unsupported executable acquired loaded-code evidence");
}
