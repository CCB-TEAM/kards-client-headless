use std::collections::{BTreeMap,HashSet,VecDeque};
use serde::{Deserialize,Serialize};
use serde_json::{json,Value};
use sha2::{Digest,Sha256};
use crate::{ir::*,model::*};
use crate::advanced;

pub struct Core {pub defs:BTreeMap<String,Definition>,pub hash:String}
pub fn hash<T:Serialize>(value:&T)->String {format!("{:X}",Sha256::digest(serde_json::to_vec(value).unwrap()))}
impl Core {
    pub fn new(definitions:Vec<Definition>,supplied_hash:Option<String>)->Result<Self> {
        let mut defs=BTreeMap::new();
        for d in definitions {
            if d.id.is_empty() || !(0..=100000).contains(&d.cost) || !(0..=100000).contains(&d.operation_cost) || !(0..=100000).contains(&d.attack) || !(0..=100000).contains(&d.defense) || (d.kind!=Kind::Order && d.defense==0) || defs.contains_key(&d.id) {return Err(format!("Invalid/duplicate card {}",d.id));}
            if let Some(bindings)=&d.effects {
                if bindings.len()>64{return Err("Too many trigger bindings".into());}
                let mut nodes=0;for b in bindings {validate_effects(&b.effects,0,&mut nodes)?;}
            }
            defs.insert(d.id.clone(),d);
        }
        if defs.is_empty() || defs.len()>10000{return Err("Catalog must contain 1..10000 definitions".into());}
        fn refs(effects:&[Effect],defs:&BTreeMap<String,Definition>)->Result<()> {
            for e in effects {match e {
                Effect::Create{card,..}=>{if !defs.contains_key(card){return Err(format!("Create references undefined card {card}"));}},
                Effect::If{then,otherwise,..}=>{refs(then,defs)?;refs(otherwise,defs)?;},
                Effect::ForEach{effects,..}|Effect::Repeat{effects,..}|Effect::Schedule{effects,..}|Effect::Bind{effects,..}|Effect::Capture{effects,..}=>refs(effects,defs)?,_=>{}
            }}Ok(())
        }
        for d in defs.values(){if let Some(bs)=&d.effects{for b in bs{refs(&b.effects,&defs)?;}}}
        let fingerprint=hash(&serde_json::to_value(defs.values().collect::<Vec<_>>()).map_err(|e|e.to_string())?);
        if supplied_hash.as_ref().is_some_and(|expected|expected!=&fingerprint){return Err("Catalog fingerprint does not match definitions".into());}
        Ok(Self{defs,hash:fingerprint})
    }
    pub fn def<'a>(&'a self,c:&Card)->Result<&'a Definition> {self.defs.get(&c.definition).ok_or_else(||format!("Unsupported card {}",c.definition))}
    pub fn new_game(&self,setup:Setup)->Result<State> {
        let mut s=State{schema_version:2,rules_version:RULES.into(),catalog_hash:self.hash.clone(),seed:setup.seed,logic_random:Some(RandomState::new(setup.seed)),revision:0,turn:1,active:Side::Left,outcome:None,players:[Player::default(),Player::default()],cards:vec![],battle:None};
        let demo=vec!["card_unit_12th_infantry_regiment_tut","card_unit_121st_regiment_tut","card_unit_panzer_35_t_tut","card_unit_12th_infantry_regiment_tut","card_unit_t_34","card_unit_panzer_35_t_tut","card_unit_121st_regiment_tut","card_unit_t_34","card_unit_12th_infantry_regiment_tut","card_unit_panzer_35_t_tut","card_unit_121st_regiment_tut","card_unit_t_34"].into_iter().map(str::to_owned).collect::<Vec<_>>();
        for side in [Side::Left,Side::Right] {
            let hq=setup.headquarters.as_ref().map(|h|h[side.index()].as_str()).unwrap_or(if side==Side::Left{"card_location_london"}else{"card_location_berlin"});
            let id=s.cards.len()as i32+1;s.cards.push(self.card(id,hq,side,Zone::Support)?);
            let deck=if side==Side::Left{setup.left_deck.as_ref()}else{setup.right_deck.as_ref()}.unwrap_or(&demo);
            if deck.is_empty()||deck.len()>200{return Err("Test deck must contain 1..200 cards".into());}
            for key in deck {
                let id=s.cards.len()as i32+1;let c=self.card(id,key,side,Zone::Deck)?;
                if self.def(&c)?.kind==Kind::Hq{return Err("HQ cannot be in deck".into());}
                s.cards.push(c);s.players[side.index()].deck.push(id);
            }
            let mut ids=s.players[side.index()].deck.clone();s.logic_random.as_mut().unwrap().shuffle(&mut ids);s.players[side.index()].deck=ids;
            for _ in 0..if side==Side::Left{4}else{5}{if !s.players[side.index()].deck.is_empty(){self.draw(&mut s,side)?;}}
        }
        if self.defs.values().any(|d|d.combat.is_some()||d.auras.is_some()||d.native_code.is_some())||setup.rng_profile.as_deref()==Some("client"){advanced::initialize(&mut s);}
        match setup.rng_profile.as_deref(){None|Some("reference")=>{},Some("client")=>{s.battle.as_mut().unwrap().client_random=Some(ClientRandom{initial_seed:setup.seed,seed:setup.seed as u32,draws:0});},_=>return Err("Unknown RNG profile".into())}
        self.start(&mut s,false)?;Ok(s)
    }
    fn card(&self,id:i32,key:&str,side:Side,zone:Zone)->Result<Card> {
        let d=self.defs.get(key).ok_or_else(||format!("Unsupported card {key}"))?;
        Ok(Card{id,definition:key.into(),side,zone,attack:d.attack,defense:d.defense,moves:0,attacks:0,script:None})
    }
    fn validate(&self,s:&State)->Result<()> {
        if s.schema_version!=2 || s.rules_version!=RULES || s.catalog_hash!=self.hash{return Err("Incompatible state/catalog".into());}
        if s.cards.len()>10000 || s.turn<1 || s.revision<0{return Err("Invalid state bounds".into());}
        if let Some(r)=&s.logic_random{r.validate()?;}
        let mut ids=HashSet::new();
        for c in &s.cards {self.def(c)?;if c.id<=0||!ids.insert(c.id){return Err("Duplicate/invalid card id".into());}}
        for side in [Side::Left,Side::Right]{if s.cards.iter().filter(|c|c.side==side&&self.defs[&c.definition].kind==Kind::Hq).count()!=1{return Err("Require one HQ per side".into());}}
        Ok(())
    }
    pub fn legal(&self,s:&State)->Result<Vec<Command>> {
        self.validate(s)?;if s.outcome.is_some(){return Ok(vec![]);}
        let mut prepared=s.clone();self.refresh(&mut prepared)?;let s=&prepared;
        let side=s.active;let money=s.players[side.index()].kredits;let mut result=vec![];
        let mut cards=s.cards.iter().filter(|c|c.side==side).collect::<Vec<_>>();cards.sort_by_key(|c|c.id);
        for c in cards {
            let d=self.def(c)?;if d.kind==Kind::Hq{continue;}
            if c.zone==Zone::Hand&&money>=d.cost {
                if d.kind==Kind::Order || s.cards.iter().filter(|t|t.side==side&&t.zone==Zone::Support).count()<5 {
                    if let Some(q)=&d.targeting {
                        let context=Context::source(c.id);
                        let targets=select(self,s,q,&context)?;
                        if targets.is_empty()&&d.targeting_optional{result.push(Command{side,kind:ActionKind::Deploy,card_id:c.id,target_id:0});}
                        for target in targets {result.push(Command{side,kind:ActionKind::Deploy,card_id:c.id,target_id:target});}
                    }else{result.push(Command{side,kind:ActionKind::Deploy,card_id:c.id,target_id:0});}
                }
            }
            if !c.board() || c.defense<=0 || money<advanced::cost(self,c) || d.kind==Kind::Order || advanced::pinned(c){continue;}
            if c.zone==Zone::Support && c.moves>0 && !matches!(d.kind,Kind::Fighter|Kind::Bomber) && s.cards.iter().filter(|t|t.zone==Zone::Front).count()<5 && !s.cards.iter().any(|t|t.zone==Zone::Front&&t.side!=side){result.push(Command{side,kind:ActionKind::Move,card_id:c.id,target_id:0});}
            if c.attacks<=0||c.attack<=0{continue;}
            let mut targets=s.cards.iter().filter(|t|t.side!=side&&t.board()&&t.defense>0).collect::<Vec<_>>();targets.sort_by_key(|t|t.id);
            for t in targets {if advanced::can_attack(self,s,c,t){result.push(Command{side,kind:ActionKind::Attack,card_id:c.id,target_id:t.id});}}
        }
        result.push(Command{side,kind:ActionKind::EndTurn,card_id:0,target_id:0});Ok(result)
    }
    pub fn apply(&self,before:&State,command:Command,revision:i32,defer:bool)->Result<State> {self.apply_at(before,command,revision,defer,None)}
    pub fn apply_at(&self,before:&State,command:Command,revision:i32,defer:bool,insertion:Option<i32>)->Result<State> {
        if before.revision!=revision{return Err("Stale revision".into());}
        if !self.legal(before)?.contains(&command){return Err("Illegal action".into());}
        let mut s=before.clone();self.refresh(&mut s)?;
        if command.kind==ActionKind::EndTurn {
            self.event(&mut s,Event::global(Trigger::EndTurn,before.active))?;
            s.active=s.active.other();s.turn=s.turn.checked_add(1).ok_or("Turn overflow")?;if !defer{self.start(&mut s,true)?;}
        }else{
            let i=index(&s,command.card_id)?;let d=self.def(&s.cards[i])?.clone();
            s.players[s.active.index()].kredits-=if command.kind==ActionKind::Deploy{d.cost}else{advanced::cost(self,&s.cards[i])};
            match command.kind {
                ActionKind::Deploy=>{
                    advanced::place(&mut s,command.card_id,if d.kind==Kind::Order{Zone::Discard}else{Zone::Support},if d.kind==Kind::Order{None}else{insertion})?;
                    let ready=d.blitz&&!s.cards[i].suppressed();s.cards[i].moves=if ready{1}else{0};s.cards[i].attacks=if ready{advanced::operations(self,&s.cards[i])}else{0};
                    self.refresh(&mut s)?;
                    self.event(&mut s,Event::local(Trigger::Deploy,command.card_id,Some(command.card_id),Some(command.target_id),0))?;
                    if d.kind!=Kind::Order{self.event(&mut s,Event::local(Trigger::EnterPlay,command.card_id,Some(command.card_id),None,0))?;}
                },
                ActionKind::Move=>{let vacant=!s.cards.iter().any(|c|c.zone==Zone::Front);advanced::place(&mut s,command.card_id,Zone::Front,insertion)?;s.cards[i].moves-=1;if d.kind!=Kind::Tank{s.cards[i].attacks=0;}
                    self.refresh(&mut s)?;
                    if vacant{self.event(&mut s,Event::global(Trigger::FrontlineChanged,before.active))?;}},
                ActionKind::Attack=>{
                    let j=index(&s,command.target_id)?;let attack=s.cards[i].attack;
                    let retaliation=if (d.kind==Kind::Bomber&&self.def(&s.cards[j])?.kind!=Kind::Fighter)||advanced::pinned(&s.cards[j]){0}else{s.cards[j].attack};
                    s.cards[j].defense-=attack;s.cards[i].defense-=retaliation;s.cards[i].attacks-=1;if d.kind!=Kind::Tank{s.cards[i].moves=0;}
                    if d.combat.as_ref().is_some_and(|c|c.smokescreen){s.cards[i].script.get_or_insert_with(CardScript::default).revealed=true;}
                    let mut tx=Transaction::new(self,&mut s);
                    tx.events.push_back(Event::local(Trigger::Damage,command.card_id,Some(command.target_id),None,attack));
                    if retaliation>0{tx.events.push_back(Event::local(Trigger::Damage,command.target_id,Some(command.card_id),None,retaliation));}
                    tx.deaths(command.card_id)?;tx.run()?;
                    self.event(&mut s,Event::local(Trigger::AfterAttack,command.card_id,Some(command.target_id),None,0))?;
                },_=>{}
            }
            self.refresh(&mut s)?;self.finish(&mut s)?;
        }
        s.revision=s.revision.checked_add(1).ok_or("Revision overflow")?;Ok(s)
    }
    pub fn begin(&self,before:&State,revision:i32,draw:bool)->Result<State>{
        if before.revision!=revision || before.outcome.is_some(){return Err("Invalid pending turn".into());}
        self.validate(before)?;let mut s=before.clone();self.start(&mut s,draw)?;s.revision=s.revision.checked_add(1).ok_or("Revision overflow")?;Ok(s)
    }
    fn start(&self,s:&mut State,draw:bool)->Result<()> {
        let p=&mut s.players[s.active.index()];p.max_kredits=(p.max_kredits+1).min(12);p.kredits=p.max_kredits;
        if let Some(b)=s.battle.as_mut(){b.spawn_turn=s.turn;b.spawn_count=0;}
        for c in &mut s.cards{if let Some(m)=c.script.as_mut(){m.pinned_turns=(m.pinned_turns-1).max(0);}if c.side==s.active&&c.board()&&self.defs[&c.definition].kind!=Kind::Hq{c.moves=1;c.attacks=advanced::operations(self,c);}}
        if draw{self.draw(s,s.active)?;}
        self.event(s,Event::global(Trigger::StartTurn,s.active))?;
        let delayed=if let Some(b)=s.battle.as_mut(){let pending=std::mem::take(&mut b.delayed);let(due,later):(Vec<_>,Vec<_>)=pending.into_iter().partition(|e|e.due<=s.turn);b.delayed=later;due}else{vec![]};
        for entry in delayed {let mut tx=Transaction::new(self,s);tx.execute(&entry.effects,&Context::source(entry.source),0)?;tx.run()?;}
        self.refresh(s)
    }
    fn draw(&self,s:&mut State,side:Side)->Result<()> {
        if s.players[side.index()].deck.is_empty(){
            s.players[side.index()].fatigue+=1;let fatigue=s.players[side.index()].fatigue;
            let i=s.cards.iter().position(|c|c.side==side&&self.defs[&c.definition].kind==Kind::Hq).ok_or("Missing HQ")?;
            s.cards[i].defense-=fatigue;return self.finish(s);
        }
        let id=s.players[side.index()].deck.remove(0);let full=s.cards.iter().filter(|c|c.side==side&&c.zone==Zone::Hand).count()>=9;
        advanced::place(s,id,if full{Zone::Discard}else{Zone::Hand},None)
    }
    fn finish(&self,s:&mut State)->Result<()> {
        let dead=|side|s.cards.iter().find(|c|c.side==side&&self.defs[&c.definition].kind==Kind::Hq).map(|c|c.defense<=0).ok_or("Missing HQ".to_string());
        s.outcome=match(dead(Side::Left)?,dead(Side::Right)?){(true,true)=>Some("draw".into()),(true,false)=>Some("right".into()),(false,true)=>Some("left".into()),_=>None};Ok(())
    }
    fn refresh(&self,s:&mut State)->Result<()> {
        if !self.defs.values().any(|d|d.auras.is_some()){return Ok(());}
        for c in &mut s.cards {if let Some(m)=c.script.as_mut(){c.attack=c.attack.checked_sub(m.aura_attack).ok_or("Aura overflow")?;m.aura_attack=0;}}
        let mut grants=std::collections::BTreeMap::<i32,i32>::new();
        let sources=s.cards.iter().filter(|c|c.board()&&c.defense>0&&!c.suppressed()).map(|c|c.id).collect::<Vec<_>>();
        let mut tx=Transaction::new(self,s);
        for id in sources {
            let auras=self.def(&tx.state.cards[index(tx.state,id)?])?.auras.clone().unwrap_or_default();
            let ctx=Context::source(id);
            for aura in auras {
                let amount=tx.number(&aura.attack,&ctx,0)?;
                if !(0..=100000).contains(&amount){return Err("Invalid aura amount".into());}
                for target in tx.targets(&aura.target,&ctx,false)? {*grants.entry(target).or_default()+=amount;}
            }
        }
        for(id,amount)in grants {let i=index(tx.state,id)?;let c=&mut tx.state.cards[i];c.attack=c.attack.checked_add(amount).ok_or("Aura overflow")?;c.script.get_or_insert_with(CardScript::default).aura_attack=amount;}
        Ok(())
    }
    fn event(&self,s:&mut State,event:Event)->Result<()>{let mut tx=Transaction::new(self,s);tx.events.push_back(event);tx.run()}
    pub fn think(&self,s:&State)->Result<Option<Command>> {
        let legal=self.legal(s)?;let mut best=None;let mut value=f64::NEG_INFINITY;
        for a in legal {
            let score=if a.kind==ActionKind::EndTurn{-1000.0}else{
                let c=&s.cards[index(s,a.card_id)?];
                match a.kind{
                    ActionKind::Deploy=>10.0+c.attack as f64+c.defense as f64*0.5,
                    ActionKind::Move=>20.0+c.attack as f64,
                    _=>{let t=&s.cards[index(s,a.target_id)?];if self.def(t)?.kind==Kind::Hq{if t.defense<=c.attack{100000.0}else{50.0+c.attack as f64}}else{
                        let mut score=(c.attack.min(t.defense)-t.attack.min(c.defense))as f64;
                        if c.attack>=t.defense{score+=25.0+self.def(t)?.cost as f64;}if t.attack>=c.defense{score-=20.0+self.def(c)?.cost as f64;}score
                    }}
                }
            };
            if score>value{value=score;best=Some(a);}
        }
        Ok(best)
    }
}
fn index(s:&State,id:i32)->Result<usize>{s.cards.iter().position(|c|c.id==id).ok_or_else(||format!("Unknown card id {id}"))}

#[derive(Clone)]
struct Event {kind:Trigger,source:i32,subject:Option<i32>,chosen:Option<i32>,amount:i32,side:Option<Side>,dying:Vec<i32>}
impl Event {
    fn global(kind:Trigger,side:Side)->Self{Self{kind,source:0,subject:None,chosen:None,amount:0,side:Some(side),dying:vec![]}}
    fn local(kind:Trigger,source:i32,subject:Option<i32>,chosen:Option<i32>,amount:i32)->Self{Self{kind,source,subject,chosen,amount,side:None,dying:vec![]}}
}
#[derive(Clone)]
struct Context {source:i32,subject:Option<i32>,chosen:Option<i32>,bound:Option<i32>,created:Option<i32>,saved:Vec<i32>,amount:i32}
impl Context{fn source(source:i32)->Self{Self{source,subject:None,chosen:None,bound:None,created:None,saved:vec![],amount:0}}}
fn select(core:&Core,state:&State,q:&Query,ctx:&Context)->Result<Vec<i32>> {
    let source=&state.cards[index(state,ctx.source)?];
    let adjacent=if let Some(target)=&q.adjacent_to{let id=match target.as_ref(){Target::Source=>Some(ctx.source),Target::Subject=>ctx.subject,Target::Chosen=>ctx.chosen,Target::Bound=>ctx.bound,Target::Created=>ctx.created,_=>return Err("Adjacent anchor must be a bound card".into())};Some(if let Some(id)=id{advanced::adjacent(state,id)?}else{vec![]})}else{None};
    let mut ids=state.cards.iter().filter(|c|{
        let d=&core.defs[&c.definition];
        let side=match q.side{RelativeSide::Any=>true,RelativeSide::Friendly=>c.side==source.side,RelativeSide::Enemy=>c.side!=source.side};
        let zone=match q.area{Area::Board=>c.board(),Area::Hand=>c.zone==Zone::Hand,Area::Deck=>c.zone==Zone::Deck,Area::Support=>c.zone==Zone::Support,Area::Front=>c.zone==Zone::Front,Area::Discard=>c.zone==Zone::Discard};
        side&&zone&&(!q.units_only||!matches!(d.kind,Kind::Hq|Kind::Order))&&(!q.exclude_source||c.id!=source.id)
            &&q.kind.is_none_or(|k|k==d.kind)&&q.max_attack.is_none_or(|a|c.attack<=a)&&q.cost.is_none_or(|cost|d.cost==cost)
            &&q.faction.as_ref().is_none_or(|f|d.faction.as_ref()==Some(f))&&q.card.as_ref().is_none_or(|id|&c.definition==id)
            &&(!q.air_only||matches!(d.kind,Kind::Fighter|Kind::Bomber))&&adjacent.as_ref().is_none_or(|ids|ids.contains(&c.id))
    }).map(|c|c.id).collect::<Vec<_>>();
    if state.battle.as_ref().and_then(|b|b.client_random.as_ref()).is_none(){ids.sort();}Ok(ids)
}
struct Transaction<'a>{core:&'a Core,state:&'a mut State,events:VecDeque<Event>,fuel:usize,event_depth:usize}
impl<'a> Transaction<'a> {
    fn new(core:&'a Core,state:&'a mut State)->Self{Self{core,state,events:VecDeque::new(),fuel:10000,event_depth:0}}
    // Complete reactions to this instruction before the next instruction runs.
    // Keep already pending events behind those reactions, with a bounded stack.
    fn reactions(&mut self)->Result<()> {
        if self.event_depth>=64{return Err("Effect event recursion budget exhausted; transaction discarded".into());}
        self.event_depth+=1;let result=self.run();self.event_depth-=1;result
    }
    fn tick(&mut self)->Result<()>{if self.fuel==0{Err("Effect instruction/event budget exhausted; transaction discarded".into())}else{self.fuel-=1;Ok(())}}
    fn run(&mut self)->Result<()> {
        while let Some(event)=self.events.pop_front(){
            self.tick()?;
            self.core.refresh(self.state)?;
            let mut sources=self.state.cards.iter().filter(|c|{
                if c.suppressed(){return false;}
                match event.kind{
                    Trigger::StartTurn|Trigger::EndTurn=>c.board()&&Some(c.side)==event.side,
                    Trigger::FrontlineChanged=>true,
                    Trigger::OtherDeath=>c.id!=event.subject.unwrap_or(0)&&(c.board()||event.dying.contains(&c.id)),
                    _=>c.id==event.source,
                }
            }).map(|c|c.id).collect::<Vec<_>>();sources.sort();
            for source in sources {
                let i=index(self.state,source)?;
                let bindings=self.core.def(&self.state.cards[i])?.effects.clone().unwrap_or_default();
                for binding in bindings {if binding.on==event.kind {
                    if let Some(area)=binding.active_in {let zone=self.state.cards[i].zone;let active=match area{Area::Board=>self.state.cards[i].board(),Area::Hand=>zone==Zone::Hand,Area::Deck=>zone==Zone::Deck,Area::Support=>zone==Zone::Support,Area::Front=>zone==Zone::Front,Area::Discard=>zone==Zone::Discard};if !active{continue;}}
                    else if event.kind==Trigger::FrontlineChanged&&!self.state.cards[i].board(){continue;}
                    let context=Context{source,subject:event.subject,chosen:event.chosen.filter(|v|*v>0),bound:None,created:None,saved:vec![],amount:event.amount};
                    self.execute(&binding.effects,&context,0).map_err(|e|format!("card {} trigger {:?}: {e}",self.state.cards[i].definition,event.kind))?;
                }}
            }
        }
        self.core.refresh(self.state)?;self.core.finish(self.state)
    }
    fn side(&self,relative:RelativeSide,ctx:&Context)->Result<Side>{let side=self.state.cards[index(self.state,ctx.source)?].side;match relative{RelativeSide::Friendly=>Ok(side),RelativeSide::Enemy=>Ok(side.other()),RelativeSide::Any=>Err("Ambiguous operation side".into())}}
    fn query(&self,q:&Query,ctx:&Context)->Result<Vec<i32>> {
        select(self.core,self.state,q,ctx)
    }
    fn targets(&mut self,t:&Target,ctx:&Context,allow_random:bool)->Result<Vec<i32>> {
        match t {
            Target::Saved=>Ok(ctx.saved.clone()),
            Target::Source=>Ok(vec![ctx.source]),Target::Subject=>Ok(ctx.subject.into_iter().collect()),Target::Chosen=>Ok(ctx.chosen.into_iter().collect()),Target::Bound=>Ok(ctx.bound.into_iter().collect()),Target::Created=>Ok(ctx.created.into_iter().collect()),
            Target::All{query}=>self.query(query,ctx),
            Target::Random{query}=>{
                if !allow_random{return Err("Random target is not allowed inside a condition/number expression".into());}
                let ids=self.query(query,ctx)?;if ids.is_empty(){return Ok(vec![]);}
                Ok(advanced::choose(self.state,&ids)?.into_iter().collect())
            }
        }
    }
    fn number(&mut self,n:&Number,ctx:&Context,depth:usize)->Result<i32> {
        self.tick()?;if depth>24{return Err("Expression nesting exceeds 24".into());}
        match n {
            Number::Constant(v)=>Ok(*v),Number::Expression(e)=>match e {
                Expression::Count{query}=>Ok(self.query(query,ctx)?.len()as i32),Expression::EventAmount=>Ok(ctx.amount),
                Expression::Attack{target}=>{let ids=self.targets(target,ctx,false)?;if ids.len()!=1{return Err("Attack expression requires exactly one target".into());}Ok(self.state.cards[index(self.state,ids[0])?].attack)},
                Expression::Add{left,right}=>self.number(left,ctx,depth+1)?.checked_add(self.number(right,ctx,depth+1)?).ok_or("Expression overflow".into()),
                Expression::Multiply{left,right}=>self.number(left,ctx,depth+1)?.checked_mul(self.number(right,ctx,depth+1)?).ok_or("Expression overflow".into())
            }
        }
    }
    fn condition(&mut self,c:&Condition,ctx:&Context,depth:usize)->Result<bool> {
        self.tick()?;if depth>24{return Err("Condition nesting exceeds 24".into());}
        match c {Condition::Any{query}=>Ok(!self.query(query,ctx)?.is_empty()),Condition::LessOrEqual{left,right}=>Ok(self.number(left,ctx,0)?<=self.number(right,ctx,0)?),Condition::Not{condition}=>Ok(!self.condition(condition,ctx,depth+1)?),Condition::And{conditions}=>{for c in conditions {if !self.condition(c,ctx,depth+1)?{return Ok(false);}}Ok(true)},
            Condition::Exists{target}=>Ok(!self.targets(target,ctx,false)?.is_empty()),
            Condition::SubjectMatches{query}=>Ok(ctx.subject.is_some_and(|id|self.query(query,ctx).is_ok_and(|ids|ids.contains(&id)))),
            Condition::FrontlineOwned=>{let side=self.side(RelativeSide::Friendly,ctx)?;Ok(self.state.cards.iter().any(|c|c.zone==Zone::Front&&c.side==side))},
            Condition::Pinned{target}=>{let ids=self.targets(target,ctx,false)?;Ok(ids.into_iter().any(|id|index(self.state,id).is_ok_and(|i|advanced::pinned(&self.state.cards[i]))))}
        }
    }
    fn deaths(&mut self,killer:i32)->Result<()> {
        let dead=self.state.cards.iter().filter(|c|c.board()&&c.defense<=0&&self.core.defs[&c.definition].kind!=Kind::Hq).map(|c|c.id).collect::<Vec<_>>();
        let before=self.state.cards.iter().find(|c|c.zone==Zone::Front).map(|c|c.side);
        for id in &dead {advanced::place(self.state,*id,Zone::Discard,None)?;}
        self.core.refresh(self.state)?;
        for id in &dead {self.events.push_back(Event::local(Trigger::Death,*id,Some(*id),Some(killer),0));let mut event=Event::local(Trigger::OtherDeath,killer,Some(*id),None,0);event.dying=dead.clone();self.events.push_back(event);}
        if before.is_some()&&!self.state.cards.iter().any(|c|c.zone==Zone::Front){self.events.push_back(Event::global(Trigger::FrontlineChanged,self.state.active));}
        Ok(())
    }
    fn execute(&mut self,effects:&[Effect],ctx:&Context,depth:usize)->Result<()> {
        if depth>24{return Err("Runtime effect nesting exceeds 24".into());}
        let mut context=ctx.clone();let ctx=&mut context;
        for effect in effects {
            self.tick()?;
            let pending=std::mem::take(&mut self.events);
            match effect {
                Effect::Damage{target,amount}=>{
                    let amount=self.number(amount,ctx,0)?;if !(0..=100000).contains(&amount){return Err("Invalid damage amount".into());}
                    let ids=self.targets(target,ctx,true)?;
                    for id in ids {let i=index(self.state,id)?;if !self.state.cards[i].board(){continue;}self.state.cards[i].defense-=amount;
                        if amount>0{self.events.push_back(Event::local(Trigger::Damage,ctx.source,Some(id),None,amount));}}
                    self.deaths(ctx.source)?;
                },
                Effect::Destroy{target}=>{for id in self.targets(target,ctx,true)?{let i=index(self.state,id)?;if self.state.cards[i].board(){self.state.cards[i].defense=0;}}self.deaths(ctx.source)?;},
                Effect::Modify{target,attack,defense}=>{
                    let a=self.number(attack,ctx,0)?;let d=self.number(defense,ctx,0)?;
                    if a.unsigned_abs()>10000||d.unsigned_abs()>10000{return Err("Invalid stat modifier".into());}
                    for id in self.targets(target,ctx,true)?{
                        let at=index(self.state,id)?;let c=&mut self.state.cards[at];c.attack=c.attack.checked_add(a).ok_or("Attack overflow")?.max(0);c.defense=c.defense.checked_add(d).ok_or("Defense overflow")?;
                        let script=c.script.get_or_insert_with(CardScript::default);script.attack_buff=script.attack_buff.checked_add(a).ok_or("Attack buff overflow")?;script.defense_buff=script.defense_buff.checked_add(d).ok_or("Defense buff overflow")?;
                    }self.deaths(ctx.source)?;
                },
                Effect::Suppress{target}=>{for id in self.targets(target,ctx,true)?{
                    let i=index(self.state,id)?;let c=&mut self.state.cards[i];let base=&self.core.defs[&c.definition];
                    c.defense=c.defense.min(base.defense);c.attack=base.attack;c.script=Some(CardScript{suppressed:true,..CardScript::default()});
                }self.deaths(ctx.source)?;},
                Effect::Draw{side,count}|Effect::DiscardRandom{side,count}=>{
                    let side=self.side(*side,ctx)?;let count=self.number(count,ctx,0)?;if !(0..=200).contains(&count){return Err("Draw/discard count exceeds 200".into());}
                    for _ in 0..count{self.tick()?;if matches!(effect,Effect::Draw{..}){self.core.draw(self.state,side)?;}else{
                        let mut ids=self.state.cards.iter().filter(|c|c.side==side&&c.zone==Zone::Hand).map(|c|c.id).collect::<Vec<_>>();ids.sort();
                        if self.state.battle.as_ref().and_then(|b|b.client_random.as_ref()).is_some(){ids.sort_by_key(|id|index(self.state,*id).unwrap());}
                        if let Some(id)=advanced::choose(self.state,&ids)?{advanced::place(self.state,id,Zone::Discard,None)?;}
                    }}
                },
                Effect::Create{card,side,zone}=>{
                    ctx.created=None;
                    let side=self.side(*side,ctx)?;
                    let full=match zone{Zone::Hand=>self.state.cards.iter().filter(|c|c.side==side&&c.zone==*zone).count()>=9,Zone::Support=>self.state.cards.iter().filter(|c|c.side==side&&c.zone==*zone).count()>=5,Zone::Front=>self.state.cards.iter().filter(|c|c.zone==Zone::Front).count()>=5||self.state.cards.iter().any(|c|c.zone==Zone::Front&&c.side!=side),_=>true};
                    if !full{
                        let id=advanced::spawned_id(self.state)?;
                        let mut c=self.core.card(id,card,side,Zone::Discard)?;if self.core.def(&c)?.kind==Kind::Hq{return Err("Cannot create an additional HQ".into());}
                        if matches!(zone,Zone::Support|Zone::Front){let ready=self.core.def(&c)?.blitz;c.moves=if ready{1}else{0};c.attacks=if ready{advanced::operations(self.core,&c)}else{0};}
                        self.state.cards.push(c);advanced::place(self.state,id,*zone,None)?;self.core.refresh(self.state)?;ctx.created=Some(id);
                        if matches!(zone,Zone::Support|Zone::Front){self.events.push_back(Event::local(Trigger::EnterPlay,id,Some(id),None,0));}
                    }
                },
                Effect::Kredits{side,amount}|Effect::Slots{side,amount}=>{
                    let side=self.side(*side,ctx)?;let amount=self.number(amount,ctx,0)?;if amount.unsigned_abs()>100{return Err("Invalid resource delta".into());}
                    let p=&mut self.state.players[side.index()];if matches!(effect,Effect::Slots{..}){p.max_kredits=(p.max_kredits+amount).clamp(0,12);p.kredits=p.kredits.min(p.max_kredits);}else{p.kredits=(p.kredits+amount).clamp(0,12);}
                },
                Effect::If{condition,then,otherwise}=>{if self.condition(condition,ctx,0)?{self.execute(then,ctx,depth+1)?;}else{self.execute(otherwise,ctx,depth+1)?;}},
                Effect::ForEach{query,effects}=>{let ids=self.query(query,ctx)?;for id in ids{let mut child=ctx.clone();child.bound=Some(id);self.execute(effects,&child,depth+1)?;}},
                Effect::Repeat{count,effects}=>{let count=self.number(count,ctx,0)?;if !(0..=200).contains(&count){return Err("Repeat count exceeds 200".into());}for _ in 0..count{self.execute(effects,ctx,depth+1)?;}},
                Effect::Bind{target,effects}=>{let mut child=ctx.clone();child.bound=self.targets(target,ctx,true)?.first().copied();self.execute(effects,&child,depth+1)?;},
                Effect::Capture{target,effects}=>{let mut child=ctx.clone();child.saved=self.targets(target,ctx,true)?;self.execute(effects,&child,depth+1)?;},
                Effect::Schedule{turns,effects}=>{if self.state.battle.is_none(){advanced::initialize(self.state);}let due=self.state.turn.checked_add(*turns).ok_or("Turn overflow")?;self.state.battle.as_mut().unwrap().delayed.push(Delayed{due,source:ctx.source,effects:effects.clone()});},
                Effect::OperationCost{target,amount}=>{if amount.unsigned_abs()>100{return Err("Invalid operation modifier".into());}for id in self.targets(target,ctx,true)?{let i=index(self.state,id)?;let m=self.state.cards[i].script.get_or_insert_with(CardScript::default);m.operation_buff=m.operation_buff.checked_add(*amount).ok_or("Operation overflow")?;}},
                Effect::Pin{target}=>{for id in self.targets(target,ctx,true)?{let i=index(self.state,id)?;let turns=if self.state.cards[i].side==self.state.active{3}else{2};let m=self.state.cards[i].script.get_or_insert_with(CardScript::default);m.pinned_turns=m.pinned_turns.max(turns);}},
                Effect::DrawSelected{query,count}=>{for _ in 0..*count{let ids=self.query(query,ctx)?;if let Some(id)=advanced::choose(self.state,&ids)?{let i=index(self.state,id)?;if self.state.cards[i].zone!=Zone::Deck{return Err("Selected draw requires deck cards".into());}let side=self.state.cards[i].side;self.state.players[side.index()].deck.retain(|v|*v!=id);let full=self.state.cards.iter().filter(|c|c.side==side&&c.zone==Zone::Hand).count()>=9;advanced::place(self.state,id,if full{Zone::Discard}else{Zone::Hand},None)?;}}},
                Effect::PlayFree{target,zone}=>{for id in self.targets(target,ctx,true)?{let i=index(self.state,id)?;let side=self.state.cards[i].side;let full=if *zone==Zone::Support{self.state.cards.iter().filter(|c|c.side==side&&c.zone==*zone).count()>=5}else{self.state.cards.iter().filter(|c|c.zone==Zone::Front).count()>=5||self.state.cards.iter().any(|c|c.zone==Zone::Front&&c.side!=side)};if !full{advanced::place(self.state,id,*zone,None)?;let ready=self.core.def(&self.state.cards[i])?.blitz;self.state.cards[i].moves=if ready{1}else{0};self.state.cards[i].attacks=if ready{advanced::operations(self.core,&self.state.cards[i])}else{0};self.events.push_back(Event::local(Trigger::EnterPlay,id,Some(id),None,0));}}self.core.refresh(self.state)?;},
            }
            if !self.events.is_empty(){self.reactions()?;}
            self.events.extend(pending);
        }
        Ok(())
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Request {
    op:String,#[serde(default)]cards:Vec<Definition>,#[serde(default)]catalog_hash:Option<String>,#[serde(default)]state:Option<State>,
    #[serde(default)]setup:Option<Setup>,#[serde(default)]command:Option<Command>,#[serde(default)]expected_revision:Option<i32>,
    #[serde(default)]defer_next_start:bool,#[serde(default)]draw:bool,#[serde(default)]pack:Option<Pack>,
    #[serde(default)]insertion:Option<i32>,
}
fn process(r:Request)->Result<Value> {
    if r.op=="validate_pack" {
        let mut pack=r.pack.ok_or("pack required")?;if pack.version!=1||pack.id.is_empty(){return Err("Unsupported pack version/id".into());}
        let core=Core::new(pack.cards,None)?;pack.cards=core.defs.into_values().collect();return Ok(json!({"pack":pack,"hash":hash(&pack)}));
    }
    let core=Core::new(r.cards,r.catalog_hash)?;
    if r.op=="catalog"{return Ok(json!({"hash":core.hash}));}
    if r.op=="new"{return serde_json::to_value(core.new_game(r.setup.ok_or("setup required")?)?).map_err(|e|e.to_string());}
    let state=r.state.ok_or("state required")?;
    match r.op.as_str(){
        "bootstrap"=>{let mut state=state;core.validate(&state)?;if state.battle.is_none(){advanced::initialize(&mut state);}core.refresh(&mut state)?;Ok(json!(state))},
        "legal"=>Ok(json!(core.legal(&state)?)),"think"=>Ok(json!(core.think(&state)?)),
        "apply"=>Ok(json!(core.apply_at(&state,r.command.ok_or("command required")?,r.expected_revision.ok_or("expected_revision required")?,r.defer_next_start,r.insertion)?)),
        "begin"=>Ok(json!(core.begin(&state,r.expected_revision.ok_or("expected_revision required")?,r.draw)?)),
        _=>Err("Unknown core operation".into()),
    }
}
pub fn request_json(text:&str)->String {
    let result=if text.len()>16*1024*1024{Err("Request too large".into())}else{serde_json::from_str::<Request>(text).map_err(|e|e.to_string()).and_then(process)};
    match result{Ok(value)=>json!({"ok":true,"result":value}).to_string(),Err(error)=>json!({"ok":false,"error":error}).to_string()}
}
