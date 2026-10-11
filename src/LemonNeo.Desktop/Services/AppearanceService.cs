using Avalonia.Media;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Desktop.Config;

namespace LemonNeo.Desktop.Services;

/// <summary>Applies theme and motion changes; configuration is owned by the host.</summary>
public sealed class AppearanceService : IDisposable
{
    private readonly Avalonia.Application _application;
    private readonly ISettingsScope _settings;
    private readonly IDisposable _subscription;
    private bool _reduceMotion;

    public AppearanceService(Avalonia.Application application, ISettingsScope settings)
    {
        _application = application;
        _settings = settings;
        Colors = new ThemeColorService(application);
        Apply(Current, true);
        _subscription = settings.Subscribe<AppearancePreferences>(value => Apply(value, false));
    }

    public AppearancePreferences Current => _settings.Get<AppearancePreferences>();
    public ThemeColorService Colors { get; }

    public void Dispose()
    {
        _subscription.Dispose();
        Colors.Dispose();
    }

    public void SetArtworkColor(Color? color)
    {
        Colors.SetArtworkColor(color);
    }

    private void Apply(AppearancePreferences value, bool initialize)
    {
        Colors.ApplyPreferences(value, !initialize);
        if (initialize || _reduceMotion != value.ReduceMotion)
        {
            _application.Resources["Motion.Fast"] = TimeSpan.FromMilliseconds(value.ReduceMotion ? 0 : 140);
            _application.Resources["Motion.Normal"] = TimeSpan.FromMilliseconds(value.ReduceMotion ? 0 : 220);
        }

        _reduceMotion = value.ReduceMotion;
    }
}