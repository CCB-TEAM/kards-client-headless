using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeadlessKards;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options) ?? throw new RuleException("Empty JSON");
}

public sealed class RuleException(string message) : Exception(message);
public enum Side { Left, Right }
public enum Kind { Hq, Infantry, Tank, Order, Fighter, Bomber }
public enum Zone { Deck, Hand, Support, Front, Discard }
public enum ActionKind { Deploy, Move, Attack, EndTurn }
public sealed record Definition(string Id, string Title, Kind Kind, int Cost, int OperationCost, int Attack, int Defense, bool Blitz,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] JsonElement? Effects=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] JsonElement? Targeting=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? Faction=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] JsonElement? Combat=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] JsonElement? Auras=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? NativeCode=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] bool TargetingOptional=false);
public sealed record Command(Side Side, ActionKind Kind, int CardId = 0, int TargetId = 0);
public sealed record CreateRequest(int Seed = 1, string[]? LeftDeck = null, string[]? RightDeck = null,
    string[]? Headquarters=null,string? RngProfile=null);

public sealed class Card
{
    public int Id { get; set; }
    public string Definition { get; set; } = "";
    public Side Side { get; set; }
    public Zone Zone { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
    public int Moves { get; set; }
    public int Attacks { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Script { get; set; }
}
public sealed class Player
{
    public int Kredits { get; set; }
    public int MaxKredits { get; set; }
    public int Fatigue { get; set; }
    public List<int> Deck { get; set; } = [];
}
public sealed class State
{
    public int SchemaVersion { get; set; } = 2;
    public string RulesVersion { get; set; } = Engine.RulesVersion;
    public string CatalogHash { get; set; } = "";
    public int Seed { get; set; }
    public LogicRandomState? LogicRandom { get; set; }
    public int Revision { get; set; }
    public int Turn { get; set; } = 1;
    public Side Active { get; set; }
    public string? Outcome { get; set; }
    public Player[] Players { get; set; } = [new(), new()];
    public List<Card> Cards { get; set; } = [];
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Battle { get; set; }
}
public sealed record Transition(int Revision, Command Command, string StateHash);
public sealed record Replay(CreateRequest Setup, string CatalogHash, List<Transition> Actions, string FinalHash, string? RandomProfile=null);

public sealed class Engine
{
    public const string RulesVersion="subset-v0.4-aggro1";
    public RulesBackend RulesBackend { get; }
    private readonly RustCore? rust;
    public RandomBackend RandomBackend { get; }
    public IReadOnlyDictionary<string, Definition> Catalog { get; }
    public string CatalogHash { get; }
    public Engine(IEnumerable<Definition> definitions,RandomBackend randomBackend=RandomBackend.Managed,RulesBackend rulesBackend=RulesBackend.Rust)
    {
        RandomBackend=randomBackend; RulesBackend=rulesBackend;
        var list = definitions.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        if (list.Length == 0 || list.Select(d => d.Id).Distinct().Count() != list.Length) throw new RuleException("Invalid catalog");
        foreach (var d in list)
            if (string.IsNullOrWhiteSpace(d.Id) || d.Cost < 0 || d.OperationCost < 0 || d.Attack < 0 || (d.Kind!=Kind.Order && d.Defense <= 0))
                throw new RuleException("Invalid card: " + d.Id);
        Catalog = list.ToDictionary(d => d.Id, StringComparer.Ordinal);
        CatalogHash = RustCore.Fingerprint(list);
        if(rulesBackend==RulesBackend.Rust) rust=new(list,CatalogHash);
        else if(list.Any(d=>d.Effects is not null || d.Targeting is not null || d.Combat is not null || d.Auras is not null || d.NativeCode is not null || d.Kind is Kind.Order or Kind.Fighter or Kind.Bomber))
            throw new RuleException("Managed reference backend only supports the original unscripted card subset");
    }
    public Definition Def(Card card) => Catalog.TryGetValue(card.Definition, out var d) ? d : throw new RuleException("Unsupported card: " + card.Definition);
    public static Side Other(Side side) => side == Side.Left ? Side.Right : Side.Left;
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string StateHash(State state) => Hash(Json.Write(state));
    public static readonly string[] DemoDeck = [
        "card_unit_12th_infantry_regiment_tut", "card_unit_121st_regiment_tut",
        "card_unit_panzer_35_t_tut", "card_unit_12th_infantry_regiment_tut",
        "card_unit_t_34", "card_unit_panzer_35_t_tut", "card_unit_121st_regiment_tut",
        "card_unit_t_34", "card_unit_12th_infantry_regiment_tut", "card_unit_panzer_35_t_tut",
        "card_unit_121st_regiment_tut", "card_unit_t_34"];

    public State New(CreateRequest setup)
    {
        if(rust is not null) return rust.Call<State>("new",setup:setup);
        // Fixed demo opening; not a reproduction of the native mulligan protocol.
        var state = new State { Seed = setup.Seed, CatalogHash = CatalogHash };
        var random = new LogicRandom(setup.Seed,RandomBackend);
        int id = 1;
        foreach (var side in Enum.GetValues<Side>())
        {
            var hq = side == Side.Left ? "card_location_london" : "card_location_berlin";
            state.Cards.Add(CreateCard(id++, hq, side, Zone.Support));
            var deck = side == Side.Left ? setup.LeftDeck ?? DemoDeck : setup.RightDeck ?? DemoDeck;
            if (deck.Length is < 1 or > 200) throw new RuleException("Test deck must contain 1..200 cards");
            foreach (var card in deck)
            {
                var item = CreateCard(id++, card, side, Zone.Deck);
                if (Def(item).Kind == Kind.Hq) throw new RuleException("HQ cannot be in the unit deck");
                state.Cards.Add(item);
                state.Players[(int)side].Deck.Add(item.Id);
            }
            var ids = state.Players[(int)side].Deck;
            random.Shuffle(ids);
            for (int i = 0; i < (side == Side.Left ? 4 : 5) && ids.Count > 0; ++i) Draw(state, side);
        }
        state.LogicRandom=random.Capture();
        StartTurn(state, draw: false);
        return state;
    }
    private Card CreateCard(int id, string key, Side side, Zone zone)
    {
        if (!Catalog.TryGetValue(key, out var d)) throw new RuleException("Unsupported card: " + key);
        return new() { Id = id, Definition = key, Side = side, Zone = zone, Attack = d.Attack, Defense = d.Defense };
    }
    private static Card Find(State state, int id) => state.Cards.SingleOrDefault(c => c.Id == id) ?? throw new RuleException("Unknown card id");
    private Card Hq(State state, Side side) => state.Cards.Single(c => c.Side == side && Def(c).Kind == Kind.Hq);
    private static bool Board(Card card) => card.Zone is Zone.Support or Zone.Front;
    private void Draw(State state, Side side)
    {
        var player = state.Players[(int)side];
        if (player.Deck.Count == 0) { Hq(state, side).Defense -= ++player.Fatigue; Finish(state); return; }
        var card = Find(state, player.Deck[0]);
        player.Deck.RemoveAt(0);
        card.Zone = state.Cards.Count(c => c.Side == side && c.Zone == Zone.Hand) >= 9 ? Zone.Discard : Zone.Hand;
    }
    private void StartTurn(State state, bool draw)
    {
        var p = state.Players[(int)state.Active];
        p.Kredits = p.MaxKredits = Math.Min(12, p.MaxKredits + 1);
        foreach (var card in state.Cards.Where(c => c.Side == state.Active && Board(c) && Def(c).Kind != Kind.Hq)) card.Moves = card.Attacks = 1;
        if (draw) Draw(state, state.Active);
    }
    private void Finish(State state)
    {
        var left = Hq(state, Side.Left).Defense <= 0;
        var right = Hq(state, Side.Right).Defense <= 0;
        state.Outcome = left && right ? "draw" : left ? "right" : right ? "left" : null;
    }
    public List<Command> Legal(State state)
    {
        if(rust is not null) return rust.Call<List<Command>>("legal",state);
        if (state.CatalogHash != CatalogHash || state.RulesVersion != RulesVersion || state.SchemaVersion != 2) throw new RuleException("Incompatible state/catalog");
        if (state.LogicRandom is not null) _=HeadlessKards.LogicRandom.Restore(state.LogicRandom);
        // Do not infer a vanilla body for any unimplemented card.
        foreach (var card in state.Cards) _ = Def(card);
        if (state.Outcome is not null) return [];
        var side = state.Active;
        int money = state.Players[(int)side].Kredits;
        var result = new List<Command>();
        foreach (var card in state.Cards.Where(c => c.Side == side).OrderBy(c => c.Id))
        {
            var d = Def(card);
            if (d.Kind == Kind.Hq) continue;
            if (card.Zone == Zone.Hand && money >= d.Cost && state.Cards.Count(c => c.Side == side && c.Zone == Zone.Support) < 5)
                result.Add(new(side, ActionKind.Deploy, card.Id));
            if (!Board(card) || card.Defense <= 0 || money < d.OperationCost) continue;
            if (card.Zone == Zone.Support && card.Moves > 0 && state.Cards.Count(c => c.Zone == Zone.Front) < 5 && !state.Cards.Any(c => c.Zone == Zone.Front && c.Side != side))
                result.Add(new(side, ActionKind.Move, card.Id));
            if (card.Attacks <= 0 || card.Attack <= 0) continue;
            foreach (var target in state.Cards.Where(c => c.Side != side && Board(c) && c.Defense > 0).OrderBy(c => c.Id))
                if (card.Zone == Zone.Front || target.Zone == Zone.Front) result.Add(new(side, ActionKind.Attack, card.Id, target.Id));
        }
        result.Add(new(side, ActionKind.EndTurn));
        return result;
    }
    public State Apply(State before, Command action, int expectedRevision, bool deferNextStart = false,int? insertion=null)
    {
        if(rust is not null) return rust.Call<State>("apply",before,command:action,revision:expectedRevision,defer:deferNextStart,insertion:insertion);
        if (before.Revision != expectedRevision) throw new RuleException("Stale revision");
        if (!Legal(before).Contains(action)) throw new RuleException("Illegal action");
        // Transactional: invalid commands or failed transitions never mutate the caller's state.
        var state = Json.Read<State>(Json.Write(before));
        var p = state.Players[(int)state.Active];
        if (action.Kind == ActionKind.EndTurn)
        {
            state.Active = Other(state.Active);
            state.Turn++;
            if (!deferNextStart) StartTurn(state, draw: true);
        }
        else
        {
            var card = Find(state, action.CardId);
            var d = Def(card);
            p.Kredits -= action.Kind == ActionKind.Deploy ? d.Cost : d.OperationCost;
            switch (action.Kind)
            {
                case ActionKind.Deploy:
                    card.Zone = Zone.Support;
                    card.Moves = card.Attacks = d.Blitz ? 1 : 0;
                    break;
                case ActionKind.Move:
                    card.Zone = Zone.Front;
                    card.Moves--;
                    if (d.Kind != Kind.Tank) card.Attacks = 0;
                    break;
                case ActionKind.Attack:
                    var target = Find(state, action.TargetId);
                    var retaliation = target.Attack;
                    target.Defense -= card.Attack;
                    card.Defense -= retaliation;
                    card.Attacks--;
                    if (d.Kind != Kind.Tank) card.Moves = 0;
                    foreach (var dead in state.Cards.Where(c => Board(c) && c.Defense <= 0 && Def(c).Kind != Kind.Hq)) dead.Zone = Zone.Discard;
                    break;
            }
            Finish(state);
        }
        state.Revision++;
        return state;
    }
    // Native wire actions split end/start. The protocol session enforces that
    // this can only execute once after a pending start (or an explicit bootstrap).
    public State BeginProtocolTurn(State before, int expectedRevision, bool draw)
    {
        if(rust is not null) return rust.Call<State>("begin",before,revision:expectedRevision,draw:draw);
        if (before.Revision != expectedRevision || before.Outcome is not null) throw new RuleException("Invalid pending turn");
        _ = Legal(before); // Validate catalog and rules before modifying state.
        var state = Json.Read<State>(Json.Write(before));
        StartTurn(state, draw);
        state.Revision++;
        return state;
    }
    public Command? Think(State state)
    {
        if(rust is not null) return rust.Think(state);
        var legal = Legal(state);
        if (legal.Count == 0) return null;
        double Score(Command action)
        {
            if (action.Kind == ActionKind.EndTurn) return -1000;
            var c = Find(state, action.CardId);
            if (action.Kind == ActionKind.Deploy) return 10 + c.Attack + c.Defense * .5;
            if (action.Kind == ActionKind.Move) return 20 + c.Attack;
            var t = Find(state, action.TargetId);
            if (Def(t).Kind == Kind.Hq) return t.Defense <= c.Attack ? 100000 : 50 + c.Attack;
            var score = Math.Min(c.Attack, t.Defense) - Math.Min(t.Attack, c.Defense);
            if (c.Attack >= t.Defense) score += 25 + Catalog[t.Definition].Cost;
            if (t.Attack >= c.Defense) score -= 20 + Catalog[c.Definition].Cost;
            return score;
        }
        return legal.OrderByDescending(Score).First();
    }
    public Replay SelfPlay(CreateRequest setup, int maxActions)
    {
        var state = New(setup);
        List<Transition> actions = [];
        for (int i = 0; i < maxActions && state.Outcome is null; ++i)
        {
            var action = Think(state) ?? throw new RuleException("No legal action in running game");
            state = Apply(state, action, state.Revision);
            actions.Add(new(state.Revision, action, StateHash(state)));
        }
        return new(setup, CatalogHash, actions, StateHash(state),LogicRandom.Profile);
    }
    public State Verify(Replay replay)
    {
        if(replay.RandomProfile!=LogicRandom.Profile) throw new RuleException("Replay random profile differs or predates RNG migration; use the matching old build");
        if (replay.CatalogHash != CatalogHash) throw new RuleException("Replay catalog differs");
        var state = New(replay.Setup);
        foreach (var entry in replay.Actions)
        {
            state = Apply(state, entry.Command, state.Revision);
            if (entry.Revision != state.Revision || entry.StateHash != StateHash(state)) throw new RuleException("Replay diverged at " + entry.Revision);
        }
        if (replay.FinalHash != StateHash(state)) throw new RuleException("Final state mismatch");
        return state;
    }
}
