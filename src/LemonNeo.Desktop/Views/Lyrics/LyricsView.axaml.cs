using Avalonia;
using Avalonia.Controls;

namespace LemonNeo.Desktop.Views.Lyrics;

public partial class LyricsView : UserControl
{
    public LyricsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {

        var size = Math.Clamp(Math.Min(Bounds.Width * .36, Bounds.Height * .7), 180, 512);
        NowPlayingCover.Width = NowPlayingCover.Height = size;
    }
}