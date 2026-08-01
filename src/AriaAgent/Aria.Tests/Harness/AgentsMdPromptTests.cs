using System.Text.Json;
using Aria.Agent;
using Aria.Harness.Core;
using Aria.Harness.Tools;
using Aria.Tests.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aria.Tests.HarnessCore;

public class AgentsMdPromptTests
{
    [Fact]
    public async Task CreateSessionAsync_InjectsAgentsMd_WhenPresentOnActiveProject()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), "aria-agents-md-test");
        var runtime = new FakeHarnessRuntime { BridgeAvailable = true };
        runtime.AddSource(new ModelSource
        {
            Name = "OpenAI",
            Url = "https://api.openai.com/v1",
            IsPublicProvider = true,
            Models = ["gpt-4o"]
        });
        runtime.BridgePostHandler = (url, body, _, _) =>
        {
            Assert.Contains("/tools/call", url, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("read_file", doc.RootElement.GetProperty("toolName").GetString());
            var path = doc.RootElement.GetProperty("toolArguments").GetProperty("path").GetString();
            Assert.EndsWith("AGENTS.md", path, StringComparison.Ordinal);
            // Numbered form, as the real bridge returns.
            return Task.FromResult("""{"text":"1\t# AGENTS.md\n2\tNever trust the relay.","isError":false}""");
        };

        var progress = new List<string>();
        var harness = new Aria.Harness.Core.Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);
        var (agent, _) = await harness.CreateSessionAsync(new HarnessOptions
        {
            SelectedSourceName = "OpenAI",
            SelectedModel = "gpt-4o",
            EnabledTools = [new ActiveToolConfig("datetime")],
            ActiveProjectPath = projectPath,
            TerminalProjects = [("Demo", projectPath, "", null, null)],
            OnProgress = progress.Add,
        }, HarnessContext.Empty);

        var instructions = Assert.IsType<ChatClientAgent>(agent).Instructions;
        Assert.Contains("Project Charter (AGENTS.md)", instructions);
        Assert.Contains("Never trust the relay.", instructions);
        Assert.Contains(progress, p => p.Contains("AGENTS:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateSessionAsync_SkipsAgentsMd_WhenFileMissing()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), "aria-agents-md-missing");
        var runtime = new FakeHarnessRuntime { BridgeAvailable = true };
        runtime.AddSource(new ModelSource
        {
            Name = "OpenAI",
            Url = "https://api.openai.com/v1",
            IsPublicProvider = true,
            Models = ["gpt-4o"]
        });
        runtime.BridgePostHandler = (_, _, _, _) =>
            Task.FromResult("""{"text":"File not found: /x/AGENTS.md","isError":true}""");

        var harness = new Aria.Harness.Core.Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);
        var (agent, _) = await harness.CreateSessionAsync(new HarnessOptions
        {
            SelectedSourceName = "OpenAI",
            SelectedModel = "gpt-4o",
            EnabledTools = [new ActiveToolConfig("datetime")],
            ActiveProjectPath = projectPath,
            TerminalProjects = [("Demo", projectPath, "", null, null)],
        }, HarnessContext.Empty);

        var instructions = Assert.IsType<ChatClientAgent>(agent).Instructions;
        Assert.DoesNotContain("Project Charter (AGENTS.md)", instructions);
    }

    [Fact]
    public void BuildAddendum_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(AgentsMdPrompt.BuildAddendum(null));
        Assert.Null(AgentsMdPrompt.BuildAddendum(""));
        Assert.Null(AgentsMdPrompt.BuildAddendum("   \n  "));
    }

    [Fact]
    public void BuildAddendum_StripsReadFileLineNumbers_AndMarksBinding()
    {
        var numbered = "1\t# AGENTS.md\n2\tNever widen trust from the server.\n3\t";
        var addendum = AgentsMdPrompt.BuildAddendum(numbered);

        Assert.NotNull(addendum);
        Assert.Contains("Project Charter (AGENTS.md)", addendum);
        Assert.Contains("binding for this session", addendum);
        Assert.Contains("# AGENTS.md", addendum);
        Assert.Contains("Never widen trust from the server.", addendum);
        Assert.DoesNotContain("1\t#", addendum);
        Assert.DoesNotContain("2\tNever", addendum);
    }

    [Fact]
    public void BuildAddendum_AcceptsRawContent()
    {
        var addendum = AgentsMdPrompt.BuildAddendum("# Charter\nFail closed.");
        Assert.NotNull(addendum);
        Assert.Contains("# Charter", addendum);
        Assert.Contains("Fail closed.", addendum);
    }

    [Fact]
    public void BuildAddendum_TruncatesOverCap()
    {
        var huge = new string('x', AgentsMdPrompt.MaxChars + 500);
        var addendum = AgentsMdPrompt.BuildAddendum(huge);

        Assert.NotNull(addendum);
        Assert.Contains("truncated", addendum, StringComparison.OrdinalIgnoreCase);
        // Body inside the fences should not exceed the cap by much (wrapper text is extra).
        Assert.True(addendum!.Length < AgentsMdPrompt.MaxChars + 800);
    }

    [Theory]
    [InlineData("/Users/me/proj", "/Users/me/proj/AGENTS.md")]
    [InlineData("/Users/me/proj/", "/Users/me/proj/AGENTS.md")]
    [InlineData(@"C:\Users\me\proj", @"C:\Users\me\proj\AGENTS.md")]
    [InlineData(@"C:\Users\me\proj\", @"C:\Users\me\proj\AGENTS.md")]
    public void ResolvePath_PreservesNativeSeparator(string root, string expected) =>
        Assert.Equal(expected, AgentsMdPrompt.ResolvePath(root));

    [Fact]
    public void StripReadFileLineNumbers_LeavesUnnumberedLinesAlone()
    {
        const string raw = "plain\n1 not-a-number-prefix\nok";
        Assert.Equal(raw, AgentsMdPrompt.StripReadFileLineNumbers(raw));
    }
}
