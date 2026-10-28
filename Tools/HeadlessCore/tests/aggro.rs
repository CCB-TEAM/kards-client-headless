use headless_core::{Core,model::*};

fn core()->Core{let p:Pack=serde_json::from_str(include_str!("../../CardExamples/pack-aggro.json")).unwrap();Core::new(p.cards,None).unwrap()}
fn state(c:&Core)->State{
    let deck=c.defs.values().filter(|d|d.kind!=Kind::Hq).map(|d|d.id.clone()).collect::<Vec<_>>();
    let mut s=c.new_game(Setup{seed:12345,left_deck:Some(deck.clone()),right_deck:Some(deck),headquarters:Some(["card_location_changchun".into(),"card_location_changchun".into()]),rng_profile:Some("client".into())}).unwrap();
    for p in &mut s.players{p.kredits=12;p.max_kredits=12;}
    s
}
fn put(s:&mut State,side:Side,key:&str,zone:Zone,pos:i32)->i32{
    let c=s.cards.iter_mut().find(|c|c.side==side&&c.definition==key).unwrap();let id=c.id;c.zone=zone;c.moves=1;c.attacks=1;
    for p in &mut s.players{p.deck.retain(|v|*v!=id);}
    s.battle.as_mut().unwrap().positions.insert(id,pos);id
}
fn play(c:&Core,s:&State,id:i32,target:i32)->State{c.apply(s,Command{side:s.active,kind:ActionKind::Deploy,card_id:id,target_id:target},s.revision,false).unwrap()}
fn card(s:&State,id:i32)->&Card{s.cards.iter().find(|c|c.id==id).unwrap()}
fn attack(c:&Core,s:&State,id:i32,target:i32)->State{c.apply(s,Command{side:s.active,kind:ActionKind::Attack,card_id:id,target_id:target},s.revision,false).unwrap()}
fn end(c:&Core,s:&State)->State{c.apply(s,Command{side:s.active,kind:ActionKind::EndTurn,card_id:0,target_id:0},s.revision,false).unwrap()}
fn hq(s:&State,side:Side)->i32{s.cards.iter().find(|c|c.side==side&&c.definition=="card_location_changchun").unwrap().id}

#[test]
fn all_twenty_one_deck_cards_and_spawn_closure_are_present(){let c=core();assert_eq!(c.defs.len(),24);assert!(c.defs.values().all(|d|d.native_code.is_some()));}
#[test]
fn bombing_raid_snapshots_neighbors_before_primary_dies(){
    let c=core();let mut s=state(&c);let spell=put(&mut s,Side::Left,"card_event_bombing_raid",Zone::Hand,0);
    let a=put(&mut s,Side::Right,"card_unit_15th_cavalry_regiment",Zone::Support,1);
    let b=put(&mut s,Side::Right,"card_unit_type94_tk",Zone::Support,2);
    let d=put(&mut s,Side::Right,"card_unit_ki_83",Zone::Support,3);
    let result=play(&c,&s,spell,b);assert_eq!(card(&result,a).zone,Zone::Discard);assert_eq!(card(&result,b).zone,Zone::Discard);assert_eq!(card(&result,d).defense,3);assert_eq!(card(&result,hq(&s,Side::Right)).defense,20);
}
#[test]
fn desperate_measures_damages_hq_and_all_units_and_loses_slot(){let c=core();let mut s=state(&c);let id=put(&mut s,Side::Left,"card_event_desperate_measures",Zone::Hand,0);let e=put(&mut s,Side::Right,"card_unit_type94_tk",Zone::Support,1);let a=play(&c,&s,id,0);assert_eq!(a.players[0].max_kredits,11);assert_eq!(card(&a,e).zone,Zone::Discard);assert_eq!(card(&a,hq(&s,Side::Right)).defense,18);}
#[test]
fn raiding_brigade_requires_target_only_when_one_exists(){let c=core();let mut s=state(&c);let id=put(&mut s,Side::Left,"card_unit_2nd_raiding_brigade",Zone::Hand,0);assert!(c.legal(&s).unwrap().iter().any(|a|a.card_id==id&&a.target_id==0));let e=put(&mut s,Side::Right,"card_unit_type94_tk",Zone::Support,1);let a=play(&c,&s,id,e);assert_eq!(card(&a,e).zone,Zone::Discard);assert_eq!(card(&a,id).attacks,1);}
#[test]
fn fury_and_zero_operation_cost_allow_two_attacks(){let c=core();let mut s=state(&c);let id=put(&mut s,Side::Left,"card_unit_41st_bicycle_regiment",Zone::Hand,0);let mut a=play(&c,&s,id,0);assert_eq!(card(&a,id).attacks,2);a=c.apply(&a,Command{side:Side::Left,kind:ActionKind::Move,card_id:id,target_id:0},a.revision,false).unwrap();assert_eq!(card(&a,id).attacks,0); // Moving infantry consumes attacks.
    put(&mut s,Side::Left,"card_unit_41st_bicycle_regiment",Zone::Front,0);s.cards.iter_mut().find(|x|x.id==id).unwrap().attacks=2;let money=s.players[0].kredits;let h=hq(&s,Side::Right);let a=attack(&c,&s,id,h);let b=attack(&c,&a,id,h);assert_eq!(b.players[0].kredits,money);assert_eq!(card(&b,h).defense,18);}
#[test]
fn smokescreen_blocks_attacks_but_not_orders(){let c=core();let mut s=state(&c);let fighter=put(&mut s,Side::Left,"card_unit_ki_83",Zone::Support,1);let e=put(&mut s,Side::Right,"card_unit_1st_signal_regiment",Zone::Support,1);assert!(!c.legal(&s).unwrap().iter().any(|a|a.card_id==fighter&&a.target_id==e));let spell=put(&mut s,Side::Left,"card_event_bombing_raid",Zone::Hand,0);assert_eq!(card(&play(&c,&s,spell,e),e).defense,1);}
#[test]
fn fighter_protects_its_row_and_bomber_avoids_ground_retaliation(){let c=core();let mut s=state(&c);let b=put(&mut s,Side::Left,"card_unit_ki_30_ann",Zone::Support,1);let e=put(&mut s,Side::Right,"card_unit_panzer_35_t",Zone::Support,1);put(&mut s,Side::Right,"card_unit_dinah_iii",Zone::Support,2);assert!(!c.legal(&s).unwrap().iter().any(|a|a.card_id==b&&a.target_id==e));put(&mut s,Side::Right,"card_unit_panzer_35_t",Zone::Front,0);let a=attack(&c,&s,b,e);assert_eq!(card(&a,b).defense,2);assert_eq!(card(&a,hq(&s,Side::Right)).defense,18);}
#[test]
fn adjacent_japanese_aura_excludes_hq_and_changes_after_move(){let c=core();let mut s=state(&c);let y=put(&mut s,Side::Left,"card_unit_3rd_yokosuka",Zone::Support,1);let friend=put(&mut s,Side::Left,"card_unit_15th_cavalry_regiment",Zone::Support,2);let a=c.apply(&s,Command{side:Side::Left,kind:ActionKind::Move,card_id:friend,target_id:0},s.revision,false).unwrap();assert_eq!(card(&a,y).attack,1);}
#[test]
fn type93_aura_and_suppression_preserve_damaged_health(){let c=core();let mut s=state(&c);let aura=put(&mut s,Side::Right,"card_unit_type_93",Zone::Support,1);let other=put(&mut s,Side::Right,"card_unit_ki_83",Zone::Support,2);let spell=put(&mut s,Side::Left,"card_event_suppression_fire",Zone::Hand,0);let a=play(&c,&s,spell,aura);assert!(card(&a,aura).suppressed());assert_eq!(card(&a,other).attack,6);
    let mut s=state(&c);let id=put(&mut s,Side::Right,"card_unit_ki_83",Zone::Support,1);let e=s.cards.iter_mut().find(|x|x.id==id).unwrap();e.defense=3;e.attack=8;e.script=Some(CardScript{attack_buff:2,defense_buff:1,..CardScript::default()});let spell=put(&mut s,Side::Left,"card_event_suppression_fire",Zone::Hand,0);assert_eq!(card(&play(&c,&s,spell,id),id).defense,3);}
#[test]
fn signal_fires_for_other_units_in_same_death_batch(){let c=core();let mut s=state(&c);let spell=put(&mut s,Side::Left,"card_event_desperate_measures",Zone::Hand,0);let signal=put(&mut s,Side::Right,"card_unit_1st_signal_regiment",Zone::Support,1);s.cards.iter_mut().find(|x|x.id==signal).unwrap().defense=2;put(&mut s,Side::Right,"card_unit_type94_tk",Zone::Support,2);let a=play(&c,&s,spell,0);assert_eq!(card(&a,hq(&s,Side::Left)).defense,19);}
#[test]
fn supply_run_discards_only_at_next_friendly_start(){let c=core();let mut s=state(&c);let id=put(&mut s,Side::Left,"card_event_naval_supply_run",Zone::Hand,0);let u=put(&mut s,Side::Left,"card_unit_ki_83",Zone::Support,1);let a=play(&c,&s,id,0);assert_eq!((card(&a,u).attack,card(&a,u).defense),(8,6));assert_eq!(a.battle.as_ref().unwrap().delayed.len(),1);let b=end(&c,&a);assert_eq!(b.battle.as_ref().unwrap().delayed.len(),1);let d=end(&c,&b);assert!(d.battle.as_ref().unwrap().delayed.is_empty());assert!(d.battle.as_ref().unwrap().client_random.as_ref().unwrap().draws>0);}
#[test]
fn targeted_draw_uses_without_replacement_and_exact_burn_stream(){let c=core();let mut s=state(&c);let id=put(&mut s,Side::Left,"card_unit_dinah_iii",Zone::Hand,0);for x in &mut s.cards{if x.side==Side::Left&&x.id!=id&&x.definition!="card_location_changchun"{x.zone=Zone::Deck;}}s.players[0].deck=s.cards.iter().filter(|x|x.side==Side::Left&&x.zone==Zone::Deck).map(|x|x.id).collect();let mut expected=s.battle.as_ref().unwrap().client_random.clone().unwrap();let mut candidates=s.cards.iter().filter(|x|x.side==Side::Left&&x.zone==Zone::Deck&&c.def(x).unwrap().kind!=Kind::Order&&c.def(x).unwrap().cost==1).map(|x|x.id).collect::<Vec<_>>();let first=candidates.remove(expected.card_index(candidates.len()).unwrap());let second=candidates.remove(expected.card_index(candidates.len()).unwrap());let a=play(&c,&s,id,0);assert_eq!(card(&a,first).zone,Zone::Hand);assert_eq!(card(&a,second).zone,Zone::Hand);assert_eq!(a.battle.unwrap().client_random.unwrap().seed,expected.seed);}
#[test]
fn command_tank_enters_front_from_hand_for_free(){let c=core();let mut s=state(&c);let id=put(&mut s,Side::Left,"card_unit_panzer_35t_commander",Zone::Hand,0);let u=put(&mut s,Side::Left,"card_unit_15th_cavalry_regiment",Zone::Support,1);let a=c.apply(&s,Command{side:Side::Left,kind:ActionKind::Move,card_id:u,target_id:0},s.revision,false).unwrap();assert_eq!(card(&a,id).zone,Zone::Front);assert_eq!(a.players[0].kredits,12);}
#[test]
fn type94_spawns_surprise_with_native_id_and_second_cast_destroys_pinned(){let c=core();let mut s=state(&c);let id=put(&mut s,Side::Left,"card_unit_type94_tk",Zone::Hand,0);let e=put(&mut s,Side::Right,"card_unit_ki_83",Zone::Support,1);let a=play(&c,&s,id,0);let spawned=card(&a,1001);assert_eq!(spawned.definition,"card_event_surprise_attack");assert_eq!(spawned.zone,Zone::Hand);let b=play(&c,&a,1001,e);assert_eq!(card(&b,e).script.as_ref().unwrap().pinned_turns,2);let mut b=b;put(&mut b,Side::Left,"card_event_surprise_attack",Zone::Hand,0);let id=b.cards.iter().find(|x|x.side==Side::Left&&x.definition=="card_event_surprise_attack"&&x.id!=1001).unwrap().id;let d=play(&c,&b,id,e);assert_eq!(card(&d,e).zone,Zone::Discard);}
#[test]
fn audacity_uses_buffed_spawn_attack_and_spawns_without_enemy(){let c=core();let mut s=state(&c);let id=put(&mut s,Side::Left,"card_event_audacity",Zone::Hand,0);put(&mut s,Side::Left,"card_unit_aichi_d3a",Zone::Support,1);let e=put(&mut s,Side::Right,"card_unit_ki_83",Zone::Support,1);s.cards.iter_mut().find(|x|x.id==e).unwrap().defense=8;let a=play(&c,&s,id,0);assert_eq!(card(&a,e).defense,3);assert_eq!(card(&a,1001).attack,5);let mut s=state(&c);let id=put(&mut s,Side::Left,"card_event_audacity",Zone::Hand,0);let a=play(&c,&s,id,0);assert_eq!(card(&a,1001).definition,"card_unit_aichi_d3a");assert_eq!(a.battle.unwrap().client_random.unwrap().draws,0);}
#[test]
fn shinanohits_count_initial_units_and_is_deterministic(){let c=core();let mut s=state(&c);let id=put(&mut s,Side::Left,"card_event_shinano",Zone::Hand,0);put(&mut s,Side::Left,"card_unit_ki_83",Zone::Support,1);put(&mut s,Side::Right,"card_unit_ki_83",Zone::Support,1);let a=play(&c,&s,id,0);let b=play(&c,&s,id,0);assert_eq!(serde_json::to_value(&a).unwrap(),serde_json::to_value(&b).unwrap());let damage:i32=s.cards.iter().filter(|x|x.side==Side::Right&&x.board()).map(|x|x.defense-card(&a,x.id).defense).sum();assert_eq!(damage,2);}
#[test]
fn te_ke_grows_after_hq_hit_and_panzer_operation_discount_applies(){let c=core();let mut s=state(&c);let id=put(&mut s,Side::Left,"card_unit_type_97_te_ke",Zone::Front,0);let a=attack(&c,&s,id,hq(&s,Side::Right));assert_eq!(card(&a,id).attack,2);put(&mut s,Side::Left,"card_unit_15th_cavalry_regiment",Zone::Support,1);let p=put(&mut s,Side::Left,"card_unit_panzer_35_t",Zone::Hand,0);let a=play(&c,&s,p,0);assert_eq!(card(&a,p).script.as_ref().unwrap().operation_buff,-1);}
#[test]
fn rising_sun_and_end_turn_infantry_require_frontline(){let c=core();let mut s=state(&c);let id=put(&mut s,Side::Left,"card_event_rising_sun",Zone::Hand,0);let n=s.players[0].deck.len();assert_eq!(play(&c,&s,id,0).players[0].deck.len(),n);put(&mut s,Side::Left,"card_unit_15th_cavalry_regiment",Zone::Front,0);let a=play(&c,&s,id,0);assert_eq!(a.players[0].deck.len(),n-3); // Putting cavalry removed one deck card, then draw two.
    let mut s=state(&c);put(&mut s,Side::Left,"card_unit_22_infantry",Zone::Support,1);put(&mut s,Side::Left,"card_unit_15th_cavalry_regiment",Zone::Front,0);let n=s.players[0].deck.len();assert_eq!(end(&c,&s).players[0].deck.len(),n-1);}
