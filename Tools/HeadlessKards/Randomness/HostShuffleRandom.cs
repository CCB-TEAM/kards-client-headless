namespace HeadlessKards;

public sealed record HostRandomState(string Profile,int[] Table,int Index,int NextIndex,ulong RawDraws);

// Models the reference Host.cs process-global new System.Random(12345).
// Explicit state replaces opaque System.Random internals, enabling checkpoints.
public sealed class HostShuffleRandom
{
    public const string Profile="kards-sim/5067a25321456e191ae4083ce098b8a2e49a31ca/host-random12345-v1";
    private readonly int[] table=new int[56];
    private int index,nextIndex=21;
    private ulong draws;
    public HostShuffleRandom()
    {
        int mj=161803398-12345; table[55]=mj;
        int mk=1;
        for(int i=1;i<55;i++)
        {
            int ii=21*i%55; table[ii]=mk; mk=mj-mk;
            if(mk<0) mk+=int.MaxValue; mj=table[ii];
        }
        for(int k=0;k<4;k++) for(int i=1;i<56;i++)
        { table[i]-=table[1+(i+30)%55]; if(table[i]<0) table[i]+=int.MaxValue; }
    }
    private HostShuffleRandom(HostRandomState snapshot)
    {
        if(snapshot.Profile!=Profile || snapshot.Table.Length!=56 || snapshot.Index is <0 or >55 || snapshot.NextIndex is <0 or >55
            || snapshot.Table.Any(n=>n<0)) throw new RuleException("Invalid host random checkpoint");
        snapshot.Table.CopyTo(table,0); index=snapshot.Index; nextIndex=snapshot.NextIndex; draws=snapshot.RawDraws;
    }
    public static HostShuffleRandom Restore(HostRandomState state) => new(state);
    public HostRandomState Capture() => new(Profile,[..table],index,nextIndex,draws);
    private int Sample()
    {
        if(++index>=56) index=1;
        if(++nextIndex>=56) nextIndex=1;
        int value=table[index]-table[nextIndex];
        if(value==int.MaxValue) value--;
        if(value<0) value+=int.MaxValue;
        table[index]=value; draws++;
        return value;
    }
    public void Shuffle<T>(IList<T>? list)
    {
        if(list is null) return;
        for(int i=list.Count-1;i>0;i--)
        {
            int j=(int)(Sample()*(1.0/int.MaxValue)*(i+1));
            (list[i],list[j])=(list[j],list[i]);
        }
    }
}

// One global stream, not one per match. Call order across sessions matters.
public static class ProcessHostShuffle
{
    private static readonly object Gate=new();
    private static readonly HostShuffleRandom Stream=new();
    public static void Shuffle<T>(IList<T>? list) { lock(Gate) Stream.Shuffle(list); }
    public static HostRandomState Capture() { lock(Gate) return Stream.Capture(); }
}
