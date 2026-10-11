using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace MediaPlayer.Models;

public sealed class Track : INotifyPropertyChanged
{
    private bool _isCurrent;

    public Track(string filePath, string title, string artist, TimeSpan duration, ImageSource? cover)
    {
        FilePath = filePath;
        Title = title;
        Artist = artist;
        Duration = duration;
        Cover = cover;
    }

    public string FilePath { get; }

    public string Title { get; }

    public string Artist { get; }

    public TimeSpan Duration { get; }

    public ImageSource? Cover { get; }

    public string FileName => Path.GetFileName(FilePath);

    public string ArtistDisplay => string.IsNullOrWhiteSpace(Artist) ? "Неизвестный исполнитель" : Artist;

    public string DurationDisplay => Duration > TimeSpan.Zero ? Format(Duration) : "0:00";

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
            : $"{value.Minutes}:{value.Seconds:D2}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
