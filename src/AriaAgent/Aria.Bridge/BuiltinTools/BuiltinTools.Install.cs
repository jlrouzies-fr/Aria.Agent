using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Aria.Bridge;

public static partial class BuiltinTools
{
    // Package managers install_software is allowed to drive, by name only — never a path, so the
    // model can't point the tool at an arbitrary binary. Resolved on PATH at call time.
    private static readonly string[] InstallManagers =
        ["brew", "npm", "pip", "pipx", "dotnet", "cargo", "go", "uv", "yarn", "pnpm", "apt", "choco", "winget"];

    // Strict charset for everything that ends up in the process argument list: alphanumerics plus
    // the characters legitimate package names/versions/flags use. No whitespace or shell
    // metacharacters — the args are passed via ProcessStartInfo.ArgumentList (no shell), and this
    // keeps it that way even if a future refactor reintroduces one.
    private static bool IsSafeArg(string s) =>
        s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || "._@/:-=+[]~".Contains(c));

    private static IEnumerable<BridgeToolInfo> InstallToolInfos()
    {
        yield return new("install_software",
            "Install a software package via a system package manager (brew, npm, pip, pipx, dotnet, cargo, go, uv, yarn, pnpm, apt, choco, winget). " +
            "Requires human approval in every governed mode — use only when the user asked for the install. " +
            "For Python projects, prefer reading pyproject.toml with project_info and using uv/pip -r; for Node projects, check package.json first.",
            Js("""
               {"type":"object",
                "properties":{
                  "manager":    {"type":"string","enum":["brew","npm","pip","pipx","dotnet","cargo","go","uv","yarn","pnpm","apt","choco","winget"],"description":"Package manager to use."},
                  "package":    {"type":"string","description":"Package name (e.g. ripgrep, playwright, dotnet-ef)."},
                  "version":    {"type":"string","description":"Optional version/tag (e.g. 1.2.3, latest)."},
                  "global":     {"type":"boolean","description":"Install globally where the manager has the notion (npm -g, dotnet tool -g, yarn global, pnpm -g, apt system-wide). Default true. Ignored for brew/pip/pipx/cargo/go/uv/choco/winget."},
                  "extra_args": {"type":"array","items":{"type":"string"},"description":"Optional extra arguments for the manager (validated against a strict charset)."}
                },
                "required":["manager","package"]}
               """));

        yield return new("system_info",
            "Get environment information: OS/arch, shell, CPU/RAM, disk free space, and which package managers and runtimes (dotnet, node, python, go) are available with their versions. Read-only.",
            Js("""
               {"type":"object","properties":{}}
               """));
    }

    // ── install_software ──────────────────────────────────────────────────────

    private static async Task<ToolCallResponse> InstallSoftwareAsync(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var manager = (args.Str("manager") ?? throw new ArgumentException("'manager' is required"))
            .ToLowerInvariant();
        var package  = args.Str("package") ?? throw new ArgumentException("'package' is required");
        var version  = args.Str("version");
        var global   = args.Bool("global") ?? true;
        var extra    = args.StrArray("extra_args");

        var argv = BuildInstallCommand(manager, package, version, global, extra);

        // No EnforcePath: installs write to system locations by design — that's exactly why this
        // tool is approval-gated server-side. The rendered command still goes through the command
        // blocklist as defense-in-depth.
        policy?.EnforceCommand(string.Join(' ', argv));

        if (FindOnPath(argv[0]) == null)
            return Err($"Package manager '{manager}' was not found on PATH. " +
                       $"Install it first (or ask the user to), or retry with a different manager " +
                       $"({string.Join(", ", InstallManagers)}).");

        var workDir = _sessionCwd != null && Directory.Exists(_sessionCwd)
            ? _sessionCwd
            : Expand("~");

        using var proc = BuildProcessRaw(argv[0], argv[1..], workDir);
        string stdout, stderr; int exitCode; bool timedOut;
        try
        {
            (stdout, stderr, exitCode, timedOut) = await RunAsync(proc, TimeSpan.FromSeconds(MaxTimeoutSeconds));
        }
        catch (Win32Exception)
        {
            return Err($"Package manager '{manager}' was not found on PATH. " +
                       $"Install it first (or ask the user to), or retry with a different manager " +
                       $"({string.Join(", ", InstallManagers)}).");
        }

        var result = JsonSerializer.Serialize(new
        {
            manager,
            command   = string.Join(' ', argv),
            exit_code = timedOut ? (int?)null : exitCode,
            stdout    = stdout.Length > 0 ? stdout : null,
            stderr    = stderr.Length > 0 ? stderr : null,
            timed_out = timedOut,
        });
        return new ToolCallResponse(result, timedOut || exitCode != 0);
    }

    /// <summary>
    /// Maps (manager, package, version, global, extra) to the real process invocation as an argv
    /// list (element 0 = executable). Pure and shell-free; throws ArgumentException on a manager
    /// outside the allowlist or any argument outside the safe charset.
    /// </summary>
    internal static string[] BuildInstallCommand(
        string manager, string package, string? version, bool global, string[]? extra)
    {
        if (!InstallManagers.Contains(manager))
            throw new ArgumentException(
                $"Unknown package manager '{manager}'. Allowed: {string.Join(", ", InstallManagers)}.");

        foreach (var (label, value) in new[] { ("package", package), ("version", version) })
            if (value != null && !IsSafeArg(value))
                throw new ArgumentException(
                    $"Invalid {label} '{value}': only alphanumerics and ._@/:-=+[]~ are allowed.");

        if (extra != null)
            foreach (var e in extra)
                if (!IsSafeArg(e))
                    throw new ArgumentException(
                        $"Invalid extra_args entry '{e}': only alphanumerics and ._@/:-=+[]~ are allowed.");

        List<string> argv = manager switch
        {
            // brew pins versions in the formula name; 'global' has no meaning there.
            "brew" => ["brew", "install", version != null ? $"{package}@{version}" : package],
            // Never a bare system-wide pip install: --user keeps it out of system site-packages.
            "pip"  => ["pip", "install", "--user", version != null ? $"{package}=={version}" : package],
            "pipx" => ["pipx", "install", version != null ? $"{package}=={version}" : package],
            "go"   => ["go", "install", $"{package}@{version ?? "latest"}"],
            "uv"   => ["uv", "pip", "install", version != null ? $"{package}=={version}" : package],
            "npm"  => ["npm", "install"],
            "yarn" => global ? ["yarn", "global", "add"] : ["yarn", "add"],
            "pnpm" => ["pnpm", "add"],
            "dotnet" => ["dotnet", "tool", "install"],
            "cargo"  => ["cargo", "install"],
            "apt"    => ["apt-get", "install", "-y"],
            "choco"  => ["choco", "install", package, "-y"],
            "winget" => ["winget", "install", "--id", package, "--accept-source-agreements", "--disable-interactivity"],
            _    => throw new ArgumentException($"Unknown package manager '{manager}'."),
        };
        switch (manager)
        {
            case "npm":
                if (global) argv.Add("-g");
                argv.Add(version != null ? $"{package}@{version}" : package);
                break;
            case "yarn":
                // argv already contains ["yarn", ("global"), "add"]; append package@version.
                argv.Add(version != null ? $"{package}@{version}" : package);
                break;
            case "pnpm":
                if (global) argv.Add("-g");
                argv.Add(version != null ? $"{package}@{version}" : package);
                break;
            case "dotnet":
                if (global) argv.Add("-g");
                argv.Add(package);
                if (version != null) { argv.Add("--version"); argv.Add(version); }
                break;
            case "cargo":
                argv.Add(package);
                if (version != null) { argv.Add("--version"); argv.Add(version); }
                break;
            case "apt":
                if (!global)
                    throw new ArgumentException(
                        "apt is system-only. Use global=true for apt-get install, " +
                        "or prefer project_info + uv/pip -r for project-local Python installs.");
                argv.Add(version != null ? $"{package}={version}" : package);
                break;
        }

        if (extra != null) argv.AddRange(extra);
        return [.. argv];
    }

    // Resolve an executable name on PATH (no shell). Returns null when absent.
    internal static string? FindOnPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var names = IsWindows
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(ext => exe.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? exe : exe + ext)
                .Prepend(exe)
                .ToArray()
            : [exe];

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    // ── system_info ───────────────────────────────────────────────────────────

    private static async Task<ToolCallResponse> SystemInfoAsync()
    {
        var workDir = _sessionCwd != null && Directory.Exists(_sessionCwd)
            ? _sessionCwd
            : Expand("~");

        long? diskFree = null, diskTotal = null;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(workDir) ?? workDir);
            diskFree  = drive.AvailableFreeSpace;
            diskTotal = drive.TotalSize;
        }
        catch { /* best-effort */ }

        // Version probes are best-effort with a short timeout: a missing or hung tool is omitted
        // from the result, never an error.
        var managers = new Dictionary<string, string>();
        foreach (var m in InstallManagers)
        {
            var verArgs = m switch
            {
                "go" => new[] { "version" },
                "yarn" => new[] { "--version" },
                "pnpm" => new[] { "--version" },
                "apt" => new[] { "--version" },
                "choco" => new[] { "--version" },
                "winget" => new[] { "--version" },
                _ => new[] { "--version" }
            };
            var v = await ProbeVersionAsync(m, verArgs, workDir);
            if (v != null) managers[m] = v;
        }

        var runtimes = new Dictionary<string, string>();
        foreach (var (exe, verArgs) in new (string, string[])[]
        {
            ("dotnet",  ["--version"]),
            ("node",    ["--version"]),
            ("python3", ["--version"]),
            ("go",      ["version"]),
        })
        {
            var v = await ProbeVersionAsync(exe, verArgs, workDir);
            if (v != null) runtimes[exe] = v;
        }

        var result = JsonSerializer.Serialize(new
        {
            os              = RuntimeInformation.OSDescription,
            arch            = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            shell           = IsWindows
                ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
                : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh",
            cpu_count       = Environment.ProcessorCount,
            total_ram_bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            cwd             = workDir,
            disk_free_bytes = diskFree,
            disk_total_bytes = diskTotal,
            package_managers = managers,
            runtimes,
        });
        return new ToolCallResponse(result, IsError: false);
    }

    private static async Task<string?> ProbeVersionAsync(string exe, string[] versionArgs, string workDir)
    {
        if (FindOnPath(exe) == null) return null;
        try
        {
            using var proc = BuildProcessRaw(exe, versionArgs, workDir);
            var (stdout, stderr, exitCode, timedOut) = await RunAsync(proc, TimeSpan.FromSeconds(5));
            if (timedOut || exitCode != 0) return null;
            var firstLine = (stdout.Length > 0 ? stdout : stderr)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(firstLine) ? null : firstLine;
        }
        catch { return null; }
    }
}
