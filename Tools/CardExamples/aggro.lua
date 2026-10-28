-- Audited against Flora's 1.61 blueprints; see Research/Headless/Aggro20261028.
-- This file only authors IR. No Lua VM or Lua RNG is used during matches.
local cards={}
local function q(side,area,extra)
    local v={side=side,area=area,units_only=true,exclude_source=false}
    for k,x in pairs(extra or {}) do v[k]=x end
    return v
end
local function target(kind,query) return {kind=kind,query=query} end
local source=target("source")
local chosen=target("chosen")
local function all(query) return target("all",query) end
local function random(query) return target("random",query) end
local function count(query) return {expr="count",query=query} end
local function fx(op,t,n) return {op=op,target=t,amount=n} end
local function damage(t,n) return fx("damage",t,n) end
local function draw(side,n) return {op="draw",side=side,count=n} end
local function test(query) return {test="any",query=query} end
local function iff(condition,effects) return {op="if",condition=condition,["then"]=effects} end
local function bind(on,effects,area) return {on=on,effects=effects,active_in=area} end
local function add(id,title,kind,cost,operation,attack,defense,code,extra)
    local c={id=id,title=title,kind=kind,cost=cost,operation_cost=operation,attack=attack,defense=defense,
        blitz=false,native_code=code,faction="Japan"}
    for k,v in pairs(extra or {}) do c[k]=v end
    cards[#cards+1]=c
    return c
end
local enemy=q("enemy","board")
local enemy_all=q("enemy","board",{units_only=false})
local own=q("friendly","board")
local enemy_hq=q("enemy","board",{units_only=false,kind="hq"})
local friendly_front=q("friendly","front")
local japanese_front=q("friendly","front",{faction="Japan"})
local function deploy(effects) return {bind("deploy",effects)} end
local function modify(t,a,d) return {op="modify",target=t,attack=a,defense=d} end

add("card_location_changchun","TRUK","hq",0,0,0,20,"6l")
add("card_event_desperate_measures","DESPERATE MEASURES","order",2,0,0,0,"5C",{
    effects=deploy({damage(all(enemy_all),2),{op="slots",side="friendly",amount=-1}})})
add("card_event_bombing_raid","BOMBING RAID","order",4,0,0,0,"5x",{targeting=enemy_all,
    effects=deploy({{op="capture",target=all(q("enemy","board",{units_only=false,adjacent_to=chosen})),effects={
        damage(chosen,3),damage(target("saved"),2)}}})})
add("card_unit_2nd_raiding_brigade","2nd RAIDING BRIGADE","infantry",3,1,2,2,"6B",{
    blitz=true,targeting=q("enemy","board",{max_attack=2}),targeting_optional=true,
    effects=deploy({{op="destroy",target=chosen}})})
add("card_unit_1st_signal_regiment","1st SIGNAL REGIMENT","infantry",1,4,0,4,"6y",{
    combat={smokescreen=true},effects={bind("other_death",{
        iff({test="subject_matches",query=q("friendly","discard")},{damage(all(enemy_hq),1)})})}})
add("card_unit_ki_83","Ki-83","fighter",4,2,6,5,"7x",{
    effects={bind("start_turn",{draw("friendly",1),draw("enemy",2)})}})
add("card_unit_dinah_iii","Ki-46 DINAH III","fighter",3,1,1,3,"hH",{
    effects=deploy({{op="draw_selected",query=q("friendly","deck",{cost=1}),count=2}})})
add("card_event_audacity","AUDACITY","order",3,0,0,0,"lf",{
    effects=deploy({{op="bind",target=random(enemy),effects={
        {op="create",card="card_unit_aichi_d3a",side="friendly",zone="support"},
        iff({test="exists",target=target("created")},{damage(target("bound"),{expr="attack",target=target("created")})})}}})})
add("card_event_shinano","IJN SHINANO","order",3,0,0,0,"m6",{
    effects=deploy({{op="repeat",count=count(q("any","board")),effects={damage(random(enemy_all),1)}}})})
add("card_unit_ki_30_ann","Ki-30 ANN","bomber",3,1,2,2,"rk",{blitz=true,
    effects={bind("after_attack",{damage(all(enemy_hq),2)})}})
add("card_event_suppression_fire","SUPPRESSION","order",1,0,0,0,"3f",{
    faction="Germany",targeting=q("any","board"),effects=deploy({{op="suppress",target=chosen},
        iff(test(q("friendly","board",{air_only=true})),{draw("friendly",1)})})})
add("card_unit_22_infantry","22. INFANTRY REGIMENT","infantry",2,1,2,1,"3Q",{faction="Germany",
    effects={bind("end_turn",{iff(test(friendly_front),{draw("friendly",1)})})}})
add("card_event_naval_supply_run","NAVAL SUPPLY RUN","order",3,0,0,0,"5X",{
    effects=deploy({modify(all(own),2,1),{op="schedule",turns=2,effects={{op="discard_random",side="friendly",count=1}}}})})
add("card_unit_41st_bicycle_regiment","41st BICYCLE REGIMENT","infantry",1,0,1,1,"6E",{
    blitz=true,combat={fury=true}})
add("card_unit_panzer_35t_commander","35(t) COMMAND TANK","tank",2,1,2,2,"fR",{faction="Germany",
    effects={bind("frontline_changed",{iff({test="frontline_owned"},{
        {op="play_free",target=source,zone="front"}})},"hand")}})
add("card_unit_3rd_yokosuka","3rd YOKOSUKA","infantry",1,0,1,1,"lk",{blitz=true,
    auras={{target=source,attack=count(q("friendly","board",{faction="Japan",adjacent_to=source}))}}})
add("card_unit_type_97_te_ke","TYPE 97 TE-KE","tank",1,0,1,1,"nI",{blitz=true,
    effects={bind("damage",{iff({test="subject_matches",query=enemy_hq},{modify(source,1,0)})})}})
add("card_unit_panzer_35_t","PANZER 35(t)","tank",2,1,2,2,"4p",{faction="Germany",blitz=true,
    effects=deploy({iff(test(q("friendly","board",{kind="infantry"})),{{op="operation_cost",target=source,amount=-1}})})})
add("card_unit_15th_cavalry_regiment","15th CAVALRY REGIMENT","infantry",1,0,2,1,"6x",{blitz=true})
add("card_unit_type94_tk","TYPE 94 TK","tank",1,0,1,1,"7e",{
    effects=deploy({{op="create",card="card_event_surprise_attack",side="friendly",zone="hand"}})})
add("card_unit_type_93","TYPE 93","tank",1,1,1,1,"7l",{
    auras={{target=all(q("friendly","board",{exclude_source=true})),attack=1}}})
add("card_event_rising_sun","RISING SUN","order",1,0,0,0,"5Z",{
    effects=deploy({iff(test(japanese_front),{draw("friendly",2)})})})
add("card_event_surprise_attack","SURPRISE ATTACK","order",1,0,0,0,"61",{targeting=enemy,
    effects=deploy({{op="if",condition={test="pinned",target=chosen},["then"]={{op="destroy",target=chosen}},
        otherwise={{op="pin",target=chosen}}}})})
add("card_unit_aichi_d3a","AICHI D3A","bomber",3,2,3,2,"6K",{
    auras={{target=source,attack={expr="multiply",left=2,right=count(q("friendly","board",{
        card="card_unit_aichi_d3a",exclude_source=true}))}}}})
return {version=1,id="native.japan-germany.aggro.v1",cards=cards}
