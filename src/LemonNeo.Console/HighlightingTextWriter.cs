using System.Text;
using System.Text.RegularExpressions;

namespace LemonNeo.ConsoleApp;

internal sealed class HighlightingTextWriter(TextWriter inner) : TextWriter
{
    private static readonly Regex HighlightedValues = new(
        "(\\\"[^\\\"]*\\\")|" +
        "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})|" +
        "(?<![A-Za-z])([0-9]+(?:\\.[0-9]+)?%?)|" +
        "(\\|)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();

    public override Encoding Encoding => inner.Encoding;

    public static bool CanUseColor =>
        !global::System.Console.IsOutputRedirected &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    public override void Write(char value)
    {
        lock (_gate)
        {
            inner.Write(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            inner.Write(value);
        }
    }

    public override void WriteLine()
    {
        lock (_gate)
        {
            inner.WriteLine();
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(value))
            {
                inner.WriteLine(value);
                return;
            }

            var original = global::System.Console.ForegroundColor;
            try
            {
                WriteHighlighted(value);
                inner.WriteLine();
            }
            finally
            {
                global::System.Console.ForegroundColor = original;
            }
        }
    }

    private static bool StartsWithAny(string value, params string[] prefixes) =>
        prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private void WriteHighlighted(string value)
    {
        if (StartsWithAny(value, "Error:", "Fatal error:", "Plugin load failed", "! "))
        {
            WriteColored(value, ConsoleColor.Red);
            return;
        }

        if (StartsWithAny(value, "Unknown command", "Expected ", "No ", "Not authenticated"))
        {
            WriteColored(value, ConsoleColor.Yellow);
            return;
        }

        if (StartsWithAny(
                value,
                "Imported ",
                "Created ",
                "Added ",
                "Matched ",
                "Playing",
                "Authenticated",
                "Signed out",
                "Playback paused",
                "Playback resumed",
                "Playback stopped",
                "Library saved",
                "Library updated"))
        {
            WriteColored(value, ConsoleColor.Green);
            return;
        }

        if (value is "[Plugin]" ||
            (value.StartsWith("[", StringComparison.Ordinal) && value.Contains(']')))
        {
            WriteColored(value, ConsoleColor.Magenta);
            return;
        }

        if (value is "COMMANDS" or "PROVIDER PARAMETERS" or "KEYS" or "Capabilities:" ||
            value.StartsWith("Lemon Neo", StringComparison.Ordinal))
        {
            WriteColored(value, ConsoleColor.Cyan);
            return;
        }

        var labelEnd = value.IndexOf(':');
        var position = 0;
        if (labelEnd > 0 && labelEnd < 28 &&
            value[..labelEnd].All(character => char.IsLetterOrDigit(character) ||
                                                char.IsWhiteSpace(character) ||
                                                character is '-' or '_'))
        {
            WriteColored(value[..(labelEnd + 1)], ConsoleColor.Cyan);
            position = labelEnd + 1;
        }

        foreach (Match match in HighlightedValues.Matches(value, position))
        {
            if (match.Index > position)
            {
                inner.Write(value[position..match.Index]);
            }

            var color = match.Value switch
            {
                "|" => ConsoleColor.DarkGray,
                _ when match.Value.StartsWith('"') => ConsoleColor.Yellow,
                _ when match.Value.Contains('-') && match.Value.Length == 36 => ConsoleColor.DarkCyan,
                _ => ConsoleColor.DarkYellow
            };
            WriteColored(match.Value, color);
            position = match.Index + match.Length;
        }

        if (position < value.Length)
        {
            inner.Write(value[position..]);
        }
    }

    private void WriteColored(string value, ConsoleColor color)
    {
        global::System.Console.ForegroundColor = color;
        inner.Write(value);
        global::System.Console.ResetColor();
    }
}
