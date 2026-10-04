using Dianbo.Core.Models;
using Windows.Media;

namespace Dianbo.App;

public sealed partial class MainWindow
{
    private SystemMediaTransportControls? _systemMediaControls;
    private long? _systemMediaSongId;
    private MediaPlaybackStatus? _systemMediaStatus;

    private void InitializeSystemMediaControls(nint hwnd)
    {
        try
        {
            _systemMediaControls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            _systemMediaControls.ButtonPressed += SystemMediaControls_ButtonPressed;
            UpdateSystemMediaControls(_services.Coordinator.Snapshot);
        }
        catch (Exception error)
        {
            _services.Log.Write($"system media controls unavailable: {error.GetType().Name}");
            _systemMediaControls = null;
            UseSystemMediaKeysToggle.IsEnabled = false;
            SystemMediaKeysDescription.Text = "当前系统无法启用媒体键控制。";
        }
    }

    private void UpdateSystemMediaControls(PlaybackSnapshot snapshot)
    {
        var controls = _systemMediaControls;
        if (controls is null) return;

        var song = _services.Coordinator.CurrentSong;
        var enabled = _services.Settings.UseSystemMediaKeys && song is not null;
        controls.IsEnabled = enabled;
        if (!enabled)
        {
            _systemMediaStatus = null;
            return;
        }

        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.IsNextEnabled = true;
        controls.IsPreviousEnabled = true;

        if (_systemMediaSongId != song!.Id)
        {
            _systemMediaSongId = song.Id;
            var display = controls.DisplayUpdater;
            display.Type = MediaPlaybackType.Music;
            display.AppMediaId = ShellIdentity.AppId;
            display.MusicProperties.Title = song.Name;
            display.MusicProperties.Artist = song.Artist;
            display.Thumbnail = null;
            display.Update();
        }

        var status = snapshot.State switch
        {
            PlaybackState.Playing when !snapshot.IsPaused => MediaPlaybackStatus.Playing,
            PlaybackState.Paused => MediaPlaybackStatus.Paused,
            PlaybackState.Resolving or PlaybackState.Loading or PlaybackState.Seeking => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Stopped
        };
        if (_systemMediaStatus != status)
        {
            controls.PlaybackStatus = status;
            _systemMediaStatus = status;
        }
    }

    private void SystemMediaControls_ButtonPressed(
        SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (_closing || !_services.Settings.UseSystemMediaKeys) return;
        var button = args.Button;
        _dispatcher.Post(() => _ = HandleSystemMediaButtonAsync(button));
    }

    private async Task HandleSystemMediaButtonAsync(SystemMediaTransportControlsButton button)
    {
        if (_closing || !_services.Settings.UseSystemMediaKeys || _services.Coordinator.CurrentSong is null) return;
        try
        {
            var snapshot = _services.Coordinator.Snapshot;
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                    if (snapshot.IsPaused || snapshot.AudioSource is null)
                        await _services.Coordinator.TogglePauseAsync(_lifetime.Token);
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    if (!snapshot.IsPaused && snapshot.State == PlaybackState.Playing)
                        await _services.Coordinator.TogglePauseAsync(_lifetime.Token);
                    break;
                case SystemMediaTransportControlsButton.Next:
                    await _services.Coordinator.NextAsync(_lifetime.Token);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    await _services.Coordinator.PreviousAsync(_lifetime.Token);
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _services.Log.Write($"system media command failed: {error.GetType().Name}");
        }
    }

    private void DisposeSystemMediaControls()
    {
        var controls = _systemMediaControls;
        if (controls is null) return;
        controls.ButtonPressed -= SystemMediaControls_ButtonPressed;
        controls.IsEnabled = false;
        _systemMediaControls = null;
    }
}
