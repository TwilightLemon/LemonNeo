using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using LemonNeo.Core.Playback;

namespace LemonNeo.Desktop.Controls.Playback;

public sealed class EqualizerCurveEditor : TemplatedControl
{
    private const double LowHz = 20, HighHz = 20000, RangeDb = 18;
    public static readonly StyledProperty<ImmutableArray<double>> GainsProperty =
        AvaloniaProperty.Register<EqualizerCurveEditor, ImmutableArray<double>>(nameof(Gains), DecodeProfile.Flat,
            coerce: (_, gains) => !gains.IsDefault && gains.Length == DecodeProfile.BandFrequencies.Length ? gains : DecodeProfile.Flat);
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<EqualizerCurveEditor, int>(nameof(SelectedIndex));
    public static readonly StyledProperty<int> SampleRateProperty =
        AvaloniaProperty.Register<EqualizerCurveEditor, int>(nameof(SampleRate), EqualizerResponse.ReferenceSampleRate,
            coerce: (_, rate) => rate > 0 ? rate : EqualizerResponse.ReferenceSampleRate);
    public static readonly StyledProperty<IBrush?> CurveBrushProperty =
        AvaloniaProperty.Register<EqualizerCurveEditor, IBrush?>(nameof(CurveBrush));

    public ImmutableArray<double> Gains { get => GetValue(GainsProperty); set => SetValue(GainsProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public int SampleRate { get => GetValue(SampleRateProperty); set => SetValue(SampleRateProperty, value); }
    public IBrush? CurveBrush { get => GetValue(CurveBrushProperty); set => SetValue(CurveBrushProperty, value); }

    private EqualizerResponse _response = new(DecodeProfile.Flat, EqualizerResponse.ReferenceSampleRate);
    private IPointer? _dragPointer;
    private double _pressY, _pressGain;
    private int _dragBand;

    static EqualizerCurveEditor()
    {
        AffectsRender<EqualizerCurveEditor>(GainsProperty, SelectedIndexProperty, SampleRateProperty, CurveBrushProperty,
            ForegroundProperty, BackgroundProperty, BorderBrushProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GainsProperty || change.Property == SampleRateProperty)
            _response = new EqualizerResponse(Gains, SampleRate);
    }

    private Rect Plot => new(42, 14, Math.Max(0, Bounds.Width - 65), Math.Max(0, Bounds.Height - 41));
    private static double X(Rect plot, double hz) => plot.X + plot.Width * Math.Log(hz / LowHz, HighHz / LowHz);
    private static double Hz(double fraction) => LowHz * Math.Pow(HighHz / LowHz, fraction);
    private static double Y(Rect plot, double db) => plot.Center.Y - db * plot.Height / (2 * RangeDb);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var plot = Plot;
        if (plot.Width < 1 || plot.Height < 1 || Foreground is null || CurveBrush is null) return;
        context.DrawRectangle(Background, null, new Rect(Bounds.Size), 8, 8);
        var grid = new Pen(BorderBrush, 1);
        for (var db = -12; db <= 12; db += 6)
        {
            var y = Y(plot, db);
            context.DrawLine(db == 0 ? new Pen(Foreground, 1) : grid, new Point(plot.X, y), new Point(plot.Right, y));
            var label = Label(db == 0 ? "0 dB" : db.ToString("+0;-0", CultureInfo.CurrentCulture));
            context.DrawText(label, new Point(plot.X - label.Width - 7, y - label.Height / 2));
        }

        DrawTick(context, plot, LowHz, "20 Hz", grid);
        foreach (var hz in DecodeProfile.BandFrequencies)
            DrawTick(context, plot, hz, hz >= 1000 ? $"{hz / 1000}k" : hz.ToString(), grid);
        DrawTick(context, plot, HighHz, "20k", grid);

        var end = Math.Min(HighHz, SampleRate / 2d);
        if (end > LowHz)
        {
            var line = new StreamGeometry();
            var width = X(plot, end) - plot.X;
            var steps = Math.Max(1, (int)Math.Ceiling(width / 2));
            using (var path = line.Open())
            {
                path.BeginFigure(new Point(plot.X, Y(plot, _response.GainAt(LowHz))), false);
                for (var i = 1; i <= steps; i++)
                {
                    var x = plot.X + width * i / steps;
                    var hz = Hz((x - plot.X) / plot.Width);
                    path.LineTo(new Point(x, Y(plot, _response.GainAt(hz))));
                }
                path.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(CurveBrush, 2.5), line);
        }

        for (var i = 0; i < DecodeProfile.BandFrequencies.Length && i < Gains.Length; i++)
        {
            var hz = DecodeProfile.BandFrequencies[i];
            var audible = hz < SampleRate / 2d;
            var point = new Point(X(plot, hz), Y(plot, audible ? _response.GainAt(hz) : Gains[i]));
            var ink = audible ? CurveBrush : Foreground;
            context.DrawEllipse(i == SelectedIndex ? ink : Background, new Pen(ink, 2), point, 4.5, 4.5);
            if (i == SelectedIndex) context.DrawEllipse(null, new Pen(ink, 1), point, 8, 8);
        }
    }

    private FormattedText Label(string value) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily), 11, Foreground);

    private void DrawTick(DrawingContext context, Rect plot, double hz, string text, Pen grid)
    {
        var x = X(plot, hz);
        context.DrawLine(grid, new Point(x, plot.Y), new Point(x, plot.Bottom));
        var label = Label(text);
        context.DrawText(label, new Point(x - label.Width / 2, plot.Bottom + 4));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var plot = Plot;
        if (_dragPointer is not null || !plot.Contains(point) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragBand = Enumerable.Range(0, DecodeProfile.BandFrequencies.Length)
            .MinBy(i => Math.Abs(X(plot, DecodeProfile.BandFrequencies[i]) - point.X));
        SetCurrentValue(SelectedIndexProperty, _dragBand);
        _pressGain = Gains[_dragBand];
        // The first edit can remove an inheritance hint and reposition the chart inside the dialog.
        _pressY = e.GetPosition(TopLevel.GetTopLevel(this)).Y;
        _dragPointer = e.Pointer;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer != _dragPointer || Plot.Height <= 0) return;
        // At its own center, a filter contributes exactly its configured dB to the total.
        SetGain(_dragBand, _pressGain + (_pressY - e.GetPosition(TopLevel.GetTopLevel(this)).Y) * 2 * RangeDb / Plot.Height);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer != _dragPointer) return;
        e.Pointer.Capture(null);
        _dragPointer = null;
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragPointer == e.Pointer) _dragPointer = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((uint)SelectedIndex >= Gains.Length) return;
        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
                SetCurrentValue(SelectedIndexProperty, Math.Clamp(SelectedIndex + (e.Key == Key.Left ? -1 : 1), 0, Gains.Length - 1));
                break;
            case Key.Up:
            case Key.Down:
                SetGain(SelectedIndex, Gains[SelectedIndex] + (e.Key == Key.Up ? 0.5 : -0.5));
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void SetGain(int index, double value)
    {
        var gain = Math.Clamp(Math.Round(value * 2) / 2, -12, 12);
        if (gain != Gains[index]) SetCurrentValue(GainsProperty, Gains.SetItem(index, gain));
    }
}
