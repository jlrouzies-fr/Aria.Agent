using System.Text;
using System.Text.Json;
using Aria.Agent;
using Xunit;

namespace Aria.Tests.Agent;

public class UniversalSSEStreamTests
{
    private static Stream CreateSseStream(params string[] lines)
    {
        var text = string.Join("\n", lines) + "\n";
        return new MemoryStream(Encoding.UTF8.GetBytes(text));
    }

    private static async Task<string> ReadAllTextAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<List<string>> ReadAllLinesAsync(Stream stream)
    {
        var lines = new List<string>();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            lines.Add(line);
        return lines;
    }

    private static string DataLine(string json) => $"data: {json}";

    private static string ContentDelta(string text) =>
        DataLine($"{{\"choices\":[{GetChoice($"{{\"content\":{JsonSerializer.Serialize(text)}}}")}]}}");

    private static string ReasoningDelta(string text) =>
        DataLine($"{{\"choices\":[{GetChoice($"{{\"reasoning_content\":{JsonSerializer.Serialize(text)}}}")}]}}");

    // LM Studio's GPT-OSS field: "reasoning", not "reasoning_content" (Harmony parsed server-side).
    private static string GptOssReasoningDelta(string text) =>
        DataLine($"{{\"choices\":[{GetChoice($"{{\"reasoning\":{JsonSerializer.Serialize(text)}}}")}]}}");

    private static string NativeToolCallDelta(string name, string args) =>
        DataLine($"{{\"choices\":[{GetChoice($"{{\"tool_calls\":[{GetToolCall(name, args)}]}}")}]}}");

    private static string FinishStop() =>
        DataLine($"{{\"choices\":[{GetChoice("{}", "stop")}]}}");

    private static string FinishToolCalls() =>
        DataLine($"{{\"choices\":[{GetChoice("{}", "tool_calls")}]}}");

    private static string GetChoice(string delta, string? finishReason = null) =>
        $"{{\"index\":0,\"delta\":{delta}{(finishReason != null ? $",\"finish_reason\":\"{finishReason}\"" : "")}}}";

    private static string GetToolCall(string name, string args) =>
        $"{{\"index\":0,\"id\":\"call_test\",\"type\":\"function\",\"function\":{{\"name\":\"{name}\",\"arguments\":{JsonSerializer.Serialize(args)}}}}}";

    // ── Thinking tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task ReasoningContent_EmitsThinkingAndStripsFromContent()
    {
        var reasoning = new List<string>();
        var inner = CreateSseStream(
            ReasoningDelta("Let me"),
            ReasoningDelta(" think"),
            ContentDelta("The answer is 42."),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner)
        {
            StreamThinkingLive = true,
            OnReasoningContent = r => reasoning.Add(r)
        };

        var output = await ReadAllTextAsync(stream);

        Assert.Equal(["Let me", " think"], reasoning);
        Assert.DoesNotContain("reasoning_content", output);
        Assert.Contains("The answer is 42.", output);
    }

    // Regression: GPT-OSS (via LM Studio) reasons under a "reasoning" key, then emits a NATIVE
    // tool_calls delta with no content field at all. The [DONE] pure-reasoning-model fallback used
    // to miss native tool calls (_hadToolCalls only covered client-parsed formats) and re-emitted
    // the whole reasoning buffer as bogus content — the same sentence appearing once as thinking
    // and once as the visible message.
    [Fact]
    public async Task GptOssReasoningThenNativeToolCall_DoesNotReemitReasoningAsContent()
    {
        var reasoning = new List<string>();
        var inner = CreateSseStream(
            GptOssReasoningDelta("Need to list dir of Spectra.MLX path."),
            NativeToolCallDelta("list_dir", "{\"path\":\"/tmp/spectra\"}"),
            FinishToolCalls(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner)
        {
            StreamThinkingLive = true,
            OnReasoningContent = r => reasoning.Add(r)
        };

        var output = await ReadAllTextAsync(stream);

        Assert.Equal(["Need to list dir of Spectra.MLX path."], reasoning);
        Assert.Contains("list_dir", output);                     // native tool call passes through
        Assert.DoesNotContain("\"content\":\"Need to list", output); // reasoning NOT re-emitted as content
    }

    // Guard the other side: a genuinely pure-reasoning model (no content, no tool calls) must still
    // get its reasoning re-emitted as content at [DONE], or the reply bubble would be blank.
    [Fact]
    public async Task PureReasoningModel_NoToolCalls_StillReemitsAsContent()
    {
        var inner = CreateSseStream(
            GptOssReasoningDelta("All my output is reasoning."),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner) { StreamThinkingLive = true };
        var output = await ReadAllTextAsync(stream);

        Assert.Contains("All my output is reasoning.", output);
    }

    [Fact]
    public async Task ThinkTags_EmitsThinkingAndStripsFromContent()
    {
        var reasoning = new List<string>();
        var inner = CreateSseStream(
            ContentDelta("<think>step 1"),
            ContentDelta(" step 2"),
            ContentDelta("</think>The answer is 42."),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner)
        {
            StreamThinkingLive = true,
            OnReasoningContent = r => reasoning.Add(r)
        };

        var output = await ReadAllTextAsync(stream);

        Assert.Equal(["step 1", " step 2"], reasoning);
        Assert.Contains("The answer is 42.", output);
        Assert.DoesNotContain("<think>", output);
    }

    [Fact]
    public async Task StartsInThinkMode_EmitsThinkingUntilCloseTag()
    {
        var reasoning = new List<string>();
        var inner = CreateSseStream(
            ContentDelta("step 1"),
            ContentDelta(" step 2"),
            ContentDelta("</think>The answer is 42."),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner, startsInThinkMode: true)
        {
            StreamThinkingLive = true,
            OnReasoningContent = r => reasoning.Add(r)
        };

        var output = await ReadAllTextAsync(stream);

        Assert.Equal(["step 1", " step 2"], reasoning);
        Assert.Contains("The answer is 42.", output);
        Assert.DoesNotContain("<think>", output);
    }

    // Regression: some models (e.g. Qwen3.x via LM Studio) close their think block with the
    // long-form </thinking> tag. The parser only matched </think>, so the close was missed and
    // the entire reply — literal </thinking> tag included — was swallowed into the thinking
    // buffer; the unresolved-thinking retry then regurgitated the same text as content,
    // doubling the whole output.
    [Fact]
    public async Task StartsInThinkMode_LongFormCloseTag_EmitsContentAfterClose()
    {
        var reasoning = new List<string>();
        var inner = CreateSseStream(
            ContentDelta("step 1"),
            ContentDelta(" step 2"),
            ContentDelta("</thinking>"),
            ContentDelta("The answer is 42."),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner, startsInThinkMode: true)
        {
            StreamThinkingLive = true,
            OnReasoningContent = r => reasoning.Add(r)
        };

        var output = await ReadAllTextAsync(stream);

        Assert.Equal(["step 1", " step 2"], reasoning);
        Assert.Contains("The answer is 42.", output);
        Assert.DoesNotContain("</thinking>", output);
        Assert.False(stream.EndedWithUnresolvedThinking);
    }

    // Same long-form close, but split across SSE chunks ("</think" + "ing>"): the held-back
    // partial close tail must be reassembled and still recognized.
    [Fact]
    public async Task StartsInThinkMode_LongFormCloseTag_SplitAcrossChunks()
    {
        var reasoning = new List<string>();
        var inner = CreateSseStream(
            ContentDelta("step 1</think"),
            ContentDelta("ing>The answer is 42."),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner, startsInThinkMode: true)
        {
            StreamThinkingLive = true,
            OnReasoningContent = r => reasoning.Add(r)
        };

        var output = await ReadAllTextAsync(stream);

        Assert.Equal(["step 1"], reasoning);
        Assert.Contains("The answer is 42.", output);
        Assert.DoesNotContain("</thinking>", output);
        Assert.False(stream.EndedWithUnresolvedThinking);
    }

    [Fact]
    public async Task ThinkTags_LongFormPair_StripsFromContent()
    {
        var reasoning = new List<string>();
        var inner = CreateSseStream(
            ContentDelta("<thinking>step 1"),
            ContentDelta(" step 2"),
            ContentDelta("</thinking>The answer is 42."),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner)
        {
            StreamThinkingLive = true,
            OnReasoningContent = r => reasoning.Add(r)
        };

        var output = await ReadAllTextAsync(stream);

        Assert.Equal(["step 1", " step 2"], reasoning);
        Assert.Contains("The answer is 42.", output);
        Assert.DoesNotContain("<thinking>", output);
        Assert.DoesNotContain("</thinking>", output);
    }

    [Fact]
    public async Task StartsInThinkMode_NoCloseTag_Stop_DiscardsMonologueAndSetsFlag()
    {
        var reasoning = new List<string>();
        var inner = CreateSseStream(
            ContentDelta("found the current rate is 1.1690"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner, startsInThinkMode: true)
        {
            StreamThinkingLive = true,
            OnReasoningContent = r => reasoning.Add(r)
        };

        var output = await ReadAllTextAsync(stream);

        Assert.Equal(["found the current rate is 1.1690"], reasoning);
        Assert.DoesNotContain("found the current rate", output);
        Assert.DoesNotContain("1.1690", output);
        Assert.True(stream.EndedWithUnresolvedThinking);
    }

    [Fact]
    public async Task StartsInThinkMode_NoCloseTag_ToolCallsFinish_DiscardsWithoutFlag()
    {
        var reasoning = new List<string>();
        var inner = CreateSseStream(
            ContentDelta("I need a tool"),
            FinishToolCalls(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner, startsInThinkMode: true)
        {
            StreamThinkingLive = true,
            OnReasoningContent = r => reasoning.Add(r)
        };

        var output = await ReadAllTextAsync(stream);

        Assert.Equal(["I need a tool"], reasoning);
        Assert.DoesNotContain("I need a tool", output);
        Assert.False(stream.EndedWithUnresolvedThinking);
    }

    [Fact]
    public async Task ThinkTags_NotLive_BuffersAndEmitsOnClose()
    {
        var reasoning = new List<string>();
        var inner = CreateSseStream(
            ContentDelta("<think>step 1"),
            ContentDelta(" step 2"),
            ContentDelta("</think>The answer is 42."),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner)
        {
            StreamThinkingLive = false,
            OnReasoningContent = r => reasoning.Add(r)
        };

        var output = await ReadAllTextAsync(stream);

        Assert.Single(reasoning);
        Assert.Equal("step 1 step 2", reasoning[0]);
        Assert.Contains("The answer is 42.", output);
    }

    // ── Tool-call tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCallTag_RewritesToNativeToolCall()
    {
        var inner = CreateSseStream(
            ContentDelta("<tool_call>{\"name\":\"get_time\",\"arguments\":{}}</tool_call>"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner);
        var lines = await ReadAllLinesAsync(stream);

        var toolLine = lines.FirstOrDefault(l => l.Contains("\"tool_calls\""));
        Assert.NotNull(toolLine);
        Assert.Contains("\"name\":\"get_time\"", toolLine);
        Assert.DoesNotContain("<tool_call>", string.Join("\n", lines));
    }

    [Fact]
    public async Task ToolCallTag_SplitOpenTag_NoMarkupLeaks()
    {
        // llama.cpp streams token-per-delta: "<tool_call>" can be split mid-tag.
        // The partial tail must be held back, re-attached, and the whole block rewritten.
        var inner = CreateSseStream(
            ContentDelta("<to"),
            ContentDelta("ol_call><function=bash_exec><parameter=command>curl -sI http://localhost</parameter></function></tool_call>"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner);
        var lines = await ReadAllLinesAsync(stream);

        var toolLine = lines.FirstOrDefault(l => l.Contains("\"tool_calls\""));
        Assert.NotNull(toolLine);
        Assert.Contains("\"name\":\"bash_exec\"", toolLine);
        Assert.DoesNotContain("<tool_call>", string.Join("\n", lines));
        Assert.DoesNotContain("<function=", string.Join("\n", lines));
    }

    [Fact]
    public async Task ToolCallTag_SplitOpenTagAfterProse_KeepsProseNoMarkupLeaks()
    {
        // Same split, but with prose preceding the tool call in the same chunk —
        // the prose must stay visible while the markup is consumed.
        var inner = CreateSseStream(
            ContentDelta("Good, server is up. Now let me verify:\n\n<to"),
            ContentDelta("ol_call><function=bash_exec><parameter=command>curl -sI http://localhost</parameter></function></tool_call>"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner);
        var lines = await ReadAllLinesAsync(stream);
        var output = string.Join("\n", lines);

        Assert.Contains("Good, server is up. Now let me verify:", output);
        var toolLine = lines.FirstOrDefault(l => l.Contains("\"tool_calls\""));
        Assert.NotNull(toolLine);
        Assert.Contains("\"name\":\"bash_exec\"", toolLine);
        Assert.DoesNotContain("<tool_call>", output);
        Assert.DoesNotContain("<function=", output);
    }

    [Fact]
    public async Task ToolCallTag_PartialOpenTagTailAtDone_FlushedAsContent()
    {
        // Stream ends while only a partial open tag was held back: it is not a tool
        // call after all — emit the tail as content instead of dropping it.
        var inner = CreateSseStream(
            ContentDelta("Let me show a literal tag: <to"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner);
        var output = await ReadAllTextAsync(stream);

        Assert.Contains("Let me show a literal tag:", output);
        Assert.Contains("\\u003Cto", output); // "<to" JSON-escaped by System.Text.Json
        Assert.DoesNotContain("\"tool_calls\"", output);
    }

    [Fact]
    public async Task ToolCallTag_TruncatedAtDone_BestEffortFlush()
    {
        // Close tag never arrives (stream truncated mid tool call): flush the buffer
        // through the same parse path instead of silently dropping the tool call.
        var inner = CreateSseStream(
            ContentDelta("<tool_call>{\"name\":\"get_time\",\"arguments\":{}}"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner);
        var lines = await ReadAllLinesAsync(stream);

        var toolLine = lines.FirstOrDefault(l => l.Contains("\"tool_calls\""));
        Assert.NotNull(toolLine);
        Assert.Contains("\"name\":\"get_time\"", toolLine);
        Assert.DoesNotContain("<tool_call>", string.Join("\n", lines));
    }

    [Fact]
    public async Task MistralToolCalls_RewritesToNativeToolCall()
    {
        var inner = CreateSseStream(
            ContentDelta("[TOOL_CALLS] [{\"name\":\"get_time\",\"arguments\":\"{}\"}]"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner);
        var lines = await ReadAllLinesAsync(stream);

        var toolLine = lines.FirstOrDefault(l => l.Contains("\"tool_calls\""));
        Assert.NotNull(toolLine);
        Assert.Contains("\"name\":\"get_time\"", toolLine);
        Assert.DoesNotContain("[TOOL_CALLS]", string.Join("\n", lines));
    }

    [Fact]
    public async Task Gemma4ToolCall_RewritesToNativeToolCall()
    {
        var inner = CreateSseStream(
            ContentDelta("<|tool_call>call:server:get_time{\"queries\":[]}<tool_call|>"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner);
        var lines = await ReadAllLinesAsync(stream);

        var toolLine = lines.FirstOrDefault(l => l.Contains("\"tool_calls\""));
        Assert.NotNull(toolLine);
        Assert.Contains("\"name\":\"get_time\"", toolLine);
    }

    // ── Functionary (human-forced, delimiter-less name\n{args}) ────────────────

    [Fact]
    public async Task Functionary_SplitTokens_RewritesToNativeToolCall()
    {
        // Mirrors the real LM Studio stream: the name arrives across several content deltas,
        // then a newline, then the JSON args — with finish_reason=stop, no native tool_calls.
        var inner = CreateSseStream(
            ContentDelta("get"),
            ContentDelta("_time"),
            ContentDelta("\n"),
            ContentDelta("{}"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner)
        {
            ForcedToolFormat = ToolCallFormat.Functionary,
            KnownToolNames   = new HashSet<string> { "get_time" }
        };
        var lines = await ReadAllLinesAsync(stream);

        var toolLine = lines.FirstOrDefault(l => l.Contains("\"tool_calls\""));
        Assert.NotNull(toolLine);
        Assert.Contains("\"name\":\"get_time\"", toolLine);
        // The bare name must NOT leak into assistant content.
        var contentLines = lines.Where(l => l.Contains("\"content\"")).ToList();
        Assert.DoesNotContain(contentLines, l => l.Contains("get_time"));
    }

    [Fact]
    public async Task Functionary_ArgsWithBracesInStrings_ParsedWhole()
    {
        var inner = CreateSseStream(
            ContentDelta("search_web\n{\"query\":\"a {nested} brace\"}"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner)
        {
            ForcedToolFormat = ToolCallFormat.Functionary,
            KnownToolNames   = new HashSet<string> { "search_web" }
        };
        var lines = await ReadAllLinesAsync(stream);

        var toolLine = lines.FirstOrDefault(l => l.Contains("\"tool_calls\""));
        Assert.NotNull(toolLine);
        Assert.Contains("\"name\":\"search_web\"", toolLine);
        Assert.Contains("nested", toolLine);   // full args captured, braces-in-string didn't truncate
    }

    [Fact]
    public async Task Functionary_PlainProse_NotMistakenForToolCall()
    {
        // A normal reply that does NOT open with a known tool name must pass straight through.
        var inner = CreateSseStream(
            ContentDelta("Hello, how "),
            ContentDelta("can I help?"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner)
        {
            ForcedToolFormat = ToolCallFormat.Functionary,
            KnownToolNames   = new HashSet<string> { "get_time" }
        };
        var output = await ReadAllTextAsync(stream);

        Assert.DoesNotContain("\"tool_calls\"", output);
        Assert.Contains("Hello, how", output);
        Assert.Contains("can I help?", output);
    }

    [Fact]
    public async Task NativeToolCall_PassesThrough()
    {
        var inner = CreateSseStream(
            NativeToolCallDelta("get_time", "{}"),
            FinishToolCalls(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner);
        var output = await ReadAllTextAsync(stream);

        Assert.Contains("\"tool_calls\"", output);
        Assert.Contains("\"name\":\"get_time\"", output);
    }

    [Fact]
    public async Task ToolCallTag_FinishReasonStop_RewritesToToolCalls()
    {
        var inner = CreateSseStream(
            ContentDelta("<tool_call>{\"name\":\"get_time\",\"arguments\":{}}</tool_call>"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner);
        var lines = await ReadAllLinesAsync(stream);

        var finishLine = lines.FirstOrDefault(l => l.Contains("\"finish_reason\""));
        Assert.NotNull(finishLine);
        Assert.Contains("tool_calls", finishLine);
    }

    // ── Combined / edge-case tests ───────────────────────────────────────────

    [Fact]
    public async Task ThinkThenToolCall_HandlesBoth()
    {
        var reasoning = new List<string>();
        var inner = CreateSseStream(
            ContentDelta("<think>I need a tool</think>"),
            ContentDelta("<tool_call>{\"name\":\"get_time\",\"arguments\":{}}</tool_call>"),
            FinishStop(),
            "data: [DONE]");

        var stream = new UniversalSSEStream(inner)
        {
            StreamThinkingLive = true,
            OnReasoningContent = r => reasoning.Add(r)
        };

        var lines = await ReadAllLinesAsync(stream);

        Assert.Single(reasoning);
        Assert.Equal("I need a tool", reasoning[0]);
        var toolLine = lines.FirstOrDefault(l => l.Contains("\"tool_calls\""));
        Assert.NotNull(toolLine);
        Assert.Contains("\"name\":\"get_time\"", toolLine);
    }

    [Fact]
    public async Task EmptyStream_DoneOnly_PassesThrough()
    {
        var inner = CreateSseStream("data: [DONE]");
        var stream = new UniversalSSEStream(inner);
        var output = await ReadAllTextAsync(stream);
        Assert.Contains("data: [DONE]", output);
    }
}
