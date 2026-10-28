using System.Text.Json;

namespace HeadlessKards;

// Synthetic post-mulligan fixtures use the server's actual DTO layout and the
// six audited native card IDs. They are not recorded live-client traffic.
public static class ProtocolFixtures
{
    public static JsonElement Snapshot(FyCheckpoint checkpoint)
    {
        return JsonSerializer.SerializeToElement(new
        {
            kredits_left=checkpoint.State.Players[0].Kredits, kredits_right=checkpoint.State.Players[1].Kredits,
            max_kredits_left=checkpoint.State.Players[0].MaxKredits,max_kredits_right=checkpoint.State.Players[1].MaxKredits,
            cards=checkpoint.State.Cards.Select(c=>new
            {
                card_id=c.Id,name=c.Definition,attack=c.Attack,defense=c.Defense,
                side=c.Side==Side.Left?"left":"right",location_number=checkpoint.Positions[c.Id],
                location=c.Zone switch { Zone.Front=>"board_frontline",Zone.Discard=>"discard",
                    Zone.Support=>c.Side==Side.Left?"board_hqleft":"board_hqright",
                    Zone.Hand=>c.Side==Side.Left?"hand_left":"hand_right",_=>c.Side==Side.Left?"deck_left":"deck_right" }
            }).ToArray()
        });
    }
    public static FyBootstrap FromDemo(Engine engine, int seed, int matchId = 123475)
        =>FromSetup(engine,new(seed),matchId);
    public static FyBootstrap FromSetup(Engine engine,CreateRequest setup,int matchId=123475)
    {
        var state = engine.New(setup);
        object Card(Card c, int number) => new
        {
            card_id = c.Id, is_gold = false, location_number = number, name = c.Definition,
            location = c.Zone == Zone.Support ? (c.Side == Side.Left ? "board_hqleft" : "board_hqright") :
                c.Zone == Zone.Hand ? (c.Side == Side.Left ? "hand_left" : "hand_right") :
                c.Side == Side.Left ? "deck_left" : "deck_right"
        };
        object[] Hand(Side side) => state.Cards.Where(c => c.Side == side && c.Zone == Zone.Hand).Select((c,i) => Card(c,i)).ToArray();
        object[] Deck(Side side) => state.Players[(int)side].Deck.Select((id,i) => Card(state.Cards.Single(c => c.Id == id),i+5)).ToArray();
        object Hq(Side side) => Card(state.Cards.Single(c => c.Side == side && engine.Def(c).Kind == Kind.Hq), 0);
        var info = new
        {
            local_subactions = true,
            match_and_starting_data = new
            {
                match = new { match_id = matchId, start_side = "left", current_action_id = 0 },
                starting_data = new
                {
                    player_id_left = 1001, player_id_right = -9178,
                    location_card_left = Hq(Side.Left), location_card_right = Hq(Side.Right),
                    starting_hand_left = Hand(Side.Left), starting_hand_right = Hand(Side.Right),
                    deck_left = Deck(Side.Left), deck_right = Deck(Side.Right)
                }
            }
        };
        return new(JsonSerializer.SerializeToElement(info), true, false,LogicRandom:state.LogicRandom);
    }
}
