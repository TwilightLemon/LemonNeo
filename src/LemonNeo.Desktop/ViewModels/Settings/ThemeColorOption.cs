using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.Utils;

namespace LemonNeo.Desktop.ViewModels.Settings;

public sealed class ThemeColorOption(ThemeColorDefinition definition, AppearanceService appearance,
    LocalizationService text, Action<ThemeColorPreset> select) : ObservableObject
{
    public string Name => text[definition.NameKey];
    public bool IsSelected => appearance.Current.ThemeColor == definition.Id;
    public IBrush Accent { get; } = new SolidColorBrush(definition.Accent);
    public IBrush Dark { get; } = new SolidColorBrush(definition.DarkBase);
    public IBrush Light { get; } = new SolidColorBrush(definition.LightBase);
    public RelayCommand SelectCommand { get; } = new(() => select(definition.Id));

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsSelected));
    }
}
