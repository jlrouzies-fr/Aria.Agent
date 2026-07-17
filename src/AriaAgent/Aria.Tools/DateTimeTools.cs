using System.ComponentModel;

namespace Aria.Tools;

public static class DateTimeTools
{
    [Description("Report the current temporal datum and time.")]
    public static string GetCurrentDateTime()
    {
        return DateTime.Now.ToString("yyyy-MM-ddtHH:mm:ss");
    }
}