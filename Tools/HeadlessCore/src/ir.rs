use serde::{Deserialize,Serialize};
use crate::model::*;

#[derive(Clone,Copy,Debug,PartialEq,Eq,Serialize,Deserialize)]
#[serde(rename_all="snake_case")]
pub enum Trigger {Deploy,EnterPlay,StartTurn,EndTurn,AfterAttack,Damage,Death,OtherDeath,FrontlineChanged}
#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Binding {pub on:Trigger,pub effects:Vec<Effect>,#[serde(default,skip_serializing_if="Option::is_none")]pub active_in:Option<Area>}
#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Aura {pub target:Target,pub attack:Number}
#[derive(Clone,Copy,Debug,PartialEq,Eq,Serialize,Deserialize)]
#[serde(rename_all="snake_case")]
pub enum RelativeSide {Friendly,Enemy,Any}
#[derive(Clone,Copy,Debug,PartialEq,Eq,Serialize,Deserialize)]
#[serde(rename_all="snake_case")]
pub enum Area {Board,Hand,Deck,Support,Front,Discard}
#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Query {
    pub side:RelativeSide,pub area:Area,
    #[serde(default)]pub units_only:bool,
    #[serde(default)]pub exclude_source:bool,
    #[serde(default,skip_serializing_if="Option::is_none")]pub kind:Option<Kind>,
    #[serde(default,skip_serializing_if="Option::is_none")]pub max_attack:Option<i32>,
    #[serde(default,skip_serializing_if="Option::is_none")]pub cost:Option<i32>,
    #[serde(default,skip_serializing_if="Option::is_none")]pub faction:Option<String>,
    #[serde(default,skip_serializing_if="Option::is_none")]pub card:Option<String>,
    #[serde(default,skip_serializing_if="Option::is_none")]pub adjacent_to:Option<Box<Target>>,
    #[serde(default,skip_serializing_if="std::ops::Not::not")]pub air_only:bool,
}
#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(tag="kind",rename_all="snake_case",deny_unknown_fields)]
pub enum Target {Source,Subject,Chosen,Bound,Created,Saved,All{query:Query},Random{query:Query}}
#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(untagged)]
pub enum Number {Constant(i32),Expression(Expression)}
#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(tag="expr",rename_all="snake_case",deny_unknown_fields)]
pub enum Expression {Count{query:Query},Attack{target:Box<Target>},EventAmount,Add{left:Box<Number>,right:Box<Number>},Multiply{left:Box<Number>,right:Box<Number>}}
#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(tag="test",rename_all="snake_case",deny_unknown_fields)]
pub enum Condition {Any{query:Query},Exists{target:Target},LessOrEqual{left:Number,right:Number},Not{condition:Box<Condition>},And{conditions:Vec<Condition>},SubjectMatches{query:Query},FrontlineOwned,Pinned{target:Target}}
#[derive(Clone,Debug,PartialEq,Serialize,Deserialize)]
#[serde(tag="op",rename_all="snake_case",deny_unknown_fields)]
pub enum Effect {
    Damage{target:Target,amount:Number},Destroy{target:Target},Modify{target:Target,attack:Number,defense:Number},
    Draw{side:RelativeSide,count:Number},DiscardRandom{side:RelativeSide,count:Number},
    Create{card:String,side:RelativeSide,zone:Zone},Suppress{target:Target},
    Kredits{side:RelativeSide,amount:Number},Slots{side:RelativeSide,amount:Number},
    If{condition:Condition,then:Vec<Effect>,#[serde(default)]otherwise:Vec<Effect>},
    ForEach{query:Query,effects:Vec<Effect>},Repeat{count:Number,effects:Vec<Effect>},
    Schedule{turns:i32,effects:Vec<Effect>},OperationCost{target:Target,amount:i32},
    DrawSelected{query:Query,count:i32},PlayFree{target:Target,zone:Zone},Pin{target:Target},
    Bind{target:Target,effects:Vec<Effect>},
    Capture{target:Target,effects:Vec<Effect>},
}

pub fn validate_effects(effects:&[Effect],depth:usize,nodes:&mut usize)->Result<()> {
    if depth>24{return Err("Effect graph nesting exceeds 24".into());}
    for e in effects {
        *nodes+=1;if *nodes>4096{return Err("Effect graph exceeds 4096 nodes".into());}
        match e {
            Effect::If{then,otherwise,..}=>{validate_effects(then,depth+1,nodes)?;validate_effects(otherwise,depth+1,nodes)?;},
            Effect::ForEach{effects,..}|Effect::Repeat{effects,..}|Effect::Bind{effects,..}|Effect::Capture{effects,..}=>validate_effects(effects,depth+1,nodes)?,
            Effect::Schedule{turns,effects}=>{if !(1..=200).contains(turns){return Err("Schedule delay must be 1..200".into());}validate_effects(effects,depth+1,nodes)?;},
            Effect::DrawSelected{count,..} if !(0..=200).contains(count)=>return Err("Selected draw count exceeds 200".into()),
            Effect::PlayFree{zone,..} if !matches!(zone,Zone::Support|Zone::Front)=>return Err("Free play supports support/front".into()),
            Effect::Create{side:RelativeSide::Any,..}|Effect::Draw{side:RelativeSide::Any,..}|Effect::DiscardRandom{side:RelativeSide::Any,..}
                |Effect::Kredits{side:RelativeSide::Any,..}|Effect::Slots{side:RelativeSide::Any,..}=>return Err("Operation requires friendly or enemy side".into()),
            Effect::Create{zone,..} if !matches!(zone,Zone::Hand|Zone::Support|Zone::Front)=>return Err("Create supports hand/support/front".into()),
            _=>{},
        }
    }
    Ok(())
}
