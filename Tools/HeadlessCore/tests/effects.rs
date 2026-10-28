use headless_core::{Core,model::*,ir::*};

fn base()->Vec<Definition>{serde_json::from_str(include_str!("../../HeadlessKards/cards.json")).unwrap()}
fn scripted(id:&str,on:Trigger,effects:Vec<Effect>)->Definition{
    Definition{id:id.into(),title:id.into(),kind:Kind::Infantry,cost:1,operation_cost:1,attack:1,defense:2,blitz:true,effects:Some(vec![Binding{active_in:None,on,effects}]),targeting:None,faction:None,combat:None,auras:None,native_code:None,targeting_optional:false}
}
fn query(side:RelativeSide,area:Area)->Query{Query{side,area,units_only:true,exclude_source:false,kind:None,max_attack:None,cost:None,faction:None,card:None,adjacent_to:None,air_only:false}}
fn setup(core:&Core,id:&str)->(State,i32){
    let deck=vec![id.into(),"card_unit_12th_infantry_regiment_tut".into(),"card_unit_12th_infantry_regiment_tut".into(),"card_unit_12th_infantry_regiment_tut".into(),"card_unit_12th_infantry_regiment_tut".into(),"card_unit_12th_infantry_regiment_tut".into()];
    let mut s=core.new_game(Setup{seed:42,left_deck:Some(deck.clone()),right_deck:Some(deck),headquarters:None,rng_profile:None}).unwrap();
    let source=s.cards.iter_mut().find(|c|c.side==Side::Left&&c.definition==id).unwrap();let source_id=source.id;source.zone=Zone::Hand;
    s.players[0].deck.retain(|id|*id!=source_id);s.players[0].kredits=12;(s,source_id)
}
fn deploy(core:&Core,s:&State,id:i32)->Result<State>{core.apply(s,Command{side:Side::Left,kind:ActionKind::Deploy,card_id:id,target_id:0},s.revision,false)}
#[test]
fn random_deployment_uses_authoritative_rng_and_is_replayable(){
    let mut defs=base();defs.push(scripted("arc",Trigger::Deploy,vec![Effect::Damage{target:Target::Random{query:query(RelativeSide::Enemy,Area::Board)},amount:Number::Constant(1)}]));
    let core=Core::new(defs,None).unwrap();let(mut s,id)=setup(&core,"arc");
    let enemies=s.cards.iter().filter(|c|c.side==Side::Right&&core.def(c).unwrap().kind!=Kind::Hq).take(2).map(|c|c.id).collect::<Vec<_>>();
    for enemy in &enemies{s.players[1].deck.retain(|i|i!=enemy);s.cards.iter_mut().find(|c|c.id==*enemy).unwrap().zone=Zone::Support;}
    let original=serde_json::to_value(&s).unwrap();let a=deploy(&core,&s,id).unwrap();let b=deploy(&core,&s,id).unwrap();
    assert_eq!(serde_json::to_value(&a).unwrap(),serde_json::to_value(&b).unwrap());assert_eq!(serde_json::to_value(&s).unwrap(),original);
    let mut rng=s.logic_random.clone().unwrap();let expected=enemies[rng.range(0,1).unwrap()as usize];
    assert_eq!(a.logic_random,Some(rng));assert_eq!(a.cards.iter().find(|c|c.id==expected).unwrap().defense,s.cards.iter().find(|c|c.id==expected).unwrap().defense-1);
}
#[test]
fn empty_random_query_does_not_consume(){
    let mut defs=base();defs.push(scripted("arc",Trigger::Deploy,vec![Effect::Damage{target:Target::Random{query:query(RelativeSide::Enemy,Area::Board)},amount:Number::Constant(2)}]));
    let core=Core::new(defs,None).unwrap();let(s,id)=setup(&core,"arc");let after=deploy(&core,&s,id).unwrap();assert_eq!(s.logic_random,after.logic_random);
}
#[test]
fn unknown_rng_rejects_random_effect_atomically(){
    let mut defs=base();defs.push(scripted("arc",Trigger::Deploy,vec![Effect::Damage{target:Target::Random{query:query(RelativeSide::Enemy,Area::Board)},amount:Number::Constant(2)}]));
    let core=Core::new(defs,None).unwrap();let(mut s,id)=setup(&core,"arc");s.logic_random=None;
    let enemy=s.cards.iter_mut().find(|c|c.side==Side::Right&&c.definition=="arc").unwrap();let eid=enemy.id;enemy.zone=Zone::Support;s.players[1].deck.retain(|i|*i!=eid);
    let before=serde_json::to_value(&s).unwrap();assert!(deploy(&core,&s,id).unwrap_err().contains("Unknown RNG"));assert_eq!(serde_json::to_value(&s).unwrap(),before);
}
#[test]
fn late_failure_rolls_back_cost_and_earlier_effects(){
    let mut defs=base();defs.push(scripted("bad",Trigger::Deploy,vec![Effect::Modify{target:Target::Source,attack:Number::Constant(10),defense:Number::Constant(10)},Effect::Draw{side:RelativeSide::Friendly,count:Number::Constant(201)}]));
    let core=Core::new(defs,None).unwrap();let(s,id)=setup(&core,"bad");let before=serde_json::to_value(&s).unwrap();assert!(deploy(&core,&s,id).is_err());assert_eq!(serde_json::to_value(&s).unwrap(),before);
}
#[test]
fn death_effect_fires_after_lethal_damage(){
    let mut defs=base();defs.push(scripted("supply",Trigger::Death,vec![Effect::Draw{side:RelativeSide::Friendly,count:Number::Constant(1)}]));
    let core=Core::new(defs,None).unwrap();let(mut s,id)=setup(&core,"supply");
    let source=s.cards.iter_mut().find(|c|c.id==id).unwrap();source.zone=Zone::Front;source.attacks=1;
    let enemy=s.cards.iter_mut().find(|c|c.side==Side::Right&&c.definition=="card_unit_12th_infantry_regiment_tut").unwrap();let eid=enemy.id;enemy.zone=Zone::Support;s.players[1].deck.retain(|i|*i!=eid);
    let old=s.players[0].deck.len();let after=core.apply(&s,Command{side:Side::Left,kind:ActionKind::Attack,card_id:id,target_id:eid},s.revision,false).unwrap();assert_eq!(after.players[0].deck.len(),old-1);assert_eq!(after.cards.iter().find(|c|c.id==id).unwrap().zone,Zone::Discard);
}
#[test]
fn foreach_and_number_expression_execute_inside_rust(){
    let q=query(RelativeSide::Friendly,Area::Board);let mut defs=base();defs.push(scripted("buff",Trigger::Deploy,vec![Effect::ForEach{query:q.clone(),effects:vec![Effect::Modify{target:Target::Bound,attack:Number::Expression(Expression::Count{query:q}),defense:Number::Constant(1)}]}]));
    let core=Core::new(defs,None).unwrap();let(s,id)=setup(&core,"buff");let after=deploy(&core,&s,id).unwrap();let c=after.cards.iter().find(|c|c.id==id).unwrap();assert_eq!((c.attack,c.defense),(2,3));
}
#[test]
fn unresolved_spawn_reference_and_unknown_operations_are_rejected(){
    let mut defs=base();defs.push(scripted("bad",Trigger::Deploy,vec![Effect::Create{card:"not_a_card".into(),side:RelativeSide::Friendly,zone:Zone::Support}]));assert!(Core::new(defs,None).is_err());
    assert!(serde_json::from_str::<Effect>(r#"{"op":"run_native_code"}"#).is_err());
    assert!(serde_json::from_str::<Effect>(r#"{"op":"damage","target":{"kind":"source"},"amount":1,"typo":true}"#).is_err());
}
#[test]
fn supplied_fingerprint_cannot_hide_a_different_catalog(){
    let definitions=base();let core=Core::new(definitions.clone(),None).unwrap();
    assert!(Core::new(definitions.clone(),Some(core.hash)).is_ok());
    assert!(Core::new(definitions,Some("not-the-catalog".into())).is_err());
}
#[test]
fn self_generating_death_loop_is_bounded_and_atomic(){
    let mut defs=base();defs.push(Definition{id:"loop".into(),title:"loop".into(),kind:Kind::Infantry,cost:1,operation_cost:1,attack:1,defense:1,blitz:false,effects:Some(vec![
        Binding{active_in:None,on:Trigger::EnterPlay,effects:vec![Effect::Damage{target:Target::Source,amount:Number::Constant(1)}]},
        Binding{active_in:None,on:Trigger::Death,effects:vec![Effect::Create{card:"loop".into(),side:RelativeSide::Friendly,zone:Zone::Support}]}]),targeting:None,faction:None,combat:None,auras:None,native_code:None,targeting_optional:false});
    let core=Core::new(defs,None).unwrap();let(s,id)=setup(&core,"loop");let before=serde_json::to_value(&s).unwrap();assert!(deploy(&core,&s,id).unwrap_err().contains("budget"));assert_eq!(serde_json::to_value(&s).unwrap(),before);
}
#[test]
fn chosen_targets_are_enumerated_and_validated_by_core(){
    let mut defs=base();let mut d=scripted("precise",Trigger::Deploy,vec![Effect::Damage{target:Target::Chosen,amount:Number::Constant(1)}]);
    d.targeting=Some(query(RelativeSide::Enemy,Area::Board));defs.push(d);
    let core=Core::new(defs,None).unwrap();let(mut s,id)=setup(&core,"precise");
    let enemy=s.cards.iter_mut().find(|c|c.side==Side::Right&&c.definition=="precise").unwrap();let eid=enemy.id;enemy.zone=Zone::Support;s.players[1].deck.retain(|i|*i!=eid);
    let command=Command{side:Side::Left,kind:ActionKind::Deploy,card_id:id,target_id:eid};
    assert!(core.legal(&s).unwrap().contains(&command));
    assert!(!core.legal(&s).unwrap().contains(&Command{target_id:id,..command.clone()}));
    assert!(core.apply(&s,Command{target_id:id,..command.clone()},s.revision,false).is_err());
    let after=core.apply(&s,command,s.revision,false).unwrap();assert_eq!(after.cards.iter().find(|c|c.id==eid).unwrap().defense,1);
}
#[test]
fn suppress_disables_death_binding_and_removes_recorded_buffs(){
    let mut defs=base();defs.push(scripted("supply",Trigger::Death,vec![Effect::Draw{side:RelativeSide::Friendly,count:Number::Constant(1)}]));
    defs.push(scripted("suppressor",Trigger::Deploy,vec![Effect::Suppress{target:Target::All{query:query(RelativeSide::Enemy,Area::Board)}},Effect::Damage{target:Target::All{query:query(RelativeSide::Enemy,Area::Board)},amount:Number::Constant(2)}]));
    let core=Core::new(defs,None).unwrap();let(mut s,id)=setup(&core,"suppressor");
    let enemy=s.cards.iter_mut().find(|c|c.side==Side::Right&&c.definition=="card_unit_12th_infantry_regiment_tut").unwrap();
    enemy.definition="supply".into();enemy.zone=Zone::Support;enemy.attack=5;enemy.defense=5;enemy.script=Some(CardScript{suppressed:false,attack_buff:4,defense_buff:3,..CardScript::default()});let eid=enemy.id;s.players[1].deck.retain(|i|*i!=eid);
    let deck_count=s.players[1].deck.len();let after=deploy(&core,&s,id).unwrap();let c=after.cards.iter().find(|c|c.id==eid).unwrap();
    assert_eq!(c.zone,Zone::Discard);assert_eq!(c.attack,1);assert!(c.suppressed());assert_eq!(after.players[1].deck.len(),deck_count);
}
