use serde::{Deserialize,Serialize};
use crate::ir::Binding;

pub const RULES: &str="subset-v0.4-aggro1";
pub const RNG: &str="kards-sim/5067a25321456e191ae4083ce098b8a2e49a31ca/core-v1";
pub type Result<T> = std::result::Result<T,String>;

#[derive(Clone,Copy,Debug,PartialEq,Eq,Serialize,Deserialize)]
#[serde(rename_all="snake_case")]
pub enum Side { Left, Right }
impl Side { pub fn other(self)->Self {if self==Self::Left {Self::Right}else{Self::Left}} pub fn index(self)->usize {if self==Self::Left{0}else{1}} }
#[derive(Clone,Copy,Debug,PartialEq,Eq,Serialize,Deserialize)]
#[serde(rename_all="snake_case")]
pub enum Kind { Hq,Infantry,Tank,Order,Fighter,Bomber }
#[derive(Clone,Copy,Debug,PartialEq,Eq,Serialize,Deserialize)]
#[serde(rename_all="snake_case")]
pub enum Zone { Deck,Hand,Support,Front,Discard }
#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Definition {
    pub id:String,pub title:String,pub kind:Kind,pub cost:i32,pub operation_cost:i32,pub attack:i32,pub defense:i32,pub blitz:bool,
    #[serde(default,skip_serializing_if="Option::is_none")] pub effects:Option<Vec<Binding>>,
    #[serde(default,skip_serializing_if="Option::is_none")] pub targeting:Option<crate::ir::Query>,
    #[serde(default,skip_serializing_if="Option::is_none")] pub faction:Option<String>,
    #[serde(default,skip_serializing_if="Option::is_none")] pub combat:Option<Combat>,
    #[serde(default,skip_serializing_if="Option::is_none")] pub auras:Option<Vec<crate::ir::Aura>>,
    #[serde(default,skip_serializing_if="Option::is_none")] pub native_code:Option<String>,
    #[serde(default,skip_serializing_if="std::ops::Not::not")] pub targeting_optional:bool,
}
#[derive(Clone,Debug,Default,PartialEq,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Combat {#[serde(default)]pub fury:bool,#[serde(default)]pub smokescreen:bool}
#[derive(Clone,Debug,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Card {
    pub id:i32,pub definition:String,pub side:Side,pub zone:Zone,pub attack:i32,pub defense:i32,pub moves:i32,pub attacks:i32,
    #[serde(default,skip_serializing_if="Option::is_none")] pub script:Option<CardScript>,
}
#[derive(Clone,Debug,Default,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CardScript {
    pub suppressed:bool,pub attack_buff:i32,pub defense_buff:i32,
    #[serde(default,skip_serializing_if="zero")]pub aura_attack:i32,
    #[serde(default,skip_serializing_if="zero")]pub operation_buff:i32,
    #[serde(default,skip_serializing_if="zero")]pub pinned_turns:i32,
    #[serde(default,skip_serializing_if="std::ops::Not::not")]pub revealed:bool,
}
fn zero(v:&i32)->bool{*v==0}
impl Card {pub fn board(&self)->bool {matches!(self.zone,Zone::Support|Zone::Front)} pub fn suppressed(&self)->bool {self.script.as_ref().is_some_and(|s|s.suppressed)} }
#[derive(Clone,Debug,Default,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Player {pub kredits:i32,pub max_kredits:i32,pub fatigue:i32,pub deck:Vec<i32>}
#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RandomState {pub profile:String,pub seed:i32,pub s0:u64,pub s1:u64,pub raw_draws:u64}
impl RandomState {
    pub fn new(seed:i32)->Self {
        let mut raw=headless_rng::State {s0:0,s1:0,draws:0};
        unsafe {headless_rng::hrng_init(seed,&mut raw);}
        Self{profile:RNG.into(),seed,s0:raw.s0,s1:raw.s1,raw_draws:raw.draws}
    }
    pub fn validate(&self)->Result<()> { if self.profile!=RNG || self.s0|self.s1==0 || self.raw_draws<8 {Err("Invalid/incompatible random checkpoint".into())}else{Ok(())} }
    fn native(&self)->headless_rng::State {headless_rng::State{s0:self.s0,s1:self.s1,draws:self.raw_draws}}
    fn store(&mut self,s:headless_rng::State) {self.s0=s.s0;self.s1=s.s1;self.raw_draws=s.draws;}
    pub fn next(&mut self,n:i32)->i32 {let mut s=self.native();let mut out=0;unsafe{headless_rng::hrng_next(&mut s,n,&mut out);} self.store(s);out}
    pub fn range(&mut self,min:i32,max:i32)->Result<i32> {self.validate()?;let mut s=self.native();let mut out=0;let code=unsafe{headless_rng::hrng_range(&mut s,min,max,&mut out)};self.store(s);if code==0{Ok(out)}else{Err("Reference range overflow".into())}}
    pub fn shuffle(&mut self,ids:&mut [i32]) {for i in (1..ids.len()).rev(){let j=self.next((i+1)as i32)as usize;ids.swap(i,j);}}
}
#[derive(Clone,Debug,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct State {
    pub schema_version:i32,pub rules_version:String,pub catalog_hash:String,pub seed:i32,pub logic_random:Option<RandomState>,
    pub revision:i32,pub turn:i32,pub active:Side,pub outcome:Option<String>,pub players:[Player;2],pub cards:Vec<Card>,
    #[serde(default,skip_serializing_if="Option::is_none")]pub battle:Option<Battle>,
}
#[derive(Clone,Debug,Default,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Battle {
    pub positions:std::collections::BTreeMap<i32,i32>,pub spawn_turn:i32,pub spawn_count:i32,
    pub delayed:Vec<Delayed>,pub client_random:Option<ClientRandom>,
}
#[derive(Clone,Debug,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Delayed {pub due:i32,pub source:i32,pub effects:Vec<crate::ir::Effect>}
#[derive(Clone,Debug,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ClientRandom {pub initial_seed:i32,pub seed:u32,pub draws:u64}
impl ClientRandom {
    pub fn range(&mut self,min:i32,max:i32)->Result<i32>{let mut value=0;let code=unsafe{headless_rng::hrng_client_range(&mut self.seed,&mut self.draws,min,max,&mut value)};if code==0{Ok(value)}else{Err("Client RNG failure".into())}}
    pub fn card_index(&mut self,count:usize)->Result<usize>{if count==0{return Err("Empty random query".into());}let burn=self.range(1,10)?;for _ in 0..burn{self.range(0,1)?;}Ok(self.range(0,count as i32-1)? as usize)}
}
#[derive(Clone,Copy,Debug,PartialEq,Eq,Serialize,Deserialize)]
#[serde(rename_all="snake_case")]
pub enum ActionKind {Deploy,Move,Attack,EndTurn}
#[derive(Clone,Debug,PartialEq,Eq,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Command {pub side:Side,pub kind:ActionKind,#[serde(default)]pub card_id:i32,#[serde(default)]pub target_id:i32}
#[derive(Clone,Debug,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Setup {
    #[serde(default="one")]pub seed:i32,#[serde(default)]pub left_deck:Option<Vec<String>>,#[serde(default)]pub right_deck:Option<Vec<String>>,
    #[serde(default)]pub headquarters:Option<[String;2]>,#[serde(default)]pub rng_profile:Option<String>,
}
fn one()->i32{1}
#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Pack {pub version:u32,pub id:String,pub cards:Vec<Definition>}
