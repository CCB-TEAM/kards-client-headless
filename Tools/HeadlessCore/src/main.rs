use std::io::{self, BufRead};
fn main() {
    // One JSON request/reply per line; also usable without .NET or a GPU.
    for line in io::stdin().lock().lines() {
        match line {
            Ok(text) => println!("{}",headless_core::engine::request_json(&text)),
            Err(_) => break,
        }
    }
}
