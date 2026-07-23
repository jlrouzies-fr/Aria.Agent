using Aria.Shared;
using Xunit;

namespace Aria.Tests.Shared;

/// <summary>
/// Locks the Layer B (§4) sensitivity taxonomy: only the high-authority server→bridge paths are
/// Sensitive; the control plane stays Benign so enabling enforcement can't wedge it.
/// </summary>
public class RequestClassifierTests
{
    [Theory]
    [InlineData("/llm/proxy")]
    [InlineData("/tools/call")]
    [InlineData("/terminal/exec")]
    [InlineData("/project-files/list")]      // Explorer / "#" picker: server-driven project reads are exfiltration
    [InlineData("/project-files/tree")]
    [InlineData("/project-files/read")]
    [InlineData("/project-files/write")]
    [InlineData("/project-files/revert")]
    [InlineData("/project-git/run")]
    [InlineData("/tools/call/whatever")]     // sub-path
    [InlineData("/LLM/Proxy")]               // case-insensitive
    [InlineData("/llm/proxy?stream=1")]      // query stripped
    [InlineData("/tools/call/")]             // trailing slash
    public void SensitivePaths_AreSensitive(string path)
        => Assert.Equal(RequestSensitivity.Sensitive, RequestClassifier.Classify("POST", path));

    [Theory]
    [InlineData("/health")]
    [InlineData("/keys")]
    [InlineData("/tools/list")]              // listing tools is not calling them
    [InlineData("/seal/poll")]
    [InlineData("/sync/apply")]
    [InlineData("/metrics")]
    [InlineData("/oauth/google/token")]
    [InlineData("/llm/probe")]               // format probe, not a real spend
    [InlineData("/tools")]                   // not the /tools/call prefix
    [InlineData("")]
    [InlineData(null)]
    public void ControlPlaneAndReads_AreBenign(string? path)
        => Assert.Equal(RequestSensitivity.Benign, RequestClassifier.Classify("POST", path));

    [Fact]
    public void ToolsListIsNotToolsCall()
    {
        Assert.False(RequestClassifier.IsSensitive("POST", "/tools/list"));
        Assert.True(RequestClassifier.IsSensitive("POST", "/tools/call"));
    }

    // ── Body-aware /tools/call classification ──────────────────────────────────

    [Theory]
    [InlineData("read_file")]
    [InlineData("list_dir")]
    [InlineData("glob")]
    [InlineData("grep")]
    [InlineData("commands_index")]
    [InlineData("git_status")]
    [InlineData("git_diff")]
    [InlineData("git_log")]
    [InlineData("system_info")]
    [InlineData("process_list")]
    [InlineData("process_output")]
    [InlineData("read_image")]
    [InlineData("wait_for")]
    public void ToolsCall_ReadOnlyBuiltin_IsBenign(string tool)
    {
        var body = $$"""{"toolName":"{{tool}}"}""";
        Assert.Equal(RequestSensitivity.Benign, RequestClassifier.Classify("POST", "/tools/call", body));
    }

    [Theory]
    [InlineData("bash_exec")]
    [InlineData("run_background")]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("delete_file")]
    [InlineData("move_path")]
    [InlineData("git_stage")]
    [InlineData("git_commit")]
    [InlineData("git_discard")]
    [InlineData("install_software")]
    [InlineData("process_kill")]
    [InlineData("multi_edit")]
    [InlineData("undo_file")]
    public void ToolsCall_WriteOrExecBuiltin_IsSensitive(string tool)
    {
        var body = $$"""{"toolName":"{{tool}}"}""";
        Assert.Equal(RequestSensitivity.Sensitive, RequestClassifier.Classify("POST", "/tools/call", body));
    }

    [Fact]
    public void ToolsCall_McpTool_IsSensitive()
    {
        // ServerName present → MCP tool of unknown capability → sensitive even if its name looks read-y.
        var body = """{"toolName":"read_file","serverName":"some-mcp"}""";
        Assert.Equal(RequestSensitivity.Sensitive, RequestClassifier.Classify("POST", "/tools/call", body));
    }

    [Fact]
    public void ToolsCall_PascalCaseBody_IsHandled()
    {
        Assert.Equal(RequestSensitivity.Benign,
            RequestClassifier.Classify("POST", "/tools/call", """{"ToolName":"read_file"}"""));
        Assert.Equal(RequestSensitivity.Sensitive,
            RequestClassifier.Classify("POST", "/tools/call", """{"ToolName":"bash_exec"}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]                       // no toolName → can't tell
    public void ToolsCall_UnparseableOrUnknownBody_FailsSensitive(string? body)
        => Assert.Equal(RequestSensitivity.Sensitive, RequestClassifier.Classify("POST", "/tools/call", body));

    [Fact]
    public void BodyAware_NonToolCallPath_MatchesPathOnly()
    {
        // For non-/tools/call paths the body is irrelevant; behaviour matches the path-only overload.
        Assert.Equal(RequestSensitivity.Sensitive, RequestClassifier.Classify("POST", "/llm/proxy", "{}"));
        Assert.Equal(RequestSensitivity.Benign, RequestClassifier.Classify("GET", "/keys", "{}"));
    }
}
