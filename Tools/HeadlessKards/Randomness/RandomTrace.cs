namespace HeadlessKards;

public enum RandomOperationKind { Raw, Next, Range, Shuffle, HostShuffle }
public sealed record RandomOperation(RandomOperationKind Kind,string? Stream=null,int N=0,int Low=0,int High=0,int[]? Values=null);
public sealed record RandomTraceCheckpoint(Dictionary<string,LogicRandomState> Streams,HostRandomState Host,int CompletedSteps);
public sealed record RandomTracePlan(Dictionary<string,int>? Seeds,List<RandomOperation> Operations,RandomTraceCheckpoint? Initial=null);
public sealed record RandomTraceStep(int Index,RandomOperation Operation,ulong? Raw,int? Integer,int[]? Values,string? Error,
    Dictionary<string,LogicRandomState> Streams,ulong HostRawDraws);
public sealed record RandomTraceResult(string Scope,List<RandomTraceStep> Steps,RandomTraceCheckpoint Final);

public static class RandomTrace
{
    // Each trace is one fresh reference process. All named matches in this
    // trace share ONE host stream; logic streams remain per-match. A checkpoint
    // restores the whole context, not just one game's view of global state.
    public static RandomTraceResult Run(RandomTracePlan plan,RandomBackend backend)
    {
        if(plan.Operations is null || plan.Operations.Count>100000) throw new RuleException("Invalid random trace length");
        if(plan.Initial is not null && plan.Seeds is { Count:>0 }) throw new RuleException("Checkpoint restore cannot silently reseed streams");
        var streams=plan.Initial is null
            ? (plan.Seeds??[]).ToDictionary(k=>k.Key,v=>new LogicRandom(v.Value,backend),StringComparer.Ordinal)
            : plan.Initial.Streams.ToDictionary(k=>k.Key,v=>LogicRandom.Restore(v.Value,backend),StringComparer.Ordinal);
        var host=plan.Initial is null?new HostShuffleRandom():HostShuffleRandom.Restore(plan.Initial.Host);
        int offset=plan.Initial?.CompletedSteps??0;
        if(offset<0 || offset>int.MaxValue-plan.Operations.Count) throw new RuleException("Invalid trace checkpoint index");
        Dictionary<string,LogicRandomState> Capture() => streams.OrderBy(k=>k.Key,StringComparer.Ordinal).ToDictionary(k=>k.Key,v=>v.Value.Capture(),StringComparer.Ordinal);
        List<RandomTraceStep> output=[];
        foreach(var operation in plan.Operations)
        {
            ulong? raw=null; int? integer=null; int[]? values=null; string? error=null;
            if(operation.Kind==RandomOperationKind.HostShuffle)
            {
                // Null is meaningful: reference Host.Shuffle(null) is a no-op.
                values=operation.Values?.ToArray(); host.Shuffle(values);
            }
            else
            {
                if(operation.Stream is null || !streams.TryGetValue(operation.Stream,out var stream)) throw new RuleException("Unknown random stream");
                try
                {
                    switch(operation.Kind)
                    {
                        case RandomOperationKind.Raw: raw=stream.NextULong(); break;
                        case RandomOperationKind.Next: integer=stream.Next(operation.N); break;
                        case RandomOperationKind.Range: integer=stream.BridgeRange(operation.Low,operation.High); break;
                        case RandomOperationKind.Shuffle:
                            values=operation.Values?.ToArray()??throw new RuleException("Logic shuffle requires an array");
                            stream.Shuffle(values); break;
                        default: throw new RuleException("Unsupported random operation");
                    }
                }
                catch(DivideByZeroException) { error="reference_range_width_zero_after_draw"; }
            }
            output.Add(new(offset+output.Count,operation,raw,integer,values,error,Capture(),host.Capture().RawDraws));
        }
        return new("one-reference-process; shared-host/per-match-core",output,new(Capture(),host.Capture(),offset+output.Count));
    }
}
