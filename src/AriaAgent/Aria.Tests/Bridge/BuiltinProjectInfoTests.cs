using System.Text.Json;
using Aria.Bridge;
using Xunit;

namespace Aria.Tests.Bridge;

/// <summary>
/// project_info builtin: ecosystem detection, dependency parsing, command inference,
/// lockfile/venv hints, and path policy enforcement.
/// </summary>
public class BuiltinProjectInfoTests : IDisposable
{
    private readonly string _tempRoot;

    public BuiltinProjectInfoTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"aria-project-info-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static Dictionary<string, JsonElement> Args(object o)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(o));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private string MkDir(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string path, string content) => File.WriteAllText(path, content);

    private static async Task<JsonElement> CallProjectInfoAsync(string path, string[] allowedPaths)
    {
        var policy = new SecurityPolicy(AllowedPaths: allowedPaths);
        var r = await BuiltinTools.InvokeAsync("project_info", Args(new { path }), policy);
        Assert.False(r.IsError, r.Text);
        using var doc = JsonDocument.Parse(r.Text);
        return doc.RootElement.Clone();
    }

    private static JsonElement FirstEcosystem(JsonElement root)
    {
        var arr = root.GetProperty("ecosystems").EnumerateArray().ToArray();
        Assert.NotEmpty(arr);
        return arr[0];
    }

    // ── Python ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Python_PyprojectUv_Detected()
    {
        var dir = MkDir("py-uv");
        Write(Path.Combine(dir, "pyproject.toml"), """
            [project]
            name = "demo"
            version = "0.1.0"
            dependencies = [
                "requests>=2.31",
                "pydantic"
            ]

            [tool.uv]
            dev-dependencies = ["pytest"]
            """);
        Directory.CreateDirectory(Path.Combine(dir, ".venv"));

        var root = await CallProjectInfoAsync(dir, [dir]);
        var eco = FirstEcosystem(root);

        Assert.Equal("python", eco.GetProperty("ecosystem").GetString());
        Assert.Contains("pyproject.toml", eco.GetProperty("dependency_files_found").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("uv", eco.GetProperty("recommended_package_manager").GetString());
        Assert.Equal("uv sync", eco.GetProperty("install_command").GetString());
        Assert.Equal("uv run python main.py", eco.GetProperty("run_command").GetString());
        Assert.Equal("uv run pytest", eco.GetProperty("test_command").GetString());
        Assert.Equal("uv build", eco.GetProperty("build_command").GetString());
        Assert.True(eco.GetProperty("has_venv").GetBoolean());
    }

    [Fact]
    public async Task Python_Requirements_Detected()
    {
        var dir = MkDir("py-req");
        Write(Path.Combine(dir, "requirements.txt"), """
            requests==2.31.0
            numpy>=1.24
            # comment
            black
            """);
        Write(Path.Combine(dir, "Pipfile.lock"), "{}");

        var root = await CallProjectInfoAsync(dir, [dir]);
        var eco = FirstEcosystem(root);

        Assert.Equal("python", eco.GetProperty("ecosystem").GetString());
        Assert.Contains("requests==2.31.0", eco.GetProperty("dependencies").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("pip", eco.GetProperty("recommended_package_manager").GetString());
        Assert.Equal("pip install -r requirements.txt", eco.GetProperty("install_command").GetString());
        Assert.True(eco.GetProperty("has_lockfile").GetBoolean());
    }

    // ── .NET ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DotNet_CsprojAndSln_Detected()
    {
        var dir = MkDir("dotnet");
        Write(Path.Combine(dir, "Demo.sln"), "Microsoft Visual Studio Solution File");
        Write(Path.Combine(dir, "Demo.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
            </Project>
            """);

        var root = await CallProjectInfoAsync(dir, [dir]);
        var eco = FirstEcosystem(root);

        Assert.Equal(".net", eco.GetProperty("ecosystem").GetString());
        Assert.Contains("Demo.csproj", eco.GetProperty("dependency_files_found").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("Newtonsoft.Json", eco.GetProperty("dependencies").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("dotnet", eco.GetProperty("recommended_package_manager").GetString());
        Assert.Equal("dotnet restore", eco.GetProperty("install_command").GetString());
        Assert.Equal("dotnet build Demo.sln", eco.GetProperty("build_command").GetString());
        Assert.Equal("dotnet test", eco.GetProperty("test_command").GetString());
    }

    // ── Node.js ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Node_Pnpm_Detected()
    {
        var dir = MkDir("node-pnpm");
        Write(Path.Combine(dir, "package.json"), """
            {
              "name": "demo",
              "scripts": {
                "dev": "vite",
                "build": "tsc && vite build",
                "test": "vitest"
              },
              "dependencies": {
                "react": "^18.0.0"
              },
              "devDependencies": {
                "vite": "^5.0.0"
              }
            }
            """);
        Write(Path.Combine(dir, "pnpm-lock.yaml"), "lockfileVersion: '6.0'");

        var root = await CallProjectInfoAsync(dir, [dir]);
        var eco = FirstEcosystem(root);

        Assert.Equal("node", eco.GetProperty("ecosystem").GetString());
        Assert.Contains("react", eco.GetProperty("dependencies").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("vite", eco.GetProperty("dependencies").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("pnpm", eco.GetProperty("recommended_package_manager").GetString());
        Assert.Equal("pnpm install", eco.GetProperty("install_command").GetString());
        Assert.Equal("npm run dev", eco.GetProperty("run_command").GetString());
        Assert.Equal("npm run build", eco.GetProperty("build_command").GetString());
        Assert.Equal("npm run test", eco.GetProperty("test_command").GetString());
        Assert.True(eco.GetProperty("has_lockfile").GetBoolean());
    }

    [Fact]
    public async Task Node_Npm_Detected()
    {
        var dir = MkDir("node-npm");
        Write(Path.Combine(dir, "package.json"), """
            {"name":"demo","dependencies":{"express":"^4.0.0"}}
            """);
        Write(Path.Combine(dir, "package-lock.json"), "{}");

        var root = await CallProjectInfoAsync(dir, [dir]);
        var eco = FirstEcosystem(root);

        Assert.Equal("node", eco.GetProperty("ecosystem").GetString());
        Assert.Equal("npm", eco.GetProperty("recommended_package_manager").GetString());
        Assert.Equal("npm install", eco.GetProperty("install_command").GetString());
        Assert.True(eco.GetProperty("has_lockfile").GetBoolean());
    }

    // ── Rust ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rust_Cargo_Detected()
    {
        var dir = MkDir("rust");
        Write(Path.Combine(dir, "Cargo.toml"), """
            [package]
            name = "demo"
            version = "0.1.0"

            [dependencies]
            serde = { version = "1.0", features = ["derive"] }
            tokio = "1"
            """);
        Write(Path.Combine(dir, "Cargo.lock"), "");

        var root = await CallProjectInfoAsync(dir, [dir]);
        var eco = FirstEcosystem(root);

        Assert.Equal("rust", eco.GetProperty("ecosystem").GetString());
        Assert.Contains("serde", eco.GetProperty("dependencies").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("tokio", eco.GetProperty("dependencies").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("cargo", eco.GetProperty("recommended_package_manager").GetString());
        Assert.Equal("cargo build", eco.GetProperty("install_command").GetString());
        Assert.Equal("cargo run", eco.GetProperty("run_command").GetString());
        Assert.True(eco.GetProperty("has_lockfile").GetBoolean());
    }

    // ── Go ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Go_Mod_Detected()
    {
        var dir = MkDir("go");
        Write(Path.Combine(dir, "go.mod"), """
            module example.com/demo

            go 1.22

            require (
                github.com/gin-gonic/gin v1.9.0
                github.com/stretchr/testify v1.8.0
            )
            """);
        Write(Path.Combine(dir, "go.sum"), "");

        var root = await CallProjectInfoAsync(dir, [dir]);
        var eco = FirstEcosystem(root);

        Assert.Equal("go", eco.GetProperty("ecosystem").GetString());
        Assert.Contains("github.com/gin-gonic/gin", eco.GetProperty("dependencies").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("go", eco.GetProperty("recommended_package_manager").GetString());
        Assert.Equal("go mod download", eco.GetProperty("install_command").GetString());
        Assert.Equal("go run .", eco.GetProperty("run_command").GetString());
        Assert.True(eco.GetProperty("has_lockfile").GetBoolean());
    }

    // ── PowerShell ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PowerShell_Psd1_Detected()
    {
        var dir = MkDir("ps");
        Write(Path.Combine(dir, "MyModule.psd1"), """
            @{
                ModuleName = 'MyModule'
                RequiredModules = @(
                    @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
                )
            }
            """);

        var root = await CallProjectInfoAsync(dir, [dir]);
        var eco = FirstEcosystem(root);

        Assert.Equal("powershell", eco.GetProperty("ecosystem").GetString());
        Assert.Contains("MyModule", eco.GetProperty("dependencies").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("powershell", eco.GetProperty("recommended_package_manager").GetString());
        Assert.Equal("Invoke-Pester", eco.GetProperty("test_command").GetString());
    }

    // ── Ruby ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ruby_Gemfile_Detected()
    {
        var dir = MkDir("ruby");
        Write(Path.Combine(dir, "Gemfile"), """
            source 'https://rubygems.org'
            gem 'rails', '~> 7.0'
            gem 'pg'
            """);
        Write(Path.Combine(dir, "Gemfile.lock"), "");

        var root = await CallProjectInfoAsync(dir, [dir]);
        var eco = FirstEcosystem(root);

        Assert.Equal("ruby", eco.GetProperty("ecosystem").GetString());
        Assert.Contains("rails", eco.GetProperty("dependencies").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("bundle", eco.GetProperty("recommended_package_manager").GetString());
        Assert.Equal("bundle install", eco.GetProperty("install_command").GetString());
        Assert.True(eco.GetProperty("has_lockfile").GetBoolean());
    }

    // ── PHP ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Php_Composer_Detected()
    {
        var dir = MkDir("php");
        Write(Path.Combine(dir, "composer.json"), """
            {
              "name": "demo/app",
              "require": {
                "laravel/framework": "^10.0"
              },
              "require-dev": {
                "phpunit/phpunit": "^10.0"
              }
            }
            """);
        Write(Path.Combine(dir, "composer.lock"), "");

        var root = await CallProjectInfoAsync(dir, [dir]);
        var eco = FirstEcosystem(root);

        Assert.Equal("php", eco.GetProperty("ecosystem").GetString());
        Assert.Contains("laravel/framework", eco.GetProperty("dependencies").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("phpunit/phpunit", eco.GetProperty("dependencies").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("composer", eco.GetProperty("recommended_package_manager").GetString());
        Assert.Equal("composer install", eco.GetProperty("install_command").GetString());
        Assert.True(eco.GetProperty("has_lockfile").GetBoolean());
    }

    // ── Security / policy ─────────────────────────────────────────────────────

    [Fact]
    public async Task PathOutsideAllowedDirs_Blocked()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"aria-project-info-out-{Guid.NewGuid()}");
        Directory.CreateDirectory(outside);
        try
        {
            var policy = new SecurityPolicy(AllowedPaths: [_tempRoot]);
            var r = await BuiltinTools.InvokeAsync("project_info", Args(new { path = outside }), policy);
            Assert.True(r.IsError);
            Assert.Contains("BLOCKED", r.Text);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task MissingDirectory_ReturnsError()
    {
        var missing = Path.Combine(_tempRoot, "does-not-exist");
        var r = await BuiltinTools.InvokeAsync("project_info", Args(new { path = missing }), policy: null);
        Assert.True(r.IsError);
        Assert.Contains("Directory not found", r.Text);
    }

    // ── Multiple ecosystems ───────────────────────────────────────────────────

    [Fact]
    public async Task MixedDir_DetectsAllRelevantEcosystems()
    {
        var dir = MkDir("mixed");
        Write(Path.Combine(dir, "package.json"), "{\"name\":\"mixed\"}");
        Write(Path.Combine(dir, "requirements.txt"), "requests");

        var root = await CallProjectInfoAsync(dir, [dir]);
        var names = root.GetProperty("ecosystems").EnumerateArray()
            .Select(e => e.GetProperty("ecosystem").GetString())
            .ToArray();

        Assert.Contains("python", names);
        Assert.Contains("node", names);
    }
}
