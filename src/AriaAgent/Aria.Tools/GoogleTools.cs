using System.ComponentModel;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace Aria.Tools;

public static class GoogleTools
{
    private static string? _credentialsFile;
    private static Func<Task<string>>? _tokenOverride;

    private static readonly string[] Scopes =
    [
        GmailService.Scope.GmailReadonly,
        CalendarService.Scope.CalendarReadonly,
    ];

    private static readonly Lazy<Task<UserCredential>> _credentialLazy = new(InitializeAsync);

    public static void Configure(string credentialsFile)
    {
        _credentialsFile = credentialsFile;
    }

    /// <summary>
    /// Override file-based token acquisition with a custom provider (used by Aria.Web to inject DB-stored tokens).
    /// Pass null to revert to GoogleWebAuthorizationBroker.
    /// </summary>
    public static void SetTokenOverride(Func<Task<string>>? provider) => _tokenOverride = provider;

    private static string ResolvePath(string path) =>
        path.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;

    private static async Task<UserCredential> InitializeAsync()
    {
        if (string.IsNullOrEmpty(_credentialsFile))
            throw new InvalidOperationException(
                "Call GoogleTools.Configure(credentialsFile) before using Google tools. " +
                "Download the OAuth2 credentials JSON from Google Cloud Console " +
                "(APIs & Services → Credentials → your Desktop App client → Download JSON).");

        var resolved = ResolvePath(_credentialsFile);
        if (!File.Exists(resolved))
            throw new FileNotFoundException($"Google credentials file not found: {resolved}");

        var tokenStore = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aria-agent", "google-token");

        await using var stream = File.OpenRead(resolved);
        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            Scopes,
            "user",
            CancellationToken.None,
            new FileDataStore(tokenStore, fullPath: true));
    }

    private static async Task<Google.Apis.Http.IConfigurableHttpClientInitializer> GetCredentialAsync()
    {
        if (_tokenOverride is not null)
            return GoogleCredential.FromAccessToken(await _tokenOverride());
        return await _credentialLazy.Value;
    }

    private static async Task<GmailService> GetGmailServiceAsync() =>
        new(new BaseClientService.Initializer
        {
            HttpClientInitializer = await GetCredentialAsync(),
            ApplicationName = "Aria Agent",
        });

    private static async Task<CalendarService> GetCalendarServiceAsync() =>
        new(new BaseClientService.Initializer
        {
            HttpClientInitializer = await GetCredentialAsync(),
            ApplicationName = "Aria Agent",
        });

    public static async Task<string> EnsureAuthenticatedAsync()
    {
        var service = await GetGmailServiceAsync();
        var profile = await service.Users.GetProfile("me").ExecuteAsync();
        return profile.EmailAddress ?? "Authenticated";
    }

    [Description(
        "Fetches emails from the authenticated soul's Gmail with optional filtering. " +
        "Parameters:\n" +
        "- subject: Filter by keyword or phrase in the subject\n" +
        "- from: Filter by sender email or name\n" +
        "- to: Filter by recipient email or name\n" +
        "- hasAttachment: Set to true to include only messages with attachments\n" +
        "- startDate: Only include emails received after this date (ISO 8601 format)\n" +
        "- endDate: Only include emails received before this date (ISO 8601 format)\n" +
        "- top: Number of results to return (default 10, max 500)\n" +
        "- label: Gmail label to scope the search (e.g. INBOX, SENT, SPAM, TRASH, or a custom label name)\n" +
        "Returns a formatted list of matching emails with subject, from/to, date, and preview.")]
    public static async Task<string> GetGmailEmails(
        string? subject = null,
        string? from = null,
        string? to = null,
        bool? hasAttachment = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int top = 10,
        string? label = null)
    {
        try
        {
            var service = await GetGmailServiceAsync();

            var queryParts = new List<string>();
            if (!string.IsNullOrEmpty(subject))
                queryParts.Add(subject.Contains(' ') ? $"subject:\"{subject}\"" : $"subject:{subject}");
            if (!string.IsNullOrEmpty(from))
                queryParts.Add($"from:{from}");
            if (!string.IsNullOrEmpty(to))
                queryParts.Add($"to:{to}");
            if (hasAttachment == true)
                queryParts.Add("has:attachment");
            if (startDate.HasValue)
                queryParts.Add($"after:{startDate.Value:yyyy/M/d}");
            if (endDate.HasValue)
                queryParts.Add($"before:{endDate.Value.AddDays(1):yyyy/M/d}");
            if (!string.IsNullOrEmpty(label))
                queryParts.Add($"label:{label}");

            var listReq = service.Users.Messages.List("me");
            listReq.MaxResults = Math.Min(top, 500);
            if (queryParts.Count > 0)
                listReq.Q = string.Join(" ", queryParts);

            var listResult = await listReq.ExecuteAsync();
            var msgRefs = listResult.Messages;
            if (msgRefs is null || msgRefs.Count == 0)
                return "No emails found matching the specified criteria.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Found {msgRefs.Count} email(s):\n");

            foreach (var msgRef in msgRefs)
            {
                var getReq = service.Users.Messages.Get("me", msgRef.Id);
                getReq.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
                getReq.MetadataHeaders = new string[] { "From", "To", "Cc", "Subject", "Date" };
                var msg = await getReq.ExecuteAsync();

                var headers = (msg.Payload?.Headers ?? [])
                    .Where(h => h.Name is not null)
                    .ToDictionary(h => h.Name!, h => h.Value ?? "");

                sb.AppendLine($"Subject: {headers.GetValueOrDefault("Subject", "(no subject)")}");
                sb.AppendLine($"From: {headers.GetValueOrDefault("From", "Unknown")}");
                sb.AppendLine($"To: {headers.GetValueOrDefault("To", "")}");
                if (headers.TryGetValue("Cc", out var cc))
                    sb.AppendLine($"CC: {cc}");
                sb.AppendLine($"Date: {headers.GetValueOrDefault("Date", "")}");
                sb.AppendLine($"Preview: {msg.Snippet ?? "(empty)"}");
                sb.AppendLine("---");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Failed to fetch Gmail emails: {ex.Message}";
        }
    }

    [Description(
        "Lists Gmail labels (folders) for the authenticated soul, including system labels (INBOX, SENT, etc.) and custom labels. " +
        "Use this to discover label names before filtering emails by label.")]
    public static async Task<string> ListGmailLabels()
    {
        try
        {
            var service = await GetGmailServiceAsync();
            var response = await service.Users.Labels.List("me").ExecuteAsync();
            var labels = response.Labels ?? [];

            if (labels.Count == 0)
                return "No labels found.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Gmail Labels:\n");
            foreach (var label in labels.OrderBy(l => l.Type).ThenBy(l => l.Name))
                sb.AppendLine($"  [{label.Type ?? "user"}] {label.Name} (ID: {label.Id})");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Failed to list Gmail labels: {ex.Message}";
        }
    }

    [Description(
        "Fetches calendar events from the authenticated soul's Google Calendar with optional filtering. " +
        "Parameters:\n" +
        "- startDate: Start of the date range (ISO 8601 format, e.g., '2024-01-01'). Defaults to today.\n" +
        "- endDate: End of the date range (ISO 8601 format). Defaults to 30 days from start.\n" +
        "- searchText: Free-text search across event title and description\n" +
        "- top: Number of results to return (default 50, max 1000)\n" +
        "- calendarId: Calendar to query (default 'primary'). Use ListGoogleCalendars to discover other calendar IDs.\n" +
        "Returns a formatted list of events with subject, time, location, and organizer.")]
    public static async Task<string> GetGoogleCalendarEvents(
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? searchText = null,
        int top = 50,
        string? calendarId = null)
    {
        try
        {
            var service = await GetCalendarServiceAsync();

            var start = startDate ?? DateTime.Today;
            var end = endDate ?? start.AddDays(30);

            var request = service.Events.List(calendarId ?? "primary");
            request.TimeMinDateTimeOffset = new DateTimeOffset(start.Date, TimeSpan.Zero);
            request.TimeMaxDateTimeOffset = new DateTimeOffset(end.Date.AddDays(1), TimeSpan.Zero);
            request.MaxResults = Math.Min(top, 1000);
            request.SingleEvents = true;
            request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
            if (!string.IsNullOrEmpty(searchText))
                request.Q = searchText;

            var events = await request.ExecuteAsync();
            var items = events.Items ?? [];

            if (items.Count == 0)
                return "No calendar events found matching the specified criteria.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Found {items.Count} event(s):\n");

            foreach (var item in items)
            {
                sb.AppendLine($"Subject: {item.Summary ?? "(no title)"}");
                var startStr = item.Start?.DateTimeDateTimeOffset?.ToString("g") ?? item.Start?.Date ?? "";
                var endStr = item.End?.DateTimeDateTimeOffset?.ToString("g") ?? item.End?.Date ?? "";
                sb.AppendLine($"When: {startStr} — {endStr}");
                if (!string.IsNullOrEmpty(item.Location))
                    sb.AppendLine($"Location: {item.Location}");
                if (item.Organizer is not null)
                    sb.AppendLine($"Organizer: {item.Organizer.DisplayName} <{item.Organizer.Email}>");
                if (!string.IsNullOrEmpty(item.Description))
                    sb.AppendLine($"Description: {item.Description[..Math.Min(item.Description.Length, 200)]}");
                sb.AppendLine("---");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Failed to fetch Google Calendar events: {ex.Message}";
        }
    }

    [Description("Lists the authenticated soul's Google Calendars. Use this to discover calendar IDs for use with GetGoogleCalendarEvents.")]
    public static async Task<string> ListGoogleCalendars()
    {
        try
        {
            var service = await GetCalendarServiceAsync();
            var response = await service.CalendarList.List().ExecuteAsync();
            var calendars = response.Items ?? [];

            if (calendars.Count == 0)
                return "No calendars found.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Google Calendars:\n");
            foreach (var cal in calendars)
                sb.AppendLine($"  {cal.Summary ?? "(unnamed)"} — ID: {cal.Id} [{cal.AccessRole}]");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Failed to list Google Calendars: {ex.Message}";
        }
    }
}
