using System.Security.Cryptography;

namespace LemonNeo.Desktop.Services;

internal static class PlaylistArtworkFiles
{
    private const string CoverDirectory = "covers";
    private const string Extension = ".img";

    public static Uri? Resolve(string libraryDirectory, Uri? artworkUri)
    {
        if (artworkUri is null || artworkUri.IsAbsoluteUri) return artworkUri;
        var path = artworkUri.OriginalString;
        if (!path.StartsWith(CoverDirectory + "/", StringComparison.Ordinal)) return null;

        var fileName = path[(CoverDirectory.Length + 1)..];
        if (fileName.Length != 64 + Extension.Length ||
            !fileName.EndsWith(Extension, StringComparison.Ordinal) ||
            !fileName[..64].All(Uri.IsHexDigit)) return null;

        return new Uri(Path.Combine(libraryDirectory, CoverDirectory, fileName));
    }

    public static async Task<Uri> SaveAsync(string libraryDirectory, ReadOnlyMemory<byte> image,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(libraryDirectory, CoverDirectory);
        var fileName = Convert.ToHexStringLower(SHA256.HashData(image.Span)) + Extension;
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, image, cancellationToken);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return new Uri(CoverDirectory + "/" + fileName, UriKind.Relative);
    }
}
