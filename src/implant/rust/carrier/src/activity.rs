//! The native_activity.h seam. The framework allocates the callbacks
//! table itself and hands it over inside the activity -- the entry fills
//! in the pointers it serves and leaves the rest null, which the contract
//! allows (a no-UI activity serves none of the window/input callbacks).
//! The struct is opaque beyond that leading table pointer; the table's
//! field order is the ABI.

use std::ffi::c_void;
use std::sync::Once;

/// The framework's ANativeActivity. Only the leading callbacks pointer is
/// ever read here; the rest of the struct stays untouched.
#[repr(C)]
struct NativeActivity {
    callbacks: *mut Callbacks,
}

/// ANativeActivityCallbacks, field order per the NDK header -- the order
/// is the ABI. The window/input handle pointers stay opaque; the
/// callbacks the implant does not serve stay null.
#[repr(C)]
struct Callbacks {
    on_start: Option<extern "C" fn(*mut NativeActivity)>,
    on_resume: Option<extern "C" fn(*mut NativeActivity)>,
    on_save_instance_state: Option<extern "C" fn(*mut NativeActivity, *mut usize) -> *mut c_void>,
    on_pause: Option<extern "C" fn(*mut NativeActivity)>,
    on_stop: Option<extern "C" fn(*mut NativeActivity)>,
    on_destroy: Option<extern "C" fn(*mut NativeActivity)>,
    on_window_focus_changed: Option<extern "C" fn(*mut NativeActivity, i32)>,
    on_native_window_created: Option<extern "C" fn(*mut NativeActivity, *mut c_void)>,
    on_native_window_resized: Option<extern "C" fn(*mut NativeActivity, *mut c_void)>,
    on_native_window_redraw_needed: Option<extern "C" fn(*mut NativeActivity, *mut c_void)>,
    on_native_window_destroyed: Option<extern "C" fn(*mut NativeActivity, *mut c_void)>,
    on_input_queue_created: Option<extern "C" fn(*mut NativeActivity, *mut c_void)>,
    on_input_queue_destroyed: Option<extern "C" fn(*mut NativeActivity, *mut c_void)>,
    on_content_rect_changed: Option<extern "C" fn(*mut NativeActivity, *const c_void)>,
    on_configuration_changed: Option<extern "C" fn(*mut NativeActivity)>,
    on_low_memory: Option<extern "C" fn(*mut NativeActivity)>,
}

static START: Once = Once::new();

/// The entry the framework calls after dlopen of this library: set the
/// lifecycle callbacks the shell serves. The run itself starts at the
/// first lifecycle callback, when the process is visible and the loop it
/// dials has a process to outlive.
#[no_mangle]
#[allow(non_snake_case)]
extern "C" fn ANativeActivity_onCreate(
    activity: *mut NativeActivity,
    _saved_state: *mut c_void,
    _saved_state_size: usize,
) {
    unsafe {
        let callbacks = (*activity).callbacks;
        if callbacks.is_null() {
            eprintln!("rod carrier: the framework handed over no callbacks table");
            return;
        }
        (*callbacks).on_start = Some(on_start);
        (*callbacks).on_destroy = Some(on_destroy);
    }
}

extern "C" fn on_start(_activity: *mut NativeActivity) {
    START.call_once(|| {
        let spawned = std::thread::Builder::new()
            .name("rod-carrier".into())
            .spawn(rod_implant::carrier_run);
        if let Err(err) = spawned {
            eprintln!("rod carrier: spawning the core thread failed: {err}");
        }
    });
}

extern "C" fn on_destroy(_activity: *mut NativeActivity) {
    // The core's loop is built to run until process death; there is
    // nothing to stop cleanly here, and the process dies with the
    // activity's task anyway.
}
