use std::{env,fs,sync::{Arc,atomic::{AtomicUsize,Ordering}}};
use mlua::{Lua,LuaOptions,LuaSerdeExt,StdLib,HookTriggers,VmState};
use headless_core::Pack;

fn run()->Result<(),Box<dyn std::error::Error>> {
    let args=env::args().collect::<Vec<_>>();if args.len()!=3{return Err("usage: card-lua <cards.lua> <pack.json>".into());}
    let lua=Lua::new_with(StdLib::TABLE|StdLib::STRING|StdLib::MATH,LuaOptions::default())?;
    lua.set_memory_limit(16*1024*1024)?;
    let math:mlua::Table=lua.globals().get("math")?;math.set("random",mlua::Value::Nil)?;math.set("randomseed",mlua::Value::Nil)?;
    let fuel=Arc::new(AtomicUsize::new(0));let used=fuel.clone();
    lua.set_hook(HookTriggers::new().every_nth_instruction(1000),move |_,_|{
        if used.fetch_add(1000,Ordering::Relaxed)>=1_000_000{return Err(mlua::Error::RuntimeError("Card authoring instruction budget exceeded".into()));}
        Ok(VmState::Continue)
    })?;
    let text=fs::read_to_string(&args[1])?;
    let value=lua.load(&text).set_name(&args[1]).eval()?;
    let pack:Pack=lua.from_value(value)?;
    let request=serde_json::json!({"op":"validate_pack","pack":pack});
    let reply:serde_json::Value=serde_json::from_str(&headless_core::engine::request_json(&request.to_string()))?;
    if reply["ok"]!=true{return Err(reply["error"].as_str().unwrap_or("Invalid card pack").to_owned().into());}
    fs::write(&args[2],serde_json::to_string_pretty(&reply["result"]["pack"])?+"\n")?;
    println!("{}",serde_json::json!({"exported":args[2],"hash":reply["result"]["hash"],"cards":reply["result"]["pack"]["cards"].as_array().unwrap().len()}));
    Ok(())
}
fn main(){if let Err(error)=run(){eprintln!("{error}");std::process::exit(1);}}
