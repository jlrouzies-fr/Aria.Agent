using Aria.Agent;
using Aria.Harness.Core;
using Aria.Harness.Tools;
using Aria.Tests.Fakes;
using Aria.Web.Services.Chat;
using Aria.Web.Services.Cogitations;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aria.Tests.Web;

public class CogitationRunSteerTests
{
    [Fact]
    public async Task TrySteer_WhenStreaming_EnqueuesPendingMessage()
    {
        var runtime = new FakeHarnessRuntime();
        runtime.AddSource(new ModelSource
        {
            Name = "OpenAI",
            Url = "https://api.openai.com/v1",
            IsPublicProvider = true,
            Models = ["gpt-4o"]
        });

        var harness = new Aria.Harness.Core.Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);
        var (agent, session) = await harness.CreateSessionAsync(new HarnessOptions
        {
            SelectedSourceName = "OpenAI",
            SelectedModel = "gpt-4o",
            EnabledTools = [new ActiveToolConfig("datetime")]
        }, Aria.Harness.Core.HarnessContext.Empty);

        var run = new CogitationRun(
            cogitationId: 1,
            userId: "test-user",
            originNodeId: null,
            subAgentId: null,
            agentSourceName: "OpenAI",
            agentModel: "gpt-4o",
            agent: agent,
            session: session,
            router: new CogitationStreamRouter(),
            reply: new MessageEntry("assistant", ""),
            sealService: null!);

        Assert.True(run.TrySteer("use the other approach"));

        var chatAgent = Assert.IsType<ChatClientAgent>(agent);
#pragma warning disable MAAI001
        var injector = Assert.IsType<MessageInjectingChatClient>(
            chatAgent.GetService(typeof(MessageInjectingChatClient)));
        var pending = injector.GetPendingMessages(session);
#pragma warning restore MAAI001
        var expected = CogitationRun.FormatSteerForModel("use the other approach");
        Assert.Contains(pending, m => m.Text == expected);
        Assert.Contains("CONTINUE", expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrySteer_WhenNotStreaming_ReturnsFalse()
    {
        var runtime = new FakeHarnessRuntime();
        runtime.AddSource(new ModelSource
        {
            Name = "OpenAI",
            Url = "https://api.openai.com/v1",
            IsPublicProvider = true,
            Models = ["gpt-4o"]
        });

        var harness = new Aria.Harness.Core.Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);
        var (agent, session) = await harness.CreateSessionAsync(new HarnessOptions
        {
            SelectedSourceName = "OpenAI",
            SelectedModel = "gpt-4o",
            EnabledTools = []
        }, Aria.Harness.Core.HarnessContext.Empty);

        var run = new CogitationRun(
            1, "test-user", null, null, "OpenAI", "gpt-4o",
            agent, session, new CogitationStreamRouter(), new MessageEntry("assistant", ""), null!);

        run.SetStatus(CogitationRunStatus.Completed);

        Assert.False(run.TrySteer("too late"));
        Assert.False(run.TrySteer(""));
        Assert.False(run.TrySteer("   "));
    }

    [Fact]
    public async Task RotateReplyForSteer_SealsMeaningfulContent_AndStartsFreshReply()
    {
        var runtime = new FakeHarnessRuntime();
        runtime.AddSource(new ModelSource
        {
            Name = "OpenAI",
            Url = "https://api.openai.com/v1",
            IsPublicProvider = true,
            Models = ["gpt-4o"]
        });

        var harness = new Aria.Harness.Core.Harness(NullLogger<Aria.Harness.Core.Harness>.Instance, runtime);
        var (agent, session) = await harness.CreateSessionAsync(new HarnessOptions
        {
            SelectedSourceName = "OpenAI",
            SelectedModel = "gpt-4o",
            EnabledTools = []
        }, Aria.Harness.Core.HarnessContext.Empty);

        var reply = new MessageEntry("assistant", "") { AgentName = "ARIA" };
        var run = new CogitationRun(
            1, "test-user", null, null, "OpenAI", "gpt-4o",
            agent, session, new CogitationStreamRouter(), reply, null!);

        // Empty bubble (constructor adds a blank Content section) — nothing to seal.
        Assert.Null(run.RotateReplyForSteer());
        Assert.Same(reply, run.Reply);

        run.AppendContent("1 AD + 1 = 2");
        var sealedReply = run.RotateReplyForSteer();
        Assert.NotNull(sealedReply);
        Assert.Same(reply, sealedReply);
        Assert.Equal("1 AD + 1 = 2", sealedReply!.Content);
        Assert.NotSame(reply, run.Reply);
        Assert.Equal("ARIA", run.Reply.AgentName);
        Assert.True(string.IsNullOrWhiteSpace(run.Reply.Content));

        run.AppendContent("A B C D");
        Assert.Equal("A B C D", run.Reply.Content);
        Assert.Equal("1 AD + 1 = 2", sealedReply.Content);
    }
}
