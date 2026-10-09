//! The Linux module shape's stdio protocol (architecture.md Sec 5.4): the
//! implant execs the module and the two sides speak length-prefixed
//! request/response frames -- the same string grammar the Windows extern
//! family carries, no dynamic loader and no libc in the picture.
//!
//! Frames are little-endian throughout. A request is
//! `[op: u8][arg: u32][len: u32][len bytes]`; a response is
//! `[version: u8][status: u8][len: u32][len bytes]`. The loop is
//! request-in, response-out, until stdin ends -- one process may answer a
//! whole discovery run (ping, name, count, verb names), and the host
//! execs a fresh process for each verb dispatch.

use std::io::{Read, Write};

/// The protocol version this crate speaks. The host refuses a module
/// whose response names a version it does not recognize, whole and named
/// -- the stdio twin of `rod_plugin_abi`.
pub const PROTOCOL_VERSION: u8 = 1;

/// The request operations.
pub mod op {
    /// Liveness and version probe: any well-formed response answers it.
    pub const PING: u8 = 0;
    /// The module's self-declared name (`arg` and payload empty).
    pub const NAME: u8 = 1;
    /// The verb count, as a `u32` payload.
    pub const COUNT: u8 = 2;
    /// The verb name at `arg`, as the payload.
    pub const VERB_NAME: u8 = 3;
    /// Run the verb at `arg` over the payload as the argument string.
    pub const RUN: u8 = 4;
}

/// The response statuses.
pub mod status {
    /// The operation succeeded; the payload is the output.
    pub const OK: u8 = 0;
    /// The handler ran and refused; the payload is the failure text.
    pub const FAILED: u8 = 1;
    /// The operation or its argument is not one this module answers.
    pub const BAD_REQUEST: u8 = 2;
    /// The handler panicked; the payload names it.
    pub const PANICKED: u8 = 3;
}

/// The largest frame payload either side will read, so a hostile or broken
/// peer's length word cannot make the reader allocate without bound.
pub const MAX_PAYLOAD: u32 = 64 << 20;

/// One parsed request.
pub struct Request {
    pub op: u8,
    pub arg: u32,
    pub payload: Vec<u8>,
}

/// Reads one request frame, or None at a clean end of stream.
pub fn read_request<R: Read>(reader: &mut R) -> Option<Request> {
    let mut header = [0u8; 9];
    reader.read_exact(&mut header).ok()?;
    let len = u32::from_le_bytes([header[5], header[6], header[7], header[8]]);
    if len > MAX_PAYLOAD {
        return None;
    }
    let mut payload = vec![0u8; len as usize];
    reader.read_exact(&mut payload).ok()?;
    Some(Request {
        op: header[0],
        arg: u32::from_le_bytes([header[1], header[2], header[3], header[4]]),
        payload,
    })
}

/// Writes one response frame.
pub fn write_response<W: Write>(writer: &mut W, status: u8, payload: &[u8]) -> std::io::Result<()> {
    let mut frame = Vec::with_capacity(9 + payload.len());
    frame.push(PROTOCOL_VERSION);
    frame.push(status);
    frame.extend_from_slice(&(payload.len() as u32).to_le_bytes());
    frame.extend_from_slice(payload);
    writer.write_all(&frame)?;
    writer.flush()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frames_round_trip_through_bytes() {
        let mut wire = std::io::Cursor::new(Vec::new());
        // The host writes requests with the same header shape; parse one
        // back through the reader to pin the layout.
        let mut request = vec![op::RUN];
        request.extend_from_slice(&7u32.to_le_bytes());
        request.extend_from_slice(&3u32.to_le_bytes());
        request.extend_from_slice(b"xyz");
        wire.get_mut().extend_from_slice(&request);
        wire.set_position(0);
        let parsed = read_request(&mut wire).expect("the frame parses");
        assert_eq!(parsed.op, op::RUN);
        assert_eq!(parsed.arg, 7);
        assert_eq!(parsed.payload, b"xyz");

        let mut out = Vec::new();
        write_response(&mut out, status::OK, b"done").expect("the response writes");
        assert_eq!(out[0], PROTOCOL_VERSION);
        assert_eq!(out[1], status::OK);
        assert_eq!(u32::from_le_bytes([out[2], out[3], out[4], out[5]]), 4);
        assert_eq!(&out[6..], b"done");
    }

    #[test]
    fn the_stream_ends_cleanly_at_eof_and_refuses_oversized_frames() {
        let mut empty = std::io::Cursor::new(Vec::new());
        assert!(read_request(&mut empty).is_none());

        let mut huge = vec![op::RUN];
        huge.extend_from_slice(&0u32.to_le_bytes());
        huge.extend_from_slice(&(MAX_PAYLOAD + 1).to_le_bytes());
        let mut reader = std::io::Cursor::new(huge);
        assert!(read_request(&mut reader).is_none());
    }
}
