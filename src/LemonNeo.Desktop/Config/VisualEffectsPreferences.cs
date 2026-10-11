namespace LemonNeo.Desktop.Config;

public sealed record VisualEffectsPreferences(
    bool AudioVisualizerEnabled = true,
    int AudioVisualizerStripCount = 36,
    bool AppleMusicMeshEnabled = true,
    bool AppleMusicMeshLyricsOnly = false,
    int AppleMusicMeshPresetSlot = -1,
    int AppleMusicMeshFrameRateLimit = 120,
    double AppleMusicMeshRenderScale = .4,
    double AppleMusicMeshRotationScale = 1,
    double AppleMusicMeshBassPulseScale = .2,
    double AppleMusicMeshBlurScale = 1.6);
