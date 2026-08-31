//! Static-analysis-only Rust code for validating Klocwork integration analysis.
//! This crate is not referenced by the application and none of its functions run.

#![allow(dead_code)]

unsafe fn read_pointer(pointer: *const i32) -> i32 {
    unsafe { *pointer }
}

fn return_null_pointer() -> *const i32 {
    std::ptr::null()
}

/// Expected checker: RS.NPD.GEN.MUST or RS.NPD.CONST.DEREF.
pub unsafe fn trigger_direct_null_dereference() -> i32 {
    let pointer: *const i32 = std::ptr::null();
    unsafe { *pointer }
}

/// Expected checker: RS.NPD.GEN.CALL.MUST or RS.NPD.CONST.CALL.
pub unsafe fn trigger_null_pointer_call() -> i32 {
    let pointer: *const i32 = std::ptr::null();
    unsafe { read_pointer(pointer) }
}

/// Expected checker: RS.NPD.FUNC.MUST.
pub unsafe fn trigger_function_null_dereference() -> i32 {
    let pointer = return_null_pointer();
    unsafe { *pointer }
}

/// Expected checker: RS.DBZ.GENERAL.
pub fn trigger_division_by_zero(value: i32) -> i32 {
    let divisor = value - value;
    100 / divisor
}

