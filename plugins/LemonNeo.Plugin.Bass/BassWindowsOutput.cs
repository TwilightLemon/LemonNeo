using System.Diagnostics;
using System.Runtime.InteropServices;
using LemonNeo.Abstractions.Playback;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Mix;
using Un4seen.BassAsio;
using Un4seen.BassWasapi;
using BassApi = Un4seen.Bass.Bass;

namespace LemonNeo.Plugin.Bass;

/// <summary>Owns one active Windows output and its mixer. The prepared stream owns the decoder.</summary>
internal sealed class BassWindowsOutput(AudioOutputBackend backend, int source) : IDisposable
{
    private const BASSWASAPIDeviceInfo WasapiFilter = BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED |
                                                     BASSWASAPIDeviceInfo.BASS_DEVICE_INPUT |
                                                     BASSWASAPIDeviceInfo.BASS_DEVICE_LOOPBACK;
    private int? _device;
    private int _mixer;
    private int _rate;
    private int _channels;
    private int _wasapiDevice;
    private BASSWASAPIInit _wasapiFlags;
    private bool _wasapiNeedsFlush;
    private float _latency;
    private long? _pausedPosition;
    private long? _endedAt;

    public static bool IsSupported => OperatingSystem.IsWindows() &&
                                      RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;

    public static IReadOnlyList<AudioOutputDevice> GetDevices(AudioOutputBackend backend)
    {
        return EnumerateDevices(backend).Select(device => device.Option).ToArray();
    }

    public static BassWindowsOutput Start(int source, int decodeDevice, AudioOutputConfiguration configuration, bool playing = true)
    {
        var output = new BassWindowsOutput(configuration.Backend, source);
        try
        {
            var info = BassApi.BASS_ChannelGetInfo(source)
                       ?? throw BassPlayer.BassFailure("read the decoded audio format");
            if (!BassApi.BASS_SetDevice(decodeDevice)) throw BassPlayer.BassFailure("select the decoding device");
            var device = configuration.DeviceId is null ? -1 : EnumerateDevices(configuration.Backend)
                .FirstOrDefault(device => string.Equals(device.Option.Id, configuration.DeviceId,
                    StringComparison.OrdinalIgnoreCase))?.Index
                ?? throw new InvalidOperationException($"The configured {configuration.Backend} output device is unavailable. Select an available device in Tools → Decoders.");

            switch (configuration.Backend)
            {
                case AudioOutputBackend.Wasapi:
                    output.OpenWasapi(device, info, configuration);
                    break;
                case AudioOutputBackend.Asio:
                    output.OpenAsio(device, configuration.SampleRate);
                    break;
                default:
                    output.OpenDefault(device, configuration.SampleRate);
                    break;
            }

            if (playing) output.Play();
            else
            {
                if (configuration.Backend == AudioOutputBackend.Wasapi) output.InitializeWasapi();
                output._pausedPosition = BassApi.BASS_ChannelGetPosition(source);
            }
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    public void Play()
    {
        if (backend == AudioOutputBackend.Wasapi && _device is null) InitializeWasapi();
        SelectDevice();
        var started = backend switch
        {
            AudioOutputBackend.Wasapi => BassWasapi.BASS_WASAPI_Start(),
            AudioOutputBackend.Asio => BassAsio.BASS_ASIO_Start(0),
            _ => BassApi.BASS_ChannelPlay(_mixer, false)
        };
        if (!started) throw Failure("start playback");
        _pausedPosition = null;
        _endedAt = null;
        if (backend == AudioOutputBackend.Asio)
            SetLatency(Math.Max(0, BassAsio.BASS_ASIO_GetLatency(false)) / (float)_rate);
    }

    public void Pause(bool flush)
    {
        var position = GetPosition();
        SelectDevice();
        var paused = backend switch
        {
            AudioOutputBackend.Wasapi => BassWasapi.BASS_WASAPI_Stop(flush),
            AudioOutputBackend.Asio => BassAsio.BASS_ASIO_Stop(),
            _ => BassApi.BASS_ChannelPause(_mixer)
        };
        if (!paused) throw Failure("pause playback");
        _pausedPosition = position;
        _wasapiNeedsFlush = backend == AudioOutputBackend.Wasapi && !flush;
    }

    public bool Seek(long position)
    {
        if (!BassMix.BASS_Mixer_ChannelSetPosition(source, position, BASSMode.BASS_POS_BYTE | BASSMode.BASS_POS_MIXER_RESET))
            return false;
        if (_wasapiNeedsFlush)
        {
            // A stopped WASAPI session cannot flush a buffer retained by Pause.
            // Reopen it on resume so the new position is the first audio played.
            SelectDevice();
            if (!BassWasapi.BASS_WASAPI_Free()) throw Failure("reset the WASAPI device after seeking");
            _device = null;
            _wasapiNeedsFlush = false;
        }
        if (backend == AudioOutputBackend.Wasapi) SetLatency(0);
        _pausedPosition = position;
        _endedAt = null;
        return true;
    }

    public long GetPosition()
    {
        if (_pausedPosition is { } position) return position;
        UpdateWasapiLatency();
        return BassMix.BASS_Mixer_ChannelGetPosition(source);
    }

    public int ReadFft(float[] buffer, int flags)
    {
        return BassMix.BASS_Mixer_ChannelGetData(source, buffer, flags);
    }

    public bool HasEnded()
    {
        if (BassApi.BASS_ChannelIsActive(_mixer) != BASSActive.BASS_ACTIVE_STOPPED) return false;
        _endedAt ??= Stopwatch.GetTimestamp();
        return Stopwatch.GetElapsedTime(_endedAt.Value).TotalSeconds >= _latency;
    }

    public void Dispose()
    {
        // Release the consumer before freeing the mixer it reads from.
        if (_device is { } device)
        {
            if (backend == AudioOutputBackend.Wasapi && BassWasapi.BASS_WASAPI_SetDevice(device))
                BassWasapi.BASS_WASAPI_Free();
            if (backend == AudioOutputBackend.Asio && BassAsio.BASS_ASIO_SetDevice(device))
                BassAsio.BASS_ASIO_Free();
        }
        if (_mixer != 0) BassApi.BASS_StreamFree(_mixer);
        if (backend == AudioOutputBackend.Default && _device is { } bassDevice && BassApi.BASS_SetDevice(bassDevice))
            BassApi.BASS_Free();
    }

    private void OpenDefault(int device, int? rate)
    {
        if (!BassApi.BASS_Init(device, 44100, BASSInit.BASS_DEVICE_CPSPEAKERS, IntPtr.Zero))
            throw Failure("open the output device");
        _device = BassApi.BASS_GetDevice();
        var info = BassApi.BASS_GetInfo() ?? throw Failure("read the output format");
        // Windows controls the hardware rate in BASS's built-in output. An explicit
        // rate here is the mixer rate, which Windows can resample to the device format.
        CreateMixer(rate ?? info.freq, Math.Max(1, info.speakers));
    }

    private void OpenWasapi(int device, BASS_CHANNELINFO sourceInfo, AudioOutputConfiguration configuration)
    {
        if (device == -1)
            device = EnumerateDevices(AudioOutputBackend.Wasapi).FirstOrDefault(candidate => candidate.IsDefault)?.Index
                     ?? throw new InvalidOperationException("Windows has no default WASAPI output device.");
        var info = BassWasapi.BASS_WASAPI_GetDeviceInfo(device)
                   ?? throw Failure("read the WASAPI device format");
        var flags = configuration.Exclusive ? BASSWASAPIInit.BASS_WASAPI_EXCLUSIVE : BASSWASAPIInit.BASS_WASAPI_SHARED;
        var rate = configuration.SampleRate ?? info.mixfreq;
        var channels = info.mixchans;
        if (configuration.Exclusive)
        {
            var rates = configuration.SampleRate is { } requested ? new[] { requested } : new[] { sourceInfo.freq, info.mixfreq };
            var formats = from candidateRate in rates.Distinct()
                          from candidateChannels in new[] { channels, Math.Min(channels, 2) }.Distinct()
                          select (candidateRate, candidateChannels);
            foreach (var format in formats)
            {
                rate = format.candidateRate;
                channels = format.candidateChannels;
                var support = (int)BassWasapi.BASS_WASAPI_CheckFormat(device, rate, channels, flags);
                if (support != -1) break;
                if (BassApi.BASS_ErrorGetCode() != BASSError.BASS_ERROR_FORMAT)
                    throw Failure("check the WASAPI exclusive format");
            }
        }

        CreateMixer(rate, channels);
        _wasapiDevice = device;
        _wasapiFlags = flags;
    }

    private void InitializeWasapi()
    {
        var exclusive = _wasapiFlags.HasFlag(BASSWASAPIInit.BASS_WASAPI_EXCLUSIVE);
        if (!BASS_WASAPI_Init(_wasapiDevice, 0, 0, _wasapiFlags, exclusive ? 0.1f : 0f, 0,
                new IntPtr(-1), new IntPtr(_mixer)))
            throw Failure($"open the {(exclusive ? "exclusive" : "shared")} output at {_rate} Hz");
        _device = _wasapiDevice;
    }

    private void OpenAsio(int device, int? rate)
    {
        if (!BassAsio.BASS_ASIO_Init(device, BASSASIOInit.BASS_ASIO_THREAD))
            throw Failure("open an ASIO driver");
        _device = BassAsio.BASS_ASIO_GetDevice();
        var info = BassAsio.BASS_ASIO_GetInfo() ?? throw Failure("read the ASIO driver format");
        if (info.outputs <= 0) throw new InvalidOperationException("The selected ASIO driver has no output channels.");

        var currentRate = BassAsio.BASS_ASIO_GetRate();
        if (rate is { } requested && requested != currentRate)
        {
            if (!BassAsio.BASS_ASIO_SetRate(requested)) throw Failure($"set the ASIO sample rate to {requested} Hz");
            currentRate = BassAsio.BASS_ASIO_GetRate();
            if (currentRate != requested)
                throw new InvalidOperationException($"The ASIO driver reported {currentRate} Hz after requesting {requested} Hz.");
        }
        if (currentRate <= 0 || !double.IsFinite(currentRate))
            throw Failure("read the ASIO sample rate");
        CreateMixer(checked((int)Math.Round(currentRate)), Math.Min(info.outputs, 2));
        if (!BassAsio.BASS_ASIO_ChannelEnableBASS(false, 0, _mixer, true))
            throw Failure("route audio to the ASIO output channels");
    }

    private void CreateMixer(int rate, int channels)
    {
        var flags = BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_MIXER_END;
        if (backend != AudioOutputBackend.Default)
            flags |= BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_MIXER_POSEX;
        _mixer = BassMix.BASS_Mixer_StreamCreate(rate, channels, flags);
        if (_mixer == 0 || !BassMix.BASS_Mixer_StreamAddChannel(_mixer, source,
                BASSFlag.BASS_MIXER_BUFFER | BASSFlag.BASS_MIXER_DOWNMIX | BASSFlag.BASS_MIXER_LIMIT))
            throw BassPlayer.BassFailure("configure the output mixer");
        _rate = rate;
        _channels = channels;
    }

    private void SelectDevice()
    {
        var selected = backend switch
        {
            AudioOutputBackend.Wasapi => BassWasapi.BASS_WASAPI_SetDevice(_device!.Value),
            AudioOutputBackend.Asio => BassAsio.BASS_ASIO_SetDevice(_device!.Value),
            _ => true
        };
        if (!selected) throw Failure("select the active device");
    }

    private void UpdateWasapiLatency()
    {
        if (backend != AudioOutputBackend.Wasapi || !BassWasapi.BASS_WASAPI_SetDevice(_device!.Value)) return;
        var buffered = BassWasapi.BASS_WASAPI_GetData(IntPtr.Zero, (int)BASSData.BASS_DATA_AVAILABLE);
        if (buffered >= 0) SetLatency(buffered / ((float)_rate * _channels * sizeof(float)));
    }

    private void SetLatency(float seconds)
    {
        _latency = seconds;
        BassApi.BASS_ChannelSetAttribute(_mixer, BASSAttribute.BASS_ATTRIB_MIXER_LATENCY, seconds);
    }

    private InvalidOperationException Failure(string operation)
    {
        var error = backend == AudioOutputBackend.Asio ? BassAsio.BASS_ASIO_ErrorGetCode() : BassApi.BASS_ErrorGetCode();
        return new InvalidOperationException($"BASS {backend} could not {operation}. Audio error: {error} ({(int)error}).");
    }

    private static IEnumerable<Device> EnumerateDevices(AudioOutputBackend backend)
    {
        switch (backend)
        {
            case AudioOutputBackend.Wasapi:
                for (var index = 0; BassWasapi.BASS_WASAPI_GetDeviceInfo(index) is { } wasapi; index++)
                    if ((wasapi.flags & WasapiFilter) == BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED)
                        yield return new Device(index, new AudioOutputDevice(wasapi.id, wasapi.name),
                            wasapi.flags.HasFlag(BASSWASAPIDeviceInfo.BASS_DEVICE_DEFAULT));
                break;
            case AudioOutputBackend.Asio:
                for (var index = 0; BassAsio.BASS_ASIO_GetDeviceInfo(index) is { } asio; index++)
                    yield return new Device(index, new AudioOutputDevice($"{asio.name}|{asio.driver}", asio.name));
                break;
            default:
                for (var index = 1; BassApi.BASS_GetDeviceInfo(index) is { } bass; index++)
                    if (bass.flags.HasFlag(BASSDeviceInfo.BASS_DEVICE_ENABLED) && !string.IsNullOrEmpty(bass.id))
                        yield return new Device(index, new AudioOutputDevice(bass.id, bass.name));
                break;
        }
    }

    private sealed record Device(int Index, AudioOutputDevice Option, bool IsDefault = false);

    // WASAPIPROC_BASS (-1) asks BASSWASAPI to pull directly from the mixer handle.
    [DllImport("basswasapi", ExactSpelling = true)]
    private static extern bool BASS_WASAPI_Init(int device, int rate, int channels, BASSWASAPIInit flags,
        float buffer, float period, IntPtr callback, IntPtr user);
}
