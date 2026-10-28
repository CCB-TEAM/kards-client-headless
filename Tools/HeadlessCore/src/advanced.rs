use crate::{Core,model::*};

pub fn row_key(c:&Card)->(Zone,Side){(c.zone,if matches!(c.zone,Zone::Front|Zone::Discard){Side::Left}else{c.side})}
pub fn position(s:&State,id:i32)->i32 {s.battle.as_ref().and_then(|b|b.positions.get(&id).copied()).unwrap_or(id)}
pub fn initialize(s:&mut State){
    let mut positions=std::collections::BTreeMap::new();
    for c in &s.cards{
        let number=match c.zone{
            Zone::Deck=>s.players[c.side.index()].deck.iter().position(|id|*id==c.id).unwrap_or(0)as i32+if c.side==Side::Left{4}else{5},
            _=>s.cards.iter().filter(|other|row_key(other)==row_key(c)).take_while(|other|other.id!=c.id).count()as i32,
        };positions.insert(c.id,number);
    }
    s.battle=Some(Battle{positions,spawn_turn:s.turn,..Battle::default()});
}
pub fn place(s:&mut State,id:i32,zone:Zone,at:Option<i32>)->Result<()> {
    let index=s.cards.iter().position(|c|c.id==id).ok_or("Unknown placed card")?;
    let old=row_key(&s.cards[index]);s.cards[index].zone=zone;let new=row_key(&s.cards[index]);
    if s.battle.is_none(){return Ok(());}
    if old==new && at.is_none(){return Ok(());}
    let mut ids=s.cards.iter().filter(|c|c.id!=id&&row_key(c)==new).map(|c|c.id).collect::<Vec<_>>();ids.sort_by_key(|id|position(s,*id));
    let target=at.unwrap_or(ids.len()as i32);
    if target<0||target as usize>ids.len(){return Err("Invalid insertion position".into());}
    if matches!(zone,Zone::Support|Zone::Front){
        ids.insert(target as usize,id);for(i,id)in ids.into_iter().enumerate(){s.battle.as_mut().unwrap().positions.insert(id,i as i32);}
    }else{
        let tail=ids.iter().map(|id|position(s,*id)).max().unwrap_or(-1)+1;s.battle.as_mut().unwrap().positions.insert(id,tail);
    }
    if matches!(old.0,Zone::Support|Zone::Front){
        let mut ids=s.cards.iter().filter(|c|row_key(c)==old).map(|c|c.id).collect::<Vec<_>>();ids.sort_by_key(|id|position(s,*id));
        for(i,id)in ids.into_iter().enumerate(){s.battle.as_mut().unwrap().positions.insert(id,i as i32);}
    }
    Ok(())
}
pub fn adjacent(s:&State,id:i32)->Result<Vec<i32>>{
    let c=s.cards.iter().find(|c|c.id==id).ok_or("Unknown adjacent anchor")?;
    if !c.board(){return Ok(vec![]);}
    let mut row=s.cards.iter().filter(|other|row_key(other)==row_key(c)).map(|c|c.id).collect::<Vec<_>>();row.sort_by_key(|id|position(s,*id));
    let at=row.iter().position(|v|*v==id).unwrap();Ok(row.into_iter().enumerate().filter(|(i,_)|i.abs_diff(at)==1).map(|(_,id)|id).collect())
}
pub fn operations(core:&Core,c:&Card)->i32{if !c.suppressed()&&core.defs[&c.definition].combat.as_ref().is_some_and(|m|m.fury){2}else{1}}
pub fn cost(core:&Core,c:&Card)->i32{(core.defs[&c.definition].operation_cost+c.script.as_ref().map_or(0,|s|s.operation_buff)).max(0)}
pub fn pinned(c:&Card)->bool{c.script.as_ref().is_some_and(|s|s.pinned_turns>0)}
pub fn smoke(core:&Core,c:&Card)->bool{!c.suppressed()&&!c.script.as_ref().is_some_and(|s|s.revealed)&&core.defs[&c.definition].combat.as_ref().is_some_and(|m|m.smokescreen)}
pub fn can_attack(core:&Core,s:&State,c:&Card,target:&Card)->bool{
    if smoke(core,target){return false;}
    let kind=core.defs[&c.definition].kind;
    if kind==Kind::Bomber && core.defs[&target.definition].kind!=Kind::Fighter && s.cards.iter().any(|other|other.side==target.side&&row_key(other)==row_key(target)&&other.board()&&other.defense>0&&core.defs[&other.definition].kind==Kind::Fighter){return false;}
    c.zone==Zone::Front||target.zone==Zone::Front||matches!(kind,Kind::Fighter|Kind::Bomber)
}
pub fn choose(s:&mut State,ids:&[i32])->Result<Option<i32>>{
    if ids.is_empty(){return Ok(None);}
    let at=if let Some(r)=s.battle.as_mut().and_then(|b|b.client_random.as_mut()){r.card_index(ids.len())?}
        else{s.logic_random.as_mut().ok_or("Unknown RNG state; cannot execute random effect")?.range(0,ids.len()as i32-1)?as usize};
    Ok(Some(ids[at]))
}
pub fn spawned_id(s:&mut State)->Result<i32>{
    if let Some(b)=s.battle.as_mut(){
        if b.spawn_turn!=s.turn{b.spawn_turn=s.turn;b.spawn_count=0;}
        b.spawn_count+=1;let id=s.turn.checked_mul(1000).and_then(|v|v.checked_add(b.spawn_count)).ok_or("Spawn id overflow")?;
        if s.cards.iter().any(|c|c.id==id){return Err("Native spawned card id collision".into());}Ok(id)
    }else{s.cards.iter().map(|c|c.id).max().unwrap_or(0).checked_add(1).ok_or("Card id overflow".into())}
}
