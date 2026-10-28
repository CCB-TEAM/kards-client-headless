-- Lua runs when exporting the pack. Rust executes these effects during battle.
local function card(id,title,kind,cost,operation,attack,defense,blitz,effects)
    return {id=id,title=title,kind=kind,cost=cost,operation_cost=operation,
        attack=attack,defense=defense,blitz=blitz or false,effects=effects}
end
local enemy_units={side="enemy",area="board",units_only=true,exclude_source=false}
return {
    version=1,id="example.cards",
    cards={
        card("card_location_london","ALEXANDRIA","hq",0,0,0,20),
        card("card_location_berlin","DANZIG","hq",0,0,0,20),
        card("card_unit_12th_infantry_regiment_tut","12th Infantry (tutorial)","infantry",1,1,2,2),
        card("card_unit_121st_regiment_tut","121st Regiment (tutorial)","infantry",1,1,2,2),
        card("card_unit_panzer_35_t_tut","Panzer 35(t) (tutorial)","tank",2,1,2,2),
        card("card_unit_t_34","T-34 1942","tank",5,2,5,5,true),
        card("mod_arc_infantry","Arc infantry","infantry",1,1,1,3,true,{
            {on="deploy",effects={{op="damage",target={kind="random",query=enemy_units},amount=2}}}
        }),
        card("mod_last_supply","Last supply","infantry",1,1,1,1,false,{
            {on="death",effects={{op="draw",side="friendly",count=1}}}
        }),
        card("mod_reinforcements","Reinforcements","order",1,0,0,0,false,{
            {on="deploy",effects={{op="create",card="mod_arc_infantry",side="friendly",zone="support"}}}
        })
    }
}
