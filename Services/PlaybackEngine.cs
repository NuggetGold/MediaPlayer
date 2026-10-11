namespace MediaPlayer.Services;

public sealed class PlaybackEngine : IDisposable
{
    private readonly System.Windows.Media.MediaPlayer _player = new();

    public PlaybackEngine()
    {
        _player.MediaOpened += (_, _) => Opened?.Invoke(this, EventArgs.Empty);
        _player.MediaEnded += (_, _) => Ended?.Invoke(this, EventArgs.Empty);
        _player.MediaFailed += (_, _) => Failed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Opened;

    public event EventHandler? Ended;

    public event EventHandler? Failed;

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0d, 1d);
    }

    public bool HasDuration => _player.NaturalDuration.HasTimeSpan;

    public TimeSpan Duration => _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : TimeSpan.Zero;

    public TimeSpan Position
    {
        get
        {
            try
            {
                return _player.Position;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
        set
        {
            try
            {
                _player.Position = value;
            }
            catch
            {
            }
        }
    }

    public void Open(string path)
    {
        try
        {
            _player.Stop();
            _player.Close();
        }
        catch
        {
        }

        _player.Open(new Uri(path, UriKind.Absolute));
    }

    public void Play()
    {
        _player.Play();
    }

    public void Pause()
    {
        _player.Pause();
    }

    public void Stop()
    {
        try
        {
            _player.Stop();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _player.Close();
        }
        catch
        {
        }
    }
}
