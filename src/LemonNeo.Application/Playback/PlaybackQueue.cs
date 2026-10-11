using System.Collections.Immutable;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;

namespace LemonNeo.Application.Playback;

public interface IPlaybackQueue
{
    IReadOnlyList<PlaybackQueueItem> Items { get; }
    PlaybackQueueItem? CurrentItem { get; }
    int CurrentIndex { get; }
    PlaybackMode Mode { get; }
    PlaybackRequest Request { get; }

    void Restore(PlaybackQueueState state);
    PlaybackQueueState Snapshot();
    void Replace(IEnumerable<Track> tracks, int startIndex = 0);
    PlaybackQueueItem Enqueue(Track track);
    bool UpdateTrack(Guid itemId, Track track);
    bool Remove(Guid itemId);
    PlaybackQueueItem? Select(Guid itemId);
    PlaybackQueueItem? MoveNext();
    PlaybackQueueItem? MovePrevious();
    void SetMode(PlaybackMode mode);
    void SetRequest(PlaybackRequest request);
    void Clear();
}

public sealed class PlaybackQueue : IPlaybackQueue
{
    private readonly object _gate = new();
    private readonly List<PlaybackQueueItem> _items = [];
    private readonly List<Guid> _shuffleOrder = [];
    private Guid? _currentItemId;
    private PlaybackMode _mode;
    private PlaybackRequest _request = new();
    private int _shuffleCursor = -1;

    public IReadOnlyList<PlaybackQueueItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    public PlaybackQueueItem? CurrentItem
    {
        get
        {
            lock (_gate)
            {
                return FindCurrent();
            }
        }
    }

    public int CurrentIndex
    {
        get
        {
            lock (_gate)
            {
                return _currentItemId is { } id
                    ? _items.FindIndex(item => item.Id == id)
                    : -1;
            }
        }
    }

    public PlaybackMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }
    }

    public PlaybackRequest Request
    {
        get
        {
            lock (_gate)
            {
                return _request;
            }
        }
    }

    public void Restore(PlaybackQueueState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(state.Items
                .GroupBy(item => item.Id)
                .Select(group => group.First()));
            _mode = Enum.IsDefined(state.Mode) ? state.Mode : PlaybackMode.Sequential;
            _request = state.Request;
            _currentItemId = state.CurrentItemId is { } currentId &&
                             _items.Any(item => item.Id == currentId)
                ? currentId
                : _items.FirstOrDefault()?.Id;

            _shuffleOrder.Clear();
            var validIds = _items.Select(item => item.Id).ToHashSet();
            _shuffleOrder.AddRange(state.ShuffleOrder
                .Where(validIds.Contains)
                .Distinct());
            _shuffleOrder.AddRange(_items.Select(item => item.Id).Where(id => !_shuffleOrder.Contains(id)));
            _shuffleCursor = _currentItemId is { } id ? _shuffleOrder.IndexOf(id) : -1;
            if (_mode == PlaybackMode.Shuffle && _shuffleCursor < 0 && _items.Count > 0)
                StartShuffleCycle(_items[0].Id);
        }
    }

    public PlaybackQueueState Snapshot()
    {
        lock (_gate)
        {
            return new PlaybackQueueState
            {
                Items = _items.ToImmutableArray(),
                CurrentItemId = _currentItemId,
                Mode = _mode,
                ShuffleOrder = _shuffleOrder.ToImmutableArray(),
                ShuffleCursor = _shuffleCursor,
                Request = _request,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public void Replace(IEnumerable<Track> tracks, int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        lock (_gate)
        {
            var replacements = tracks.Select(track => new PlaybackQueueItem { Track = track }).ToList();
            if (replacements.Count == 0)
            {
                ClearCore();
                return;
            }

            if (startIndex < 0 || startIndex >= replacements.Count)
                throw new ArgumentOutOfRangeException(
                    nameof(startIndex),
                    $"Start index must be between 0 and {replacements.Count - 1}.");

            _items.Clear();
            _items.AddRange(replacements);
            _currentItemId = _items[startIndex].Id;
            _shuffleOrder.Clear();
            _shuffleCursor = -1;
            if (_mode == PlaybackMode.Shuffle) StartShuffleCycle(_currentItemId.Value);
        }
    }

    public PlaybackQueueItem Enqueue(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_gate)
        {
            var item = new PlaybackQueueItem { Track = track };
            _items.Add(item);
            if (_currentItemId is null) _currentItemId = item.Id;

            if (_mode == PlaybackMode.Shuffle)
            {
                if (_shuffleOrder.Count == 0)
                {
                    StartShuffleCycle(_currentItemId.Value);
                }
                else
                {
                    var minimum = Math.Clamp(_shuffleCursor + 1, 0, _shuffleOrder.Count);
                    _shuffleOrder.Insert(Random.Shared.Next(minimum, _shuffleOrder.Count + 1), item.Id);
                }
            }

            return item;
        }
    }

    public bool UpdateTrack(Guid itemId, Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_gate)
        {
            var index = _items.FindIndex(item => item.Id == itemId);
            if (index < 0) return false;
            _items[index] = _items[index] with { Track = track };
            return true;
        }
    }

    public bool Remove(Guid itemId)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(item => item.Id == itemId);
            if (index < 0) return false;

            var wasCurrent = _currentItemId == itemId;
            _items.RemoveAt(index);
            var shuffleIndex = _shuffleOrder.IndexOf(itemId);
            if (shuffleIndex >= 0)
            {
                _shuffleOrder.RemoveAt(shuffleIndex);
                if (shuffleIndex <= _shuffleCursor) _shuffleCursor--;
            }

            if (wasCurrent)
            {
                _currentItemId = _items.Count == 0
                    ? null
                    : _items[Math.Min(index, _items.Count - 1)].Id;
                if (_mode == PlaybackMode.Shuffle && _currentItemId is { } currentId)
                    _shuffleCursor = _shuffleOrder.IndexOf(currentId);
            }

            return true;
        }
    }

    public PlaybackQueueItem? Select(Guid itemId)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(candidate => candidate.Id == itemId);
            if (item is null) return null;

            _currentItemId = item.Id;
            if (_mode == PlaybackMode.Shuffle)
            {
                _shuffleCursor = _shuffleOrder.IndexOf(item.Id);
                if (_shuffleCursor < 0) StartShuffleCycle(item.Id);
            }

            return item;
        }
    }

    public PlaybackQueueItem? MoveNext()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return null;

            if (_currentItemId is null)
            {
                _currentItemId = _items[0].Id;
                return _items[0];
            }

            if (_mode == PlaybackMode.Shuffle) return MoveNextShuffle();

            var currentIndex = _items.FindIndex(item => item.Id == _currentItemId);
            if (currentIndex < _items.Count - 1)
            {
                _currentItemId = _items[currentIndex + 1].Id;
                return _items[currentIndex + 1];
            }

            if (_mode == PlaybackMode.RepeatAll)
            {
                _currentItemId = _items[0].Id;
                return _items[0];
            }

            return null;
        }
    }

    public PlaybackQueueItem? MovePrevious()
    {
        lock (_gate)
        {
            if (_items.Count == 0 || _currentItemId is null) return null;

            if (_mode == PlaybackMode.Shuffle)
            {
                if (_shuffleCursor <= 0) return null;

                _shuffleCursor--;
                _currentItemId = _shuffleOrder[_shuffleCursor];
                return FindCurrent();
            }

            var currentIndex = _items.FindIndex(item => item.Id == _currentItemId);
            if (currentIndex > 0)
            {
                _currentItemId = _items[currentIndex - 1].Id;
                return _items[currentIndex - 1];
            }

            if (_mode == PlaybackMode.RepeatAll)
            {
                _currentItemId = _items[^1].Id;
                return _items[^1];
            }

            return null;
        }
    }

    public void SetMode(PlaybackMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));

        lock (_gate)
        {
            if (_mode == mode) return;

            _mode = mode;
            _shuffleOrder.Clear();
            _shuffleCursor = -1;
            if (mode == PlaybackMode.Shuffle && _currentItemId is { } currentId) StartShuffleCycle(currentId);
        }
    }

    public void SetRequest(PlaybackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _request = request;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearCore();
        }
    }

    private PlaybackQueueItem? MoveNextShuffle()
    {
        if (_shuffleOrder.Count != _items.Count || _shuffleCursor < 0)
            StartShuffleCycle(_currentItemId ?? _items[0].Id);

        if (_shuffleCursor < _shuffleOrder.Count - 1)
        {
            _shuffleCursor++;
            _currentItemId = _shuffleOrder[_shuffleCursor];
            return FindCurrent();
        }

        var previousId = _currentItemId!.Value;
        StartShuffleCycle(null);
        if (_shuffleOrder.Count > 1 && _shuffleOrder[0] == previousId)
            (_shuffleOrder[0], _shuffleOrder[1]) = (_shuffleOrder[1], _shuffleOrder[0]);

        _shuffleCursor = 0;
        _currentItemId = _shuffleOrder[0];
        return FindCurrent();
    }

    private void StartShuffleCycle(Guid? firstItemId)
    {
        _shuffleOrder.Clear();
        _shuffleOrder.AddRange(_items.Select(item => item.Id));
        Shuffle(_shuffleOrder);
        if (firstItemId is { } first)
        {
            var index = _shuffleOrder.IndexOf(first);
            if (index > 0) (_shuffleOrder[0], _shuffleOrder[index]) = (_shuffleOrder[index], _shuffleOrder[0]);
        }

        _shuffleCursor = _shuffleOrder.Count == 0 ? -1 : 0;
    }

    private static void Shuffle(List<Guid> items)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapIndex = Random.Shared.Next(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }

    private PlaybackQueueItem? FindCurrent()
    {
        return _currentItemId is { } id
            ? _items.FirstOrDefault(item => item.Id == id)
            : null;
    }

    private void ClearCore()
    {
        _items.Clear();
        _shuffleOrder.Clear();
        _currentItemId = null;
        _shuffleCursor = -1;
    }
}