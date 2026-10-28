use std::ffi::{c_char, CString};

#[no_mangle]
pub extern "C" fn hk_core_abi() -> u32 { 1 }

/// SAFETY: request points to len readable bytes; returned allocation must be
/// freed exactly once using hk_core_free, never by the managed allocator.
#[no_mangle]
pub unsafe extern "C" fn hk_core_request(request: *const u8,len: usize) -> *mut c_char {
    if request.is_null() || len>16*1024*1024 { return std::ptr::null_mut(); }
    let result=std::panic::catch_unwind(|| {
        match std::str::from_utf8(std::slice::from_raw_parts(request,len)) {
            Ok(s)=>crate::engine::request_json(s),
            Err(_)=>"{\"ok\":false,\"error\":\"Invalid UTF-8\"}".into(),
        }
    }).unwrap_or_else(|_|"{\"ok\":false,\"error\":\"Rust core panic; transaction discarded\"}".into());
    CString::new(result).expect("JSON never contains raw NUL").into_raw()
}

/// SAFETY: ptr is null or a live pointer returned by hk_core_request.
#[no_mangle]
pub unsafe extern "C" fn hk_core_free(ptr: *mut c_char) { if !ptr.is_null() { drop(CString::from_raw(ptr)); } }
