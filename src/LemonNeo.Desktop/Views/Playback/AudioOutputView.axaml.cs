using Avalonia;
using Avalonia.Controls;

namespace LemonNeo.Desktop.Views.Playback;

public partial class AudioOutputView : UserControl
{
    public static readonly StyledProperty<bool> ShowAdvancedProperty =
        AvaloniaProperty.Register<AudioOutputView, bool>(nameof(ShowAdvanced), true);
    public bool ShowAdvanced
    {
        get => GetValue(ShowAdvancedProperty);
        set => SetValue(ShowAdvancedProperty, value);
    }

    public AudioOutputView() => InitializeComponent();
}
