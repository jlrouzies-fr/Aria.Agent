using System.Collections.Concurrent;
using System.Text;

namespace Aria.Web.Services.Chat;

public class ExchangeSession
{
    public string         Id                  { get; set; } = Guid.NewGuid().ToString("N");
    public string         Topic               { get; set; } = "";
    public int            TotalRounds         { get; set; } = 5;
    public int            CompletedRounds     { get; set; } = 0;
    public ExchangeStatus Status              { get; set; } = ExchangeStatus.Pending;

    public string InitiatorUserId    { get; set; } = "";
    public string InitiatorName      { get; set; } = "";
    public string InitiatorAgentLabel { get; set; } = "ARIA";
    public int?   InitiatorAgentId   { get; set; }

    public string RecipientUserId    { get; set; } = "";
    public string RecipientName      { get; set; } = "";
    public string RecipientAgentLabel { get; set; } = "ARIA";
    public int?   RecipientAgentId   { get; set; }

    public readonly object         TurnsLock = new();
    public          List<ExchangeTurn> Turns { get; } = [];
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
    public string?  ErrorMessage { get; set; }
}

public enum ExchangeStatus { Pending, Active, Completed, Declined, Error }

public record ExchangeTurn(int Round, bool IsInitiator, string AgentLabel, string Content, DateTime At);

public class ExchangeSessionService(
    AgentBackgroundExecutor executor,
    BridgeCogitationClient  bridgeClient,
    ILogger<ExchangeSessionService> logger)
{
    private readonly ConcurrentDictionary<string, ExchangeSession> _sessions = new();

    // Blazor components subscribe to these; filter by userId/exchangeId as needed.
    public event Action<string /*recipientUserId*/, ExchangeSession>? InviteReceived;
    public event Action<string /*exchangeId*/, ExchangeTurn>?      TurnCompleted;
    public event Action<string /*exchangeId*/>?                    StatusChanged;

    public static bool IsTopicAllowed(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return false;
        var lower = topic.ToLowerInvariant();
        string[] blocked =
        [
            "fuck", "shit", "cunt", "nigger", "nigga", "faggot", "kike", "chink", "spic", "wetback",
            "rape", "pedophil", "child porn", "porn", "bomb making", "terrorist", "nazi", "hitler",
            "kill yourself", "kys", "suicide",
        ];
        return !blocked.Any(t => lower.Contains(t));
    }

    public ExchangeSession CreateInvite(
        string initiatorUserId,    string initiatorName,   string initiatorAgentLabel, int? initiatorAgentId,
        string recipientUserId,    string recipientName,
        string topic,              int    roundCount)
    {
        var session = new ExchangeSession
        {
            Topic               = topic,
            TotalRounds         = roundCount,
            InitiatorUserId     = initiatorUserId,
            InitiatorName       = initiatorName,
            InitiatorAgentLabel = initiatorAgentLabel,
            InitiatorAgentId    = initiatorAgentId,
            RecipientUserId     = recipientUserId,
            RecipientName       = recipientName,
        };
        _sessions[session.Id] = session;
        InviteReceived?.Invoke(recipientUserId, session);
        return session;
    }

    public ExchangeSession? AcceptInvite(string exchangeId, string recipientAgentLabel, int? recipientAgentId)
    {
        if (!_sessions.TryGetValue(exchangeId, out var session)) return null;
        if (session.Status != ExchangeStatus.Pending) return session;

        session.RecipientAgentLabel = recipientAgentLabel;
        session.RecipientAgentId    = recipientAgentId;
        session.Status              = ExchangeStatus.Active;
        StatusChanged?.Invoke(exchangeId);

        _ = Task.Run(() => RunOrchestrationAsync(session, CancellationToken.None));
        return session;
    }

    public ExchangeSession? DeclineInvite(string exchangeId)
    {
        if (!_sessions.TryGetValue(exchangeId, out var session)) return null;
        session.Status = ExchangeStatus.Declined;
        StatusChanged?.Invoke(exchangeId);
        return session;
    }

    public ExchangeSession? GetSession(string id) => _sessions.GetValueOrDefault(id);

    public IEnumerable<ExchangeSession> GetPendingForUser(string userId) =>
        _sessions.Values.Where(s => s.RecipientUserId == userId && s.Status == ExchangeStatus.Pending);

    public IEnumerable<ExchangeSession> GetActiveForUser(string userId) =>
        _sessions.Values.Where(s =>
            (s.InitiatorUserId == userId || s.RecipientUserId == userId) &&
            s.Status is ExchangeStatus.Active or ExchangeStatus.Completed);

    // ─── Orchestration ────────────────────────────────────────────────────────

    private async Task RunOrchestrationAsync(ExchangeSession session, CancellationToken ct)
    {
        try
        {
            for (int round = 1; round <= session.TotalRounds; round++)
            {
                // Initiator's turn
                var initPrompt = BuildPrompt(session, round, isInitiator: true);
                var initText   = await executor.RunHeadlessAsync(
                    session.InitiatorUserId, session.InitiatorAgentId,
                    initPrompt, null, null, ct: ct);

                var initTurn = new ExchangeTurn(round, true, session.InitiatorAgentLabel, initText.Trim(), DateTime.UtcNow);
                lock (session.TurnsLock) session.Turns.Add(initTurn);
                TurnCompleted?.Invoke(session.Id, initTurn);

                if (ct.IsCancellationRequested) break;

                // Recipient's turn
                var recipPrompt = BuildPrompt(session, round, isInitiator: false);
                var recipText   = await executor.RunHeadlessAsync(
                    session.RecipientUserId, session.RecipientAgentId,
                    recipPrompt, null, null, ct: ct);

                var recipTurn = new ExchangeTurn(round, false, session.RecipientAgentLabel, recipText.Trim(), DateTime.UtcNow);
                lock (session.TurnsLock) session.Turns.Add(recipTurn);
                session.CompletedRounds = round;
                TurnCompleted?.Invoke(session.Id, recipTurn);
            }

            session.Status = ExchangeStatus.Completed;
            StatusChanged?.Invoke(session.Id);
            _ = PushTranscriptsToBridgesAsync(session);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exchange orchestration failed for {ExchangeId}", session.Id);
            session.Status       = ExchangeStatus.Error;
            session.ErrorMessage = ex.Message;
            StatusChanged?.Invoke(session.Id);
        }
    }

    private static string BuildPrompt(ExchangeSession session, int round, bool isInitiator)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// AGENT-TO-AGENT INTELLECTUAL EXCHANGE //");
        sb.AppendLine($"Topic: {session.Topic}");
        sb.AppendLine($"Round {round} of {session.TotalRounds}.");
        sb.AppendLine();

        List<ExchangeTurn> snapshot;
        lock (session.TurnsLock) snapshot = [.. session.Turns];

        if (snapshot.Count > 0)
        {
            sb.AppendLine("EXCHANGE TRANSCRIPT SO FAR:");
            foreach (var t in snapshot)
            {
                var label = t.IsInitiator ? session.InitiatorAgentLabel : session.RecipientAgentLabel;
                sb.AppendLine();
                sb.AppendLine($"[{label}]:");
                sb.AppendLine(t.Content);
            }
            sb.AppendLine();
        }

        if (isInitiator && round == 1)
            sb.AppendLine("Begin the exchange. Present your opening position or perspective on the topic clearly and substantively. Aim for 2–4 paragraphs.");
        else if (isInitiator)
            sb.AppendLine("Your turn. Build upon or challenge the previous message. Advance the dialogue. Stay focused. 2–4 paragraphs.");
        else
            sb.AppendLine("Your turn. Engage critically and constructively with the previous message. Move the conversation forward. 2–4 paragraphs.");

        return sb.ToString();
    }

    private async Task PushTranscriptsToBridgesAsync(ExchangeSession session)
    {
        try
        {
            List<ExchangeTurn> turns;
            lock (session.TurnsLock) turns = [.. session.Turns];

            // Push from initiator's perspective (their turns = isOurs)
            await bridgeClient.PushExchangeTranscriptAsync(
                session.InitiatorUserId.ToString(), session.InitiatorUserId,
                session.Id, session.Topic,
                turns.Select(t => (t.AgentLabel, t.Content, t.IsInitiator)).ToList());

            // Push from recipient's perspective (recipient turns = isOurs)
            await bridgeClient.PushExchangeTranscriptAsync(
                session.RecipientUserId.ToString(), session.RecipientUserId,
                session.Id, session.Topic,
                turns.Select(t => (t.AgentLabel, t.Content, !t.IsInitiator)).ToList());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push exchange transcript to bridges");
        }
    }
}
