using System.Text.Json;

namespace HeadlessKards;

public static class ScriptTest
{
    public static void Run(string csharpPath,string luaPath)
    {
        int checks=0;
        void Check(bool condition,string label){if(!condition)throw new RuleException("SCRIPT TEST FAILED: "+label);checks++;}
        JsonElement Load(string path){using var d=JsonDocument.Parse(File.ReadAllText(path));return RustCore.ValidatePack(d.RootElement);}
        var a=Load(csharpPath);var b=Load(luaPath);
        Check(a.GetProperty("hash").GetString()==b.GetProperty("hash").GetString(),"C#/Lua canonical pack hash");
        var left=new Engine(Json.Read<Definition[]>(a.GetProperty("pack").GetProperty("cards").GetRawText()));
        var right=new Engine(Json.Read<Definition[]>(b.GetProperty("pack").GetProperty("cards").GetRawText()));
        Check(left.CatalogHash==right.CatalogHash,"Runtime catalog hash");
        string[] deck=["mod_arc_infantry","mod_last_supply","mod_reinforcements","mod_arc_infantry","mod_last_supply","mod_reinforcements","card_unit_12th_infantry_regiment_tut","card_unit_panzer_35_t_tut","card_unit_t_34","mod_arc_infantry","mod_last_supply","card_unit_t_34"];
        for(int seed=0;seed<25;seed++)
        {
            var setup=new CreateRequest(seed,deck,deck);
            var s=left.New(setup);var t=right.New(setup);
            Check(Engine.StateHash(s)==Engine.StateHash(t),"Opening "+seed);
            int count=0;
            while(s.Outcome is null && count++<2000)
            {
                var command=left.Think(s)??throw new RuleException("No script action");
                Check(right.Legal(t).Contains(command),"Same legal command");
                s=left.Apply(s,command,s.Revision);t=right.Apply(t,command,t.Revision);
                Check(Engine.StateHash(s)==Engine.StateHash(t),"C#/Lua same step result");
            }
            Check(s.Outcome is not null,"Script selfplay terminates");
            var replay=left.SelfPlay(setup,2000);Check(Engine.StateHash(right.Verify(replay))==replay.FinalHash,"Cross-language replay");
        }
        // A concrete random deployment receipt, not just comparisons between
        // two empty event paths. Force exactly two available enemy units.
        var state=left.New(new(42,deck,deck));state.Players[0].Kredits=12;
        var arc=state.Cards.First(c=>c.Side==Side.Left&&c.Definition=="mod_arc_infantry");state.Players[0].Deck.Remove(arc.Id);arc.Zone=Zone.Hand;
        var enemies=state.Cards.Where(c=>c.Side==Side.Right&&left.Def(c).Kind is Kind.Infantry or Kind.Tank).Take(2).ToArray();
        foreach(var c in enemies){state.Players[1].Deck.Remove(c.Id);c.Zone=Zone.Support;}
        var before=Engine.StateHash(state);var after=left.Apply(state,new(Side.Left,ActionKind.Deploy,arc.Id),state.Revision);
        Check(Engine.StateHash(state)==before,"Caller state is immutable");
        Check(after.LogicRandom!.RawDraws==state.LogicRandom!.RawDraws+1,"Rust consumes reference random draw");
        Check(enemies.Sum(c=>c.Defense)-after.Cards.Where(c=>enemies.Any(e=>e.Id==c.Id)).Sum(c=>c.Defense)==2,"Deployment damage was executed");
        string artifacts=Path.Combine(AppContext.BaseDirectory,"..","artifacts");Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts,"script-example-before.json"),Json.Write(state));
        File.WriteAllText(Path.Combine(artifacts,"script-example-after.json"),Json.Write(after));
        Console.WriteLine(Json.Write(new {passed=checks,selfplay_seeds=25,canonical_pack_hash=a.GetProperty("hash").GetString(),rules_backend="rust",authoring_languages=new[]{"csharp","lua"},native_client_tested=false}));
    }
}
