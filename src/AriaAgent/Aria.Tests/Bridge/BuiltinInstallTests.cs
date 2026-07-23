using System.Text.Json;
using Aria.Bridge;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// install_software + system_info builtins: manager allowlist, injection-safe argument handling,
/// per-manager command rendering, the manager-missing error path, EnforceCommand defense-in-depth,
/// and a best-effort system_info probe that must not error in the test environment.
/// </summary>
public class BuiltinInstallTests
{
    private static Dictionary<string, JsonElement> Args(object o)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(o));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    // ── Command rendering (pure) ──────────────────────────────────────────────

    [Fact]
    public void Brew_RendersInstall_WithVersionInFormulaName()
    {
        Assert.Equal(["brew", "install", "ripgrep"],
            BuiltinTools.BuildInstallCommand("brew", "ripgrep", null, true, null));
        Assert.Equal(["brew", "install", "python@3.12"],
            BuiltinTools.BuildInstallCommand("brew", "python", "3.12", true, null));
    }

    [Fact]
    public void Npm_GlobalByDefault_VersionAsTag()
    {
        Assert.Equal(["npm", "install", "-g", "playwright"],
            BuiltinTools.BuildInstallCommand("npm", "playwright", null, true, null));
        Assert.Equal(["npm", "install", "-g", "typescript@5.4.2"],
            BuiltinTools.BuildInstallCommand("npm", "typescript", "5.4.2", true, null));
        Assert.Equal(["npm", "install", "playwright"],
            BuiltinTools.BuildInstallCommand("npm", "playwright", null, false, null));
    }

    [Fact]
    public void Pip_AlwaysUserSite_NeverSystemWide()
    {
        Assert.Equal(["pip", "install", "--user", "requests"],
            BuiltinTools.BuildInstallCommand("pip", "requests", null, true, null));
        Assert.Equal(["pip", "install", "--user", "requests==2.31.0"],
            BuiltinTools.BuildInstallCommand("pip", "requests", "2.31.0", true, null));
    }

    [Fact]
    public void Pipx_RendersInstall()
    {
        Assert.Equal(["pipx", "install", "black"],
            BuiltinTools.BuildInstallCommand("pipx", "black", null, true, null));
    }

    [Fact]
    public void Dotnet_ToolInstall_GlobalByDefault()
    {
        Assert.Equal(["dotnet", "tool", "install", "-g", "dotnet-ef"],
            BuiltinTools.BuildInstallCommand("dotnet", "dotnet-ef", null, true, null));
        Assert.Equal(["dotnet", "tool", "install", "-g", "dotnet-ef", "--version", "8.0.0"],
            BuiltinTools.BuildInstallCommand("dotnet", "dotnet-ef", "8.0.0", true, null));
        Assert.Equal(["dotnet", "tool", "install", "dotnet-ef"],
            BuiltinTools.BuildInstallCommand("dotnet", "dotnet-ef", null, false, null));
    }

    [Fact]
    public void Cargo_RendersInstall_WithVersionFlag()
    {
        Assert.Equal(["cargo", "install", "ripgrep"],
            BuiltinTools.BuildInstallCommand("cargo", "ripgrep", null, true, null));
        Assert.Equal(["cargo", "install", "ripgrep", "--version", "14.1.0"],
            BuiltinTools.BuildInstallCommand("cargo", "ripgrep", "14.1.0", true, null));
    }

    [Fact]
    public void Go_DefaultsToLatest()
    {
        Assert.Equal(["go", "install", "golang.org/x/tools/gopls@latest"],
            BuiltinTools.BuildInstallCommand("go", "golang.org/x/tools/gopls", null, true, null));
        Assert.Equal(["go", "install", "golang.org/x/tools/gopls@v0.16.1"],
            BuiltinTools.BuildInstallCommand("go", "golang.org/x/tools/gopls", "v0.16.1", true, null));
    }

    [Fact]
    public void Uv_RendersPipInstall()
    {
        Assert.Equal(["uv", "pip", "install", "requests"],
            BuiltinTools.BuildInstallCommand("uv", "requests", null, true, null));
        Assert.Equal(["uv", "pip", "install", "requests==2.31.0"],
            BuiltinTools.BuildInstallCommand("uv", "requests", "2.31.0", true, null));
    }

    [Fact]
    public void Yarn_GlobalByDefault_VersionAsTag()
    {
        Assert.Equal(["yarn", "global", "add", "playwright"],
            BuiltinTools.BuildInstallCommand("yarn", "playwright", null, true, null));
        Assert.Equal(["yarn", "add", "playwright"],
            BuiltinTools.BuildInstallCommand("yarn", "playwright", null, false, null));
        Assert.Equal(["yarn", "global", "add", "playwright@1.2.3"],
            BuiltinTools.BuildInstallCommand("yarn", "playwright", "1.2.3", true, null));
    }

    [Fact]
    public void Pnpm_GlobalByDefault_VersionAsTag()
    {
        Assert.Equal(["pnpm", "add", "-g", "playwright"],
            BuiltinTools.BuildInstallCommand("pnpm", "playwright", null, true, null));
        Assert.Equal(["pnpm", "add", "playwright"],
            BuiltinTools.BuildInstallCommand("pnpm", "playwright", null, false, null));
        Assert.Equal(["pnpm", "add", "-g", "playwright@1.2.3"],
            BuiltinTools.BuildInstallCommand("pnpm", "playwright", "1.2.3", true, null));
    }

    [Fact]
    public void Apt_SystemOnly_RequiresGlobal()
    {
        Assert.Equal(["apt-get", "install", "-y", "ripgrep"],
            BuiltinTools.BuildInstallCommand("apt", "ripgrep", null, true, null));
        Assert.Equal(["apt-get", "install", "-y", "ripgrep=1.2.3"],
            BuiltinTools.BuildInstallCommand("apt", "ripgrep", "1.2.3", true, null));
        Assert.Throws<ArgumentException>(() =>
            BuiltinTools.BuildInstallCommand("apt", "ripgrep", null, false, null));
    }

    [Fact]
    public void Choco_RendersInstall()
    {
        Assert.Equal(["choco", "install", "ripgrep", "-y"],
            BuiltinTools.BuildInstallCommand("choco", "ripgrep", null, true, null));
    }

    [Fact]
    public void Winget_RendersInstall()
    {
        Assert.Equal(["winget", "install", "--id", "Microsoft.PowerToys", "--accept-source-agreements", "--disable-interactivity"],
            BuiltinTools.BuildInstallCommand("winget", "Microsoft.PowerToys", null, true, null));
    }

    [Fact]
    public void ExtraArgs_AppendedAfterValidation()
    {
        Assert.Equal(["brew", "install", "ripgrep", "--HEAD"],
            BuiltinTools.BuildInstallCommand("brew", "ripgrep", null, true, ["--HEAD"]));
    }

    // ── Rejections (pure) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("yum")]
    [InlineData("dnf")]
    [InlineData("sudo")]
    [InlineData("/usr/local/bin/brew")]
    [InlineData("brew; rm -rf /")]
    public void ManagerOutsideAllowlist_Rejected(string manager)
        => Assert.Throws<ArgumentException>(
            () => BuiltinTools.BuildInstallCommand(manager, "ripgrep", null, true, null));

    [Theory]
    [InlineData("foo; rm -rf /")]   // command chaining
    [InlineData("foo|cat")]         // pipe
    [InlineData("$(whoami)")]       // command substitution
    [InlineData("foo && bar")]      // whitespace + ampersand
    [InlineData("foo>bar")]         // redirection
    [InlineData("foo`id`")]         // backticks
    public void PackageWithShellMetacharacters_Rejected(string package)
        => Assert.Throws<ArgumentException>(
            () => BuiltinTools.BuildInstallCommand("brew", package, null, true, null));

    [Theory]
    [InlineData("1.0; rm -rf /")]
    [InlineData("$(id)")]
    public void VersionWithShellMetacharacters_Rejected(string version)
        => Assert.Throws<ArgumentException>(
            () => BuiltinTools.BuildInstallCommand("npm", "playwright", version, true, null));

    [Theory]
    [InlineData("; rm -rf /")]
    [InlineData("--foo;bar")]
    [InlineData("$(whoami)")]
    public void ExtraArgsWithShellMetacharacters_Rejected(string extra)
        => Assert.Throws<ArgumentException>(
            () => BuiltinTools.BuildInstallCommand("brew", "ripgrep", null, true, [extra]));

    // ── Through the dispatcher ────────────────────────────────────────────────

    [Fact]
    public async Task UnknownManager_ToolReturnsError()
    {
        var r = await BuiltinTools.InvokeAsync("install_software",
            Args(new { manager = "yum", package = "ripgrep" }), policy: null);
        Assert.True(r.IsError);
        Assert.Contains("Unknown package manager", r.Text);
    }

    [Fact]
    public async Task MaliciousPackage_ToolReturnsError()
    {
        var r = await BuiltinTools.InvokeAsync("install_software",
            Args(new { manager = "brew", package = "foo; rm -rf /" }), policy: null);
        Assert.True(r.IsError);
        Assert.Contains("Invalid package", r.Text);
    }

    [Fact]
    public async Task MissingManager_ToolReturnsClearError()
    {
        // Guaranteed-absent binary: FindOnPath drives the tool's manager-missing branch, and this
        // name must never resolve.
        Assert.Null(BuiltinTools.FindOnPath("aria-not-a-real-manager-xyz"));

        var r = await BuiltinTools.InvokeAsync("install_software",
            Args(new { manager = "cargo", package = "definitely-not-a-real-crate-xyz" }),
            policy: null);
        // On a machine WITH cargo the install is attempted and fails on the bogus crate; on a
        // machine WITHOUT cargo the tool reports the missing manager. Both are errors — the test
        // pins the missing-manager message only when cargo is genuinely absent.
        Assert.True(r.IsError);
        if (BuiltinTools.FindOnPath("cargo") == null)
            Assert.Contains("not found on PATH", r.Text);
    }

    [Fact]
    public async Task RenderedCommand_GoesThroughEnforceCommand()
    {
        // The package name passes the charset check but is on the node-side blocklist — the
        // rendered command string must be refused before anything runs.
        var policy = new SecurityPolicy(BlockedCommands: ["evilpkg"]);
        var r = await BuiltinTools.InvokeAsync("install_software",
            Args(new { manager = "npm", package = "evilpkg" }), policy);
        Assert.True(r.IsError);
        Assert.Contains("BLOCKED", r.Text);
    }

    [Theory]
    [InlineData("uv")]
    [InlineData("yarn")]
    [InlineData("pnpm")]
    [InlineData("apt")]
    [InlineData("choco")]
    [InlineData("winget")]
    public async Task NewManagers_BlockedPackage_GoesThroughEnforceCommand(string manager)
    {
        var policy = new SecurityPolicy(BlockedCommands: ["evilpkg"]);
        var r = await BuiltinTools.InvokeAsync("install_software",
            Args(new { manager, package = "evilpkg", global = true }), policy);
        Assert.True(r.IsError);
        Assert.Contains("BLOCKED", r.Text);
    }

    [Fact]
    public async Task Apt_NonGlobal_RejectedThroughDispatcher()
    {
        var r = await BuiltinTools.InvokeAsync("install_software",
            Args(new { manager = "apt", package = "ripgrep", global = false }), policy: null);
        Assert.True(r.IsError);
        Assert.Contains("system-only", r.Text);
    }

    // ── system_info ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SystemInfo_ReturnsWithoutError()
    {
        var r = await BuiltinTools.InvokeAsync("system_info", Args(new { }), policy: null);
        Assert.False(r.IsError, r.Text);

        using var doc = JsonDocument.Parse(r.Text);
        var root = doc.RootElement;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("os").GetString()));
        Assert.True(root.GetProperty("cpu_count").GetInt32() > 0);
        Assert.True(root.GetProperty("total_ram_bytes").GetInt64() > 0);
        Assert.True(root.GetProperty("disk_free_bytes").GetInt64() > 0);
        // dotnet built and ran this test, so at least one runtime must be reported.
        Assert.True(root.GetProperty("runtimes").EnumerateObject().Any());
    }
}
