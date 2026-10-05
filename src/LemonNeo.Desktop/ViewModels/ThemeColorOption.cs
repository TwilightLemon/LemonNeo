using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Infrastructure;

namespace LemonNeo.Desktop.ViewModels;

public sealed class ThemeColorOption(ThemeColorDefinition definition, AppearanceService appearance,
    LocalizationService text, Func<ThemeColorPreset, Task> select) : ObservableObject
{
    public string Name => text[definition.NameKey];
    public bool IsSelected => appearance.Current.ThemeColor == definition.Id;
    public IBrush Accent { get; } = new SolidColorBrush(definition.Accent);
    public IBrush Dark { get; } = new SolidColorBrush(definition.DarkBase);
    public IBrush Light { get; } = new SolidColorBrush(definition.LightBase);
    public AsyncRelayCommand SelectCommand { get; } = new(() => select(definition.Id), AsyncRelayCommandOptions.AllowConcurrentExecutions);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsSelected));
    }
}
