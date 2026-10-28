using System.Globalization;
using System.Text.Json;

namespace HeadlessKards;

public sealed record FyAction(int ActionId, string ActionType, int PlayerId, Dictionary<string, string> ActionData,
    JsonElement[] SubActions, int TurnNumber, int SendActionId = 0, string Action = "", Dictionary<string,string>? Value = null);
public sealed record FyBootstrap(JsonElement StartingInfo, bool MulliganComplete, bool? FirstTurnDraws, int LastSeenActionId = 0, LogicRandomState? LogicRandom = null);
public sealed record FyCheckpoint(State State, bool AwaitingStart, int LastActionId, Dictionary<int,int> Positions, string StateHash);

public sealed class FyProtocol
{
    private readonly Engine engine;
    private readonly int matchId;
    private readonly int[] players;
    private readonly bool firstTurnDraws;
    private bool firstStart = true;
    private readonly Dictionary<int,string> accepted = [];
    private Dictionary<int,int> positions;
    public State State { get; private set; }
    public bool AwaitingStart { get; private set; } = true;
    public int LastActionId { get; private set; }
    public int MatchId => matchId;
    private static readonly Dictionary<string,string> Codes = new(StringComparer.Ordinal)
    {
        ["card_location_london"]="0N", ["card_location_berlin"]="3v",
        ["card_unit_t_34"]="9p", ["card_unit_121st_regiment_tut"]="bc",
        ["card_unit_12th_infantry_regiment_tut"]="bd", ["card_unit_panzer_35_t_tut"]="bh"
    };
    public static bool HasNativeCode(string definition)=>Codes.ContainsKey(definition);
    public static bool HasNativeCode(Definition definition)=>definition.NativeCode is not null||Codes.ContainsKey(definition.Id);
    private string Code(string definition)=>engine.Catalog[definition].NativeCode??(Codes.TryGetValue(definition,out var code)?code:throw new RuleException("Missing native code: "+definition));
    private string ChecksumKey => matchId.ToString(CultureInfo.InvariantCulture)[^2..];
    private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string SideText(Side side) => side == Side.Left ? "left" : "right";
    private static string Location(Card c) => c.Zone switch
    {
        Zone.Front => "board_frontline", Zone.Discard => "discard",
        Zone.Support => c.Side == Side.Left ? "board_hqleft" : "board_hqright",
        Zone.Hand => c.Side == Side.Left ? "hand_left" : "hand_right",
        _ => c.Side == Side.Left ? "deck_left" : "deck_right"
    };
    public FyProtocol(Engine engine, FyBootstrap bootstrap)
    {
        this.engine = engine;
        if (!bootstrap.MulliganComplete || bootstrap.FirstTurnDraws is null) throw new RuleException("Require post-mulligan starting data and explicit first_turn_draws");
        firstTurnDraws = bootstrap.FirstTurnDraws.Value;
        var root = bootstrap.StartingInfo;
        if (root.TryGetProperty("local_subactions",out var local) && !local.GetBoolean()) throw new RuleException("Only local_subactions=true is supported");
        if (root.TryGetProperty("match_and_starting_data", out var wrapped)) root = wrapped;
        var match = root.GetProperty("match");
        var start = root.GetProperty("starting_data");
        if (match.TryGetProperty("current_turn",out var turn) && turn.GetInt32()!=1) throw new RuleException("Mid-match snapshot bootstrap is unsupported; replay from the opening");
        if (match.TryGetProperty("actions",out var priorActions) && priorActions.GetArrayLength()!=0) throw new RuleException("Replay prior actions explicitly");
        matchId = match.GetProperty("match_id").GetInt32();
        if (matchId < 100) throw new RuleException("Require native match id with at least three digits");
        players = [start.GetProperty("player_id_left").GetInt32(), start.GetProperty("player_id_right").GetInt32()];
        if (players[0] == 0 || players[1] == 0 || players[0] == players[1]) throw new RuleException("Invalid player mapping");
        var active = match.GetProperty("start_side").GetString() switch { "left" => Side.Left, "right" => Side.Right, _ => throw new RuleException("Invalid start_side") };
        if (bootstrap.LastSeenActionId < 0) throw new RuleException("Invalid explicit action cursor");
        if (match.TryGetProperty("current_action_id", out var cursor) && cursor.GetInt32() != 0 && bootstrap.LastSeenActionId == 0)
            throw new RuleException("Nonzero server cursor requires explicit last_seen_action_id");
        LastActionId = bootstrap.LastSeenActionId;
        // A shuffled native deck does not reveal RNG state. Never guess it from
        // match id; null explicitly means random card effects cannot execute.
        if(bootstrap.LogicRandom is not null) _=HeadlessKards.LogicRandom.Restore(bootstrap.LogicRandom);
        State = new() { CatalogHash = engine.CatalogHash, Active = active, Seed=bootstrap.LogicRandom?.Seed??0, LogicRandom=bootstrap.LogicRandom };
        positions = [];
        foreach (var side in Enum.GetValues<Side>())
        {
            string suffix = SideText(side);
            Add(start.GetProperty("location_card_"+suffix), side, Zone.Support);
            foreach (var card in start.GetProperty("starting_hand_"+suffix).EnumerateArray()) Add(card, side, Zone.Hand);
            foreach (var card in start.GetProperty("deck_"+suffix).EnumerateArray()) Add(card, side, Zone.Deck);
            State.Players[(int)side].Deck = State.Cards.Where(c => c.Side == side && c.Zone == Zone.Deck).OrderBy(c => positions[c.Id]).Select(c => c.Id).ToList();
        }
        foreach (var group in State.Cards.GroupBy(Location))
            if (group.Select(c => positions[c.Id]).Distinct().Count() != group.Count()) throw new RuleException("Duplicate location_number in " + group.Key);
        foreach (var side in Enum.GetValues<Side>())
            if (State.Cards.Count(c => c.Side == side && c.Zone == Zone.Hand) > 9) throw new RuleException("Starting hand overflow");
        if(engine.Catalog.Values.Any(d=>d.NativeCode is not null||d.Combat is not null||d.Auras is not null))
        {
            // Flora SetRandomStreamByMatchID initializes a separate UE stream.
            // This does not infer the kards-sim reference RNG from match_id.
            State.Battle=JsonSerializer.SerializeToElement(new {positions,spawn_turn=1,spawn_count=0,delayed=Array.Empty<object>(),
                client_random=new {initial_seed=matchId,seed=unchecked((uint)matchId),draws=0UL}},Json.Options);
            State=Json.Read<State>(RustCore.Send(new {op="bootstrap",cards=engine.Catalog.Values,state=State}).GetRawText());
        }
        _ = engine.Legal(State);
    }
    private void Add(JsonElement value, Side side, Zone zone)
    {
        var id = value.GetProperty("card_id").GetInt32();
        var key = value.GetProperty("name").GetString() ?? "";
        if (id <= 0 || State.Cards.Any(c => c.Id == id)) throw new RuleException("Duplicate/invalid native card id");
        if (!engine.Catalog.TryGetValue(key, out var def)) throw new RuleException("Unsupported native card: " + key);
        if ((zone == Zone.Support) != (def.Kind == Kind.Hq)) throw new RuleException("Unexpected HQ/unit in starting data");
        var card = new Card { Id = id, Definition = key, Side = side, Zone = zone, Attack = def.Attack, Defense = def.Defense };
        if (value.GetProperty("location").GetString() != Location(card)) throw new RuleException("Starting card side/location mismatch");
        var number = value.GetProperty("location_number").GetInt32();
        if (number < 0) throw new RuleException("Negative starting location_number");
        State.Cards.Add(card); positions.Add(id, number);
    }
    public FyCheckpoint Checkpoint() => new(Json.Read<State>(Json.Write(State)), AwaitingStart, LastActionId,
        new(positions), Engine.Hash(Json.Write(new { state = State, awaiting_start = AwaitingStart, last_action_id = LastActionId, positions })));
    private Card Card(int id) => State.Cards.SingleOrDefault(c => c.Id == id) ?? throw new RuleException("Unknown native card id");
    private int EnemyDefense(Side side) => State.Cards.Single(c => c.Side != side && engine.Def(c).Kind == Kind.Hq).Defense;
    public FyAction? Think(int sendActionId)
    {
        if (State.Outcome is not null) return null;
        if (AwaitingStart) return Build("XActionStartOfTurn", State.Active, new() { ["side"] = SideText(State.Active), [ChecksumKey] = Number(EnemyDefense(State.Active)) }, sendActionId);
        var command = engine.Think(State);
        return command is null ? null : EncodeCommand(command, sendActionId);
    }
    public FyAction EncodeCommand(Command command, int sendActionId)
    {
        if (AwaitingStart || !engine.Legal(State).Contains(command)) throw new RuleException("Cannot encode illegal/pending command");
        var data = new Dictionary<string,string> { [ChecksumKey] = Number(EnemyDefense(command.Side)) };
        if (command.Kind == ActionKind.EndTurn)
        {
            data["side"] = SideText(command.Side); data["reason"] = "endTurnButton";
            return Build("XActionEndOfTurn", command.Side, data, sendActionId);
        }
        var card = Card(command.CardId);
        data["0"] = Number(card.Id);
        switch (command.Kind)
        {
            case ActionKind.Deploy:
                data["1"] = Number(State.Cards.Count(c => c.Side == card.Side && c.Zone == Zone.Support));
                data["2"] = command.TargetId==0?"0":Number(command.TargetId); data["3"] = "0"; data["4"] = Code(card.Definition);
                return Build("PC", command.Side, data, sendActionId);
            case ActionKind.Move:
                data["1"] = Number(State.Cards.Count(c => c.Zone == Zone.Front)); data["2"] = Code(card.Definition);
                return Build("ML", command.Side, data, sendActionId);
            case ActionKind.Attack:
                data["1"] = Number(command.TargetId); data["2"] = Code(card.Definition); data["3"] = Code(Card(command.TargetId).Definition);
                return Build("AC", command.Side, data, sendActionId);
            default: throw new RuleException("Unsupported command");
        }
    }
    private FyAction Build(string kind, Side side, Dictionary<string,string> data, int sendActionId) => new(LastActionId + 1, kind, players[(int)side], data, [], State.Turn, sendActionId);
    public FyCheckpoint Accept(FyAction action)
    {
        if (action.ActionData is null || action.SubActions is null || action.ActionType is null || action.Action is null
            || action.ActionData.Any(k => k.Value is null)) throw new RuleException("Malformed action envelope");
        // Canonical JSON makes field-order changes benign for idempotency.
        var digest = Engine.Hash(Json.Write(action with { ActionData = action.ActionData.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, v => v.Value) }));
        if (accepted.TryGetValue(action.ActionId, out var prior))
        {
            if (digest != prior) throw new RuleException("Conflicting duplicate action");
            return Checkpoint();
        }
        if (action.ActionId != LastActionId + 1) throw new RuleException("Action stream gap/out-of-order");
        if (action.SubActions.Length != 0 || action.Action.Length != 0 || action.Value is { Count: > 0 }) throw new RuleException("Unsupported subactions/legacy action payload");
        var sideIndex = Array.IndexOf(players, action.PlayerId);
        if (sideIndex < 0 || (Side)sideIndex != State.Active) throw new RuleException("Action actor is not the active player");
        var side = (Side)sideIndex;
        if (action.TurnNumber != State.Turn) throw new RuleException("Turn number mismatch");
        string Get(string key) => action.ActionData.TryGetValue(key, out var text) ? text : throw new RuleException("Missing action field " + key);
        int Int(string key) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : throw new RuleException("Invalid integer " + key);
        if (Int(ChecksumKey) != EnemyDefense(side)) throw new RuleException("HQ checksum diverged; stop AI");
        var type = action.ActionType switch { "XActionPlayCardFromHand" => "PC", "XActionMoveCardToLine" => "ML", "XActionAttackCard" => "AC", var t => t };
        State after;
        int? insert = null;
        int moving = 0;
        bool pending;
        string[] allowed;
        if (type == "XActionStartOfTurn")
        {
            allowed = [ChecksumKey,"side"];
            if (!AwaitingStart || Get("side") != SideText(side)) throw new RuleException("Unexpected/duplicate start turn");
            after = engine.BeginProtocolTurn(State, State.Revision, firstStart ? firstTurnDraws : true);
            pending = false;
        }
        else
        {
            if (AwaitingStart) throw new RuleException("Start turn required before actions");
            Command command;
            if (type == "XActionEndOfTurn")
            {
                allowed = [ChecksumKey,"side","reason"];
                if (Get("side") != SideText(side)) throw new RuleException("Action side mismatch");
                command = new(side, ActionKind.EndTurn);
            }
            else if (type is "PC" or "ML" or "AC")
            {
                moving = Int("0"); var card = Card(moving);
                var codeKey = type == "PC" ? "4" : "2";
                if (Get(codeKey) != card.Definition && Get(codeKey) != Code(card.Definition)) throw new RuleException("Card code/id mismatch");
                if (type == "AC")
                {
                    allowed = [ChecksumKey,"0","1","2","3"];
                    int target = Int("1");
                    if (Get("3") != Card(target).Definition && Get("3") != Code(Card(target).Definition)) throw new RuleException("Target code/id mismatch");
                    command = new(side, ActionKind.Attack, moving, target);
                }
                else
                {
                    allowed = type == "PC" ? [ChecksumKey,"0","1","2","3","4"] : [ChecksumKey,"0","1","2"];
                    insert = Int("1");
                    int count = State.Cards.Count(c => type == "PC" ? c.Side == side && c.Zone == Zone.Support : c.Zone == Zone.Front);
                    bool order=type=="PC"&&engine.Def(card).Kind==Kind.Order;
                    if (!order&&(insert < 0 || insert > count)) throw new RuleException("Invalid row insertion index");
                    if (type == "PC" && Int("3") is not (-1 or 0)) throw new RuleException("Choose-one deployment not supported");
                    if (type == "PC" && Int("2") < -1) throw new RuleException("Invalid deployment target");
                    if (order) insert=null;
                    command = new(side, type == "PC" ? ActionKind.Deploy : ActionKind.Move, moving,type=="PC"?Math.Max(0,Int("2")):0);
                }
            }
            else throw new RuleException("Unsupported XAction " + type);
            after = engine.Apply(State, command, State.Revision, deferNextStart: true,insertion:insert);
            pending = type == "XActionEndOfTurn";
        }
        if (action.ActionData.Keys.Any(k => !allowed.Contains(k))) throw new RuleException("Unsupported extra action fields");
        // Build position changes separately; commit only after all checks pass.
        var nextPositions = new Dictionary<int,int>();
        if(after.Battle is JsonElement battle)
        {
            nextPositions=Json.Read<Dictionary<int,int>>(battle.GetProperty("positions").GetRawText());
        }
        else
        {
        foreach (var group in after.Cards.GroupBy(Location))
        {
            bool changed = !group.Select(c=>c.Id).ToHashSet().SetEquals(State.Cards.Where(c=>Location(c)==group.Key).Select(c=>c.Id));
            if (!changed)
            {
                foreach (var c in group) nextPositions[c.Id]=positions[c.Id];
                continue;
            }
            if (group.First().Zone is not (Zone.Support or Zone.Front))
            {
                int tail = State.Cards.Where(c=>Location(c)==group.Key).Select(c=>positions[c.Id]).DefaultIfEmpty(-1).Max()+1;
                foreach (var c in group) nextPositions[c.Id]=Location(Card(c.Id))==group.Key ? positions[c.Id] : tail++;
                continue;
            }
            var ordered = group.OrderBy(c => Location(Card(c.Id)) == group.Key ? 0 : 1).ThenBy(c => positions[c.Id]).ThenBy(c => c.Id).Select(c => c.Id).ToList();
            if (insert.HasValue && ordered.Remove(moving) && Location(after.Cards.Single(c => c.Id == moving)) == group.Key)
                ordered.Insert(insert.Value, moving);
            for (int i = 0; i < ordered.Count; i++) nextPositions[ordered[i]] = i;
        }
        }
        State = after; positions = nextPositions; AwaitingStart = pending; firstStart = false;
        LastActionId = action.ActionId; accepted.Add(action.ActionId, digest);
        return Checkpoint();
    }
    public FyCheckpoint AcceptPacket(string packet)
    {
        var decoded = FyCodec.Decode(packet);
        var action = Json.Read<FyAction>(decoded.Text);
        if (action.SendActionId != decoded.SendActionId) throw new RuleException("Packet send_action_id mismatch");
        return Accept(action);
    }
}
