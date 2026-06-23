using System.Text;

namespace LeaseBook.SharedKernel;

/// <summary>
/// Sanitizes untrusted values before they are written to logs, preventing log forging (CWE-117):
/// strips control characters (including CR/LF and tab) so a crafted value cannot inject or spoof
/// additional log entries when the log is rendered as plain text. Apply to any user-provided string
/// that flows into a log message.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Returns <paramref name="value"/> with all control characters (including CR/LF/tab) removed.
    /// <see langword="null"/> or empty input returns <see cref="string.Empty"/>. Input with no control
    /// characters is returned unchanged (no allocation).
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var firstControl = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]))
            {
                firstControl = i;
                break;
            }
        }

        if (firstControl < 0)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        sb.Append(value, 0, firstControl);
        for (var i = firstControl; i < value.Length; i++)
        {
            var c = value[i];
            if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
