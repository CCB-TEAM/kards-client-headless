using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeadlessKards;

public sealed record WireReplay(FyBootstrap Bootstrap, List<string> Packets, string FinalHash, string? RandomProfile=null);
public static class ProtocolTest
{
    public static string RandomProfile(State state)=>state.Battle is JsonElement b&&b.GetProperty("client_random").ValueKind!=JsonValueKind.Null?ClientRandom.Profile:LogicRandom.Profile;
    public static WireReplay SelfPlay(Engine engine, int seed)
    {
        var bootstrap = ProtocolFixtures.FromDemo(engine, seed);
        var protocol = new FyProtocol(engine, bootstrap);
        List<string> packets = [];
        for (int i = 1; i <= 5000 && protocol.State.Outcome is null; i++)
        {
            var action = protocol.Think(i) ?? throw new RuleException("No protocol action");
            var packet = FyCodec.Encode(Json.Write(action), action.SendActionId);
            protocol.AcceptPacket(packet);
            packets.Add(packet);
        }
        if (protocol.State.Outcome is null) throw new RuleException("Protocol AI did not terminate");
        return new(bootstrap, packets, protocol.Checkpoint().StateHash,RandomProfile(protocol.State));
    }
    public static FyCheckpoint Replay(Engine engine, WireReplay replay)
    {
        var protocol = new FyProtocol(engine, replay.Bootstrap);
        if(replay.RandomProfile!=RandomProfile(protocol.State)) throw new RuleException("Wire replay random profile differs or predates RNG migration");
        foreach (var packet in replay.Packets) protocol.AcceptPacket(packet);
        var result = protocol.Checkpoint();
        if (result.StateHash != replay.FinalHash) throw new RuleException("Wire replay diverged");
        return result;
    }
    public static void Run(Engine engine)
    {
        int checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new RuleException("PROTOCOL TEST FAILED: " + label); checks++; }
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch (RuleException) { rejected = true; }
            Check(rejected, label);
        }
        static string Comparable(State state)
        {
            var copy = Json.Read<State>(Json.Write(state)); copy.Revision = 0; copy.Seed = 0;
            // Bootstrap traverses arrays by zone, not in engine allocation order.
            copy.Cards = copy.Cards.OrderBy(c => c.Id).ToList();
            return Engine.StateHash(copy);
        }
        foreach (var text in new[]{"", "中文：兔子协议", new string('a',10000)})
        foreach (var id in new[]{0,1,0xFFFFFF})
        {
            var roundtrip = FyCodec.Decode(FyCodec.Encode(text,id));
            Check(roundtrip.Text == text && roundtrip.SendActionId == id, "UTF8 codec roundtrip");
        }
        Reject(() => FyCodec.Decode("99000000AAAA"), "Bad salt index");
        Reject(() => FyCodec.Decode("garbage"), "Bad header");
        Reject(() => FyCodec.Encode("x",0x1000000), "Truncated send id prevented");
        var packet = FyCodec.Encode("hello",7);
        Reject(() => FyCodec.Decode(packet[..^1]), "Truncated packet");
        Reject(() => FyCodec.Decode(packet[..2]+"000006"+packet[8..]), "Forged byte length");
        var bootstrap = ProtocolFixtures.FromDemo(engine,42);
        var cursorProtocol=new FyProtocol(engine,bootstrap with { LastSeenActionId=8 });
        Check(cursorProtocol.Think(9)!.ActionId==9,"Explicit post-mulligan action cursor");
        Reject(() => new FyProtocol(engine,bootstrap with { MulliganComplete=false }), "Require post-mulligan input");
        Reject(() => new FyProtocol(engine,bootstrap with { FirstTurnDraws=null }), "Require explicit opening draw policy");
        var p = new FyProtocol(engine,bootstrap);
        var initial = p.Checkpoint().StateHash;
        var start = p.Think(1)!;
        Reject(() => p.Accept(start with { ActionId=2 }), "Out of order rejected");
        Reject(() => p.Accept(start with { PlayerId=12345 }), "Unknown actor rejected");
        Reject(() => p.Accept(start with { SubActions=[JsonSerializer.SerializeToElement(new { name="unknown" })] }), "Unknown subaction rejected");
        var badData = new Dictionary<string,string>(start.ActionData) { ["75"]="999" };
        Reject(() => p.Accept(start with { ActionData=badData }), "HQ desync rejected");
        Check(initial == p.Checkpoint().StateHash, "Rejected stream entries atomic");
        var goodSnapshot=ProtocolFixtures.Snapshot(p.Checkpoint());
        Check(NativeSnapshot.Compare(engine,p.Checkpoint(),goodSnapshot).Equal,"Synthetic snapshot normalized comparison");
        var altered=JsonNode.Parse(goodSnapshot.GetRawText())!;
        altered["cards"]![0]!["defense"]=7;
        var mismatch=NativeSnapshot.Compare(engine,p.Checkpoint(),JsonSerializer.SerializeToElement(altered));
        Check(!mismatch.Equal && mismatch.Differences.Any(d=>d.Field.EndsWith(".defense")),"Locate first card defense divergence");
        altered=JsonNode.Parse(goodSnapshot.GetRawText())!;
        altered["cards"]![1]!["hasGuard"]=true;
        Check(NativeSnapshot.Compare(engine,p.Checkpoint(),JsonSerializer.SerializeToElement(altered)).Unsupported.Count==1,"Unknown snapshot ability rejected");
        p.Accept(start);
        var once = p.Checkpoint().StateHash;
        p.Accept(start);
        Check(once == p.Checkpoint().StateHash, "Exact retries idempotent");
        Reject(() => p.Accept(start with { SendActionId=2 }), "Conflicting duplicate rejected");
        Check(Comparable(p.State)==Comparable(engine.New(new(42))), "Bootstrap imports native IDs/hand/deck order");
        var ending = p.EncodeCommand(new(Side.Left,ActionKind.EndTurn),2);
        p.Accept(ending);
        Check(p.AwaitingStart && p.State.Active == Side.Right && p.State.Players[1].MaxKredits == 0, "End does not start/refresh next turn");
        var beforeDraw = p.State.Players[1].Deck.Count;
        var nextStart = p.Think(3)!;
        p.Accept(nextStart);
        Check(p.State.Players[1].Deck.Count==beforeDraw-1 && p.State.Players[1].Kredits==1, "Start draws/refills exactly once");
        p.Accept(nextStart);
        Check(p.State.Players[1].Deck.Count==beforeDraw-1, "Start retry does not draw twice");
        Reject(() => p.AcceptPacket(FyCodec.Encode(Json.Write(p.Think(4)),999)), "Envelope/header send id disagreement");

        for (int seed=0;seed<50;seed++)
        {
            var session = new FyProtocol(engine,ProtocolFixtures.FromDemo(engine,seed));
            var baseline = engine.New(new(seed));
            int iterations=0;
            while (session.State.Outcome is null && iterations++ < 5000)
            {
                var wire = session.Think(iterations)!;
                if (session.AwaitingStart)
                {
                    session.AcceptPacket(FyCodec.Encode(Json.Write(wire),wire.SendActionId));
                    Check(Comparable(session.State)==Comparable(baseline), "Split start matches baseline "+seed);
                }
                else
                {
                    var command = engine.Think(baseline)!;
                    baseline = engine.Apply(baseline,command,baseline.Revision);
                    session.AcceptPacket(FyCodec.Encode(Json.Write(wire),wire.SendActionId));
                    if (!session.AwaitingStart) Check(Comparable(session.State)==Comparable(baseline), "Wire action matches baseline "+seed);
                }
            }
            Check(session.State.Outcome is not null, "Wire selfplay terminates "+seed);
            Check(Comparable(session.State)==Comparable(baseline), "Final state matches "+seed);
        }
        var replay=SelfPlay(engine,42);
        Check(Replay(engine,replay).StateHash==replay.FinalHash,"Encrypted stream replay");
        Console.WriteLine(Json.Write(new { passed=checks, wire_selfplay_seeds=50, native_game_client_tested=false }));
    }
}
