using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rod.Audit;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// Acceptance for the implant-side plugin seam (architecture.md Sec 5.4):
/// a module built against the SDK, delivered through module.load's staged
/// content, executes a verb the artifact did not compile -- and the result
/// lands in the audit trail attributed like any task. The leg drives the
/// dev-shape implant (the glibc-linked build whose platform provides the
/// in-process loader the seam rides); the reference module is the
/// in-tree hostenum cdylib. The musl refusal and the Windows PE map are
/// pinned by the crate's own tests and the rehearsal runbook.
/// </summary>
public class ModuleLoadTests
{
    [RustFact]
    public async Task ModuleLoad_DeliversTheReferenceModule_AndItsVerbRunsLikeACompiledOne()
    {
        var (implantBinary, moduleBytes) = await BuildImplantAndModuleAsync();

        await using var env = await TestEnv.StartAsync();
        await env.CreateEngagementAsync();

        var mint = await env.Http.PostAsync(
            $"/engagements/{env.EngagementId}/deploy-tokens", content: null);
        mint.EnsureSuccessStatusCode();
        var token = await mint.Content.ReadFromJsonAsync<MintedToken>();
        Assert.False(string.IsNullOrEmpty(token?.Secret));

        var stderr = new StringBuilder();
        var start = new ProcessStartInfo
        {
            FileName = implantBinary,
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        start.Environment["ROD_ENROLL_URL"] = $"http://127.0.0.1:{env.HttpPort}/implants/enroll";
        start.Environment["ROD_DEPLOY_TOKEN"] = token!.Secret;
        // The fielded artifact is terminal-silent by default; the suite runs
        // verbose so a failed leg's stderr says why.
        start.Environment["ROD_VERBOSE"] = "1";
        start.Environment["ROD_SLEEP"] = "1";
        start.Environment["ROD_JITTER"] = "0";
        start.Environment["ROD_ENVELOPE"] = "none";
        start.Environment["ROD_VERBS"] = "shell.exec,module.load,module.unload,module.list";
        using var process = Process.Start(start);
        Assert.NotNull(process);
        process!.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();

        try
        {
            var implantId = await ModuleLoadTestSupport.WaitForOnlineAsync(env, TimeSpan.FromSeconds(60), stderr);

            // The load: the module's bytes ride the task's staged content,
            // integrity-bound by the sha256 the issuer appends to the signed
            // arguments.
            var load = await IssueAsync(env, implantId, "module.load", "hostenum", moduleBytes);
            var loaded = await ModuleLoadTestSupport.WaitForTaskAsync(
                env, load.TaskId, t => t.Status == "Completed", stderr, "the module.load task");
            Assert.Equal("Succeeded", loaded.Outcome);
            Assert.Contains("recon.hostenum", loaded.Output);

            // The audit arc is any task's: issued, dispatched, completed,
            // attributed through the implant to the issuing operator.
            var audit = env.Host.Services.GetRequiredService<IAuditStore>();
            var trail = await audit.ForTaskAsync(Guid.Parse(load.TaskId));
            Assert.Contains(trail, e => e.Kind == AuditEventKind.TaskIssued);
            Assert.Contains(trail, e => e.Kind == AuditEventKind.TaskCompleted);
            Assert.All(trail, e => Assert.Equal(Guid.Parse(env.EngagementId!), e.EngagementId));

            // The family's reads: the listing names the module and its verb.
            var list = await IssueAsync(env, implantId, "module.list", "");
            var listed = await ModuleLoadTestSupport.WaitForTaskAsync(
                env, list.TaskId, t => t.Status == "Completed", stderr, "the module.list task");
            Assert.Contains("hostenum: recon.hostenum", listed.Output);

            // The acceptance's center: a verb the artifact did not compile
            // answers tasking like a compiled one, and its result lands in
            // the trail attributed like any task.
            var sweep = await IssueAsync(env, implantId, "recon.hostenum", "");
            var swept = await ModuleLoadTestSupport.WaitForTaskAsync(
                env, sweep.TaskId, t => t.Status == "Completed", stderr, "the recon.hostenum task");
            Assert.Equal("Succeeded", swept.Outcome);
            Assert.Contains("\"host\":", swept.Output);
            var sweepTrail = await audit.ForTaskAsync(Guid.Parse(sweep.TaskId));
            Assert.Contains(sweepTrail, e => e.Kind == AuditEventKind.TaskCompleted);
            Assert.Contains(sweepTrail, e => e.ImplantId == Guid.Parse(implantId));

            // The retraction: unload drops the routes, and the verb goes
            // back to failing with the grammar named.
            var unload = await IssueAsync(env, implantId, "module.unload", "hostenum");
            var unloaded = await ModuleLoadTestSupport.WaitForTaskAsync(
                env, unload.TaskId, t => t.Status == "Completed", stderr, "the module.unload task");
            Assert.Contains("recon.hostenum", unloaded.Output);
            var again = await IssueAsync(env, implantId, "recon.hostenum", "");
            var refused = await ModuleLoadTestSupport.WaitForTaskAsync(
                env, again.TaskId, t => t.Status == "Completed", stderr, "the post-unload task");
            Assert.Equal("Failed", refused.Outcome);
            Assert.Contains("no handler", refused.Output);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(5000);
            }
        }
    }

    [RustFact]
    public async Task ModuleLoad_OfGarbageBytes_FailsCleanlyOnTheTask()
    {
        var (implantBinary, _) = await BuildImplantAndModuleAsync();

        await using var env = await TestEnv.StartAsync();
        await env.CreateEngagementAsync();

        var mint = await env.Http.PostAsync(
            $"/engagements/{env.EngagementId}/deploy-tokens", content: null);
        mint.EnsureSuccessStatusCode();
        var token = await mint.Content.ReadFromJsonAsync<MintedToken>();

        var stderr = new StringBuilder();
        var start = new ProcessStartInfo
        {
            FileName = implantBinary,
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        start.Environment["ROD_ENROLL_URL"] = $"http://127.0.0.1:{env.HttpPort}/implants/enroll";
        start.Environment["ROD_DEPLOY_TOKEN"] = token!.Secret;
        start.Environment["ROD_VERBOSE"] = "1";
        start.Environment["ROD_SLEEP"] = "1";
        start.Environment["ROD_JITTER"] = "0";
        start.Environment["ROD_ENVELOPE"] = "none";
        start.Environment["ROD_VERBS"] = "module.load";
        using var process = Process.Start(start);
        Assert.NotNull(process);
        process!.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();

        try
        {
            var implantId = await ModuleLoadTestSupport.WaitForOnlineAsync(env, TimeSpan.FromSeconds(60), stderr);
            var load = await IssueAsync(env, implantId, "module.load", "junk",
                Encoding.UTF8.GetBytes("definitely not an ELF image"));
            var failed = await ModuleLoadTestSupport.WaitForTaskAsync(
                env, load.TaskId, t => t.Status == "Completed", stderr, "the garbage module.load task");
            Assert.Equal("Failed", failed.Outcome);
            Assert.Contains("not a loadable module", failed.Output);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(5000);
            }
        }
    }

    private static async Task<TaskBody> IssueAsync(
        TestEnv env, string implantId, string verb, string arguments, byte[]? content = null)
    {
        var issued = await env.Http.PostAsJsonAsync(
            $"/engagements/{env.EngagementId}/tasks",
            new TaskEndpoints.IssueTaskRequest(implantId, verb, arguments, content));
        Assert.True(issued.IsSuccessStatusCode, await issued.Content.ReadAsStringAsync());
        var task = await issued.Content.ReadFromJsonAsync<TaskBody>();
        Assert.NotNull(task);
        return task!;
    }

    /// <summary>
    /// Cargo-builds the dev-shape implant and the reference hostenum module
    /// (the platform's own cdylib shape), once per test run.
    /// </summary>
    private static async Task<(string Implant, byte[] Module)> BuildImplantAndModuleAsync()
    {
        var rustDir = ModuleLoadTestSupport.FindRustTree()
            ?? throw new InvalidOperationException("the Rust implant tree was not found from the test assembly");
        await RunCargoAsync(rustDir, "build --release");
        var binary = Path.Combine(rustDir, "target", "release", "rod-implant");
        Assert.True(File.Exists(binary), $"cargo reported success but {binary} is missing");

        var moduleDir = Path.Combine(rustDir, "modules", "hostenum");
        Assert.True(Directory.Exists(moduleDir), $"the reference module tree is missing: {moduleDir}");
        await RunCargoAsync(moduleDir, "build --release");
        var module = Path.Combine(moduleDir, "target", "release", "librod_module_hostenum.so");
        if (!File.Exists(module))
            module = Path.Combine(moduleDir, "target", "release", "rod_module_hostenum.dll");
        Assert.True(File.Exists(module), $"cargo reported success but {module} is missing");
        return (binary, await File.ReadAllBytesAsync(module));
    }

    private static async Task RunCargoAsync(string workingDirectory, string arguments)
    {
        var build = Process.Start(new ProcessStartInfo
        {
            FileName = "cargo",
            WorkingDirectory = workingDirectory,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        var buildOutput = (await build!.StandardOutput.ReadToEndAsync())
            + await build.StandardError.ReadToEndAsync();
        build.WaitForExit(300_000);
        Assert.True(build.ExitCode == 0, $"cargo {arguments} failed:\n{buildOutput}");
    }

    private sealed record MintedToken(string Secret);
}

/// <summary>
/// The shared helpers of the module-load legs: the same wait shapes the
/// Rust e2e suite uses, kept local so this file reads on its own.
/// </summary>
internal static class ModuleLoadTestSupport
{
    public static async Task<string> WaitForOnlineAsync(
        TestEnv env, TimeSpan deadline, StringBuilder stderr)
    {
        var end = DateTimeOffset.UtcNow + deadline;
        while (DateTimeOffset.UtcNow < end)
        {
            try
            {
                var implants = await env.Http.GetFromJsonAsync<ImplantEndpoints.ImplantResponse[]>(
                    $"/engagements/{env.EngagementId}/implants");
                var online = implants?.FirstOrDefault(i => i.Class == "Implant" && i.IsOnline);
                if (online is not null)
                    return online.ImplantId;
            }
            catch (HttpRequestException)
            {
                // The listing read races the enrollment; retry.
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("the implant did not appear online. Stderr:\n" + stderr);
    }

    public static async Task<TaskBody> WaitForTaskAsync(
        TestEnv env, string taskId, Func<TaskBody, bool> done, StringBuilder stderr, string awaiting)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        TaskBody? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var read = await env.Http.GetFromJsonAsync<TaskBody>(
                $"/engagements/{env.EngagementId}/tasks/{taskId}");
            if (read is not null)
                last = read;
            if (read is not null && done(read))
                return read;
            await Task.Delay(500);
        }
        Assert.Fail(
            $"timed out waiting for {awaiting}. Task status: {last?.Status} {last?.Outcome} {last?.Output}. Implant stderr:\n{stderr}");
        throw new InvalidOperationException("unreachable");
    }

    public static string? FindRustTree()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "implant", "rust");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(dir.FullName, "src", "teamserver")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

/// <summary>Task read shape for the module-load legs.</summary>
internal record TaskBody
{
    public string TaskId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Outcome { get; set; }
    public string? Output { get; set; }
}

/// <summary>
/// A real Kestrel teamserver with the plain-HTTP operator/enroll API and
/// the implant endpoint, logged in; the same harness shape the Rust e2e
/// suite runs, kept private per file like its siblings.
/// </summary>
internal sealed class TestEnv : IAsyncDisposable
{
    public IHost Host { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public int HttpPort { get; private set; }

    public static async Task<TestEnv> StartAsync()
    {
        var env = new TestEnv();
        env.HttpPort = TestSupport.GetFreeTcpPort();

        var config = AuthenticatedHost.BuildConfig();
        env.Host = TransportHost.CreateHostBuilder(
                configureServices: services => AuthenticatedHost.ComposeServices(services, config),
                mapEndpoints: endpoints => AuthenticatedHost.ComposeEndpoints(endpoints),
                configuration: config)
            .ConfigureWebHost(webBuilder => webBuilder
                .UseRodListeners(new List<Rod.Transport.Listeners.ListenerConfig>())
                .ConfigureKestrel(kestrel => kestrel.ListenLocalhost(env.HttpPort)))
            .Build();
        await env.Host.StartAsync();

        env.Http = new HttpClient(new CookieHandler(new HttpClientHandler()))
        {
            BaseAddress = new Uri($"http://127.0.0.1:{env.HttpPort}"),
        };
        await AuthenticatedHost.LoginAsync(env.Http);
        return env;
    }

    public async Task CreateEngagementAsync()
    {
        var createResponse = await Http.PostAsJsonAsync("/engagements",
            new EngagementEndpoints.CreateEngagementRequest(Name: "Operation Module Seam"));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<EngagementEndpoints.EngagementResponse>();
        EngagementId = created!.EngagementId;
    }

    public string? EngagementId { get; private set; }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        if (Host is not null)
            await Host.StopAsync();
        Host?.Dispose();
    }
}
