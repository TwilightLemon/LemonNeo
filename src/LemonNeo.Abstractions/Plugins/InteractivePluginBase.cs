using System.Threading.Channels;
using LemonNeo.Abstractions.Playback;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Abstractions.Plugins;

/// <summary>
///     Keeps native callbacks brief, coalesces pending state updates, observes
///     asynchronous failures, and stops all workers before releasing native resources.
/// </summary>
public abstract class InteractivePluginBase : IInteractivePlugin
{
    private readonly Channel<(PlaybackCommand Command, TimeSpan? Position)> _commands =
        Channel.CreateBounded<(PlaybackCommand, TimeSpan?)>(64);

    private readonly CancellationTokenSource _lifetime = new();

    private readonly Channel<bool> _updates = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private Task? _commandTask;
    private IPlaybackInteraction? _playback;
    private bool _stopped;
    private Task? _updateTask;
    protected ILogger Logger { get; private set; } = null!;

    protected IPlaybackInteraction Playback =>
        _playback ?? throw new InvalidOperationException("Plugin is not initialized.");

    public abstract PluginDescriptor Descriptor { get; }

    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        Logger = context.LoggerFactory.CreateLogger(GetType());
        _playback = context.Playback;
        await InitializeCoreAsync(context, cancellationToken);
        _playback.Changed += OnChanged;
        _updateTask = RunUpdatesAsync(_lifetime.Token);
        _commandTask = RunCommandsAsync(_lifetime.Token);
        _updates.Writer.TryWrite(true);
    }

    public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        _stopped = true;
        if (_playback is not null) _playback.Changed -= OnChanged;
        _updates.Writer.TryComplete();
        _commands.Writer.TryComplete();
        await _lifetime.CancelAsync();
        try
        {
            if (_updateTask is not null) await _updateTask;
            if (_commandTask is not null) await _commandTask;
        }
        finally
        {
            try
            {
                await ShutdownCoreAsync();
            }
            finally
            {
                _lifetime.Dispose();
                _playback = null;
            }
        }
    }

    protected abstract ValueTask InitializeCoreAsync(IPluginContext context, CancellationToken cancellationToken);
    protected abstract ValueTask UpdateAsync(InteractivePlaybackSnapshot snapshot, CancellationToken cancellationToken);
    protected abstract ValueTask ShutdownCoreAsync();

    protected bool RequestCommand(PlaybackCommand command, TimeSpan? position = null)
    {
        return !_stopped && _commands.Writer.TryWrite((command, position));
    }

    private void OnChanged(object? sender, InteractivePlaybackChangedEventArgs args)
    {
        _updates.Writer.TryWrite(true);
    }

    private async Task RunUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _updates.Reader.ReadAllAsync(cancellationToken))
                // Notifications can arrive out of order after concurrent host
                // commands. Always publish the latest authoritative snapshot.
                try
                {
                    await UpdateAsync(Playback.Snapshot, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(exception, "Could not update media controls for {PluginId}.", Descriptor.Id);
                }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _commands.Reader.ReadAllAsync(cancellationToken))
                try
                {
                    await Playback.ExecuteAsync(request.Command, request.Position, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(exception, "Media command {Command} failed for {PluginId}.", request.Command,
                        Descriptor.Id);
                }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}