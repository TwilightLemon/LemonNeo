using LemonNeo.Application.Library;

namespace LemonNeo.Desktop.Config;

public sealed record TrackExportPreferences(string? Directory = null, bool WriteLyrics = true, bool WriteTags = true,
    string FileNameFormat = TrackExportService.DefaultFileNameFormat)
{
    public static string DefaultDirectory
    {
        get
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(music))
                music = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
            return Path.Combine(music, "LemonNeo");
        }
    }
}
