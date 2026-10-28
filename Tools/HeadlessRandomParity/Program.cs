using System.Reflection;
using System.Text.Json;
using HeadlessKards;
using KardsSim.Kismet;
using ReferenceRandom=KardsSim.Core.Rng;

int checks=0;
void Check(bool condition,string label)
{ if(!condition) throw new Exception("RANDOM PARITY FAILED: "+label); checks++; }
var s0=typeof(ReferenceRandom).GetField("_s0",BindingFlags.NonPublic|BindingFlags.Instance)!;
var s1=typeof(ReferenceRandom).GetField("_s1",BindingFlags.NonPublic|BindingFlags.Instance)!;
void Same(ReferenceRandom reference,LogicRandom managed,LogicRandom rust,string label)
{
    var m=managed.Capture(); var r=rust.Capture();
    Check((ulong)s0.GetValue(reference)! == m.S0 && (ulong)s1.GetValue(reference)! == m.S1,label+" reference state");
    Check(m==r,label+" managed/Rust state+draw count");
}
int Bridge(ReferenceRandom random,int low,int high)
{
    // Exact expression in the pinned EngineHost RandomIntegerInRangeFromStream
    // handler. That large host is not compiled here; Core.Rng and Host are.
    if(high<low) (low,high)=(high,low);
    return unchecked(low+(int)(random.NextULong()%unchecked((ulong)(high-low+1))));
}
var seeds=new[]{int.MinValue,int.MaxValue,-1,0,1,42,12345,-7919}.Concat(Enumerable.Range(0,256).Select(i=>unchecked(i*15485863))).Distinct().ToArray();
int[] bounds=[int.MinValue,-1,0,1,2,3,7,16,31,65537,int.MaxValue];
(int low,int high)[] ranges=[(0,0),(0,1),(9,-9),(-100,-1),(int.MinValue,int.MaxValue),(int.MinValue,0),(-1,int.MaxValue),(int.MaxValue,int.MaxValue)];
foreach(int seed in seeds)
{
    var reference=new ReferenceRandom(seed);
    var managed=new LogicRandom(seed);
    var rust=new LogicRandom(seed,RandomBackend.Rust);
    Same(reference,managed,rust,"seed "+seed);
    for(int step=0;step<320;step++)
    {
        switch(step%4)
        {
            case 0:
                ulong expected=reference.NextULong();
                Check(managed.NextULong()==expected && rust.NextULong()==expected,"raw"); break;
            case 1:
                int n=bounds[(step/4)%bounds.Length],value=reference.Next(n);
                Check(managed.Next(n)==value && rust.Next(n)==value,"Next(n)"); break;
            case 2:
                var range=ranges[(step/4)%ranges.Length];
                int expectedRange=0; bool throws=false;
                try{expectedRange=Bridge(reference,range.low,range.high);}catch(DivideByZeroException){throws=true;}
                foreach(var candidate in new[]{managed,rust})
                {
                    bool actualThrows=false; int actual=0;
                    try{actual=candidate.BridgeRange(range.low,range.high);}catch(DivideByZeroException){actualThrows=true;}
                    Check(actualThrows==throws && (throws||actual==expectedRange),"bridge modulo range");
                }
                break;
            case 3:
                var a=Enumerable.Range(0,(step/4)%40).ToArray(); var b=a.ToArray(); var c=a.ToArray();
                reference.Shuffle(a); managed.Shuffle(b); rust.Shuffle(c);
                Check(a.SequenceEqual(b)&&a.SequenceEqual(c),"shuffle"); break;
        }
        Same(reference,managed,rust,"mixed trace");
        if(step%37==0)
        {
            // Restore to the other backend without advancing the stream.
            var saved=Json.Read<LogicRandomState>(Json.Write(managed.Capture()));
            managed=LogicRandom.Restore(saved); rust=LogicRandom.Restore(saved,RandomBackend.Rust);
            Same(reference,managed,rust,"restore");
        }
    }
}

// Force the rare rejection boundary instead of hoping a random test hits it.
var boundary=new ReferenceRandom(0); s0.SetValue(boundary,ulong.MaxValue); s1.SetValue(boundary,0UL);
var bm=LogicRandom.Restore(new(LogicRandom.Profile,0,ulong.MaxValue,0,8));
var br=LogicRandom.Restore(bm.Capture(),RandomBackend.Rust);
int rejectedValue=boundary.Next(2);
Check(bm.Next(2)==rejectedValue && br.Next(2)==rejectedValue,"rejection boundary value");
Same(boundary,bm,br,"rejection boundary"); Check(bm.Capture().RawDraws>=10,"extra draw on rejection");

// Real unmodified Host.cs, including its static Random(12345), two Host objects
// interleaved. The passed stream argument must be ignored like the reference.
var hostA=new Host(); var hostB=new Host(); var hostLocal=new HostShuffleRandom();
for(int i=0;i<700;i++)
{
    var expected=Enumerable.Range(0,i%67).ToList(); var actual=expected.ToArray();
    var host=i%2==0?hostA:hostB;
    var array=host.MakeArray(expected.Select(Val.Of));
    host.Call("Array_ShuffleFromStream",[Val.Ref("KismetArrayLibrary"),array,Val.Of(i*17)]);
    hostLocal.Shuffle(actual);
    var got=((KArr)array.O).Items.Select(v=>(int)v.AsInt()).ToArray();
    Check(got.SequenceEqual(actual),"process-global host shuffle");
    if(i==333) hostLocal=HostShuffleRandom.Restore(Json.Read<HostRandomState>(Json.Write(hostLocal.Capture())));
}

// Engine opening consumes one shared logical stream: left shuffle then right.
var definitions=Json.Read<Definition[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"cards.json")));
foreach(int seed in seeds.Take(32))
{
    var oracle=new ReferenceRandom(seed);
    int length=Engine.DemoDeck.Length;
    var left=Enumerable.Range(2,length).ToList(); var right=Enumerable.Range(length+3,length).ToList();
    oracle.Shuffle(left); oracle.Shuffle(right);
    var em=new Engine(definitions).New(new(seed));
    var er=new Engine(definitions,RandomBackend.Rust).New(new(seed));
    Check(em.Players[0].Deck.SequenceEqual(left.Skip(4)) && em.Players[1].Deck.SequenceEqual(right.Skip(5)),"engine shuffle ordering");
    Check(Engine.StateHash(em)==Engine.StateHash(er),"engine backend independence");
    Check(em.LogicRandom!.S0==(ulong)s0.GetValue(oracle)! && em.LogicRandom.S1==(ulong)s1.GetValue(oracle)!,"engine persisted stream");
}
var plan=new RandomTracePlan(new(){["a"]=42,["b"]=-1},[
    new(RandomOperationKind.Raw,"a"),new(RandomOperationKind.Next,"a",N:1),
    new(RandomOperationKind.Range,"a",Low:7,High:7),new(RandomOperationKind.HostShuffle,"a",Values:[1,2,3,4,5]),
    new(RandomOperationKind.HostShuffle,"b",Values:[1,2,3,4,5]),new(RandomOperationKind.Range,"a",Low:int.MinValue,High:int.MaxValue),
    new(RandomOperationKind.Shuffle,"b",Values:[0,1,2,3,4]),new(RandomOperationKind.Raw,"a")]);
var traceManaged=RandomTrace.Run(plan,RandomBackend.Managed);
var traceRust=RandomTrace.Run(plan,RandomBackend.Rust);
Check(Json.Write(traceManaged)==Json.Write(traceRust),"whole mixed trace byte-for-byte cross-backend");
var prefix=RandomTrace.Run(plan with {Operations=plan.Operations.Take(4).ToList()},RandomBackend.Managed);
var suffix=RandomTrace.Run(new(null,plan.Operations.Skip(4).ToList(),Json.Read<RandomTraceCheckpoint>(Json.Write(prefix.Final))),RandomBackend.Rust);
Check(Json.Write(suffix.Final)==Json.Write(traceManaged.Final),"whole-process checkpoint resume across backends");
Check(traceManaged.Steps[0].Streams["a"]==traceManaged.Steps[1].Streams["a"],"trace Next(1) consumes nothing");
Check(traceManaged.Steps[2].Streams["a"].RawDraws==traceManaged.Steps[1].Streams["a"].RawDraws+1,"trace equal-range consumes");
Check(traceManaged.Steps[3].HostRawDraws==4 && traceManaged.Steps[4].HostRawDraws==8,"two matches share host draws");
Console.WriteLine(Json.Write(new{passed=checks,seeds=seeds.Length,mixed_steps_per_seed=320,host_shuffles=700,
    reference_commit="5067a25321456e191ae4083ce098b8a2e49a31ca",actual_core_and_host_source_linked=true,rust_backend=true,
    bridge_range="exact expression oracle; full EngineHost not compiled",native_ue_rng_verified=false}));
