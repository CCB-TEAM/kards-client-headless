using System.Runtime.InteropServices;

namespace HeadlessKards;

public sealed record ClientRandomState(string Profile,int InitialSeed,uint Seed,ulong Draws);

// Separate from the fixed kards-sim contract. This follows UE 5.6's
// RandomStream.h and the current Flora RandomIntFromRangeWithStream graph.
public sealed class ClientRandom
{
    public const string Profile="kards-client/ue5.6-high23/flora-20261028-burn-v1";
    private uint seed;
    private ulong draws;
    private readonly int initialSeed;
    private readonly RandomBackend backend;
    public ClientRandom(int seed,RandomBackend backend=RandomBackend.Managed)
    { initialSeed=seed; this.seed=unchecked((uint)seed); this.backend=backend; }
    private ClientRandom(ClientRandomState state,RandomBackend backend)
    {
        if(state.Profile!=Profile) throw new RuleException("Incompatible client RNG checkpoint");
        initialSeed=state.InitialSeed;seed=state.Seed;draws=state.Draws;this.backend=backend;
    }
    public static ClientRandom Restore(ClientRandomState state,RandomBackend backend=RandomBackend.Managed)=>new(state,backend);
    public ClientRandomState Capture()=>new(Profile,initialSeed,seed,draws);
    public int Range(int min,int max)
    {
        if(backend==RandomBackend.Rust)
        {
            int value=0;
            try { if(NativeRange(ref seed,ref draws,min,max,out value)!=0) throw new RuleException("Client RNG native error"); }
            catch(Exception e) when(e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { throw new RuleException("Build/copy the updated Rust RNG library for client RNG"); }
            return value;
        }
        int width=unchecked(max-min+1);
        if(width<=0) return min;
        seed=unchecked(seed*196314165u+907633515u); draws=unchecked(draws+1);
        float fraction=BitConverter.UInt32BitsToSingle(0x3F800000u|(seed>>9))-1.0f;
        return unchecked(min+(int)(fraction*(float)width));
    }
    public int CardRange(int min,int max)
    {
        int burn=Range(1,10);
        for(int i=0;i<burn;i++) _=Range(0,1);
        return Range(min,max);
    }
    public int CardIndex(int count)=>count<=0 ? -1 : CardRange(0,count-1);
    [DllImport("headless_rng",EntryPoint="hrng_client_range",CallingConvention=CallingConvention.Cdecl)]
    private static extern int NativeRange(ref uint seed,ref ulong draws,int min,int max,out int result);
}
