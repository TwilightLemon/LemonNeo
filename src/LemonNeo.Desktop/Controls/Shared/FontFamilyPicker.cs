using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace LemonNeo.Desktop.Controls.Shared;

/// <summary>An editable fallback-font list. Suggestions replace only the final comma-separated family.
/// Keystrokes stay local and are committed on Enter, on focus loss, or when a suggestion is picked,
/// so a partially typed list never reaches the settings store.</summary>
public sealed class FontFamilyPicker : AutoCompleteBox
{
    public static readonly StyledProperty<string?> SelectedFamiliesProperty =
        AvaloniaProperty.Register<FontFamilyPicker, string?>(nameof(SelectedFamilies), "",
            defaultBindingMode: BindingMode.TwoWay);

    protected override Type StyleKeyOverride => typeof(AutoCompleteBox);

    public FontFamilyPicker()
    {
        MinimumPrefixLength = 0;
        IsTextCompletionEnabled = false;
        MaxDropDownHeight = 320;
        ItemsSource = FontManager.Current.SystemFonts.Select(font => font.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        TextFilter = (text, candidate) => candidate?.Contains(CurrentFamily(text), StringComparison.CurrentCultureIgnoreCase) == true;
        TextSelector = CompleteFamily;
        ItemTemplate = new FuncDataTemplate<string>((name, _) => string.IsNullOrEmpty(name) ? null : new TextBlock
        {
            Text = name, FontFamily = new FontFamily(name), Margin = new Thickness(8, 6)
        });
    }

    public string? SelectedFamilies
    {
        get => GetValue(SelectedFamiliesProperty);
        set => SetValue(SelectedFamiliesProperty, value);
    }

    private static string CurrentFamily(string? text)
    {
        text ??= "";
        return text[(text.LastIndexOf(',') + 1)..].Trim();
    }

    private static string CompleteFamily(string? text, string? selected)
    {
        var separator = text?.LastIndexOf(',') ?? -1;
        return separator < 0 ? selected ?? "" : text![..(separator + 1)] + " " + selected;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        // AutoCompleteBox selects all text when it gains focus; that's unwanted for a
        // picker, so collapse the selection and leave the caret at the end. The first
        // write forces a change even when the caret already sits at the end (a
        // same-value write would be a no-op).
        var length = Text?.Length ?? 0;
        if (length > 0)
        {
            SetCurrentValue(CaretIndexProperty, 0);
            SetCurrentValue(CaretIndexProperty, length);
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        QueueCommit();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter) QueueCommit();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Settings flow into the editor; keystrokes stay local until an edit is committed.
        if (change.Property == SelectedFamiliesProperty && Text != SelectedFamilies)
            SetCurrentValue(TextProperty, SelectedFamilies ?? "");
        else if (change.Property == SelectedItemProperty && change.NewValue is not null)
            QueueCommit();
    }

    /// <summary>Commits once the box has settled, so a picked suggestion has already reached <see cref="AutoCompleteBox.Text"/>.</summary>
    private void QueueCommit() => Dispatcher.UIThread.Post(Commit, DispatcherPriority.Background);

    private void Commit()
    {
        if (SelectedFamilies != Text) SetCurrentValue(SelectedFamiliesProperty, Text ?? "");
    }
}
