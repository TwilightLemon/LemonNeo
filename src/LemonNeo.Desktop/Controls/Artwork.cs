using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace LemonNeo.Desktop.Controls;

public sealed class Artwork : TemplatedControl
{
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<Artwork, IImage?>(nameof(Source));
    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
}
