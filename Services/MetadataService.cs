using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaPlayer.Models;

namespace MediaPlayer.Services;

public static class MetadataService
{
    private static readonly string[] SupportedExtensions = { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac" };

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return SupportedExtensions.Any(item => string.Equals(item, extension, StringComparison.OrdinalIgnoreCase));
    }

    public static Track Create(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var title = fileName;
        var artist = string.Empty;
        var duration = TimeSpan.Zero;
        ImageSource? cover = null;

        try
        {
            using var file = TagLib.File.Create(path);

            var tag = file.Tag;
            if (tag is not null)
            {
                if (!string.IsNullOrWhiteSpace(tag.Title))
                {
                    title = tag.Title.Trim();
                }

                var performer = tag.FirstPerformer;
                if (string.IsNullOrWhiteSpace(performer))
                {
                    performer = tag.FirstAlbumArtist;
                }

                if (!string.IsNullOrWhiteSpace(performer))
                {
                    artist = performer.Trim();
                }

                var pictures = tag.Pictures;
                if (pictures is { Length: > 0 })
                {
                    cover = LoadImage(pictures[0].Data.Data);
                }
            }

            if (file.Properties is not null && file.Properties.Duration > TimeSpan.Zero)
            {
                duration = file.Properties.Duration;
            }
        }
        catch
        {
            cover = null;
        }

        var parsedArtist = string.Empty;
        var parsedTitle = fileName;

        var separatorIndex = fileName.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex > 0)
        {
            parsedArtist = fileName[..separatorIndex].Trim();
            parsedTitle = fileName[(separatorIndex + 3)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(artist))
        {
            artist = parsedArtist;
        }

        if (string.Equals(title, fileName, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(parsedTitle))
        {
            title = parsedTitle;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = fileName;
        }

        return new Track(path, title, artist, duration, cover);
    }

    private static ImageSource? LoadImage(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
