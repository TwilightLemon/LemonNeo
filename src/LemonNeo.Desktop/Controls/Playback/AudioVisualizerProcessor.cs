using System.Diagnostics;

namespace LemonNeo.Desktop.Controls.Playback;

internal sealed class AudioVisualizerProcessor : IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _frameGate = new();
    private readonly IAudioVisualizerSource _source;
    private readonly Thread _thread;
    private float[] _back;
    private float[] _fft = [];
    private float[] _front;
    private bool _sourceErrorReported;

    public AudioVisualizerProcessor(IAudioVisualizerSource source, int stripCount)
    {
        _source = source;
        _front = new float[stripCount];
        _back = new float[stripCount];
        _thread = new Thread(Worker)
        {
            IsBackground = true,
            Name = "Lemon Neo audio visualizer"
        };
    }

    public void Start()
    {
        _thread.Start();
    }

    public void CopyCurrent(Span<float> destination)
    {
        lock (_frameGate)
        {
            _front.AsSpan(0, Math.Min(_front.Length, destination.Length)).CopyTo(destination);
        }
    }

    public void Dispose()
    {
        if (!_cancellation.IsCancellationRequested) _cancellation.Cancel();
        if (_thread.IsAlive) _thread.Join();
        _cancellation.Dispose();
    }

    private void Worker()
    {
        var cancellationToken = _cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            var hasData = false;
            try
            {
                var binCount = _source.FftBinCount;
                if (binCount is > 1 and <= 65536)
                {
                    if (_fft.Length != binCount) _fft = new float[binCount];
                    hasData = _source.TryGetFftData(_fft);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception exception)
            {
                if (!_sourceErrorReported)
                {
                    Trace.WriteLine($"Audio visualizer source failed: {exception}");
                    _sourceErrorReported = true;
                }
            }

            lock (_frameGate)
            {
                var bars = _back.AsSpan();
                if (hasData) DownsampleLog(_fft, bars);
                else bars.Clear();
                Smooth(bars, _front);
                (_front, _back) = (_back, _front);
            }

            if (cancellationToken.WaitHandle.WaitOne(FrameInterval)) break;
        }
    }

    private static void DownsampleLog(ReadOnlySpan<float> fft, Span<float> bars)
    {
        const float scale = 40;
        var step = Math.Log(fft.Length) / bars.Length;
        for (var i = 0; i < bars.Length; i++)
        {
            var index = Math.Min((int)Math.Exp(step * i), fft.Length - 1);
            bars[i] = Math.Clamp(MathF.Log10(1 + scale * Math.Max(0, fft[index])), 0, 1);
        }
    }

    private static void Smooth(Span<float> data, ReadOnlySpan<float> previous)
    {
        const float attack = .25f;
        const float decay = .08f;
        for (var i = 0; i < data.Length; i++)
        {
            var factor = data[i] > previous[i] ? attack : decay;
            data[i] = previous[i] + (data[i] - previous[i]) * factor;
        }
    }
}
