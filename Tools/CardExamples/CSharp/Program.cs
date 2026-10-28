using Kards.Cards;

var enemyUnits=new Query(Side.Enemy,Area.Board,UnitsOnly:true);
var cards=new List<Card> {
    new("card_location_london","ALEXANDRIA",Kind.Hq,0,0,0,20),
    new("card_location_berlin","DANZIG",Kind.Hq,0,0,0,20),
    new("card_unit_12th_infantry_regiment_tut","12th Infantry (tutorial)",Kind.Infantry,1,1,2,2),
    new("card_unit_121st_regiment_tut","121st Regiment (tutorial)",Kind.Infantry,1,1,2,2),
    new("card_unit_panzer_35_t_tut","Panzer 35(t) (tutorial)",Kind.Tank,2,1,2,2),
    new("card_unit_t_34","T-34 1942",Kind.Tank,5,2,5,5,true),
    new("mod_arc_infantry","Arc infantry",Kind.Infantry,1,1,1,3,true,
        [new(Trigger.Deploy,[Fx.Damage(Target.Random(enemyUnits),2)])]),
    new("mod_last_supply","Last supply",Kind.Infantry,1,1,1,1,false,
        [new(Trigger.Death,[Fx.Draw(Side.Friendly,1)])]),
    new("mod_reinforcements","Reinforcements",Kind.Order,1,0,0,0,false,
        [new(Trigger.Deploy,[Fx.Create("mod_arc_infantry",Side.Friendly,Zone.Support)])])
};
new Pack(1,"example.cards",cards).Export(args.Length>0?args[0]:"pack-csharp.json");
