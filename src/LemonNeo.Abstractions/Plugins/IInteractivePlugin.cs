namespace LemonNeo.Abstractions.Plugins;

/// <summary>
/// A plugin that publishes the host's playback session to an external interface
/// and routes its controls back through IPluginContext.Playback.
/// </summary>
public interface IInteractivePlugin : ILemonPlugin;
