"""Build an oracle from the user's installed UE header, not a second handwritten RNG."""
from pathlib import Path
import hashlib
import json

root = Path(__file__).resolve().parent
header = Path(r'D:\UE\UE_5.6\Engine\Source\Runtime\Core\Public\Math\RandomStream.h')
text = header.read_text(encoding='utf-8-sig')
def method(signature):
    start = text.index(signature)
    opening = text.index('{',start)
    depth = 1
    at = opening + 1
    while depth:
        if text[at] == '{': depth += 1
        if text[at] == '}': depth -= 1
        at += 1
    return text[start:at]

methods = '\n'.join(method(s) for s in ['float GetFraction() const','FORCEINLINE int32 RandHelper( int32 A ) const',
    'FORCEINLINE int32 RandRange( int32 Min, int32 Max ) const','void MutateSeed() const'])
prefix = r'''
#include <cstdint>
#include <cstdio>
#include <climits>
using int32 = std::int32_t;
using uint32 = std::uint32_t;
#define FORCEINLINE inline
struct FMath { static int32 TruncToInt(float value) { return static_cast<int32>(value); } };
struct Oracle {
    mutable uint32 Seed;
    std::uint64_t draws = 0;
    explicit Oracle(int32 initial) : Seed(static_cast<uint32>(initial)) {}
    int32 raw(int32 min, int32 max) { if (max-min+1>0) ++draws; return RandRange(min,max); }
    int32 wrapped(int32 min,int32 max) { int burn=raw(1,10); for(int i=0;i<burn;++i) raw(0,1); return raw(min,max); }
'''
suffix = r'''
};
int main() {
    const int32 seeds[] = {INT_MIN, INT_MAX, -1, 0, 1, 42, 648741};
    for (int32 seed : seeds) {
        Oracle rng(seed);
        for (int step=0;step<5000;++step) {
            bool wrapper=step%2==0;
            int32 min=step%3==0?-100:0;
            int32 max=step%5==0?min:(step%97);
            int32 value=wrapper?rng.wrapped(min,max):rng.raw(min,max);
            std::printf("{\"initial\":%d,\"min\":%d,\"max\":%d,\"wrapper\":%s,\"value\":%d,\"seed\":%u,\"draws\":%llu}\n",
                seed,min,max,wrapper?"true":"false",value,rng.Seed,(unsigned long long)rng.draws);
        }
    }
}
'''
(root/'client_oracle.cpp').write_text(prefix+methods+suffix,encoding='utf-8')
(root/'client_oracle_source.json').write_text(json.dumps({'header':str(header),'sha256':hashlib.sha256(header.read_bytes()).hexdigest(),
    'methods_extracted_verbatim':True,'blueprint_wrapper':'Flora RandomIntFromRangeWithStream 2026-10-28'},indent=2),encoding='utf-8')
