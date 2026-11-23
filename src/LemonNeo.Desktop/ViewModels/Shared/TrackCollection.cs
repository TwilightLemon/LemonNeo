using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LemonNeo.Desktop.ViewModels.Shared;

internal sealed class TrackCollection<T> : ObservableCollection<T> where T : class
{
    public void Update(IReadOnlyList<T> values, bool replaceResults)
    {
        if (Count == values.Count && this.SequenceEqual(values, ReferenceEqualityComparer.Instance)) return;
        if (replaceResults)
        {
            // Publish a new filter projection in one notification, even for very large playlists.
            CheckReentrancy();
            Items.Clear();
            foreach (var value in values) Items.Add(value);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return;
        }

        // Keep existing containers for ordinary playlist edits and reordering.
        var wanted = values.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var i = Count - 1; i >= 0; i--)
            if (!wanted.Contains(this[i])) RemoveAt(i);

        var existing = this.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < values.Count; i++)
        {
            if (i < Count && ReferenceEquals(this[i], values[i])) continue;
            if (existing.Contains(values[i])) Move(IndexOf(values[i]), i);
            else Insert(i, values[i]);
        }
    }
}
