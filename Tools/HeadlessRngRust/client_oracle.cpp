
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
float GetFraction() const
	{
		MutateSeed();

		float Result;

		*(uint32*)&Result = 0x3F800000U | (Seed >> 9);

		return Result - 1.0f; 
	}
FORCEINLINE int32 RandHelper( int32 A ) const
	{
		// GetFraction guarantees a result in the [0,1) range.
		return ((A > 0) ? FMath::TruncToInt(GetFraction() * float(A)) : 0);
	}
FORCEINLINE int32 RandRange( int32 Min, int32 Max ) const
	{
		const int32 Range = (Max - Min) + 1;

		return Min + RandHelper(Range);
	}
void MutateSeed() const
	{
		Seed = (Seed * 196314165U) + 907633515U; 
	}
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
