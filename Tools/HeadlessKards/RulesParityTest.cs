namespace HeadlessKards;

public static class RulesParityTest
{
    public static void Run(IEnumerable<Definition> catalog)
    {
        var definitions=catalog.ToArray();var managed=new Engine(definitions,RandomBackend.Managed,RulesBackend.Managed);var rust=new Engine(definitions,RandomBackend.Managed,RulesBackend.Rust);
        int checks=0;
        void Same(bool condition,string message){if(!condition)throw new RuleException("RULES PARITY FAILED: "+message);checks++;}
        for(int seed=-10;seed<40;seed++)
        {
            var a=managed.New(new(seed));var b=rust.New(new(seed));
            Same(Engine.StateHash(a)==Engine.StateHash(b),"opening "+seed);
            int steps=0;
            while(a.Outcome is null && steps++<2000)
            {
                var la=managed.Legal(a);var lb=rust.Legal(b);Same(la.SequenceEqual(lb),"legal actions");
                var action=managed.Think(a)!;Same(action==rust.Think(b),"greedy AI");
                a=managed.Apply(a,action,a.Revision);b=rust.Apply(b,action,b.Revision);Same(Engine.StateHash(a)==Engine.StateHash(b),"action result");
            }
            Same(a.Outcome is not null,"termination");
        }
        Console.WriteLine(Json.Write(new {passed=checks,seeds=50,reference="previous C# six-card rule subset",rules_backend="rust",native_client_tested=false}));
    }
}
