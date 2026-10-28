//! Zero external dependencies. C ABI v1, caller-owned 24-byte state.
//! Port of the behavior of kards-sim commit 5067a253...; not UE FRandomStream.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct State { pub s0: u64, pub s1: u64, pub draws: u64 }
impl State {
    fn new(seed: i32) -> Self {
        let mut s = Self { s0: (seed as u64).wrapping_mul(0x9e3779b97f4a7c15).wrapping_add(0x243f6a8885a308d3),
            s1: (seed as u64) ^ 0x13198a2e03707344, draws: 0 };
        if s.s0 | s.s1 == 0 { s.s0 = 1; }
        for _ in 0..8 { s.raw(); }
        s
    }
    fn raw(&mut self) -> u64 {
        let x = self.s0;
        let y = self.s1 ^ x;
        let result = self.s0.wrapping_add(self.s1);
        self.s0 = x.rotate_left(55) ^ y ^ (y << 14);
        self.s1 = y.rotate_left(36);
        self.draws = self.draws.wrapping_add(1);
        result
    }
    fn next(&mut self, n: i32) -> i32 {
        if n <= 1 { return 0; }
        let n = n as u64;
        let limit = u64::MAX - u64::MAX % n;
        loop { let v = self.raw(); if v < limit { return (v % n) as i32; } }
    }
    fn range(&mut self, mut low: i32, mut high: i32) -> Result<i32, ()> {
        if high < low { std::mem::swap(&mut low, &mut high); }
        let v = self.raw();
        let width = high.wrapping_sub(low).wrapping_add(1);
        if width == 0 { return Err(()); } // Still consumed the reference draw.
        Ok(low.wrapping_add((v % (width as u64)) as i32))
    }
}

#[no_mangle] pub extern "C" fn hrng_abi() -> i32 { 1 }

// Distinct client profile; does not alter the kards-sim xoroshiro ABI/state.
// Mirrors UE 5.6 RandomStream.h's high-23-bit GetFraction, including f32
// rounding before integer truncation. The Blueprint burn wrapper is composed
// by the caller so each primitive draw remains separately traceable.
#[no_mangle] pub unsafe extern "C" fn hrng_client_range(seed: *mut u32, draws: *mut u64, min: i32, max: i32, out: *mut i32) -> i32 {
    if seed.is_null() || draws.is_null() || out.is_null() { return 1; }
    let width = max.wrapping_sub(min).wrapping_add(1);
    if width <= 0 { out.write(min); return 0; }
    *seed = (*seed).wrapping_mul(196314165).wrapping_add(907633515);
    *draws = (*draws).wrapping_add(1);
    let fraction = f32::from_bits(0x3f800000 | (*seed >> 9)) - 1.0;
    out.write(min.wrapping_add((fraction * (width as f32)) as i32));
    0
}
// SAFETY: every non-null pointer must address valid, aligned caller-owned
// storage, with no overlapping mutable aliases. Buffers need `count` elements.
#[no_mangle] pub unsafe extern "C" fn hrng_init(seed: i32, state: *mut State) -> i32 {
    if state.is_null() { return 1; }
    state.write(State::new(seed)); 0
}
#[no_mangle] pub unsafe extern "C" fn hrng_next_raw(state: *mut State, out: *mut u64) -> i32 {
    if state.is_null() || out.is_null() { return 1; }
    out.write((*state).raw()); 0
}
#[no_mangle] pub unsafe extern "C" fn hrng_next(state: *mut State, n: i32, out: *mut i32) -> i32 {
    if state.is_null() || out.is_null() { return 1; }
    out.write((*state).next(n)); 0
}
#[no_mangle] pub unsafe extern "C" fn hrng_range(state: *mut State, low: i32, high: i32, out: *mut i32) -> i32 {
    if state.is_null() || out.is_null() { return 1; }
    match (*state).range(low, high) { Ok(v) => { out.write(v); 0 }, Err(()) => 2 }
}
#[no_mangle] pub unsafe extern "C" fn hrng_shuffle_i32(state: *mut State, values: *mut i32, count: usize) -> i32 {
    if state.is_null() || count > i32::MAX as usize { return 1; }
    if count == 0 { return 0; }
    if values.is_null() { return 1; }
    let values = std::slice::from_raw_parts_mut(values, count);
    for i in (1..count).rev() { let j = (*state).next((i + 1) as i32) as usize; values.swap(i, j); }
    0
}

#[cfg(test)] mod tests {
    use super::*;
    #[test] fn layout() { assert_eq!(std::mem::size_of::<State>(),24); }
    #[test] fn no_draw_for_small_next() {
        let mut s = State::new(-1); let before = s;
        for n in [-100, 0, 1] { assert_eq!(s.next(n),0); }
        assert_eq!(s,before);
    }
    #[test] fn equal_range_consumes() {
        let mut s = State::new(42); let before = s.draws;
        assert_eq!(s.range(7,7),Ok(7)); assert_eq!(s.draws,before+1);
    }
    #[test] fn overflow_range_consumes_then_errors() {
        let mut s = State::new(42); let before = s.draws;
        assert_eq!(s.range(i32::MIN,i32::MAX),Err(())); assert_eq!(s.draws,before+1);
    }
    #[test] fn rejection_consumes_extra_raw() {
        let mut s = State { s0: u64::MAX, s1: 0, draws: 8 };
        assert!((0..2).contains(&s.next(2))); assert!(s.draws>=10);
    }
    #[test] fn null_abi_rejected() { unsafe { assert_eq!(hrng_next_raw(std::ptr::null_mut(),std::ptr::null_mut()),1); } }
}
