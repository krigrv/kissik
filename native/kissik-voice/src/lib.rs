//! Panic-safe C API over DeepFilterNet's runtime: every call catches panics and reports failure
//! instead of aborting the host app. Processing is synchronous on the caller's thread, one
//! 480-sample (10 ms at 48 kHz) mono frame at a time.

use std::ffi::{c_char, CStr};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::PathBuf;

use df::tract::{DfParams, DfTract, RuntimeParams};
use ndarray::{ArrayView2, ArrayViewMut2};

pub struct KvState {
    df: DfTract,
    broken: bool,
}

/// Loads a DeepFilterNet .tar.gz model. Returns null on any failure.
#[no_mangle]
pub unsafe extern "C" fn kv_create(model_path: *const c_char, atten_lim_db: f32) -> *mut KvState {
    catch_unwind(|| {
        let path = CStr::from_ptr(model_path).to_str().ok()?;
        let params = DfParams::new(PathBuf::from(path)).ok()?;
        let rp = RuntimeParams::default_with_ch(1).with_atten_lim(atten_lim_db);
        let df = DfTract::new(params, &rp).ok()?;
        Some(Box::into_raw(Box::new(KvState { df, broken: false })))
    })
    .ok()
    .flatten()
    .unwrap_or(std::ptr::null_mut())
}

/// Samples per frame (480 at 48 kHz), or 0 for a null state.
#[no_mangle]
pub unsafe extern "C" fn kv_frame_length(st: *const KvState) -> usize {
    st.as_ref().map(|s| s.df.hop_size).unwrap_or(0)
}

/// Denoises one frame. Returns 0 on success; non-zero means the frame was passed through
/// untouched and the state should be dropped.
#[no_mangle]
pub unsafe extern "C" fn kv_process(st: *mut KvState, input: *const f32, output: *mut f32) -> i32 {
    let Some(s) = st.as_mut() else { return 1 };
    let n = s.df.hop_size;
    if s.broken {
        std::ptr::copy(input, output, n);
        return 2;
    }
    let r = catch_unwind(AssertUnwindSafe(|| {
        let i = ArrayView2::from_shape_ptr((1, n), input);
        let o = ArrayViewMut2::from_shape_ptr((1, n), output);
        s.df.process(i, o).is_ok()
    }));
    if !matches!(r, Ok(true)) {
        s.broken = true;
        std::ptr::copy(input, output, n);
        return 3;
    }
    0
}

#[no_mangle]
pub unsafe extern "C" fn kv_destroy(st: *mut KvState) {
    if !st.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(st))));
    }
}
