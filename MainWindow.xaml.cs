using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MediaPlayer.Models;
using MediaPlayer.Services;
using Microsoft.Win32;

namespace MediaPlayer;

public partial class MainWindow : Window
{
    private const double SeekStepSeconds = 5d;
    private const double VolumeStep = 0.05d;
    private const string DefaultStatus = "Перетащите аудиофайлы или папки сюда";

    private enum RepeatMode
    {
        Off,
        One,
        All
    }

    private enum BannerKind
    {
        Error,
        Warning,
        Info
    }

    private sealed class ExpandResult
    {
        public List<string> Files { get; } = new();

        public List<string> Missing { get; } = new();

        public int Unsupported { get; set; }
    }

    private readonly PlaybackEngine _engine = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _bannerTimer;
    private readonly DispatcherTimer _volumeSaveTimer;
    private readonly Random _random = new();
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<Track> _history = new();
    private readonly ICollectionView _view;
    private readonly DropShadowEffect _playGlow;

    private Track? _currentTrack;
    private RepeatMode _repeatMode;
    private bool _shuffle;
    private bool _isPlaying;
    private bool _suppressSeek;
    private bool _draggingSeek;
    private bool _mediaReady;
    private bool _advancing;
    private volatile bool _closing;
    private bool _initializing = true;
    private int _consecutiveFailures;
    private int _nowPlayingVersion;
    private int _bannerToken;
    private double _volume = 0.8d;
    private string _searchText = string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        _playGlow = (DropShadowEffect)PlayButton.Effect;

        _view = CollectionViewSource.GetDefaultView(Playlist);
        _view.Filter = FilterTrack;
        TrackList.ItemsSource = _view;

        _positionTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += PositionTimer_Tick;

        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _bannerTimer.Tick += BannerTimer_Tick;

        _volumeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _volumeSaveTimer.Tick += VolumeSaveTimer_Tick;

        _engine.Opened += Engine_Opened;
        _engine.Ended += Engine_Ended;
        _engine.Failed += Engine_Failed;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        _volume = PlaylistStore.LoadVolume();
        VolumeSlider.Value = _volume;
        _engine.Volume = _volume;
        VolumeText.Text = FormatVolume(_volume);

        SeekSlider.IsEnabled = false;

        _initializing = false;
    }

    public ObservableCollection<Track> Playlist { get; } = new();

    private static string FormatVolume(double value)
    {
        return $"{(int)Math.Round(Math.Clamp(value, 0d, 1d) * 100d)}%";
    }

    private static string TrackWord(int count)
    {
        var mod100 = count % 100;
        var mod10 = count % 10;

        if (mod100 is >= 11 and <= 14)
        {
            return "треков";
        }

        if (mod10 == 1)
        {
            return "трек";
        }

        if (mod10 is >= 2 and <= 4)
        {
            return "трека";
        }

        return "треков";
    }

    private static ExpandResult ExpandPaths(IEnumerable<string> rawPaths)
    {
        var result = new ExpandResult();

        foreach (var raw in rawPaths)
        {
            if (File.Exists(raw))
            {
                if (MetadataService.IsSupported(raw))
                {
                    result.Files.Add(raw);
                }
                else
                {
                    result.Unsupported++;
                }

                continue;
            }

            if (Directory.Exists(raw))
            {
                CollectDirectory(raw, result);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(raw))
            {
                result.Missing.Add(raw);
            }
        }

        return result;
    }

    private static void CollectDirectory(string root, ExpandResult result)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            try
            {
                foreach (var file in Directory.GetFiles(directory))
                {
                    if (MetadataService.IsSupported(file))
                    {
                        result.Files.Add(file);
                    }
                    else
                    {
                        result.Unsupported++;
                    }
                }
            }
            catch
            {
            }

            try
            {
                foreach (var subdirectory in Directory.GetDirectories(directory))
                {
                    pending.Push(subdirectory);
                }
            }
            catch
            {
            }
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PlayWindowIntro();
        _positionTimer.Start();
        await LoadSavedPlaylistAsync();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;

        _positionTimer.Stop();
        _bannerTimer.Stop();
        _volumeSaveTimer.Stop();

        PlaylistStore.SaveVolume(_volume);
        SavePlaylist();
        _engine.Dispose();
    }

    private void PlayWindowIntro()
    {
        var fade = new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(340))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fade);

        var slide = new DoubleAnimation(16d, 0d, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RootSlide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private async Task LoadSavedPlaylistAsync()
    {
        List<string> saved;

        try
        {
            saved = PlaylistStore.LoadPlaylist();
        }
        catch
        {
            saved = new List<string>();
        }

        if (saved.Count == 0 || _closing)
        {
            if (!_closing)
            {
                UpdateListState();
            }

            return;
        }

        SetStatus("Загрузка плейлиста…");

        var tracks = await Task.Run(() => saved
            .Where(File.Exists)
            .Where(MetadataService.IsSupported)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(MetadataService.Create)
            .ToList());

        if (_closing)
        {
            return;
        }

        foreach (var track in tracks)
        {
            Playlist.Add(track);
            _knownPaths.Add(track.FilePath);
        }

        _view.Refresh();
        UpdateListState();
        SetStatus(null);
    }

    private bool FilterTrack(object item)
    {
        if (item is not Track track)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_searchText))
        {
            return true;
        }

        return track.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || track.Artist.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
            || track.FileName.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string? text)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(text) ? DefaultStatus : text;
    }

    private void UpdateListState()
    {
        CountText.Text = Playlist.Count == 0
            ? "Пусто"
            : $"{Playlist.Count} {TrackWord(Playlist.Count)}";

        var visible = _view.Cast<object>().Count();

        if (Playlist.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyStateText.Text = "Список пуст";
            EmptyStateHint.Text = "Добавьте файлы или перетащите их в окно";
        }
        else if (visible == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyStateText.Text = "Ничего не найдено";
            EmptyStateHint.Text = "Измените поисковый запрос";
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
        }
    }

    private void SavePlaylist()
    {
        PlaylistStore.SavePlaylist(Playlist.Select(track => track.FilePath));
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Выберите аудиофайлы",
            Filter = "Аудиофайлы|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac|Все файлы|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            AddPaths(dialog.FileNames);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выберите папку с музыкой",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            AutoUpgradeEnabled = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            AddPaths(new[] { dialog.SelectedPath });
        }
    }

    private async void AddPaths(IEnumerable<string> rawPaths)
    {
        var roots = rawPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();

        if (roots.Count == 0)
        {
            return;
        }

        SetStatus("Поиск аудиофайлов…");

        ExpandResult expanded;

        try
        {
            expanded = await Task.Run(() => ExpandPaths(roots));
        }
        catch
        {
            ShowError("Не удалось прочитать выбранные папки. Проверьте, что у вас есть к ним доступ.");
            return;
        }

        if (_closing)
        {
            return;
        }

        if (expanded.Missing.Count == 1)
        {
            ShowError($"Файл «{Path.GetFileName(expanded.Missing[0])}» не найден. Возможно, он был перемещён или удалён.");
        }
        else if (expanded.Missing.Count > 1)
        {
            ShowError($"Не найдено файлов: {expanded.Missing.Count}. Возможно, они были перемещены или удалены.");
        }

        if (expanded.Unsupported > 0)
        {
            ShowWarning("Этот формат не поддерживается. Подойдут MP3, WAV, WMA, M4A, AAC и FLAC.");
        }

        var fresh = expanded.Files
            .Where(file => !_knownPaths.Contains(file))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => Path.GetFileName(file), NaturalComparer.Instance)
            .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fresh.Count == 0)
        {
            SetStatus("Новые треки не найдены");
            return;
        }

        SetStatus("Добавление треков…");

        List<Track> loaded;

        try
        {
            loaded = await Task.Run(() => fresh.Select(MetadataService.Create).ToList());
        }
        catch
        {
            ShowError("Не удалось добавить некоторые файлы. Проверьте, что они доступны.");
            return;
        }

        if (_closing)
        {
            return;
        }

        foreach (var track in loaded)
        {
            Playlist.Add(track);
            _knownPaths.Add(track.FilePath);
        }

        _view.Refresh();
        UpdateListState();
        SavePlaylist();
        SetStatus($"Добавлено треков: {loaded.Count}");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text?.Trim() ?? string.Empty;
        SearchHintPanel.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        UpdateListState();
    }

    private void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;

        while (node is not null)
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase)
            {
                return;
            }

            if (node is ListBoxItem item)
            {
                if (item.DataContext is Track track)
                {
                    PlayTrack(track, true);
                }

                return;
            }

            node = VisualTreeHelper.GetParent(node);
        }
    }

    private void DeleteTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Track track)
        {
            RemoveTrack(track);
        }

        e.Handled = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (Playlist.Count == 0)
        {
            SetStatus("Список уже пуст");
            return;
        }

        _engine.Stop();
        _isPlaying = false;
        StopPulse();
        UpdatePlayButton();
        _history.Clear();
        _currentTrack = null;

        Playlist.Clear();
        _knownPaths.Clear();

        _view.Refresh();
        ShowIdleNowPlaying();
        ResetSeekUi();
        UpdateListState();
        SavePlaylist();
        SetStatus("Плейлист очищен");
    }

    private void RemoveTrack(Track track)
    {
        var wasCurrent = ReferenceEquals(track, _currentTrack);
        var index = Playlist.IndexOf(track);

        RemoveTrackSilently(track);

        if (!wasCurrent)
        {
            SetStatus("Трек удалён из списка");
            return;
        }

        if (Playlist.Count == 0)
        {
            SetStatus("Плейлист пуст");
            return;
        }

        StartSequence(Math.Clamp(index, 0, Playlist.Count - 1));
    }

    private void RemoveTrackSilently(Track track)
    {
        if (!Playlist.Remove(track))
        {
            return;
        }

        _knownPaths.Remove(track.FilePath);
        RemoveFromHistory(track);

        if (ReferenceEquals(track, _currentTrack))
        {
            _engine.Stop();
            _isPlaying = false;
            StopPulse();
            UpdatePlayButton();
            _currentTrack = null;
            ShowIdleNowPlaying();
            ResetSeekUi();
        }

        _view.Refresh();
        UpdateListState();
        SavePlaylist();
    }

    private void RemoveFromHistory(Track track)
    {
        if (_history.Count == 0)
        {
            return;
        }

        var remaining = _history.Where(item => !ReferenceEquals(item, track)).ToList();
        _history.Clear();

        for (var index = remaining.Count - 1; index >= 0; index--)
        {
            _history.Push(remaining[index]);
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private void TogglePlayPause()
    {
        if (_currentTrack is null)
        {
            if (TrackList.SelectedItem is Track selected)
            {
                PlayTrack(selected, true);
                return;
            }

            if (Playlist.Count > 0)
            {
                PlayTrack(Playlist[0], true);
                return;
            }

            ShowWarning("Список пуст. Добавьте музыку, чтобы начать прослушивание.");
            return;
        }

        if (_isPlaying)
        {
            _engine.Pause();
            _isPlaying = false;
            StopPulse();
            UpdatePlayButton();
            SetStatus($"Пауза: {_currentTrack.Title}");
            return;
        }

        _engine.Play();
        _isPlaying = true;

        foreach (var item in Playlist)
        {
            item.IsCurrent = ReferenceEquals(item, _currentTrack);
        }

        StartPulse();
        UpdatePlayButton();
        SetStatus($"Играет: {_currentTrack.Title}");
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        _isPlaying = false;
        StopPulse();
        UpdatePlayButton();
        ResetSeekUi();

        if (_currentTrack is not null)
        {
            SetStatus($"Остановлено: {_currentTrack.Title}");
        }
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        PreviousTrack();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        NextTrack(true);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _shuffle = ShuffleButton.IsChecked == true;
        SetStatus(_shuffle ? "Перемешивание включено" : "Перемешивание выключено");
    }

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _repeatMode = _repeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        };

        RepeatButton.IsChecked = _repeatMode != RepeatMode.Off;
        RepeatButton.Content = _repeatMode == RepeatMode.One ? "\uE8ED" : "\uE8EE";
        RepeatButton.ToolTip = _repeatMode switch
        {
            RepeatMode.One => "Повтор трека",
            RepeatMode.All => "Повтор списка",
            _ => "Повтор выключен"
        };

        SetStatus(_repeatMode switch
        {
            RepeatMode.One => "Повтор: текущий трек",
            RepeatMode.All => "Повтор: весь список",
            _ => "Повтор выключен"
        });
    }

    private void PlayTrack(Track track, bool remember)
    {
        var index = Playlist.IndexOf(track);

        if (index < 0)
        {
            return;
        }

        if (remember && _currentTrack is not null && !ReferenceEquals(_currentTrack, track))
        {
            _history.Push(_currentTrack);
        }

        StartSequence(index);
    }

    private void StartSequence(int startIndex)
    {
        if (_advancing)
        {
            return;
        }

        _advancing = true;

        try
        {
            RunSequence(startIndex);
        }
        finally
        {
            _advancing = false;
        }
    }

    private void RunSequence(int startIndex)
    {
        var total = Playlist.Count;

        if (total == 0)
        {
            ShowWarning("Список пуст. Добавьте музыку, чтобы начать прослушивание.");
            return;
        }

        var index = startIndex;

        for (var attempt = 0; attempt < total; attempt++)
        {
            if (Playlist.Count == 0)
            {
                FailSequence();
                return;
            }

            if (index < 0 || index >= Playlist.Count)
            {
                index = 0;
            }

            var track = Playlist[index];

            if (!File.Exists(track.FilePath))
            {
                ShowError($"Файл «{track.FileName}» не найден. Возможно, он был перемещён или удалён.");
                RemoveTrackSilently(track);

                if (index >= Playlist.Count)
                {
                    index = 0;
                }

                continue;
            }

            if (TryStartPlayback(track))
            {
                CommitStartedTrack(track);
                return;
            }

            ShowError($"Не удалось воспроизвести «{track.Title}». Возможно, файл повреждён.");
            _consecutiveFailures++;

            if (FailureLimitReached())
            {
                FailSequence();
                return;
            }

            index = NextIndex(index);
        }

        FailSequence();
    }

    private bool TryStartPlayback(Track track)
    {
        try
        {
            _engine.Open(track.FilePath);
            _engine.Volume = _volume;
            _engine.Play();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool FailureLimitReached()
    {
        return Playlist.Count == 0 || _consecutiveFailures >= Playlist.Count;
    }

    private void FailSequence()
    {
        _engine.Stop();
        _isPlaying = false;
        StopPulse();
        UpdatePlayButton();
        ResetSeekUi();
        _consecutiveFailures = 0;
        _currentTrack = null;

        foreach (var item in Playlist)
        {
            item.IsCurrent = false;
        }

        ShowIdleNowPlaying();

        if (Playlist.Count == 0)
        {
            SetStatus("В плейлисте не осталось доступных треков");
            return;
        }

        ShowError("Не удалось воспроизвести ни один трек. Проверьте, что файлы не повреждены или не удалены.");
        SetStatus("Воспроизведение остановлено");
    }

    private void CommitStartedTrack(Track track)
    {
        _currentTrack = track;

        foreach (var item in Playlist)
        {
            item.IsCurrent = ReferenceEquals(item, track);
        }

        _isPlaying = true;
        StartPulse();
        UpdatePlayButton();
        SetCurrentSelection(track);
        UpdateNowPlaying(track);
        ResetSeekUi();

        if (track.Duration > TimeSpan.Zero)
        {
            TotalTimeText.Text = Track.Format(track.Duration);
        }

        SetStatus($"Играет: {track.Title}");
    }

    private void SetCurrentSelection(Track track)
    {
        TrackList.SelectedItem = track;
        TrackList.ScrollIntoView(track);
    }

    private int NextIndex(int index)
    {
        if (Playlist.Count <= 1)
        {
            return 0;
        }

        if (_shuffle)
        {
            var next = index;

            while (next == index)
            {
                next = _random.Next(Playlist.Count);
            }

            return next;
        }

        return (index + 1) % Playlist.Count;
    }

    private void NextTrack(bool fromUser)
    {
        if (Playlist.Count == 0)
        {
            return;
        }

        var index = _currentTrack is null ? -1 : Playlist.IndexOf(_currentTrack);

        if (_shuffle && Playlist.Count > 1)
        {
            StartSequence(NextIndex(index));
            return;
        }

        var target = index + 1;

        if (target >= Playlist.Count)
        {
            if (_repeatMode == RepeatMode.All || fromUser)
            {
                target = 0;
            }
            else
            {
                FinishPlayback();
                return;
            }
        }

        StartSequence(Math.Max(target, 0));
    }

    private void PreviousTrack()
    {
        if (Playlist.Count == 0)
        {
            return;
        }

        if (_currentTrack is not null && _engine.Position.TotalSeconds > 3d)
        {
            _engine.Position = TimeSpan.Zero;
            _suppressSeek = true;
            SeekSlider.Value = 0d;
            _suppressSeek = false;
            CurrentTimeText.Text = "0:00";
            return;
        }

        if (_shuffle)
        {
            while (_history.Count > 0)
            {
                var previous = _history.Pop();
                var previousIndex = Playlist.IndexOf(previous);

                if (previousIndex >= 0)
                {
                    StartSequence(previousIndex);
                    return;
                }
            }
        }

        var index = _currentTrack is null ? 0 : Playlist.IndexOf(_currentTrack);

        if (index < 0)
        {
            index = 0;
        }

        var target = index - 1;

        if (target < 0)
        {
            target = Playlist.Count - 1;
        }

        StartSequence(target);
    }

    private void FinishPlayback()
    {
        _engine.Stop();
        _isPlaying = false;
        StopPulse();
        UpdatePlayButton();
        ResetSeekUi();

        foreach (var item in Playlist)
        {
            item.IsCurrent = false;
        }

        SetStatus("Воспроизведение завершено");
    }

    private void UpdatePlayButton()
    {
        PlayButton.Content = _isPlaying ? "\uE769" : "\uE768";
        PlayButton.ToolTip = _isPlaying ? "Пауза" : "Воспроизведение";
    }

    private void StartPulse()
    {
        var pulse = new DoubleAnimation
        {
            From = 0.28d,
            To = 0.95d,
            Duration = TimeSpan.FromMilliseconds(950),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        _playGlow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
    }

    private void StopPulse()
    {
        var fade = new DoubleAnimation(0d, TimeSpan.FromMilliseconds(180));
        _playGlow.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
    }

    private void UpdateNowPlaying(Track track)
    {
        var version = ++_nowPlayingVersion;

        var fadeOut = new DoubleAnimation(1d, 0d, TimeSpan.FromMilliseconds(140));
        fadeOut.Completed += (_, _) =>
        {
            if (version != _nowPlayingVersion)
            {
                return;
            }

            ApplyNowPlaying(track);

            var fadeIn = new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            NowPlayingContent.BeginAnimation(OpacityProperty, fadeIn);
        };

        NowPlayingContent.BeginAnimation(OpacityProperty, fadeOut);
        ApplyBackdrop(track);
    }

    private void ApplyNowPlaying(Track track)
    {
        NowTitleText.Text = track.Title;
        NowArtistText.Text = track.ArtistDisplay;

        if (track.Cover is not null)
        {
            CoverBrush.ImageSource = track.Cover;
            CoverFrame.Visibility = Visibility.Visible;
            CoverPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            CoverBrush.ImageSource = null;
            CoverFrame.Visibility = Visibility.Collapsed;
            CoverPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void ApplyBackdrop(Track track)
    {
        if (track.Cover is not null)
        {
            BackdropBrush.ImageSource = track.Cover;
            CoverBackdrop.Visibility = Visibility.Visible;
        }
        else
        {
            BackdropBrush.ImageSource = null;
            CoverBackdrop.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowIdleNowPlaying()
    {
        _nowPlayingVersion++;
        NowPlayingContent.BeginAnimation(OpacityProperty, null);
        NowPlayingContent.Opacity = 1d;
        NowTitleText.Text = "Ничего не играет";
        NowArtistText.Text = "Выберите трек из плейлиста";
        CoverBrush.ImageSource = null;
        BackdropBrush.ImageSource = null;
        CoverBackdrop.Visibility = Visibility.Collapsed;
        CoverFrame.Visibility = Visibility.Collapsed;
        CoverPlaceholder.Visibility = Visibility.Visible;
    }

    private void ResetSeekUi()
    {
        _mediaReady = false;
        _draggingSeek = false;

        _suppressSeek = true;
        SeekSlider.IsEnabled = false;
        SeekSlider.Maximum = 1d;
        SeekSlider.Value = 0d;
        CurrentTimeText.Text = "0:00";
        TotalTimeText.Text = "0:00";
        _suppressSeek = false;
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentTrack is null)
        {
            return;
        }

        if (_engine.HasDuration)
        {
            var duration = _engine.Duration;

            if (Math.Abs(SeekSlider.Maximum - duration.TotalSeconds) > 0.5d)
            {
                _suppressSeek = true;
                SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 0.001d);
                _suppressSeek = false;
            }

            if (!_mediaReady)
            {
                _mediaReady = true;
                SeekSlider.IsEnabled = true;
            }

            TotalTimeText.Text = Track.Format(duration);
        }

        if (!_mediaReady || _draggingSeek)
        {
            return;
        }

        var position = _engine.Position;
        _suppressSeek = true;
        SeekSlider.Value = position.TotalSeconds;
        _suppressSeek = false;
        CurrentTimeText.Text = Track.Format(position);
    }

    private void Engine_Opened(object? sender, EventArgs e)
    {
        _consecutiveFailures = 0;
        _mediaReady = true;
        SeekSlider.IsEnabled = true;

        if (_currentTrack is not null && _engine.HasDuration)
        {
            var duration = _engine.Duration;
            _suppressSeek = true;
            SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 0.001d);
            _suppressSeek = false;
            TotalTimeText.Text = Track.Format(duration);
        }
    }

    private void Engine_Ended(object? sender, EventArgs e)
    {
        if (_repeatMode == RepeatMode.One && _currentTrack is not null)
        {
            _engine.Stop();
            _engine.Play();
            _isPlaying = true;
            StartPulse();
            UpdatePlayButton();
            return;
        }

        NextTrack(false);
    }

    private void Engine_Failed(object? sender, EventArgs e)
    {
        var failed = _currentTrack;

        if (failed is not null)
        {
            ShowError($"Не удалось воспроизвести «{failed.Title}». Возможно, файл повреждён.");
        }

        _isPlaying = false;
        StopPulse();
        UpdatePlayButton();

        _consecutiveFailures++;

        if (FailureLimitReached())
        {
            FailSequence();
            return;
        }

        var index = failed is null ? -1 : Playlist.IndexOf(failed);
        StartSequence(NextIndex(index));
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || _suppressSeek || !_mediaReady || _currentTrack is null)
        {
            return;
        }

        _engine.Position = TimeSpan.FromSeconds(e.NewValue);
        CurrentTimeText.Text = Track.Format(TimeSpan.FromSeconds(e.NewValue));
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mediaReady)
        {
            e.Handled = true;
            return;
        }

        _draggingSeek = true;
    }

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggingSeek = false;
    }

    private void SeekSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _draggingSeek = false;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
        {
            return;
        }

        _volume = Math.Clamp(e.NewValue, 0d, 1d);
        _engine.Volume = _volume;
        VolumeText.Text = FormatVolume(_volume);
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    private void VolumeSaveTimer_Tick(object? sender, EventArgs e)
    {
        _volumeSaveTimer.Stop();
        PlaylistStore.SaveVolume(_volume);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Восстановить" : "Развернуть";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            e.Handled = true;
            AddFiles_Click(this, new RoutedEventArgs());
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                TogglePlayPause();
                break;
            case Key.Left:
                e.Handled = true;
                SeekBy(-SeekStepSeconds);
                break;
            case Key.Right:
                e.Handled = true;
                SeekBy(SeekStepSeconds);
                break;
            case Key.Up:
                e.Handled = true;
                ChangeVolume(VolumeStep);
                break;
            case Key.Down:
                e.Handled = true;
                ChangeVolume(-VolumeStep);
                break;
            case Key.Delete:
                if (TrackList.SelectedItem is Track selected)
                {
                    e.Handled = true;
                    RemoveTrack(selected);
                }

                break;
            case Key.Enter:
                if (TrackList.SelectedItem is Track chosen)
                {
                    e.Handled = true;
                    PlayTrack(chosen, true);
                }

                break;
            case Key.Escape:
                HideBanner();
                break;
        }
    }

    private void SeekBy(double seconds)
    {
        if (_currentTrack is null || !_mediaReady)
        {
            return;
        }

        var maximum = Math.Max(SeekSlider.Maximum, 0d);
        var target = Math.Clamp(_engine.Position.TotalSeconds + seconds, 0d, maximum);

        _engine.Position = TimeSpan.FromSeconds(target);

        _suppressSeek = true;
        SeekSlider.Value = target;
        _suppressSeek = false;
        CurrentTimeText.Text = Track.Format(TimeSpan.FromSeconds(target));
    }

    private void ChangeVolume(double delta)
    {
        VolumeSlider.Value = Math.Clamp(_volume + delta, 0d, 1d);
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        SetDropOverlay(true);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        SetDropOverlay(false);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        SetDropOverlay(false);

        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddPaths(paths);
        }

        e.Handled = true;
    }

    private void SetDropOverlay(bool visible)
    {
        var fade = new DoubleAnimation(visible ? 1d : 0d, TimeSpan.FromMilliseconds(visible ? 160 : 220));
        DropOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowError(string message)
    {
        ShowBanner(message, BannerKind.Error);
    }

    private void ShowWarning(string message)
    {
        ShowBanner(message, BannerKind.Warning);
    }

    private void ShowBanner(string message, BannerKind kind)
    {
        var accent = kind switch
        {
            BannerKind.Error => (Brush)FindResource("ErrorBrush"),
            BannerKind.Warning => (Brush)FindResource("WarningBrush"),
            _ => (Brush)FindResource("Accent2Brush")
        };

        BannerBorder.BorderBrush = accent;
        BannerIcon.Foreground = accent;
        BannerIcon.Text = kind == BannerKind.Info ? "\uE946" : "\uE7BA";
        BannerText.Text = message;
        BannerBorder.Visibility = Visibility.Visible;

        var fade = new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(220));
        BannerBorder.BeginAnimation(OpacityProperty, fade);

        var slide = new DoubleAnimation(-18d, 0d, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BannerSlide.BeginAnimation(TranslateTransform.YProperty, slide);

        if (kind != BannerKind.Error)
        {
            _bannerTimer.Stop();
            _bannerTimer.Start();
        }
    }

    private void BannerTimer_Tick(object? sender, EventArgs e)
    {
        _bannerTimer.Stop();
        HideBanner();
    }

    private void BannerClose_Click(object sender, RoutedEventArgs e)
    {
        _bannerTimer.Stop();
        HideBanner();
    }

    private void HideBanner()
    {
        var token = ++_bannerToken;

        var fade = new DoubleAnimation(0d, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) =>
        {
            if (token != _bannerToken)
            {
                return;
            }

            BannerBorder.Visibility = Visibility.Collapsed;
        };

        BannerBorder.BeginAnimation(OpacityProperty, fade);
    }
}
