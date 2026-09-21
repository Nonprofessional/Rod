use std::collections::{HashMap, VecDeque};

use prost::Message;

use crate::handlers::Outcome;
use crate::wire::{Frame, FrameKind, StagedPull, TaskAck, TaskRequest, TaskResult};

/// The upstream batch and the task ledger: one owner for everything the
/// implant owes the server, so a carriage's failure semantics are a method
/// call rather than scattered state.
///
/// The batch discipline is the wire contract's own (extending/implants.md):
/// queued frames leave only when a response was processed whole, so a failed
/// contact re-sends the batch verbatim; the server records results
/// first-wins, so re-sending is safe. The ledger is the dedup half of the
/// dispatch strand: a redelivered task is answered from the cache and never
/// re-executed.
#[derive(Default)]
pub struct Outbox {
    queue: VecDeque<Frame>,
    /// Completed tasks by id: (wire outcome, output). A held entry answers
    /// any redelivery from the cache.
    ledger: HashMap<String, (Outcome, String)>,
    /// Staged tasks awaiting their payload, keyed by id: the original
    /// request is kept whole (verb and arguments ride the staged grammar),
    /// and the demand order is the map's insertion order via `demands`.
    staged: HashMap<String, TaskRequest>,
    demands: Vec<String>,
}

impl Outbox {
    pub fn queue(&mut self, frame: Frame) {
        self.queue.push_back(frame);
    }

    /// Records a task's outcome and queues its TaskResult. The wire's
    /// numeric outcome codes live behind this boundary -- the rest of the
    /// implant speaks `Outcome`.
    pub fn result(&mut self, task_id: &str, outcome: Outcome, output: &str) {
        self.ledger
            .insert(task_id.to_string(), (outcome, output.to_string()));
        let wire = if outcome == Outcome::Succeeded { 1 } else { 2 };
        self.queue.push_back(Frame {
            payload: TaskResult {
                task_id: task_id.to_string(),
                outcome: wire,
                output: output.to_string(),
            }
            .encode_to_vec(),
            kind: FrameKind::TaskResult as i32,
        });
    }

    /// The batch snapshot a contact attempt sends; nothing is consumed until
    /// [`Self::batch_crossed`] names the attempt a success.
    pub fn batch(&self) -> Vec<Frame> {
        self.queue.iter().cloned().collect()
    }

    /// The attempt crossed and its response was processed: everything queued
    /// for it is delivered.
    pub fn batch_crossed(&mut self, sent: usize) {
        for _ in 0..sent {
            self.queue.pop_front();
        }
    }

    pub fn holds(&self, task_id: &str) -> bool {
        self.ledger.contains_key(task_id)
    }

    pub fn cached(&self, task_id: &str) -> Option<(Outcome, String)> {
        self.ledger.get(task_id).cloned()
    }

    pub fn acknowledge(&mut self, task_id: &str) {
        self.queue.push_back(Frame {
            payload: TaskAck {
                task_id: task_id.to_string(),
            }
            .encode_to_vec(),
            kind: FrameKind::TaskAck as i32,
        });
    }

    /// Queues the demand for a staged task's payload, keeping the original
    /// request for the dispatch that follows the chunk run.
    pub fn demand_staged(&mut self, task: TaskRequest) {
        self.demands.push(task.task_id.clone());
        let id = task.task_id.clone();
        self.queue.push_back(Frame {
            payload: StagedPull {
                task_id: id.clone(),
            }
            .encode_to_vec(),
            kind: FrameKind::StagedPull as i32,
        });
        self.staged.insert(id, task);
    }

    /// Takes this cycle's demands in order, removing them from the pending
    /// set (the response's chunk runs answer them below).
    pub fn take_demands(&mut self) -> Vec<TaskRequest> {
        std::mem::take(&mut self.demands)
            .into_iter()
            .filter_map(|id| self.staged.remove(&id))
            .collect()
    }
}
