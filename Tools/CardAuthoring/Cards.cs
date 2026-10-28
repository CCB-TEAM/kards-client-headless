using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kards.Cards;

public enum Side {Friendly,Enemy,Any}
public enum Area {Board,Hand,Deck,Support,Front,Discard}
public enum Kind {Hq,Infantry,Tank,Order,Fighter,Bomber}
public enum Zone {Hand,Support,Front}
public enum Trigger {Deploy,EnterPlay,StartTurn,EndTurn,AfterAttack,Damage,Death,OtherDeath,FrontlineChanged}
public sealed record Query(Side Side,Area Area,bool UnitsOnly=false,bool ExcludeSource=false,Kind? Kind=null,int? MaxAttack=null,int? Cost=null,
    string? Faction=null,string? Card=null,Target? AdjacentTo=null,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] bool AirOnly=false);
public sealed record Target(string Kind,Query? Query=null)
{
    public static Target Source=>new("source");
    public static Target Subject=>new("subject");
    public static Target Chosen=>new("chosen");
    public static Target Bound=>new("bound");
    public static Target Created=>new("created");
    public static Target Saved=>new("saved");
    public static Target All(Query query)=>new("all",query);
    public static Target Random(Query query)=>new("random",query);
}
public sealed record Binding(Trigger On,IReadOnlyList<JsonObject> Effects,Area? ActiveIn=null);
public sealed record Combat(bool Fury=false,bool Smokescreen=false);
public sealed record Aura(Target Target,object Attack);
public sealed record Card(string Id,string Title,Kind Kind,int Cost,int OperationCost,int Attack,int Defense,bool Blitz=false,IReadOnlyList<Binding>? Effects=null,Query? Targeting=null,
    string? Faction=null,Combat? Combat=null,IReadOnlyList<Aura>? Auras=null,string? NativeCode=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] bool TargetingOptional=false);
public sealed record Pack(int Version,string Id,IReadOnlyList<Card> Cards)
{
    public void Export(string path)=>File.WriteAllText(path,JsonSerializer.Serialize(this,Format.Options)+"\n");
}
public static class Format
{
    public static readonly JsonSerializerOptions Options=new() {
        PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull,WriteIndented=true,
        Converters={new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)}
    };
    internal static JsonNode? Node(object? value)=>JsonSerializer.SerializeToNode(value,Options);
    internal static JsonObject Object(string name,string value,params (string Key,object? Value)[] fields)
    {
        var node=new JsonObject {[name]=value};
        foreach(var (key,item) in fields)node[key]=Node(item);
        return node;
    }
}
public static class N
{
    public static JsonObject Count(Query query)=>Format.Object("expr","count",("query",query));
    public static JsonObject Attack(Target target)=>Format.Object("expr","attack",("target",target));
    public static JsonObject EventAmount=>Format.Object("expr","event_amount");
    public static JsonObject Add(object left,object right)=>Format.Object("expr","add",("left",left),("right",right));
    public static JsonObject Multiply(object left,object right)=>Format.Object("expr","multiply",("left",left),("right",right));
}
public static class When
{
    public static JsonObject Any(Query query)=>Format.Object("test","any",("query",query));
    public static JsonObject Exists(Target target)=>Format.Object("test","exists",("target",target));
    public static JsonObject SubjectMatches(Query query)=>Format.Object("test","subject_matches",("query",query));
    public static JsonObject FrontlineOwned=>Format.Object("test","frontline_owned");
    public static JsonObject Pinned(Target target)=>Format.Object("test","pinned",("target",target));
    public static JsonObject LessOrEqual(object left,object right)=>Format.Object("test","less_or_equal",("left",left),("right",right));
    public static JsonObject Not(JsonObject condition)=>Format.Object("test","not",("condition",condition));
    public static JsonObject And(params JsonObject[] conditions)=>Format.Object("test","and",("conditions",conditions));
}
public static class Fx
{
    public static JsonObject Damage(Target target,object amount)=>Format.Object("op","damage",("target",target),("amount",amount));
    public static JsonObject Destroy(Target target)=>Format.Object("op","destroy",("target",target));
    public static JsonObject Modify(Target target,object attack,object defense)=>Format.Object("op","modify",("target",target),("attack",attack),("defense",defense));
    public static JsonObject Draw(Side side,object count)=>Format.Object("op","draw",("side",side),("count",count));
    public static JsonObject DiscardRandom(Side side,object count)=>Format.Object("op","discard_random",("side",side),("count",count));
    public static JsonObject Create(string card,Side side,Zone zone)=>Format.Object("op","create",("card",card),("side",side),("zone",zone));
    public static JsonObject Suppress(Target target)=>Format.Object("op","suppress",("target",target));
    public static JsonObject Kredits(Side side,object amount)=>Format.Object("op","kredits",("side",side),("amount",amount));
    public static JsonObject Slots(Side side,object amount)=>Format.Object("op","slots",("side",side),("amount",amount));
    public static JsonObject If(JsonObject condition,JsonObject[] then,JsonObject[]? otherwise=null)=>Format.Object("op","if",("condition",condition),("then",then),("otherwise",otherwise??[]));
    public static JsonObject ForEach(Query query,params JsonObject[] effects)=>Format.Object("op","for_each",("query",query),("effects",effects));
    public static JsonObject Repeat(object count,params JsonObject[] effects)=>Format.Object("op","repeat",("count",count),("effects",effects));
    public static JsonObject Schedule(int turns,params JsonObject[] effects)=>Format.Object("op","schedule",("turns",turns),("effects",effects));
    public static JsonObject OperationCost(Target target,int amount)=>Format.Object("op","operation_cost",("target",target),("amount",amount));
    public static JsonObject DrawSelected(Query query,int count)=>Format.Object("op","draw_selected",("query",query),("count",count));
    public static JsonObject PlayFree(Target target,Zone zone)=>Format.Object("op","play_free",("target",target),("zone",zone));
    public static JsonObject Pin(Target target)=>Format.Object("op","pin",("target",target));
    public static JsonObject Bind(Target target,params JsonObject[] effects)=>Format.Object("op","bind",("target",target),("effects",effects));
    public static JsonObject Capture(Target target,params JsonObject[] effects)=>Format.Object("op","capture",("target",target),("effects",effects));
}
