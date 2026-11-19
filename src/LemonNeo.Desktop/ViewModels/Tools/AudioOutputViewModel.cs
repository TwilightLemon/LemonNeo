using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Tools;

public sealed partial class AudioOutputViewModel : ObservableObject
{
    private readonly IAudioOutputConfigurable _provider;
    private readonly LocalizationService _text;

    [ObservableProperty] private BackendOption _selectedBackend;
    [ObservableProperty] private IReadOnlyList<DeviceOption> _devices = [];
    [ObservableProperty] private DeviceOption? _selectedDevice;
    [ObservableProperty] private bool _exclusive;
    [ObservableProperty] private bool _automaticRate;
    [ObservableProperty] private decimal? _sampleRate;
    [ObservableProperty] private string? _deviceStatus;
    [ObservableProperty] private string? _saveStatus;

    public AudioOutputViewModel(string playerName, IAudioOutputConfigurable provider, LocalizationService text)
    {
        _provider = provider;
        _text = text;
        Title = text.Format(LanguageManager.Keys.AudioOutputTitle, playerName);
        Backends = provider.OutputBackends.Select(backend => new BackendOption(backend, backend switch
        {
            AudioOutputBackend.Wasapi => "WASAPI",
            AudioOutputBackend.Asio => "ASIO",
            _ => text[LanguageManager.Keys.AudioOutputDefaultBackend]
        })).ToArray();

        var configuration = provider.OutputConfiguration;
        _selectedBackend = Backends.FirstOrDefault(option => option.Backend == configuration.Backend) ?? Backends[0];
        _exclusive = configuration.Exclusive;
        _automaticRate = configuration.SampleRate is null;
        _sampleRate = configuration.SampleRate ?? 48000;
        LoadDevices(configuration.DeviceId);
    }

    public string Title { get; }
    public IReadOnlyList<BackendOption> Backends { get; }
    public bool IsWasapi => SelectedBackend.Backend == AudioOutputBackend.Wasapi;
    public string RateDescription => _text[SelectedBackend.Backend switch
    {
        AudioOutputBackend.Asio => LanguageManager.Keys.AudioOutputAsioRate,
        AudioOutputBackend.Wasapi => LanguageManager.Keys.AudioOutputWasapiRate,
        _ => LanguageManager.Keys.AudioOutputDefaultRate
    }];

    partial void OnSelectedBackendChanged(BackendOption value)
    {
        LoadDevices(null);
        OnPropertyChanged(nameof(IsWasapi));
        OnPropertyChanged(nameof(RateDescription));
        DraftChanged();
    }

    partial void OnSelectedDeviceChanged(DeviceOption? value) => DraftChanged();
    partial void OnAutomaticRateChanged(bool value) => DraftChanged();
    partial void OnExclusiveChanged(bool value) => DraftChanged();
    partial void OnSampleRateChanged(decimal? value) => DraftChanged();

    [RelayCommand]
    private void RefreshDevices() => LoadDevices(SelectedDevice?.Id);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!CanApply()) return;
        _provider.SetOutputConfiguration(new AudioOutputConfiguration(
            SelectedBackend.Backend,
            SelectedDevice!.Id,
            AutomaticRate ? null : (int)SampleRate!.Value,
            IsWasapi && Exclusive));
        SaveStatus = _text[LanguageManager.Keys.AudioOutputSaved];
    }

    private bool CanApply() => SelectedDevice is { Available: true } &&
                               (AutomaticRate || SampleRate is >= 1 and <= int.MaxValue &&
                                   SampleRate == decimal.Truncate(SampleRate.Value));

    private void DraftChanged()
    {
        SaveStatus = null;
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void LoadDevices(string? selectedId)
    {
        var backend = SelectedBackend.Backend;
        var asio = backend == AudioOutputBackend.Asio;
        var defaultDevice = new DeviceOption(null,
            _text[asio ? LanguageManager.Keys.AudioOutputFirstAsio : LanguageManager.Keys.AudioOutputDefaultDevice]);
        List<DeviceOption> options = [defaultDevice];
        DeviceStatus = null;
        try
        {
            var devices = _provider.GetOutputDevices(backend);
            options.AddRange(devices.Select(device => new DeviceOption(device.Id, device.Name)));
            if (devices.Count == 0)
                DeviceStatus = _text[asio ? LanguageManager.Keys.AudioOutputNoAsio : LanguageManager.Keys.AudioOutputNoDevices];
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            DeviceStatus = _text[LanguageManager.Keys.AudioOutputUnavailable];
            options[0] = defaultDevice with { Available = false };
        }

        var selected = options.FirstOrDefault(option => string.Equals(option.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            selected = new DeviceOption(selectedId, _text[LanguageManager.Keys.AudioOutputDeviceMissing], false);
            options.Add(selected);
        }
        Devices = options;
        SelectedDevice = selected;
    }

    public sealed record BackendOption(AudioOutputBackend Backend, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record DeviceOption(string? Id, string Label, bool Available = true)
    {
        public override string ToString() => Label;
    }
}
