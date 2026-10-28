namespace HeadlessKards;

public static class SelfTest
{
    private const string Infantry = "card_unit_12th_infantry_regiment_tut";
    private const string Tank = "card_unit_panzer_35_t_tut";
    private const string Blitz = "card_unit_t_34";
    public static void Run(Engine engine)
    {
        int count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new RuleException("TEST FAILED: " + label);
            count++;
        }
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); } catch (RuleException) { rejected = true; }
            Check(rejected, label);
        }
        State Fresh() => engine.New(new(123));
        Card Put(State s, Side side, string definition, Zone zone, int attacks = 1, int moves = 1)
        {
            var c = s.Cards.First(c => c.Side == side && c.Definition == definition && c.Zone is Zone.Hand or Zone.Deck);
            s.Players[(int)side].Deck.Remove(c.Id);
            c.Zone = zone;
            c.Attacks = attacks;
            c.Moves = moves;
            return c;
        }
        Card Hq(State s, Side side) => s.Cards.Single(c => c.Side == side && engine.Def(c).Kind == Kind.Hq);

        Check(engine.Catalog.Count == 6, "Only six supported definitions");
        Check(Engine.StateHash(Fresh()) == Engine.StateHash(Fresh()), "Deterministic opening");
        Reject(() => engine.New(new(1, ["not_implemented"], null)), "Unknown card rejected");
        Reject(() => engine.New(new(1, ["card_location_london"], null)), "HQ in deck rejected");
        var s = Fresh();
        var oldHash = Engine.StateHash(s);
        Reject(() => engine.Apply(s, new(Side.Right, ActionKind.EndTurn), s.Revision), "Wrong side rejected");
        Reject(() => engine.Apply(s, new(Side.Left, ActionKind.EndTurn), 100), "Stale revision rejected");
        Reject(() => engine.Apply(s, new(Side.Left, ActionKind.Attack, 123456, 999), s.Revision), "Invalid ids rejected");
        Check(Engine.StateHash(s) == oldHash, "Rejected actions are atomic");
        var infantry = s.Cards.First(c => c.Side == Side.Left && c.Definition == Infantry);
        s.Players[0].Deck.Remove(infantry.Id);
        infantry.Zone = Zone.Hand;
        var beforeDeploy = Engine.StateHash(s);
        var deployed = engine.Apply(s, new(Side.Left, ActionKind.Deploy, infantry.Id), s.Revision);
        Check(deployed.Players[0].Kredits == 0, "Deployment spends Kredits");
        var deployedCard = deployed.Cards.Single(c => c.Id == infantry.Id);
        Check(deployedCard.Attacks == 0 && deployedCard.Moves == 0, "Deployment sickness");
        Check(Engine.StateHash(s) == beforeDeploy, "Apply does not mutate caller");

        s = Fresh(); s.Players[0].Kredits = 12;
        var blitz = Put(s, Side.Left, Blitz, Zone.Hand, 0, 0);
        var b = engine.Apply(s, new(Side.Left, ActionKind.Deploy, blitz.Id), s.Revision);
        Check(b.Cards.Single(c => c.Id == blitz.Id).Moves == 1 && b.Cards.Single(c => c.Id == blitz.Id).Attacks == 1, "Blitz ready on deployment");

        s = Fresh(); s.Players[0].Kredits = 12;
        foreach (var c in s.Cards.Where(c => c.Side == Side.Left && engine.Def(c).Kind != Kind.Hq).Take(4))
        { s.Players[0].Deck.Remove(c.Id); c.Zone = Zone.Support; }
        Check(!engine.Legal(s).Any(c => c.Kind == ActionKind.Deploy), "HQ plus four units fills support");

        s = Fresh(); s.Players[0].Kredits = 12;
        infantry = Put(s, Side.Left, Infantry, Zone.Support);
        var enemy = Put(s, Side.Right, Tank, Zone.Front);
        Check(!engine.Legal(s).Any(a => a.Kind == ActionKind.Move), "Enemy controls frontline");
        Check(engine.Legal(s).Contains(new(Side.Left, ActionKind.Attack, infantry.Id, enemy.Id)), "Support can attack enemy frontline");
        Check(!engine.Legal(s).Contains(new(Side.Left, ActionKind.Attack, infantry.Id, Hq(s, Side.Right).Id)), "Support cannot attack enemy HQ");
        var fight = engine.Apply(s, new(Side.Left, ActionKind.Attack, infantry.Id, enemy.Id), s.Revision);
        Check(fight.Cards.Single(c => c.Id == infantry.Id).Zone == Zone.Discard && fight.Cards.Single(c => c.Id == enemy.Id).Zone == Zone.Discard, "Simultaneous lethal retaliation");

        s = Fresh(); s.Players[0].Kredits = 12;
        var tank = Put(s, Side.Left, Tank, Zone.Support);
        var moved = engine.Apply(s, new(Side.Left, ActionKind.Move, tank.Id), s.Revision);
        Check(moved.Cards.Single(c => c.Id == tank.Id).Attacks == 1, "Tank may attack after moving");
        var hit = engine.Apply(moved, new(Side.Left, ActionKind.Attack, tank.Id, Hq(moved, Side.Right).Id), moved.Revision);
        Check(Hq(hit, Side.Right).Defense == 18 && hit.Players[0].Kredits == 10, "HQ damage and two operation costs");
        Reject(() => engine.Apply(hit, new(Side.Left, ActionKind.Attack, tank.Id, Hq(hit, Side.Right).Id), hit.Revision), "Second attack rejected");

        s = Fresh(); s.Players[0].Kredits = 12;
        infantry = Put(s, Side.Left, Infantry, Zone.Support);
        moved = engine.Apply(s, new(Side.Left, ActionKind.Move, infantry.Id), s.Revision);
        Check(!engine.Legal(moved).Any(a => a.Kind == ActionKind.Attack && a.CardId == infantry.Id), "Infantry movement consumes attack");

        s = Fresh(); s.Players[0].Kredits = 0;
        tank = Put(s, Side.Left, Tank, Zone.Front);
        Check(engine.Legal(s).All(a => a.Kind == ActionKind.EndTurn), "Cannot operate without Kredits");
        s = Fresh(); s.Players[0].Kredits = 12;
        tank = Put(s, Side.Left, Tank, Zone.Front);
        Hq(s, Side.Right).Defense = 2;
        hit = engine.Apply(s, new(Side.Left, ActionKind.Attack, tank.Id, Hq(s, Side.Right).Id), s.Revision);
        Check(hit.Outcome == "left" && engine.Legal(hit).Count == 0, "HQ destruction ends match");

        s = Fresh();
        foreach (var c in s.Cards.Where(c => c.Side == Side.Right && c.Zone == Zone.Deck)) c.Zone = Zone.Discard;
        s.Players[1].Deck.Clear();
        var turn = engine.Apply(s, new(Side.Left, ActionKind.EndTurn), s.Revision);
        Check(turn.Active == Side.Right && turn.Players[1].Kredits == 1 && Hq(turn, Side.Right).Defense == 19, "Turn start plus fatigue");
        s = Fresh();
        foreach (var c in s.Cards.Where(c => c.Side == Side.Right && c.Zone != Zone.Support).Take(9))
        { s.Players[1].Deck.Remove(c.Id); c.Zone = Zone.Hand; }
        var nextId = s.Players[1].Deck[0];
        turn = engine.Apply(s, new(Side.Left, ActionKind.EndTurn), s.Revision);
        Check(turn.Cards.Single(c => c.Id == nextId).Zone == Zone.Discard, "Full hand burns draw");

        var replay = engine.SelfPlay(new(42), 4000);
        var final = engine.Verify(replay);
        Check(final.Outcome is not null, "AI selfplay terminates");
        var repeat = engine.SelfPlay(new(42), 4000);
        Check(Json.Write(replay) == Json.Write(repeat), "AI deterministic replay");
        var corrupted = replay with { Actions = [.. replay.Actions] };
        corrupted.Actions[0] = corrupted.Actions[0] with { StateHash = "tampered" };
        Reject(() => engine.Verify(corrupted), "Tampered replay rejected");
        for (int seed = 0; seed < 100; seed++)
        {
            var run = engine.SelfPlay(new(seed), 4000);
            var result = engine.Verify(run);
            Check(result.Outcome is not null && result.Players.All(p => p.Kredits >= 0), "Seed " + seed);
        }
        Console.WriteLine(Json.Write(new { passed = count, selfplay_seeds = 100, rules = Engine.RulesVersion, native_client_integration_tested = false }));
    }
}
