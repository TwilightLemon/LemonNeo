using LemonNeo.Abstractions.Sync;
using LemonNeo.Core.Library;

namespace LemonNeo.Application.Sync;

public interface ILibraryMergeService
{
    MergeResult Merge(MusicLibrary @base, MusicLibrary local, MusicLibrary remote);
}
