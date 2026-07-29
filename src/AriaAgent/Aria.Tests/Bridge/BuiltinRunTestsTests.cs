using System.Runtime.InteropServices;
using System.Text.Json;
using Aria.Bridge;
using Aria.Bridge.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// run_tests builtin: Projects capability gate, cwd scoping, command/filter validation, generic +
/// sniffed-parser results through real shell runs, and the inference / filter-flag helpers.
/// </summary>
public class BuiltinRunTestsTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly BridgeDbContext _db;

    public BuiltinRunTestsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"aria-rt-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // run_tests shares bash_exec's gate: the per-node Projects capability on a throwaway db.
        _dbPath = Path.Combine(Path.GetTempPath(), $"aria-rt-tests-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _db = new BridgeDbContext(opts);
        _db.Database.EnsureCreated();
        _db.Souls.Add(new BridgeSoul { Name = "test", ProjectsEnabled = true });
        _db.SaveChanges();

        BuiltinTools.ResetSessionCwd();
    }

    public void Dispose()
    {
        BuiltinTools.ResetSessionCwd();
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static Dictionary<string, JsonElement> Args(object o)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(o));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private SecurityPolicy Policy() => new(AllowedPaths: [_root]);

    private string MkDir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ── Registration & gates ────────────────────────────────────────────────────

    [Fact]
    public void Manifest_ExposesRunTests()
    {
        Assert.Contains(BuiltinTools.GetToolInfos(), t => t.Name == "run_tests");
    }

    [Fact]
    public async Task ProjectsDisabled_Refused()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"aria-rt-noproj-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<BridgeDbContext>().UseSqlite($"Data Source={dbPath}").Options;
        using var db = new BridgeDbContext(opts);
        db.Database.EnsureCreated();   // no soul → Projects off
        try
        {
            var r = await BuiltinTools.InvokeAsync("run_tests",
                Args(new { cwd = _root, command = "true" }), Policy(), db);
            Assert.True(r.IsError);
            Assert.Contains("Projects not enabled", r.Text);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task CwdOutsideAllowedPaths_Blocked()
    {
        var r = await BuiltinTools.InvokeAsync("run_tests",
            Args(new { cwd = "/etc", command = "true" }), Policy(), _db);
        Assert.True(r.IsError);
        Assert.Contains("BLOCKED", r.Text);
    }

    // ── Validation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CustomCommand_RejectsFilter()
    {
        var r = await BuiltinTools.InvokeAsync("run_tests",
            Args(new { cwd = _root, command = "dotnet test", filter = "Cart" }), Policy(), _db);
        Assert.True(r.IsError);
        Assert.Contains("filter", r.Text);
    }

    [Fact]
    public async Task UnknownKind_Rejected()
    {
        var r = await BuiltinTools.InvokeAsync("run_tests",
            Args(new { cwd = _root, kind = "explode" }), Policy(), _db);
        Assert.True(r.IsError);
        Assert.Contains("test|build|lint|run", r.Text);
    }

    [Fact]
    public async Task EmptyDir_NoInference_GuidanceError()
    {
        var empty = MkDir("empty");
        var r = await BuiltinTools.InvokeAsync("run_tests",
            Args(new { cwd = empty }), Policy(), _db);
        Assert.True(r.IsError);
        Assert.Contains("Couldn't infer a test command", r.Text);
    }

    [Fact]
    public async Task NodeProject_WithoutTestScript_GuidanceError()
    {
        var dir = MkDir("node-notests");
        File.WriteAllText(Path.Combine(dir, "package.json"), """{"name":"demo"}""");

        var r = await BuiltinTools.InvokeAsync("run_tests",
            Args(new { cwd = dir }), Policy(), _db);
        Assert.True(r.IsError);
        Assert.Contains("Couldn't infer a test command", r.Text);
    }

    // ── Shell runs (POSIX-only: the composite commands assume /bin/sh) ──────────

    [Fact]
    public async Task CustomCommand_Success_GenericParser()
    {
        if (IsWindows) return;

        var r = await BuiltinTools.InvokeAsync("run_tests",
            Args(new { cwd = _root, command = "echo suite-ok; exit 0" }), Policy(), _db);

        Assert.False(r.IsError, r.Text);
        Assert.Contains("TEST RUN [echo suite-ok; exit 0] — PASSED (exit 0", r.Text);
        Assert.Contains("suite-ok", r.Text);   // no counts recognised → trailing lines kept
    }

    [Fact]
    public async Task CustomCommand_Failure_IncludesTail()
    {
        if (IsWindows) return;

        var r = await BuiltinTools.InvokeAsync("run_tests",
            Args(new { cwd = _root, command = "echo boom-line; exit 1" }), Policy(), _db);

        Assert.True(r.IsError);
        Assert.Contains("— FAILED (exit 1", r.Text);
        Assert.Contains("output tail", r.Text);
        Assert.Contains("boom-line", r.Text);
    }

    [Fact]
    public async Task CustomCommand_SniffsPytestOutput()
    {
        if (IsWindows) return;

        // A shim whose name makes the parser sniff "pytest"; prints a captured pytest summary.
        var shim = Path.Combine(_root, "pytest-shim.sh");
        File.WriteAllText(shim, """
            #!/bin/sh
            echo 'FAILED tests/test_cart.py::test_empty - boom'
            echo '========================= 1 failed, 2 passed in 0.12s ========================='
            exit 1
            """);

        var r = await BuiltinTools.InvokeAsync("run_tests",
            Args(new { cwd = _root, command = $"sh {shim}" }), Policy(), _db);

        Assert.True(r.IsError);
        Assert.Contains("passed: 2  failed: 1", r.Text);
        Assert.Contains("✗ tests/test_cart.py::test_empty — tests/test_cart.py", r.Text);
    }

    // ── Inference & filter mapping (no shell needed) ────────────────────────────

    [Fact]
    public void InferCommand_DotNet_Test()
    {
        var dir = MkDir("dotnet");
        File.WriteAllText(Path.Combine(dir, "Demo.sln"), "Microsoft Visual Studio Solution File");
        // project_info only infers "dotnet test" from a .csproj — a bare .sln carries no test command.
        File.WriteAllText(Path.Combine(dir, "Demo.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var inferred = BuiltinTools.InferCommand(dir, "test");
        Assert.NotNull(inferred);
        Assert.Equal(".net", inferred!.Value.Ecosystem);
        Assert.Equal("dotnet test", inferred.Value.Command);
    }

    [Fact]
    public void InferCommand_Node_BuildKind()
    {
        var dir = MkDir("node-build");
        File.WriteAllText(Path.Combine(dir, "package.json"),
            """{"name":"demo","scripts":{"build":"tsc","test":"vitest"}}""");

        Assert.Equal("npm run build", BuiltinTools.InferCommand(dir, "build")!.Value.Command);
        Assert.Equal("npm run test",  BuiltinTools.InferCommand(dir, "test")!.Value.Command);
    }

    [Fact]
    public void InferCommand_Lint_NotInferred()
    {
        var dir = MkDir("lint");
        File.WriteAllText(Path.Combine(dir, "package.json"), """{"name":"demo"}""");

        Assert.Null(BuiltinTools.InferCommand(dir, "lint"));
    }

    [Fact]
    public void ApplyFilter_DotNet_WrapsBareName()
    {
        var cmd = BuiltinTools.ApplyFilter(".net", "dotnet test", "Checkout");
        Assert.NotNull(cmd);
        Assert.Contains("--filter", cmd);
        Assert.Contains("FullyQualifiedName~Checkout", cmd);
    }

    [Fact]
    public void ApplyFilter_DotNet_PassesExpressionThrough()
    {
        var cmd = BuiltinTools.ApplyFilter(".net", "dotnet test", "Category=Unit");
        Assert.NotNull(cmd);
        Assert.Contains("Category=Unit", cmd);
        Assert.DoesNotContain("FullyQualifiedName~", cmd);
    }

    [Fact]
    public void ApplyFilter_Mappings()
    {
        Assert.Contains("-k",           BuiltinTools.ApplyFilter("python", "pytest", "cart")!);
        Assert.Contains("-- -t",        BuiltinTools.ApplyFilter("node", "npm run test", "cart")!);
        Assert.Contains("-run",         BuiltinTools.ApplyFilter("go", "go test ./...", "TestCart")!);
        Assert.StartsWith("cargo test", BuiltinTools.ApplyFilter("rust", "cargo test", "cart")!);
        Assert.Null(BuiltinTools.ApplyFilter("php", "composer test", "cart"));
    }
}
