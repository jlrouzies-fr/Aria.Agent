using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Aria.Bridge;

public static partial class BuiltinTools
{
    private static IEnumerable<BridgeToolInfo> ProjectInfoToolInfos()
    {
        yield return new("project_info",
            "Read-only project introspection: detect language ecosystems, dependency files, package managers, and infer build/run/test/install commands. Prefer this before running unfamiliar commands.",
            Js("""
               {"type":"object",
                "properties":{
                  "path": {"type":"string","description":"Project root directory to inspect. Must be under the node's allowed paths."}
                },
                "required":["path"]}
               """));
    }

    private static async Task<ToolCallResponse> ProjectInfoAsync(
        Dictionary<string, JsonElement> args, SecurityPolicy? policy)
    {
        var pathArg = args.Str("path") ?? throw new ArgumentException("'path' is required");
        var root = Expand(pathArg);
        policy?.EnforcePath(root);

        if (!Directory.Exists(root))
            return Err($"Directory not found: {root}");

        var ecosystems = new List<ProjectInfoResult>();

        // Detection order matches the priority given in the requirements.
        if (TryDetectPython(root) is { } py) ecosystems.Add(py);
        if (TryDetectDotNet(root) is { } net) ecosystems.Add(net);
        if (TryDetectNode(root) is { } node) ecosystems.Add(node);
        if (TryDetectRust(root) is { } rust) ecosystems.Add(rust);
        if (TryDetectGo(root) is { } go) ecosystems.Add(go);
        if (TryDetectPowerShell(root) is { } ps) ecosystems.Add(ps);
        if (TryDetectRuby(root) is { } ruby) ecosystems.Add(ruby);
        if (TryDetectPhp(root) is { } php) ecosystems.Add(php);

        var result = JsonSerializer.Serialize(new
        {
            path = root,
            ecosystems = ecosystems,
        });

        await Task.CompletedTask; // keep signature async for dispatcher uniformity
        return new ToolCallResponse(result, IsError: false);
    }

    // ── Result model ──────────────────────────────────────────────────────────

    private sealed record ProjectInfoResult(
        [property: System.Text.Json.Serialization.JsonPropertyName("ecosystem")] string Ecosystem,
        [property: System.Text.Json.Serialization.JsonPropertyName("dependency_files_found")] string[] DependencyFilesFound,
        [property: System.Text.Json.Serialization.JsonPropertyName("dependencies")] string[] Dependencies,
        [property: System.Text.Json.Serialization.JsonPropertyName("recommended_package_manager")] string RecommendedPackageManager,
        [property: System.Text.Json.Serialization.JsonPropertyName("install_command")] string? InstallCommand,
        [property: System.Text.Json.Serialization.JsonPropertyName("run_command")] string? RunCommand,
        [property: System.Text.Json.Serialization.JsonPropertyName("build_command")] string? BuildCommand,
        [property: System.Text.Json.Serialization.JsonPropertyName("test_command")] string? TestCommand,
        [property: System.Text.Json.Serialization.JsonPropertyName("has_lockfile")] bool HasLockfile,
        [property: System.Text.Json.Serialization.JsonPropertyName("has_venv")] bool HasVenv);

    // ── Shared helpers ────────────────────────────────────────────────────────

    private const int MaxFileBytes = 64 * 1024;

    private static string? SafeReadFirstBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            return File.ReadAllText(path);
        }
        catch { return null; }
    }

    private static IEnumerable<string> SafeReadLines(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return [];
            return File.ReadLines(path);
        }
        catch { return []; }
    }

    private static string[] SafeEnumerateFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).ToArray(); }
        catch { return []; }
    }

    private static bool AnyFileExists(string root, params string[] names)
        => names.Any(n => File.Exists(Path.Combine(root, n)));

    private static string[] JsonStringArray(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToArray();
    }

    private static string[] JsonObjectKeys(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return [];
        return obj.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToArray();
    }

    // ── Python ────────────────────────────────────────────────────────────────

    private static ProjectInfoResult? TryDetectPython(string root)
    {
        var files = new List<string>();
        var deps = new List<string>();
        string? recommended = null;
        string? installCmd = null;
        bool hasLockfile = false;
        bool hasVenv = Directory.Exists(Path.Combine(root, ".venv"))
            || Directory.Exists(Path.Combine(root, "venv"))
            || Directory.Exists(Path.Combine(root, "env"));

        var pyproject = Path.Combine(root, "pyproject.toml");
        var requirements = Path.Combine(root, "requirements.txt");
        var requirementsDir = Path.Combine(root, "requirements");
        var setupPy = Path.Combine(root, "setup.py");

        if (File.Exists(pyproject))
        {
            files.Add("pyproject.toml");
            var text = SafeReadFirstBytes(pyproject);
            if (text != null)
            {
                deps.AddRange(ParsePyprojectDependencies(text));
                if (text.Contains("[tool.uv]", StringComparison.OrdinalIgnoreCase))
                {
                    recommended = "uv";
                    installCmd = "uv sync";
                    hasLockfile = hasLockfile || File.Exists(Path.Combine(root, "uv.lock"));
                }
                else if (text.Contains("[tool.poetry]", StringComparison.OrdinalIgnoreCase))
                {
                    recommended = "poetry";
                    installCmd = "poetry install";
                    hasLockfile = hasLockfile || File.Exists(Path.Combine(root, "poetry.lock"));
                }
                else
                {
                    recommended = "pip";
                    installCmd = "pip install -e .";
                }
            }
        }

        if (File.Exists(requirements))
        {
            files.Add("requirements.txt");
            deps.AddRange(SafeReadLines(requirements)
                .Select(l => l.Split('#')[0].Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l)));
            recommended ??= "pip";
            installCmd ??= "pip install -r requirements.txt";
        }

        if (Directory.Exists(requirementsDir))
        {
            var reqFiles = SafeEnumerateFiles(requirementsDir, "*.txt");
            foreach (var f in reqFiles)
                files.Add("requirements/" + Path.GetFileName(f));
            if (reqFiles.Length > 0)
            {
                recommended ??= "pip";
                installCmd ??= "pip install -r requirements/base.txt";
            }
        }

        if (File.Exists(setupPy))
        {
            files.Add("setup.py");
            recommended ??= "pip";
            installCmd ??= "pip install -e .";
        }

        if (files.Count == 0) return null;

        recommended ??= "pip";
        installCmd ??= "pip install -r requirements.txt";

        return new ProjectInfoResult(
            Ecosystem: "python",
            DependencyFilesFound: [.. files],
            Dependencies: deps.Distinct().ToArray(),
            RecommendedPackageManager: recommended,
            InstallCommand: installCmd,
            RunCommand: recommended == "uv" ? "uv run python main.py" : "python main.py",
            BuildCommand: recommended == "poetry" ? "poetry build" : recommended == "uv" ? "uv build" : null,
            TestCommand: recommended == "poetry" ? "poetry run pytest" : recommended == "uv" ? "uv run pytest" : "pytest",
            HasLockfile: hasLockfile || AnyFileExists(root, "poetry.lock", "uv.lock", "Pipfile.lock"),
            HasVenv: hasVenv);
    }

    private static string[] ParsePyprojectDependencies(string text)
    {
        var deps = new List<string>();
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        bool inProjectDeps = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("[project]", StringComparison.OrdinalIgnoreCase))
            {
                inProjectDeps = true;
                continue;
            }
            if (line.StartsWith('[') && line.Contains("dependencies"))
            {
                // Other dependency sections are handled as encountered; keep scanning.
                continue;
            }
            if (line.StartsWith('['))
            {
                if (inProjectDeps) inProjectDeps = false;
                continue;
            }

            if (inProjectDeps && line.StartsWith("dependencies", StringComparison.OrdinalIgnoreCase))
            {
                var start = line.IndexOf('[');
                var end = line.IndexOf(']');
                if (start >= 0 && end > start)
                {
                    var arr = line[(start + 1)..end];
                    foreach (var item in arr.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var dep = item.Trim().Trim('"', '\'');
                        if (!string.IsNullOrWhiteSpace(dep)) deps.Add(dep);
                    }
                }
            }
        }

        return deps.ToArray();
    }

    // ── .NET ──────────────────────────────────────────────────────────────────

    private static ProjectInfoResult? TryDetectDotNet(string root)
    {
        var sln = SafeEnumerateFiles(root, "*.sln").FirstOrDefault();
        var csproj = SafeEnumerateFiles(root, "*.csproj").FirstOrDefault();
        var props = Path.Combine(root, "Directory.Build.props");

        var files = new List<string>();
        if (sln != null) files.Add(Path.GetFileName(sln));
        if (csproj != null) files.Add(Path.GetFileName(csproj));
        if (File.Exists(props)) files.Add("Directory.Build.props");

        if (files.Count == 0) return null;

        var deps = new List<string>();
        string? tfm = null;
        string? runCmd = null;
        string? testCmd = null;

        if (csproj != null)
        {
            var xml = SafeReadFirstBytes(csproj);
            if (xml != null)
            {
                try
                {
                    var doc = XDocument.Parse(xml);
                    var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                    tfm = doc.Descendants(ns + "TargetFramework")
                        .Concat(doc.Descendants(ns + "TargetFrameworks"))
                        .Select(e => e.Value.Trim())
                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    deps.AddRange(doc.Descendants(ns + "PackageReference")
                        .Select(e => e.Attribute("Include")?.Value)
                        .Where(v => !string.IsNullOrWhiteSpace(v))!);
                }
                catch { /* best-effort */ }
            }

            runCmd = csproj != null ? $"dotnet run --project {Path.GetFileName(csproj)}" : "dotnet run";
            testCmd = "dotnet test";
        }

        if (sln != null && runCmd == null)
            runCmd = "dotnet run";

        return new ProjectInfoResult(
            Ecosystem: ".net",
            DependencyFilesFound: [.. files],
            Dependencies: deps.Distinct().ToArray(),
            RecommendedPackageManager: "dotnet",
            InstallCommand: "dotnet restore",
            RunCommand: runCmd,
            BuildCommand: sln != null ? $"dotnet build {Path.GetFileName(sln)}" : "dotnet build",
            TestCommand: testCmd,
            HasLockfile: false,
            HasVenv: false);
    }

    // ── Node.js ───────────────────────────────────────────────────────────────

    private static ProjectInfoResult? TryDetectNode(string root)
    {
        var packageJson = Path.Combine(root, "package.json");
        if (!File.Exists(packageJson)) return null;

        var files = new List<string> { "package.json" };
        var deps = new List<string>();
        string? recommended = "npm";
        string? installCmd = "npm install";
        string? runCmd = null;
        string? buildCmd = null;
        string? testCmd = null;
        bool hasLockfile = false;

        if (File.Exists(Path.Combine(root, "pnpm-lock.yaml")))
        {
            recommended = "pnpm";
            installCmd = "pnpm install";
            files.Add("pnpm-lock.yaml");
            hasLockfile = true;
        }
        else if (File.Exists(Path.Combine(root, "yarn.lock")))
        {
            recommended = "yarn";
            installCmd = "yarn install";
            files.Add("yarn.lock");
            hasLockfile = true;
        }
        else if (File.Exists(Path.Combine(root, "package-lock.json")))
        {
            recommended = "npm";
            installCmd = "npm install";
            files.Add("package-lock.json");
            hasLockfile = true;
        }

        var text = SafeReadFirstBytes(packageJson);
        if (text != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var rootEl = doc.RootElement;
                if (rootEl.TryGetProperty("dependencies", out var d)) deps.AddRange(JsonObjectKeys(d));
                if (rootEl.TryGetProperty("devDependencies", out var dd)) deps.AddRange(JsonObjectKeys(dd));
                if (rootEl.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
                {
                    runCmd = PickScript(scripts, "dev", "start", "serve");
                    buildCmd = PickScript(scripts, "build");
                    testCmd = PickScript(scripts, "test");
                }
            }
            catch { /* best-effort */ }
        }

        return new ProjectInfoResult(
            Ecosystem: "node",
            DependencyFilesFound: [.. files],
            Dependencies: deps.Distinct().ToArray(),
            RecommendedPackageManager: recommended,
            InstallCommand: installCmd,
            RunCommand: runCmd ?? "node index.js",
            BuildCommand: buildCmd,
            TestCommand: testCmd,
            HasLockfile: hasLockfile,
            HasVenv: false);
    }

    private static string? PickScript(JsonElement scripts, params string[] preferences)
    {
        foreach (var name in preferences)
            if (scripts.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return $"{GetScriptRunner()} {name}";
        return null;
    }

    private static string GetScriptRunner()
    {
        // The dispatcher will call from the Node project context; keep it simple here.
        return "npm run";
    }

    // ── Rust ──────────────────────────────────────────────────────────────────

    private static ProjectInfoResult? TryDetectRust(string root)
    {
        var cargo = Path.Combine(root, "Cargo.toml");
        if (!File.Exists(cargo)) return null;

        var deps = new List<string>();
        var text = SafeReadFirstBytes(cargo);
        if (text != null)
        {
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('[') && trimmed.Contains("dependencies"))
                {
                    // simplistic: capture the next simple key = "..." lines until next section
                    // A line like `serde = "1"` or `serde = { version = "1" }`.
                    // We just capture the key name.
                    continue;
                }
                if (trimmed.StartsWith('[')) continue;
                var eq = trimmed.IndexOf('=');
                if (eq > 0)
                {
                    var key = trimmed[..eq].Trim();
                    if (!string.IsNullOrWhiteSpace(key) && !key.Contains(' ')) deps.Add(key);
                }
            }
        }

        return new ProjectInfoResult(
            Ecosystem: "rust",
            DependencyFilesFound: ["Cargo.toml"],
            Dependencies: deps.Distinct().ToArray(),
            RecommendedPackageManager: "cargo",
            InstallCommand: "cargo build",
            RunCommand: "cargo run",
            BuildCommand: "cargo build --release",
            TestCommand: "cargo test",
            HasLockfile: File.Exists(Path.Combine(root, "Cargo.lock")),
            HasVenv: false);
    }

    // ── Go ────────────────────────────────────────────────────────────────────

    private static ProjectInfoResult? TryDetectGo(string root)
    {
        var gomod = Path.Combine(root, "go.mod");
        if (!File.Exists(gomod)) return null;

        var deps = new List<string>();
        var text = SafeReadFirstBytes(gomod);
        if (text != null)
        {
            bool inRequireBlock = false;
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("require ", StringComparison.OrdinalIgnoreCase))
                {
                    var remainder = trimmed["require ".Length..].Trim();
                    if (remainder.StartsWith('('))
                    {
                        inRequireBlock = true;
                        continue;
                    }
                    // require pkg v1.2.3
                    var space = remainder.IndexOf(' ');
                    var dep = space > 0 ? remainder[..space] : remainder;
                    if (!string.IsNullOrWhiteSpace(dep)) deps.Add(dep);
                }
                else if (inRequireBlock)
                {
                    if (trimmed.StartsWith(')'))
                    {
                        inRequireBlock = false;
                        continue;
                    }
                    // pkg v1.2.3 // indirect
                    var space = trimmed.IndexOf(' ');
                    var dep = space > 0 ? trimmed[..space] : trimmed;
                    if (!string.IsNullOrWhiteSpace(dep)) deps.Add(dep);
                }
            }
        }

        return new ProjectInfoResult(
            Ecosystem: "go",
            DependencyFilesFound: ["go.mod"],
            Dependencies: deps.Distinct().ToArray(),
            RecommendedPackageManager: "go",
            InstallCommand: "go mod download",
            RunCommand: "go run .",
            BuildCommand: "go build ./...",
            TestCommand: "go test ./...",
            HasLockfile: File.Exists(Path.Combine(root, "go.sum")),
            HasVenv: false);
    }

    // ── PowerShell ────────────────────────────────────────────────────────────

    private static readonly Regex Psd1ModuleName = new(
        "ModuleName\\s*=\\s*['\"]([^'\"]+)['\"]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static ProjectInfoResult? TryDetectPowerShell(string root)
    {
        var psd1 = SafeEnumerateFiles(root, "*.psd1").FirstOrDefault();
        if (psd1 == null) return null;

        var deps = new List<string>();
        var text = SafeReadFirstBytes(psd1);
        if (text != null)
        {
            foreach (Match m in Psd1ModuleName.Matches(text))
                if (m.Groups[1].Value is { Length: > 0 } dep) deps.Add(dep);
        }

        return new ProjectInfoResult(
            Ecosystem: "powershell",
            DependencyFilesFound: [Path.GetFileName(psd1)],
            Dependencies: deps.Distinct().ToArray(),
            RecommendedPackageManager: "powershell",
            InstallCommand: $"Install-Module -Name {Path.GetFileNameWithoutExtension(psd1)}",
            RunCommand: null,
            BuildCommand: null,
            TestCommand: "Invoke-Pester",
            HasLockfile: false,
            HasVenv: false);
    }

    // ── Ruby ──────────────────────────────────────────────────────────────────

    private static ProjectInfoResult? TryDetectRuby(string root)
    {
        var gemfile = Path.Combine(root, "Gemfile");
        if (!File.Exists(gemfile)) return null;

        var deps = new List<string>();
        foreach (var line in SafeReadLines(gemfile))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("gem ", StringComparison.OrdinalIgnoreCase))
            {
                var remainder = trimmed["gem ".Length..].Trim();
                var quote = remainder.IndexOfAny(['"', '\'']);
                if (quote >= 0)
                {
                    var end = remainder.IndexOf(remainder[quote], quote + 1);
                    if (end > quote)
                    {
                        var dep = remainder[(quote + 1)..end].Trim();
                        if (!string.IsNullOrWhiteSpace(dep)) deps.Add(dep);
                    }
                }
            }
        }

        return new ProjectInfoResult(
            Ecosystem: "ruby",
            DependencyFilesFound: ["Gemfile"],
            Dependencies: deps.Distinct().ToArray(),
            RecommendedPackageManager: "bundle",
            InstallCommand: "bundle install",
            RunCommand: "bundle exec ruby main.rb",
            BuildCommand: null,
            TestCommand: "bundle exec rspec",
            HasLockfile: File.Exists(Path.Combine(root, "Gemfile.lock")),
            HasVenv: false);
    }

    // ── PHP ───────────────────────────────────────────────────────────────────

    private static ProjectInfoResult? TryDetectPhp(string root)
    {
        var composer = Path.Combine(root, "composer.json");
        if (!File.Exists(composer)) return null;

        var deps = new List<string>();
        var text = SafeReadFirstBytes(composer);
        if (text != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var rootEl = doc.RootElement;
                if (rootEl.TryGetProperty("require", out var req)) deps.AddRange(JsonObjectKeys(req));
                if (rootEl.TryGetProperty("require-dev", out var reqDev)) deps.AddRange(JsonObjectKeys(reqDev));
            }
            catch { /* best-effort */ }
        }

        return new ProjectInfoResult(
            Ecosystem: "php",
            DependencyFilesFound: ["composer.json"],
            Dependencies: deps.Distinct().ToArray(),
            RecommendedPackageManager: "composer",
            InstallCommand: "composer install",
            RunCommand: null,
            BuildCommand: null,
            TestCommand: "composer test",
            HasLockfile: File.Exists(Path.Combine(root, "composer.lock")),
            HasVenv: false);
    }
}
