using System.ComponentModel;
using Microsoft.Graph;

namespace Aria.Tools;

public static partial class GraphTools
{
    [Description("Fetches the most recent missive from the authenticated soul's Emperor Communication system.")]
    public static async Task<string> GetFirstEmail()
    {
        try
        {
            var client = await GetClientAsync();
            var messages = await client.Me.Messages.GetAsync(config =>
            {
                config.QueryParameters.Top = 1;
                config.QueryParameters.Select = ["subject", "from", "receivedDateTime", "bodyPreview"];
                config.QueryParameters.Orderby = ["receivedDateTime desc"];
            });

            var message = messages?.Value?.FirstOrDefault();
            if (message is null)
                return "No emails found.";

            return $"Subject: {message.Subject}\n" +
                   $"From: {message.From?.EmailAddress?.Name} <{message.From?.EmailAddress?.Address}>\n" +
                   $"Received: {message.ReceivedDateTime?.ToString("g")}\n" +
                   $"Preview: {message.BodyPreview}";
        }
        catch (Exception ex)
        {
            return $"Failed to fetch email: {ex.Message}";
        }
    }

    [Description(
        "Fetches missives from the authenticated soul's Emperor Communication system with advanced filtering. " +
        "Parameters:\n" +
        "- subject: Filter by keyword or exact phrase in the subject\n" +
        "- startDate: Only include emails received after this date (ISO 8601 format, e.g., '2024-01-01T00:00:00')\n" +
        "- endDate: Only include emails received before this date (ISO 8601 format)\n" +
        "- from: Filter by sender email address or display name\n" +
        "- to: Filter by recipient email address or display name\n" +
        "- cc: Filter by carbon-copy recipient email address or display name\n" +
        "- hasAttachment: Set to 'true' to include only messages with attachments\n" +
        "- top: Number of results to return (default 50, max 1000)\n" +
        "- folder: Optional folder name or ID to scope the search (e.g., 'Inbox', 'Drafts', 'SentItems', 'DeletedItems'). If you do not know the ID of a specific non-wellknown folder, use other tool to list folders first." +
        "Uses the user's entire mailbox when omitted.\n" +
        "Returns formatted list of matching emails with subject, from/to/cc, date, and attachment info.")]
    public static async Task<string> GetEmailsWithFilters(
        string? subject = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? from = null,
        string? to = null,
        string? cc = null,
        bool? hasAttachment = false,
        int top = 10,
        string? folder = null)
    {
        try
        {
            var client = await GetClientAsync();

            var filterParts = new List<string>();
            if (!string.IsNullOrEmpty(subject))
                filterParts.Add($"contains(tolower(subject), '{subject.ToLower()}')");

            if (startDate.HasValue)
                filterParts.Add($"receivedDateTime ge {startDate.Value:O}");

            if (endDate.HasValue)
                filterParts.Add($"receivedDateTime le {endDate.Value.AddDays(1):O}");

            if (!string.IsNullOrEmpty(from))
                filterParts.Add($"contains(tolower(from/emailAddress/address), '{from.ToLower()}')");

            if (!string.IsNullOrEmpty(to))
                filterParts.Add($"any(toRecipients: contains(tolower(toRecipients/emailAddress/address), '{to.ToLower()}'))");

            if (!string.IsNullOrEmpty(cc))
                filterParts.Add($"any(ccRecipients: contains(tolower(ccRecipients/emailAddress/address), '{cc.ToLower()}'))");

            if (hasAttachment == true)
                filterParts.Add("hasAttachments eq true");

            int maxTop = Math.Min(top, 1000);
            string[] select = ["subject", "from", "toRecipients", "ccRecipients",
                "receivedDateTime", "bodyPreview", "hasAttachments"];
            string? filter = filterParts.Count > 0 ? string.Join(" and ", filterParts) : null;
            string[] orderBy = ["receivedDateTime desc"];

            var messages = !string.IsNullOrEmpty(folder)
                ? await client.Me.MailFolders[folder].Messages.GetAsync(c =>
                {
                    c.QueryParameters.Top = maxTop;
                    c.QueryParameters.Select = select;
                    if (filter is not null) c.QueryParameters.Filter = filter;
                    c.QueryParameters.Orderby = orderBy;
                })
                : await client.Me.Messages.GetAsync(c =>
                {
                    c.QueryParameters.Top = maxTop;
                    c.QueryParameters.Select = select;
                    if (filter is not null) c.QueryParameters.Filter = filter;
                    c.QueryParameters.Orderby = orderBy;
                });

            var items = messages?.Value ?? [];
            if (items.Count == 0)
                return "No emails found matching the specified criteria.";

            var result = new System.Text.StringBuilder();
            result.AppendLine($"Found {items.Count} email(s):\n");

            foreach (var item in items)
            {
                result.AppendLine($"Subject: {item.Subject ?? "(no subject)"}");
                result.Append($"From: {(item.From?.EmailAddress?.Name ?? "Unknown")} <{(item.From?.EmailAddress?.Address ?? "?")}>");
                var toLines = (item.ToRecipients ?? [])
                    .Select(r => $"{(r.EmailAddress?.Name ?? "Unknown")} <{(r.EmailAddress?.Address ?? "?")}>")
                    .OrderByDescending(n => n);
                result.Append($"\nTo: {string.Join("; ", toLines)}");

                if (item.CcRecipients is not null && item.CcRecipients.Count > 0)
                {
                    var ccLines = item.CcRecipients.Select(r => $"{(r.EmailAddress?.Name ?? "Unknown")} <{(r.EmailAddress?.Address ?? "?")}>");
                    result.Append($"\nCC: {string.Join("; ", ccLines)}");
                }

                result.AppendLine($"\nReceived: {item.ReceivedDateTime?.ToString("g")}");
                if (item.HasAttachments.HasValue && item.HasAttachments.Value)
                    result.Append("\n[Has Attachment]");
                result.AppendLine($"\nPreview: {(item.BodyPreview ?? "(empty)")}");
                result.AppendLine("---");
            }

            return result.ToString();
        }
        catch (Exception ex)
        {
            return $"Failed to fetch emails: {ex.Message}";
        }
    }

    [Description("Lists the authenticated soul's mailbox folders with display name, total item count, unread count, and subfolder hierarchy (recursively). Always return in a formated table.")]
    public static async Task<string> ListMailboxFolders()
    {
        try
        {
            var client = await GetClientAsync();
            var result = new System.Text.StringBuilder();

            await AppendMailFolderTreeAsync(client, null, result, "", 0);

            return result.Length > 0
                ? result.ToString()
                : "No mailbox folders found.";
        }
        catch (Exception ex)
        {
            return $"Failed to fetch mailbox folders: {ex.Message}";
        }
    }

    private static async Task AppendMailFolderTreeAsync(
        GraphServiceClient client,
        string? parentFolderId,
        System.Text.StringBuilder result,
        string indent,
        int depth)
    {
        var folders = parentFolderId is null
            ? await client.Me.MailFolders.GetAsync(c =>
            {
                c.QueryParameters.Select = ["id", "displayName", "totalItemCount", "unreadItemCount", "childFolderCount"];
                c.QueryParameters.Orderby = ["displayName"];
            })
            : await client.Me.MailFolders[parentFolderId].ChildFolders.GetAsync(c =>
            {
                c.QueryParameters.Select = ["id", "displayName", "totalItemCount", "unreadItemCount", "childFolderCount"];
                c.QueryParameters.Orderby = ["displayName"];
            });

        var items = folders?.Value ?? [];
        foreach (var folder in items)
        {
            result.AppendLine($"{indent}{folder.DisplayName ?? "(unnamed)"} — {folder.TotalItemCount} total, {folder.UnreadItemCount} unread, ID: {folder.Id}");

            if (folder.ChildFolderCount > 0)
                await AppendMailFolderTreeAsync(client, folder.Id, result, indent + "    ", depth + 1);
        }
    }
}
