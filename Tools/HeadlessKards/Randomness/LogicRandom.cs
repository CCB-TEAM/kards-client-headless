using System.Runtime.InteropServices;

namespace HeadlessKards;

public enum RandomBackend { Managed, Rust }
public sealed record LogicRandomState(string Profile, int Seed, ulong S0, ulong S1, ulong RawDraws);

// Independently implemented against Core/Rng.cs at the pinned reference commit.
// State transitions, unchecked int/ulong conversions and draw consumption are
// part of the compatibility contract, including the bridge's modulo bias.
public sealed class LogicRandom
{
    public const string Profile = "kards-sim/5067a25321456e191ae4083ce098b8a2e49a31ca/core-v1";
    private NativeState state;
    private readonly int seed;
    public RandomBackend Backend { get; }
    public LogicRandom(int seed, RandomBackend backend = RandomBackend.Managed)
    {
        this.seed=seed; Backend=backend;
        if (backend==RandomBackend.Rust)
        {
            NativeCheck(() => Native.CheckAbi()==1 ? 0 : -1);
            NativeCheck(() => Native.Init(seed,out state));
        }
        else
        {
            state.S0=unchecked((ulong)seed*0x9E3779B97F4A7C15UL+0x243F6A8885A308D3UL);
            state.S1=unchecked((ulong)seed)^0x13198A2E03707344UL;
            if ((state.S0|state.S1)==0) state.S0=1;
            for (int i=0;i<8;i++) NextULong();
        }
    }
    private LogicRandom(LogicRandomState snapshot,RandomBackend backend)
    {
        if (snapshot.Profile!=Profile || (snapshot.S0|snapshot.S1)==0 || snapshot.RawDraws<8) throw new RuleException("Invalid/incompatible random checkpoint");
        seed=snapshot.Seed; Backend=backend;
        state=new() { S0=snapshot.S0,S1=snapshot.S1,Draws=snapshot.RawDraws };
        if (backend==RandomBackend.Rust) NativeCheck(() => Native.CheckAbi()==1 ? 0 : -1);
    }
    public static LogicRandom Restore(LogicRandomState snapshot,RandomBackend backend=RandomBackend.Managed) => new(snapshot,backend);
    public LogicRandomState Capture() => new(Profile,seed,state.S0,state.S1,state.Draws);
    public ulong NextULong()
    {
        if (Backend==RandomBackend.Rust)
        {
            ulong value=0; NativeCheck(() => Native.NextRaw(ref state,out value)); return value;
        }
        ulong x=state.S0,y=state.S1;
        ulong result=unchecked(x+y);
        y^=x;
        state.S0=Rotate(x,55)^y^(y<<14); state.S1=Rotate(y,36);
        state.Draws=unchecked(state.Draws+1);
        return result;
    }
    public int Next(int n)
    {
        if (Backend==RandomBackend.Rust)
        {
            int nativeValue=0; NativeCheck(() => Native.Next(ref state,n,out nativeValue)); return nativeValue;
        }
        if (n<=1) return 0; // Critically: no draw for n=1, 0, or negative.
        ulong limit=ulong.MaxValue-ulong.MaxValue%(ulong)n;
        ulong value;
        do { value=NextULong(); } while (value>=limit);
        return (int)(value%(ulong)n);
    }
    public int BridgeRange(int low,int high)
    {
        if (Backend==RandomBackend.Rust)
        {
            int value=0;
            int status=NativeCall(() => Native.Range(ref state,low,high,out value));
            if (status==2) throw new DivideByZeroException("Reference int range width overflowed to zero");
            if (status!=0) throw new RuleException("Native random range failed");
            return value;
        }
        if (high<low) (low,high)=(high,low);
        ulong raw=NextULong(); // Reference draws even for a one-element range.
        int width=unchecked(high-low+1);
        return unchecked(low+(int)(raw%unchecked((ulong)width)));
    }
    public void Shuffle<T>(IList<T> list)
    {
        if (Backend==RandomBackend.Rust)
        {
            if (list.Count<=1) return;
            var order=Enumerable.Range(0,list.Count).ToArray();
            NativeCheck(() => Native.Shuffle(ref state,order,(nuint)order.Length));
            var before=list.ToArray();
            for(int i=0;i<order.Length;i++) list[i]=before[order[i]];
            return;
        }
        for(int i=list.Count-1;i>0;i--)
        { int j=Next(i+1); (list[i],list[j])=(list[j],list[i]); }
    }
    private static ulong Rotate(ulong value,int bits) => (value<<bits)|(value>>(64-bits));
    private static int NativeCall(Func<int> call)
    {
        try { return call(); }
        catch(Exception e) when(e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { throw new RuleException("Rust random backend unavailable; build/copy headless_rng.dll or explicitly select managed"); }
    }
    private static void NativeCheck(Func<int> call)
    { if(NativeCall(call)!=0) throw new RuleException("Rust random ABI/state error"); }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeState { public ulong S0,S1,Draws; }
    private static class Native
    {
        private const string Library="headless_rng";
        [DllImport(Library,EntryPoint="hrng_abi",CallingConvention=CallingConvention.Cdecl)] internal static extern int CheckAbi();
        [DllImport(Library,EntryPoint="hrng_init",CallingConvention=CallingConvention.Cdecl)] internal static extern int Init(int seed,out NativeState state);
        [DllImport(Library,EntryPoint="hrng_next_raw",CallingConvention=CallingConvention.Cdecl)] internal static extern int NextRaw(ref NativeState state,out ulong value);
        [DllImport(Library,EntryPoint="hrng_next",CallingConvention=CallingConvention.Cdecl)] internal static extern int Next(ref NativeState state,int n,out int value);
        [DllImport(Library,EntryPoint="hrng_range",CallingConvention=CallingConvention.Cdecl)] internal static extern int Range(ref NativeState state,int low,int high,out int value);
        [DllImport(Library,EntryPoint="hrng_shuffle_i32",CallingConvention=CallingConvention.Cdecl)] internal static extern int Shuffle(ref NativeState state,[In,Out] int[] values,nuint count);
    }
}
