using System.Text;

namespace LemonNeo.ConsoleApp;

internal sealed class InteractiveLineEditor(
    Func<string, int, CommandCompletion> completionSource,
    IReadOnlyList<string> history,
    Func<LyricsFrame>? lyricsSource = null)
{
    private const string PromptText = "lemon > ";
    private const int MaximumSuggestions = 5;

    private readonly IReadOnlyList<string> _history = history;
    private readonly StringBuilder _buffer = new();
    private int _cursor;
    private int _selectedSuggestion;
    private bool _suggestionsHidden;
    private bool _historyNavigationActive;
    private int _top;
    private int _reservedLines;
    private LyricsFrame? _lastLyricsFrame;
    private int _lastLyricsWidth;
    private int _windowWidth;
    private int _windowHeight;
    private int _inputColumn;

    private int LyricsLines => lyricsSource is null ? 0 : Math.Min(3, _reservedLines - 1);
    private int ContentLines => _reservedLines - LyricsLines;

    public static bool IsSupported =>
        SupportsInteractiveInput(global::System.Console.IsInputRedirected,
            global::System.Console.IsOutputRedirected, Environment.GetEnvironmentVariable("TERM"));

    internal static bool SupportsInteractiveInput(bool inputRedirected, bool outputRedirected, string? term) =>
        !inputRedirected && !outputRedirected &&
        !string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase);

    public string? ReadLine(CancellationToken cancellationToken)
    {
        _buffer.Clear();
        _cursor = 0;
        _selectedSuggestion = 0;
        _suggestionsHidden = false;
        _historyNavigationActive = false;

        var historyIndex = _history.Count;
        var historyDraft = string.Empty;
        var treatControlCAsInput = global::System.Console.TreatControlCAsInput;

        try
        {
            global::System.Console.TreatControlCAsInput = true;
            ReserveDrawingArea();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completion = GetCompletion();
                Render(completion);

                while (!global::System.Console.KeyAvailable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.Sleep(50);
                    if (_windowWidth != global::System.Console.WindowWidth ||
                        _windowHeight != global::System.Console.WindowHeight)
                    {
                        Render(completion);
                    }
                    else
                    {
                        RenderLyrics();
                    }
                }
                var key = global::System.Console.ReadKey(intercept: true);
                var control = (key.Modifiers & ConsoleModifiers.Control) != 0;

                if (control && key.Key == ConsoleKey.C)
                {
                    ClearDrawingArea();
                    MoveCursor(0, _top);
                    throw new OperationCanceledException("Console input was cancelled.");
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (completion.PreferSelection &&
                        completion.Suggestions.Count > 0 &&
                        !completion.CurrentValueIsExact)
                    {
                        ApplySuggestion(completion);
                        continue;
                    }

                    return FinishLine();
                }

                if (key.Key == ConsoleKey.Tab)
                {
                    if (completion.Suggestions.Count > 0)
                    {
                        ApplySuggestion(completion);
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (!_historyNavigationActive &&
                        _buffer.Length > 0 &&
                        completion.Suggestions.Count > 0)
                    {
                        _selectedSuggestion =
                            (_selectedSuggestion - 1 + completion.Suggestions.Count) %
                            completion.Suggestions.Count;
                    }
                    else if (_history.Count > 0 && historyIndex > 0)
                    {
                        if (historyIndex == _history.Count)
                        {
                            historyDraft = _buffer.ToString();
                        }

                        historyIndex--;
                        SetHistoryBuffer(_history[historyIndex]);
                        _historyNavigationActive = true;
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.DownArrow)
                {
                    if (!_historyNavigationActive &&
                        _buffer.Length > 0 &&
                        completion.Suggestions.Count > 0)
                    {
                        _selectedSuggestion = (_selectedSuggestion + 1) % completion.Suggestions.Count;
                    }
                    else if (historyIndex < _history.Count)
                    {
                        historyIndex++;
                        SetHistoryBuffer(historyIndex == _history.Count ? historyDraft : _history[historyIndex]);
                        _historyNavigationActive = historyIndex < _history.Count;
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.LeftArrow)
                {
                    _cursor = Math.Max(0, _cursor - 1);
                    continue;
                }

                if (key.Key == ConsoleKey.RightArrow)
                {
                    _cursor = Math.Min(_buffer.Length, _cursor + 1);
                    continue;
                }

                if (key.Key == ConsoleKey.Home || (control && key.Key == ConsoleKey.A))
                {
                    _cursor = 0;
                    continue;
                }

                if (key.Key == ConsoleKey.End || (control && key.Key == ConsoleKey.E))
                {
                    _cursor = _buffer.Length;
                    continue;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (_cursor > 0)
                    {
                        _buffer.Remove(--_cursor, 1);
                        Changed();
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.Delete)
                {
                    if (_cursor < _buffer.Length)
                    {
                        _buffer.Remove(_cursor, 1);
                        Changed();
                    }

                    continue;
                }

                if (control && key.Key == ConsoleKey.U)
                {
                    if (_cursor > 0)
                    {
                        _buffer.Remove(0, _cursor);
                        _cursor = 0;
                        Changed();
                    }

                    continue;
                }

                if (control && key.Key == ConsoleKey.D && _buffer.Length == 0)
                {
                    ClearDrawingArea();
                    MoveCursor(0, _top);
                    return null;
                }

                if (control && key.Key == ConsoleKey.Spacebar)
                {
                    _suggestionsHidden = false;
                    continue;
                }

                if (key.Key == ConsoleKey.Escape)
                {
                    _suggestionsHidden = true;
                    continue;
                }

                if (!control && key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    _buffer.Insert(_cursor++, key.KeyChar);
                    Changed();
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
            TryClearDrawingArea();
            global::System.Console.Write(PromptText);
            return global::System.Console.ReadLine();
        }
        finally
        {
            global::System.Console.TreatControlCAsInput = treatControlCAsInput;
        }
    }

    private CommandCompletion GetCompletion()
    {
        var completion = completionSource(_buffer.ToString(), _cursor);
        if (_historyNavigationActive)
        {
            return completion with
            {
                Suggestions = [],
                Hint = "Browsing history · edit the command to enable suggestions"
            };
        }

        if (_suggestionsHidden)
        {
            return completion with { Suggestions = [] };
        }

        if (_selectedSuggestion >= completion.Suggestions.Count)
        {
            _selectedSuggestion = 0;
        }

        return completion;
    }

    private void Changed()
    {
        _selectedSuggestion = 0;
        _suggestionsHidden = false;
        _historyNavigationActive = false;
    }

    private void SetHistoryBuffer(string value)
    {
        _buffer.Clear();
        _buffer.Append(value);
        _cursor = _buffer.Length;
        _selectedSuggestion = 0;
        _suggestionsHidden = false;
    }

    private void ApplySuggestion(CommandCompletion completion)
    {
        var suggestion = completion.Suggestions[_selectedSuggestion];
        _buffer.Remove(completion.ReplaceStart, completion.ReplaceLength);
        var insertText = completion.ReplaceStart > 0 &&
                         !char.IsWhiteSpace(_buffer[completion.ReplaceStart - 1])
            ? $" {suggestion.InsertText}"
            : suggestion.InsertText;
        _buffer.Insert(completion.ReplaceStart, insertText);
        _cursor = completion.ReplaceStart + insertText.Length;

        if (_cursor == _buffer.Length || !char.IsWhiteSpace(_buffer[_cursor]))
        {
            _buffer.Insert(_cursor++, ' ');
        }

        Changed();
    }

    private string FinishLine()
    {
        var value = _buffer.ToString();
        ClearDrawingArea();
        MoveCursor(0, _top);
        RenderPromptAndInput(
            CreateViewport(value, _cursor, GetInputWidth()),
            includeGhostText: false,
            completion: null);
        global::System.Console.WriteLine();

        return value;
    }

    private void ReserveDrawingArea()
    {
        var lines = GetReservedLines(global::System.Console.WindowHeight);
        for (var index = 0; index < lines; index++)
        {
            global::System.Console.WriteLine();
        }

        _reservedLines = 0;
        _lastLyricsFrame = null;
    }

    private void Render(CommandCompletion completion)
    {
        var width = global::System.Console.WindowWidth;
        var height = global::System.Console.WindowHeight;
        if (width < 2 || height < 2)
            throw new PlatformNotSupportedException("The terminal has no usable drawing area.");

        if (width != _windowWidth || height != _windowHeight) ClearDrawingArea();
        _windowWidth = width;
        _windowHeight = height;
        _reservedLines = GetReservedLines(height);
        // Unix cursor coordinates refer to the visible screen. Do not query CursorTop:
        // that can wait for a terminal CPR response and consume command input.
        _top = ViewportTop + height - _reservedLines;
        ClearDrawingArea();
        MoveCursor(0, _top);
        var viewport = CreateViewport(_buffer.ToString(), _cursor, GetInputWidth());
        _inputColumn = Math.Min(PromptWidth + viewport.CursorOffset, width - 2);
        RenderPromptAndInput(viewport, includeGhostText: true, completion);

        var row = 1;
        if (completion.Suggestions.Count > 0 && row < ContentLines)
        {
            RenderSuggestionBox(completion, ref row);
        }
        else if (!string.IsNullOrWhiteSpace(completion.Hint) && row < ContentLines)
        {
            WriteRow(row++, $"  ↳ {completion.Hint}", ConsoleColor.DarkGray);
        }

        RenderLyrics(force: true);
        MoveCursor(_inputColumn, _top);
    }

    private void RenderLyrics(bool force = false)
    {
        if (lyricsSource is null || LyricsLines == 0) return;
        var frame = lyricsSource();
        var width = Math.Max(0, _windowWidth - 2);
        if (!force && width == _lastLyricsWidth &&
            _lastLyricsFrame?.HasSameContent(frame) == true) return;

        try
        {
            for (var row = 0; row < LyricsLines; row++)
            {
                MoveCursor(0, _top + ContentLines + row);
                global::System.Console.Write(new string(' ', width));
            }
            var rows = frame.GetVisibleRows(LyricsLines);
            for (var row = 0; row < rows.Count; row++)
            {
                MoveCursor(0, _top + _reservedLines - rows.Count + row);
                LyricsRowRenderer.Render(rows[row], width);
            }
            _lastLyricsFrame = frame;
            _lastLyricsWidth = width;
        }
        finally
        {
            MoveCursor(_inputColumn, _top);
        }
    }

    private void RenderPromptAndInput(
        InputViewport viewport,
        bool includeGhostText,
        CommandCompletion? completion)
    {
        if (PromptWidth < CellWidth(PromptText))
        {
            global::System.Console.Write(Truncate(PromptText, PromptWidth));
        }
        else
        {
            SetColor(ConsoleColor.Yellow);
            global::System.Console.Write("lemon");
            SetColor(ConsoleColor.DarkGray);
            global::System.Console.Write(" > ");
        }
        if (viewport.LeftHidden)
        {
            global::System.Console.Write('…');
        }

        RenderHighlightedInput(_buffer.ToString(), viewport.Start, viewport.Length);

        if (viewport.RightHidden)
        {
            SetColor(ConsoleColor.DarkGray);
            global::System.Console.Write('…');
        }

        if (includeGhostText &&
            !viewport.RightHidden &&
            _cursor == _buffer.Length &&
            completion?.Suggestions.FirstOrDefault() is { } suggestion)
        {
            var typed = _buffer.ToString(
                completion.ReplaceStart,
                Math.Min(completion.ReplaceLength, _buffer.Length - completion.ReplaceStart));
            var proposed = completion.ReplaceStart > 0 &&
                           !char.IsWhiteSpace(_buffer[completion.ReplaceStart - 1])
                ? $" {suggestion.InsertText}"
                : suggestion.InsertText;
            if (proposed.StartsWith(typed, StringComparison.OrdinalIgnoreCase) &&
                proposed.Length > typed.Length)
            {
                SetColor(ConsoleColor.DarkGray);
                var availableWidth = Math.Max(0, GetInputWidth() - viewport.DisplayWidth);
                global::System.Console.Write(Truncate(proposed[typed.Length..], availableWidth));
            }
        }

        ResetColor();
    }

    private void RenderSuggestionBox(CommandCompletion completion, ref int row)
    {
        var visibleCount = Math.Min(
            MaximumSuggestions,
            Math.Min(completion.Suggestions.Count, Math.Max(0, ContentLines - 2)));
        var first = Math.Clamp(
            _selectedSuggestion - visibleCount + 1,
            0,
            Math.Max(0, completion.Suggestions.Count - visibleCount));

        WriteRow(
            row++,
            $"  ┌─ {completion.Title ?? "Suggestions"}  ({completion.Suggestions.Count})",
            ConsoleColor.DarkCyan);

        for (var index = first; index < first + visibleCount && row < ContentLines; index++)
        {
            var suggestion = completion.Suggestions[index];
            var selected = index == _selectedSuggestion;
            WriteSuggestionRow(row++, suggestion, selected);
        }

        if (row < ContentLines)
        {
            WriteRow(
                row++,
                completion.PreferSelection
                    ? "  └─ ↑↓ choose  Tab/Enter insert  clear input for history"
                    : "  └─ ↑↓ choose  Tab complete  clear input for history",
                ConsoleColor.DarkGray);
        }
    }

    private void WriteSuggestionRow(int relativeRow, CommandSuggestion suggestion, bool selected)
    {
        var marker = selected ? "  │ > " : "  │   ";
        WriteRow(
            relativeRow,
            $"{marker}{suggestion.DisplayText}  {suggestion.Description}",
            selected ? ConsoleColor.White : ConsoleColor.Gray);
    }

    private void WriteRow(int relativeRow, string value, ConsoleColor color)
    {
        MoveCursor(0, _top + relativeRow);
        SetColor(color);
        global::System.Console.Write(Truncate(value, Math.Max(1, global::System.Console.WindowWidth - 2)));
        ResetColor();
    }

    private static string Truncate(string value, int maximumWidth)
    {
        if (maximumWidth <= 0)
        {
            return string.Empty;
        }

        if (CellWidth(value) <= maximumWidth)
        {
            return value;
        }

        const string ellipsis = "…";
        var builder = new StringBuilder();
        var width = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeWidth = IsWide(rune.Value) ? 2 : 1;
            if (width + runeWidth + 1 > maximumWidth)
            {
                break;
            }

            builder.Append(rune.ToString());
            width += runeWidth;
        }

        return builder.Append(ellipsis).ToString();
    }

    private static void RenderHighlightedInput(string input, int start, int length)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < input.Length && char.IsWhiteSpace(input[firstNonWhitespace]))
        {
            firstNonWhitespace++;
        }

        var commandEnd = firstNonWhitespace;
        while (commandEnd < input.Length && !char.IsWhiteSpace(input[commandEnd]))
        {
            commandEnd++;
        }

        var command = input[firstNonWhitespace..commandEnd];
        var commandColor = CommandCatalog.IsCommand(command)
            ? ConsoleColor.Green
            : CommandCatalog.Commands.Any(item =>
                item.Name.StartsWith(command, StringComparison.OrdinalIgnoreCase) ||
                item.Aliases.Any(alias => alias.StartsWith(command, StringComparison.OrdinalIgnoreCase)))
                ? ConsoleColor.Cyan
                : ConsoleColor.Red;

        var quoted = false;
        var option = false;
        for (var index = 0; index < start; index++)
        {
            var character = input[index];
            if (character == '"')
            {
                quoted = !quoted;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                option = false;
            }
            else if (!quoted && character == '-' &&
                     (index == 0 || char.IsWhiteSpace(input[index - 1])))
            {
                option = true;
            }
        }

        var end = start + length;
        for (var index = start; index < end; index++)
        {
            var character = input[index];
            if (index >= firstNonWhitespace && index < commandEnd)
            {
                SetColor(commandColor);
            }
            else if (character == '"' || quoted)
            {
                SetColor(ConsoleColor.Yellow);
            }
            else if (option)
            {
                SetColor(ConsoleColor.Magenta);
            }
            else
            {
                SetColor(char.IsWhiteSpace(character) ? ConsoleColor.DarkGray : ConsoleColor.White);
            }

            global::System.Console.Write(character);

            if (character == '"')
            {
                quoted = !quoted;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                option = false;
            }
            else if (!quoted && character == '-' &&
                     (index == 0 || char.IsWhiteSpace(input[index - 1])))
            {
                option = true;
            }
        }

        ResetColor();
    }

    private void ClearDrawingArea()
    {
        var width = Math.Max(0, global::System.Console.WindowWidth - 1);
        var blank = new string(' ', width);
        var viewportTop = ViewportTop;
        var bottom = Math.Min(_top + _reservedLines, viewportTop + global::System.Console.WindowHeight);
        for (var row = Math.Max(_top, viewportTop); row < bottom; row++)
        {
            MoveCursor(0, row);
            global::System.Console.Write(blank);
        }
    }

    private void TryClearDrawingArea()
    {
        try
        {
            ClearDrawingArea();
            MoveCursor(0, _top);
        }
        catch (Exception exception) when (
            exception is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
            // The fallback ReadLine below remains usable even when cursor APIs are unavailable.
        }
    }

    private static int CellWidth(string value)
    {
        var width = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            width += IsWide(rune.Value) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWide(int value) =>
        value is >= 0x1100 and <= 0x115f or
        >= 0x2e80 and <= 0xa4cf or
        >= 0xac00 and <= 0xd7a3 or
        >= 0xf900 and <= 0xfaff or
        >= 0xfe10 and <= 0xfe6f or
        >= 0xff00 and <= 0xff60 or
        >= 0xffe0 and <= 0xffe6 or
        >= 0x1f300 and <= 0x1faff;

    private static InputViewport CreateViewport(string value, int cursor, int maximumWidth)
    {
        maximumWidth = Math.Max(1, maximumWidth);
        if (CellWidth(value) <= maximumWidth)
        {
            return new InputViewport(0, value.Length, false, false, CellWidth(value[..cursor]), CellWidth(value));
        }

        var beforeBudget = cursor < value.Length
            ? Math.Max(1, maximumWidth * 2 / 3)
            : maximumWidth;
        var start = cursor;
        var beforeWidth = 0;
        while (start > 0)
        {
            var previous = PreviousRuneStart(value, start);
            var runeWidth = RuneWidth(Rune.GetRuneAt(value, previous));
            var markerWidth = previous > 0 ? 1 : 0;
            if (beforeWidth + runeWidth + markerWidth > beforeBudget)
            {
                break;
            }

            start = previous;
            beforeWidth += runeWidth;
        }

        var leftHidden = start > 0;
        var usedWidth = beforeWidth + (leftHidden ? 1 : 0);
        var end = cursor;
        while (end < value.Length)
        {
            var rune = Rune.GetRuneAt(value, end);
            var runeWidth = RuneWidth(rune);
            var next = end + rune.Utf16SequenceLength;
            var markerWidth = next < value.Length ? 1 : 0;
            if (usedWidth + runeWidth + markerWidth > maximumWidth)
            {
                break;
            }

            end = next;
            usedWidth += runeWidth;
        }

        var rightHidden = end < value.Length;
        if (rightHidden)
        {
            usedWidth++;
        }

        return new InputViewport(
            start,
            end - start,
            leftHidden,
            rightHidden,
            (leftHidden ? 1 : 0) + CellWidth(value[start..cursor]),
            usedWidth);
    }

    private static int PreviousRuneStart(string value, int index)
    {
        var previous = index - 1;
        if (previous > 0 && char.IsLowSurrogate(value[previous]) && char.IsHighSurrogate(value[previous - 1]))
        {
            previous--;
        }

        return previous;
    }

    private static int RuneWidth(Rune rune) => IsWide(rune.Value) ? 2 : 1;

    private int PromptWidth => Math.Min(CellWidth(PromptText), Math.Max(0, _windowWidth - 3));

    private int GetInputWidth() => Math.Max(1, _windowWidth - PromptWidth - 2);

    private int GetReservedLines(int height) =>
        Math.Min(MaximumSuggestions + 3 + (lyricsSource is null ? 0 : 3), Math.Max(2, height - 1));

    private static int ViewportTop => OperatingSystem.IsWindows() ? global::System.Console.WindowTop : 0;

    private sealed record InputViewport(
        int Start,
        int Length,
        bool LeftHidden,
        bool RightHidden,
        int CursorOffset,
        int DisplayWidth);

    private static void MoveCursor(int left, int top) =>
        global::System.Console.SetCursorPosition(left, top);

    private static void SetColor(ConsoleColor color) =>
        SetColorIfEnabled(color);

    private static void SetColorIfEnabled(ConsoleColor color)
    {
        if (HighlightingTextWriter.CanUseColor)
        {
            global::System.Console.ForegroundColor = color;
        }
    }

    private static void ResetColor()
    {
        if (HighlightingTextWriter.CanUseColor)
        {
            global::System.Console.ResetColor();
        }
    }
}
