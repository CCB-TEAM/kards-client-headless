using System.Text.Json;

namespace HeadlessKards;

public sealed record SnapshotDiff(string Field, string Expected, string Actual);
public sealed record SnapshotReport(bool Equal, int CardsCompared, List<SnapshotDiff> Differences, List<string> Unsupported, string[] Limitations);
public static class NativeSnapshot
{
    // Input wrapper is explicit: cards (native GetMatchCardsAsJsonString array or
    // its JSON string), and four resource fields from GetMatchState.
    public static SnapshotReport Compare(Engine engine, FyCheckpoint checkpoint, JsonElement snapshot)
    {
        List<SnapshotDiff> differences=[];
        List<string> unsupported=[];
        var state=checkpoint.State;
        void Diff(string field,object expected,object actual)
        { if (!Equals(expected,actual)) differences.Add(new(field,expected.ToString()??"",actual.ToString()??"")); }
        foreach (var side in Enum.GetValues<Side>())
        {
            var suffix=side==Side.Left?"left":"right";
            Diff("kredits_"+suffix,state.Players[(int)side].Kredits,snapshot.GetProperty("kredits_"+suffix).GetInt32());
            Diff("max_kredits_"+suffix,state.Players[(int)side].MaxKredits,snapshot.GetProperty("max_kredits_"+suffix).GetInt32());
        }
        var cards=snapshot.GetProperty("cards");
        if (cards.ValueKind==JsonValueKind.String)
        { using var document=JsonDocument.Parse(cards.GetString()!); cards=document.RootElement.Clone(); }
        HashSet<int> seen=[];
        int count=0;
        foreach (var card in cards.EnumerateArray())
        {
            int id=card.GetProperty("card_id").GetInt32();
            if (!seen.Add(id)) { unsupported.Add("Duplicate native card_id "+id); continue; }
            var expected=state.Cards.SingleOrDefault(c=>c.Id==id);
            if (expected is null) { unsupported.Add("Unknown native card_id "+id); continue; }
            count++;
            string prefix="cards["+id+"]";
            Diff(prefix+".name",expected.Definition,card.GetProperty("name").GetString()??"");
            Diff(prefix+".attack",expected.Attack,card.GetProperty("attack").GetInt32());
            Diff(prefix+".defense",expected.Defense,card.GetProperty("defense").GetInt32());
            Diff(prefix+".side",expected.Side==Side.Left?"left":"right",card.GetProperty("side").GetString()??"");
            string location=expected.Zone switch
            {
                Zone.Support=>expected.Side==Side.Left?"board_hqleft":"board_hqright",
                Zone.Front=>"board_frontline",Zone.Hand=>expected.Side==Side.Left?"hand_left":"hand_right",
                Zone.Deck=>expected.Side==Side.Left?"deck_left":"deck_right",_=>"discard"
            };
            Diff(prefix+".location",location,card.GetProperty("location").GetString()??"");
            if (card.TryGetProperty("location_number",out var position)) Diff(prefix+".location_number",checkpoint.Positions[id],position.GetInt32());
            var def=engine.Def(expected);
            bool Suppressed()=>expected.Script is JsonElement script&&script.TryGetProperty("suppressed",out var v)&&v.GetBoolean();
            int Modifier(string name)=>expected.Script is JsonElement script&&script.TryGetProperty(name,out var v)?v.GetInt32():0;
            bool Combat(string name)=>!Suppressed()&&def.Combat is JsonElement combat&&combat.TryGetProperty(name,out var v)&&v.GetBoolean();
            foreach(var (flag,wanted) in new[]{("hasFury",Combat("fury")),("isSuppressed",Suppressed()),
                ("hasSmokescreen",Combat("smokescreen")&&!(expected.Script is JsonElement sc&&sc.TryGetProperty("revealed",out var r)&&r.GetBoolean()))})
                if(card.TryGetProperty(flag,out var value))Diff(prefix+"."+flag,wanted,value.GetBoolean());
            foreach(var (field,wanted) in new[]{("operationCostBuff",Modifier("operation_buff")),("pinnedTurns",Modifier("pinned_turns")),
                ("attackBuff",Modifier("attack_buff")+Modifier("aura_attack"))})
                if(card.TryGetProperty(field,out var value))Diff(prefix+"."+field,wanted,value.GetInt32());
            foreach (var flag in new[]{"hasAmbush","hasGuard","isImmune","hasMobilize","hasAlpine","hasSalvage","hasCovert","hasShock","hasBond","hasDestruction"})
                if (card.TryGetProperty(flag,out var value) && value.ValueKind==JsonValueKind.True) unsupported.Add(prefix+" unsupported "+flag);
            foreach (var field in new[]{"kreditsBuff","heavyArmor","heavyArmorBuff","kreditsTaxAsEnemyTarget","cipher"})
                if (card.TryGetProperty(field,out var value) && value.GetInt32()!=0) unsupported.Add(prefix+" unsupported "+field);
            if (card.TryGetProperty("hasBlitz",out var blitz))Diff(prefix+".hasBlitz",def.Blitz&&!Suppressed(),blitz.GetBoolean());
            if (card.TryGetProperty("activeUpgrades",out var upgrades) && upgrades.GetArrayLength()!=0) unsupported.Add(prefix+" campaign upgrades");
            if (card.TryGetProperty("customJson",out var custom) && custom.ValueKind==JsonValueKind.Object && custom.EnumerateObject().Any()) unsupported.Add(prefix+" customJson");
        }
        foreach (var card in state.Cards.Where(c=>!seen.Contains(c.Id))) differences.Add(new("cards["+card.Id+"]","present","missing"));
        return new(differences.Count==0 && unsupported.Count==0,count,differences,unsupported,
            ["Native card JSON does not expose all movement/attack counters; those are not verified.","Compare at the same completed-action boundary; no live client capture has been verified yet."]);
    }
}
