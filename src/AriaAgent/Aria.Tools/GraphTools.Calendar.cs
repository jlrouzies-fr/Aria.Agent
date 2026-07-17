using System.ComponentModel;

namespace Aria.Tools;

public static partial class GraphTools
{
    [Description(
        "Fetches calendar events from the authenticated soul's default calendar from the Emperor Communication System with optional filtering. " +
        "Parameters:\n" +
        "- startDate: Start of the date range (ISO 8601 format, e.g., '2024-01-01'). Defaults to today.\n" +
        "- endDate: End of the date range (ISO 8601 format). Defaults to 30 days from start.\n" +
        "- subject: Filter by keyword or exact phrase in the subject\n" +
        "- top: Number of results to return (default 50, max 1000)\n" +
        "Returns formatted list of calendar events with subject, start/end time, location, and organizer.")]
    public static async Task<string> GetCalendarEvents(
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? subject = null,
        int top = 50)
    {
        try
        {
            var client = await GetClientAsync();

            var start = startDate ?? DateTime.Today;
            var end = endDate ?? start.AddDays(30);

            var filterParts = new List<string>();
            if (!string.IsNullOrEmpty(subject))
                filterParts.Add($"contains(tolower(subject), '{subject.ToLower()}')");

            var events = await client.Me.CalendarView.GetAsync(config =>
            {
                config.QueryParameters.StartDateTime = start.ToString("yyyy-MM-ddTHH:mm:ssZ");
                config.QueryParameters.EndDateTime = end.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
                config.QueryParameters.Select = ["subject", "start", "end", "location", "organizer", "bodyPreview", "isAllDay", "categories"];

                if (filterParts.Count > 0)
                    config.QueryParameters.Filter = string.Join(" and ", filterParts);

                config.QueryParameters.Top = Math.Min(top, 1000);
                config.QueryParameters.Orderby = ["start/dateTime asc"];
            });

            var items = events?.Value ?? [];
            if (items.Count == 0)
                return "No calendar events found matching the specified criteria.";

            var result = new System.Text.StringBuilder();
            result.AppendLine($"Found {items.Count} event(s):\n");

            foreach (var item in items)
            {
                result.AppendLine($"Subject: {item.Subject ?? "(no subject)"}");
                result.AppendLine($"When: {item.Start?.DateTime} ({item.Start?.TimeZone}) - {item.End?.DateTime} ({item.End?.TimeZone})");
                if (item.IsAllDay == true)
                    result.AppendLine("  [All Day]");
                if (item.Location?.DisplayName is not null)
                    result.AppendLine($"Location: {item.Location.DisplayName}");
                if (item.Organizer?.EmailAddress is not null)
                    result.AppendLine($"Organizer: {item.Organizer.EmailAddress.Name} <{item.Organizer.EmailAddress.Address}>");
                if (item.Categories is { Count: > 0 })
                    result.AppendLine($"Categories: {string.Join(", ", item.Categories)}");
                if (!string.IsNullOrEmpty(item.BodyPreview))
                    result.AppendLine($"Preview: {item.BodyPreview}");
                result.AppendLine("---");
            }

            return result.ToString();
        }
        catch (Exception ex)
        {
            return $"Failed to fetch calendar events: {ex.Message}";
        }
    }
}
