using System.Text.Json;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Playback;
using Xunit;
using static LemonNeo.Tests.PlaybackFixture;

namespace LemonNeo.Tests;

public sealed class PlaybackResumeTests
{
    [Theory]
    [InlineData(0, PlaybackState.Stopped)]
    [InlineData(1, PlaybackState.Playing)]
    [InlineData(2, PlaybackState.Paused)]
    public void ExistingResumeJsonRetainsItsStateAndFormat(int storedStatus, PlaybackState expected)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var record = JsonSerializer.Deserialize<PlaybackResumeState>(
            $$"""{"position":"00:00:45","status":{{storedStatus}}}""", options);
        Assert.NotNull(record);
        Assert.Equal(expected, record.Status);
        Assert.Equal(TimeSpan.FromSeconds(45), record.Position);
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(record, options));
        Assert.Equal(storedStatus, saved.RootElement.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData(PlaybackState.Loading)]
    [InlineData(PlaybackState.Failed)]
    [InlineData((PlaybackState)99)]
    public async Task TransientAndUnknownResumeStatesAreNormalizedOnLoad(PlaybackState storedStatus)
    {
        await using var fixture = new PlaybackFixture();
        var item = new PlaybackQueueItem { Track = Track("A") };
        fixture.Settings.Register(nameof(PlaybackQueueState), new PlaybackQueueState { Items = [item], CurrentItemId = item.Id });
        fixture.Settings.Register(nameof(PlaybackResumeState),
            new PlaybackResumeState(item.Id, TimeSpan.FromSeconds(45), storedStatus));
        await fixture.InitializeAsync();
        Assert.Equal(PlaybackState.Stopped, fixture.Session.Snapshot.RestoredStatus);
        Assert.Equal(PlaybackState.Stopped, fixture.Settings.Get<PlaybackResumeState>().Status);
        Assert.Equal(TimeSpan.FromSeconds(45), fixture.Session.Snapshot.Timeline.Position);
    }
}
