using System.Text.Json;

namespace HeadlessKards;

public static class ClientRandomTest
{
    public static void Run(string oraclePath)
    {
        int checkedSteps=0;
        ClientRandom? managed=null,rust=null;
        int? previous=null;
        foreach(var line in File.ReadLines(oraclePath))
        {
            using var doc=JsonDocument.Parse(line);var r=doc.RootElement;
            int seed=r.GetProperty("initial").GetInt32();
            if(previous!=seed) {managed=new(seed);rust=new(seed,RandomBackend.Rust);previous=seed;}
            int min=r.GetProperty("min").GetInt32(),max=r.GetProperty("max").GetInt32();
            bool wrapper=r.GetProperty("wrapper").GetBoolean();
            int a=wrapper?managed!.CardRange(min,max):managed!.Range(min,max);
            int b=wrapper?rust!.CardRange(min,max):rust!.Range(min,max);
            if(a!=r.GetProperty("value").GetInt32() || a!=b || managed.Capture()!=rust.Capture()
                || managed.Capture().Seed!=r.GetProperty("seed").GetUInt32()
                || managed.Capture().Draws!=r.GetProperty("draws").GetUInt64()) throw new RuleException("Client RNG oracle mismatch at step "+checkedSteps);
            if(checkedSteps%41==0) { managed=ClientRandom.Restore(managed.Capture());rust=ClientRandom.Restore(rust.Capture(),RandomBackend.Rust); }
            checkedSteps++;
        }
        if(checkedSteps<35000) throw new RuleException("Client oracle trace is incomplete");
        Console.WriteLine(Json.Write(new {passed=checkedSteps,oracle="compiled methods extracted verbatim from local UE5.6 RandomStream.h",blueprint_wrapper="Flora 2026-10-28: draw burn count 1..10, burn that many draws, then requested range",native_game_client_tested=false}));
    }
}
