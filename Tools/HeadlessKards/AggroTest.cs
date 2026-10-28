using System.Text.Json;

namespace HeadlessKards;

public static class AggroTest
{
    public const string Deck="%%31|5C5x6B6y7xhHlfm6rk;3f3Q5X6EfRlknI;4p6x7e7l;5Z";
    // fyserver's parser requires the explicit HQ suffix, unlike the native client.
    public const string ServerDeck=Deck+";;;~;;;|6l3v";
    public static string[] Decode(IReadOnlyCollection<Definition> cards)
    {
        var codes=cards.Where(d=>d.NativeCode is not null).ToDictionary(d=>d.NativeCode!,d=>d.Id);
        List<string> deck=[];
        var groups=Deck.Split('|')[1].Split(';');
        for(int copies=1;copies<=4;copies++)
        {
            string group=groups[copies-1];
            for(int i=0;i<group.Length;i+=2)for(int n=0;n<copies;n++)deck.Add(codes[group.Substring(i,2)]);
        }
        if(deck.Count!=39||deck.Distinct().Count()!=21)throw new RuleException("Aggro deck decoded incorrectly");
        return deck.ToArray();
    }
    public static void Run(string path,int seeds)
    {
        if(seeds<1||seeds>1000)throw new RuleException("Aggro test seeds must be 1..1000");
        using var file=JsonDocument.Parse(File.ReadAllText(path));
        var validated=RustCore.ValidatePack(file.RootElement);
        var cards=Json.Read<Definition[]>(validated.GetProperty("pack").GetProperty("cards").GetRawText());
        var engine=new Engine(cards);
        var deck=Decode(cards);
        int actions=0,checks=0;HashSet<string> deployed=[];
        WireReplay? example=null;
        for(int seed=0;seed<seeds;seed++)
        {
            var setup=new CreateRequest(seed,deck,deck,["card_location_changchun","card_location_changchun"],"client");
            var bootstrap=ProtocolFixtures.FromSetup(engine,setup,123475+seed);
            var p=new FyProtocol(engine,bootstrap);var q=new FyProtocol(engine,bootstrap);
            List<string> packets=[];
            while(p.State.Outcome is null&&packets.Count<2000)
            {
                var a=p.Think(packets.Count+1)??throw new RuleException("Missing aggro action");
                if(a.ActionType=="PC")
                {
                    var played=p.State.Cards.Single(c=>c.Id==int.Parse(a.ActionData["0"]));
                    deployed.Add(played.Definition);
                    // Orders do not enter a row; accept the native -1 sentinel.
                    if(engine.Def(played).Kind==Kind.Order)a.ActionData["1"]="-1";
                }
                string packet=FyCodec.Encode(Json.Write(a),a.SendActionId);
                var left=p.AcceptPacket(packet);var right=q.AcceptPacket(packet);
                if(left.StateHash!=right.StateHash)throw new RuleException("Aggro peer state divergence");checks++;
                if(q.AcceptPacket(packet).StateHash!=right.StateHash)throw new RuleException("Aggro duplicate mutated state");checks++;
                packets.Add(packet);
            }
            if(p.State.Outcome is null)throw new RuleException("Aggro match did not terminate");
            var replay=new WireReplay(bootstrap,packets,p.Checkpoint().StateHash,ProtocolTest.RandomProfile(p.State));
            _=ProtocolTest.Replay(engine,replay);checks++;actions+=packets.Count;example??=replay;
        }
        string artifacts=Path.Combine(AppContext.BaseDirectory,"..","artifacts");Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts,"aggro-wire-replay.json"),Json.Write(example));
        var report=new {seeds,actions,checks,deck_cards=39,distinct_cards=21,catalog_cards=cards.Length,
            deployed=deployed.Order().ToArray(),random_profile=ClientRandom.Profile,wire_replay_verified=true,native_client_tested=false};
        File.WriteAllText(Path.Combine(artifacts,"aggro-test-report.json"),Json.Write(report));
        Console.WriteLine(Json.Write(report));
    }
}
