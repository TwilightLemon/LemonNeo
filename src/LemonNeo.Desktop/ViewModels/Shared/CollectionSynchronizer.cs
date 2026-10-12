using System.Collections.ObjectModel;

namespace LemonNeo.Desktop.ViewModels.Shared;

internal static class CollectionSynchronizer
{
    /// <summary>Preserves existing items and containers, including when filtering or reordering.</summary>
    public static void Update<T>(ObservableCollection<T> target, IReadOnlyList<T> values) where T : class
    {
        if (target.Count == values.Count && target.SequenceEqual(values, ReferenceEqualityComparer.Instance)) return;
        var wanted = values.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);

        var existing = target.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < values.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], values[i])) continue;
            if (existing.Contains(values[i])) target.Move(target.IndexOf(values[i]), i);
            else target.Insert(i, values[i]);
        }
    }
}
