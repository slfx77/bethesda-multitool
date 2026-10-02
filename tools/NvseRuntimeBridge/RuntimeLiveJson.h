#pragma once
#include <cmath>
#include <cstdlib>
#include <map>
#include <string>
#include <vector>

// Deliberately bounded JSON reader for the local live-control protocol. Rejects
// duplicate keys, trailing input, invalid UTF-8, non-finite numbers and deep trees.
namespace bmt_live_json {
struct Value {
    enum Type { Null, Bool, Number, String, Array, Object } type=Null;
    std::string text;
    double number=0;
    bool boolean=false;
    std::vector<Value> array;
    std::map<std::string,Value> object;
    const Value* Get(const std::string& key) const {
        const auto it=object.find(key);return type==Object && it!=object.end()?&it->second:nullptr;
    }
    std::string StringOr(const std::string& fallback="") const {return type==String?text:fallback;}
};
class Reader {
    const std::string& input;
    size_t position=0,nodes=0;
    std::string& error;
    bool Fail(const char* why) {if(error.empty())error=std::string(why)+" at byte "+std::to_string(position);return false;}
    void Space() {while(position<input.size() && (input[position]==' ' || input[position]=='\t' || input[position]=='\r' || input[position]=='\n'))++position;}
    bool Take(char c) {Space();if(position<input.size() && input[position]==c){++position;return true;}return false;}
    bool Hex(unsigned& value) {
        value=0;
        for(int i=0;i<4;++i) {
            if(position>=input.size())return Fail("truncated unicode escape");
            const char c=input[position++];
            const int n=c>='0' && c<='9'?c-'0':c>='a' && c<='f'?c-'a'+10:c>='A' && c<='F'?c-'A'+10:-1;
            if(n<0)return Fail("invalid unicode escape");value=value*16+unsigned(n);
        }
        return true;
    }
    static void Utf8(std::string& output,unsigned value) {
        if(value<0x80)output+=char(value);
        else if(value<0x800){output+=char(0xc0|(value>>6));output+=char(0x80|(value&63));}
        else if(value<0x10000){output+=char(0xe0|(value>>12));output+=char(0x80|((value>>6)&63));output+=char(0x80|(value&63));}
        else {output+=char(0xf0|(value>>18));output+=char(0x80|((value>>12)&63));output+=char(0x80|((value>>6)&63));output+=char(0x80|(value&63));}
    }
    bool String(std::string& output) {
        if(!Take('"'))return Fail("expected string");
        while(position<input.size()) {
            unsigned char c=input[position++];
            if(c=='"')return true;
            if(c<32)return Fail("control character in string");
            if(c=='\\') {
                if(position>=input.size())return Fail("truncated escape");
                c=input[position++];
                switch(c) {
                case '"':case '\\':case '/':output+=char(c);break;
                case 'b':output+='\b';break;case 'f':output+='\f';break;
                case 'n':output+='\n';break;case 'r':output+='\r';break;case 't':output+='\t';break;
                case 'u': {
                    unsigned value=0;if(!Hex(value))return false;
                    if(value>=0xd800 && value<=0xdbff) {
                        if(position+2>input.size() || input[position++]!='\\' || input[position++]!='u')return Fail("missing low surrogate");
                        unsigned low=0;if(!Hex(low))return false;
                        if(low<0xdc00 || low>0xdfff)return Fail("invalid low surrogate");
                        value=0x10000+((value-0xd800)<<10)+(low-0xdc00);
                    } else if(value>=0xdc00 && value<=0xdfff)return Fail("unpaired low surrogate");
                    Utf8(output,value);break;
                }
                default:return Fail("invalid escape");
                }
            } else if(c>=0x80) {
                unsigned count=c>=0xc2 && c<=0xdf?1:c>=0xe0 && c<=0xef?2:c>=0xf0 && c<=0xf4?3:0;
                if(!count || position+count>input.size())return Fail("invalid UTF-8");
                unsigned value=c & (0x7f>>count);output+=char(c);
                for(unsigned i=0;i<count;++i){const unsigned char next=input[position++];if((next&0xc0)!=0x80)return Fail("invalid UTF-8");value=(value<<6)|(next&63);output+=char(next);}
                if(value<(count==1?0x80u:count==2?0x800u:0x10000u) || value>0x10ffff || (value>=0xd800 && value<=0xdfff))return Fail("invalid UTF-8 scalar");
            } else output+=char(c);
        }
        return Fail("unterminated string");
    }
    bool Read(Value& value,unsigned depth) {
        Space();if(++nodes>8192 || depth>12)return Fail("JSON complexity limit");
        if(position>=input.size())return Fail("expected value");
        const char c=input[position];
        if(c=='"'){value.type=Value::String;return String(value.text);}
        if(c=='{' || c=='[') {
            ++position;value.type=c=='{'?Value::Object:Value::Array;
            const char end=c=='{'?'}':']';if(Take(end))return true;
            do {
                std::string key;
                if(c=='{' && (!String(key) || !Take(':')))return Fail("expected object member");
                Value child;if(!Read(child,depth+1))return false;
                if(c=='{'){if(!value.object.emplace(key,std::move(child)).second)return Fail("duplicate key");}
                else value.array.push_back(std::move(child));
                if(Take(end))return true;
            } while(Take(','));
            return Fail("expected comma or closing delimiter");
        }
        for(const auto* literal:{"true","false","null"}) {
            const std::string word=literal;
            if(input.compare(position,word.size(),word)==0){position+=word.size();value.type=word=="null"?Value::Null:Value::Bool;value.boolean=word=="true";return true;}
        }
        const size_t start=position;
        if(input[position]=='-')++position;
        if(position>=input.size())return Fail("invalid number");
        if(input[position]=='0')++position;
        else {if(input[position]<'1' || input[position]>'9')return Fail("expected number");while(position<input.size() && input[position]>='0' && input[position]<='9')++position;}
        if(position<input.size() && input[position]=='.') {
            ++position;const auto first=position;
            while(position<input.size() && input[position]>='0' && input[position]<='9')++position;
            if(position==first)return Fail("invalid number fraction");
        }
        if(position<input.size() && (input[position]=='e' || input[position]=='E')) {
            ++position;if(position<input.size() && (input[position]=='+' || input[position]=='-'))++position;
            const auto first=position;while(position<input.size() && input[position]>='0' && input[position]<='9')++position;
            if(position==first)return Fail("invalid number exponent");
        }
        value.type=Value::Number;value.text=input.substr(start,position-start);value.number=std::strtod(value.text.c_str(),nullptr);
        return std::isfinite(value.number) || Fail("non-finite number");
    }
public:
    Reader(const std::string& source,std::string& failure):input(source),error(failure){}
    bool Parse(Value& value) {
        error.clear();value={};if(input.size()>65536)return Fail("payload limit");
        if(!Read(value,0))return false;Space();return position==input.size() || Fail("trailing input");
    }
};
inline bool Parse(const std::string& input,Value& value,std::string& error){return Reader(input,error).Parse(value);}
}
