using LemonNeo.Application.Lyrics;
using Avalonia.Controls;
using Avalonia;

namespace LemonNeo.Desktop.Views.Lyrics;

public partial class LyricsView : UserControl
{
    private bool? _compact;
    public LyricsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        var compact = Bounds.Width < 640;
        if (_compact != compact)
        {
            _compact = compact;
            ListeningGrid.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "0.9*,1.25*");
            ListeningGrid.RowDefinitions = new RowDefinitions(compact ? "Auto,*" : "*");
            Grid.SetColumn(LyricsTimeline, compact ? 0 : 1);
            Grid.SetRow(LyricsTimeline, compact ? 1 : 0);
            CoverArea.Margin = compact ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 28, 0);
        }
        var size = compact ? Math.Clamp(Bounds.Height * .24, 96, 140)
            : Math.Clamp(Math.Min(Bounds.Width * .36, Bounds.Height * .7), 180, 360);
        NowPlayingCover.Width = NowPlayingCover.Height = size;
    }
}
