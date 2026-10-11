using LemonNeo.Application.Playback;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Metadata;

public sealed record PlaybackSourceRow(PlaybackSourceCandidate Candidate, LocalizationService Text)
{
    public string Provider => Candidate.ProviderId;
    public string Identity => $"{Candidate.SourceId.Namespace}:{Candidate.SourceId.Kind}:{Candidate.SourceId.Value}";
    public string Quality => Text["AudioQuality" + (Candidate.Source.Quality?.ToString() ?? "Unknown")];
    public string Method => Candidate.ResolutionMethod;

    public string Location => Candidate.Source switch
    {
        FilePlaybackSource file => file.Path, HttpPlaybackSource http => http.Uri.AbsoluteUri,
        _ => Candidate.Source.GetType().Name
    };
}