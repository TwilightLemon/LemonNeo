using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace LemonNeo.Desktop.Controls.Shared;

/// <summary>An editable fallback-font list. Suggestions replace only the final comma-separated family.</summary>
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
        SetCurrentValue(IsDropDownOpenProperty, true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedFamiliesProperty && Text != SelectedFamilies)
            SetCurrentValue(TextProperty, SelectedFamilies ?? "");
        else if (change.Property == TextProperty && SelectedFamilies != Text)
            SetCurrentValue(SelectedFamiliesProperty, Text ?? "");
    }
}
