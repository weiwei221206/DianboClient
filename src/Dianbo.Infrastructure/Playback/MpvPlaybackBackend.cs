using System.Diagnostics;
using System.Text.Json.Nodes;
using Dianbo.Core.Models;
using Dianbo.Core.Playback;
using Dianbo.Core.Services;

namespace Dianbo.Infrastructure.Playback;

public sealed class MpvOptions
{
    public string ExecutablePath { get; set; } = string.Empty;
    public int Volume { get; init; } = 60;
    public int NetworkTimeoutSeconds { get; init; } = 10;
    public int MediaOpenTimeoutSeconds { get; init; } = 20;
    public int IpcCommandTimeoutSeconds { get; init; } = 5;
    public int ConnectTimeoutSeconds { get; init; } = 10;
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public Action<string>? Log { get; init; }
}

public sealed class MpvPlaybackBackend : IPlaybackBackend
{
    private readonly MpvOptions _options;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private Process? _process;
    private MpvIpcConnection? _ipc;
    private ChildProcessJob? _job;
    private string? _pipeName;
    private long _generation;
    private long _songId;
    private PlaybackEndReason _pendingEndReason;
    private string? _pendingEndDetail;
    private int _volume;
    private bool _jobAssigned;
    private volatile bool _stoppingOwnedProcess;

    public MpvPlaybackBackend(MpvOptions options)
    {
        _options = options;
        _volume = Math.Clamp(options.Volume, 0, 100);
    }

    public bool IsRunning => _process is { HasExited: false };
    public int ProcessId => _process?.Id ?? 0;

    public string ExecutablePath => _options.ExecutablePath;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ExecutablePath) && File.Exists(_options.ExecutablePath);

    public bool HasJobObjectGuard => _job is not null && _jobAssigned;

    public string? LastError { get; private set; }

    public event EventHandler<BackendExitInfo>? Exited;

    public async Task SetExecutablePathAsync(string? path, CancellationToken cancellationToken)
    {
        var next = path ?? string.Empty;
        if (string.Equals(next, _options.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return;
        if (IsRunning) await StopAsync(cancellationToken).ConfigureAwait(false);
        _options.ExecutablePath = next;
        _jobAssigned = false;
        LastError = null;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return;
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            if (string.IsNullOrWhiteSpace(_options.ExecutablePath))
                throw new InvalidOperationException("尚未配置音频后端 mpv 路径");
            if (!File.Exists(_options.ExecutablePath))
                throw new FileNotFoundException("未找到播放器后端可执行文件", _options.ExecutablePath);

            if (_ipc is not null)
            {
                try { await _ipc.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
                _ipc = null;
            }

            _pipeName = $"dianbo-client-{Guid.NewGuid():N}";
            var arguments = new[]
            {
                "--no-config",
                "--idle=yes",
                "--terminal=no",
                "--vid=no",
                "--media-controls=no",
                "--pause=yes",
                $"--volume={_volume}",
                $"--network-timeout={_options.NetworkTimeoutSeconds}",
                $"--input-ipc-server=\\\\.\\pipe\\{_pipeName}"
            };

            var startInfo = new ProcessStartInfo(_options.ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath) ?? Environment.CurrentDirectory
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            if (!process.Start()) throw new InvalidOperationException("无法启动播放器后端");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            LastError = null;
            process.EnableRaisingEvents = true;
            process.Exited += OnOwnedProcessExited;
            _job ??= ChildProcessJob.TryCreate(_options.Log);
            _jobAssigned = _job?.TryAssign(process) ?? false;
            _options.Log?.Invoke($"backend started pid={process.Id} job={_jobAssigned}");

            var ipc = new MpvIpcConnection(_pipeName, TimeSpan.FromSeconds(_options.IpcCommandTimeoutSeconds));
            try
            {
                await ipc.ConnectAsync(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds), cancellationToken).ConfigureAwait(false);
                _ipc = ipc;
                await SendAsync(cancellationToken, "set_property", "volume", _volume).ConfigureAwait(false);
                await SendAsync(cancellationToken, "set_property", "mute", false).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                await ipc.DisposeAsync().ConfigureAwait(false);
                KillOwnedProcess();
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_ipc is not null)
        {
            try { await SendAsync(cancellationToken, "stop").ConfigureAwait(false); }
            catch (Exception) { }
            try { await SendAsync(cancellationToken, "quit").ConfigureAwait(false); }
            catch (Exception) { }
            await _ipc.DisposeAsync().ConfigureAwait(false);
            _ipc = null;
        }
        KillOwnedProcess();
    }

    public async Task LoadAsync(string url, long generation, long songId, CancellationToken cancellationToken)
    {
        _generation = generation;
        _songId = songId;
        _pendingEndReason = PlaybackEndReason.None;
        _pendingEndDetail = null;
        await SendAsync(cancellationToken, "set_property", "pause", true).ConfigureAwait(false);
        await SendAsync(cancellationToken, "loadfile", url).ConfigureAwait(false);
    }

    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
        => SendAsync(cancellationToken, "set_property", "pause", paused);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken)
        => SendAsync(cancellationToken, "seek", position.TotalSeconds, "absolute+exact");

    public Task StopMediaAsync(CancellationToken cancellationToken)
        => SendAsync(cancellationToken, "stop");

    public async Task SetVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        _volume = Math.Clamp(volume, 0, 100);
        await SendAsync(cancellationToken, "set_property", "volume", _volume).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<PlaybackSnapshot> ObserveAsync(long generation, long songId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await DrainEventsAsync().ConfigureAwait(false);

            double? position = null;
            double? duration = null;
            bool? paused = null;
            bool? idle = null;
            int? volume = null;
            var disconnected = false;
            try
            {
                position = await GetNumberAsync("time-pos", cancellationToken).ConfigureAwait(false);
                duration = await GetNumberAsync("duration", cancellationToken).ConfigureAwait(false);
                paused = await GetBoolAsync("pause", cancellationToken).ConfigureAwait(false);
                idle = await GetBoolAsync("idle-active", cancellationToken).ConfigureAwait(false);
                volume = (int?)await GetNumberAsync("volume", cancellationToken).ConfigureAwait(false);
            }
            catch (MpvIpcException)
            {
                disconnected = true;
            }

            if (disconnected)
            {
                yield return new PlaybackSnapshot
                {
                    Generation = generation,
                    SongId = songId,
                    State = PlaybackState.Failed,
                    EndReason = PlaybackEndReason.Error,
                    Error = "播放后端连接已断开"
                };
                yield break;
            }

            var endReason = _pendingEndReason;
            var state = endReason != PlaybackEndReason.None
                ? PlaybackState.Ended
                : idle == true && position is null
                    ? PlaybackState.Idle
                    : paused == true ? PlaybackState.Paused : PlaybackState.Playing;

            yield return new PlaybackSnapshot
            {
                Generation = generation,
                SongId = songId,
                State = state,
                Position = TimeSpan.FromSeconds(position ?? 0),
                Duration = TimeSpan.FromSeconds(duration ?? 0),
                IsPaused = paused ?? false,
                Volume = volume ?? _volume,
                EndReason = endReason,
                Error = _pendingEndDetail
            };

            _pendingEndReason = PlaybackEndReason.None;
            _pendingEndDetail = null;

            try
            {
                await Task.Delay(_options.ProgressInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    private async Task DrainEventsAsync()
    {
        if (_ipc is null) return;
        while (_ipc.Events.TryRead(out var message))
        {
            var kind = message["event"]?.GetValue<string>();
            switch (kind)
            {
                case "end-file":
                    _pendingEndReason = MapEndReason(message);
                    _pendingEndDetail = DescribeEndFile(message, _pendingEndReason);
                    break;
                case "start-file":
                case "file-loaded":
                    _options.Log?.Invoke($"backend event={kind}");
                    break;
            }
        }
    }

    private static PlaybackEndReason MapEndReason(JsonObject message)
    {
        var reasonText = message["reason"]?.ToString();
        var errorText = message["error"]?.ToString();
        if (string.Equals(errorText, "error", StringComparison.OrdinalIgnoreCase)) return PlaybackEndReason.Error;
        return reasonText switch
        {
            "eof" => PlaybackEndReason.EndOfFile,
            "stop" => PlaybackEndReason.Stopped,
            "quit" => PlaybackEndReason.Stopped,
            "error" => PlaybackEndReason.Error,
            _ => PlaybackEndReason.Unknown
        };
    }

    private static string? DescribeEndFile(JsonObject message, PlaybackEndReason reason)
    {
        if (reason == PlaybackEndReason.Error)
        {
            var fileError = message["file_error"]?.ToString();
            return string.IsNullOrEmpty(fileError) ? "播放器报告播放错误" : $"播放器报告播放错误（{fileError}）";
        }
        if (reason == PlaybackEndReason.Unknown) return "播放结束原因未知";
        return null;
    }

    private async Task SendAsync(CancellationToken cancellationToken, params object?[] command)
    {
        if (_ipc is null) throw new MpvIpcException("播放后端尚未启动", pipeClosed: true);
        var response = await _ipc.SendAsync(cancellationToken, command).ConfigureAwait(false);
        if (response.TryGetPropertyValue("error", out var error) && error is not null)
        {
            var text = error.ToString();
            if (!string.Equals(text, "success", StringComparison.OrdinalIgnoreCase))
                throw new MpvIpcException($"mpv 命令失败：{command.FirstOrDefault()} -> {text}");
        }
    }

    private async Task<double?> GetNumberAsync(string property, CancellationToken cancellationToken)
    {
        var response = await SendForValueAsync(property, cancellationToken).ConfigureAwait(false);
        if (response is null) return null;
        return response is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
    }

    private async Task<bool?> GetBoolAsync(string property, CancellationToken cancellationToken)
    {
        var response = await SendForValueAsync(property, cancellationToken).ConfigureAwait(false);
        if (response is null) return null;
        return response is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    }

    private async Task<JsonNode?> SendForValueAsync(string property, CancellationToken cancellationToken)
    {
        if (_ipc is null) throw new MpvIpcException("播放后端尚未启动", pipeClosed: true);
        var response = await _ipc.SendAsync(cancellationToken, "get_property", property).ConfigureAwait(false);
        if (response.TryGetPropertyValue("error", out var error) && error is not null)
        {
            var text = error.ToString();
            if (!string.Equals(text, "success", StringComparison.OrdinalIgnoreCase)) return null;
        }
        return response.TryGetPropertyValue("data", out var data) ? data : null;
    }

    private void OnOwnedProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process) return;
        if (!ReferenceEquals(process, _process)) return;
        var expected = _stoppingOwnedProcess;
        int exitCode;
        try { exitCode = process.ExitCode; }
        catch (Exception) { exitCode = -1; }
        _options.Log?.Invoke($"backend exited pid={SafeProcessId(process)} code={exitCode} expected={expected}");
        if (expected) return;
        LastError = $"后端进程已退出（退出码 {exitCode}）";
        try { Exited?.Invoke(this, new BackendExitInfo(exitCode, false)); }
        catch (Exception) { }
    }

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch (Exception) { return 0; }
    }

    private void KillOwnedProcess()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        _stoppingOwnedProcess = true;
        try
        {
            process.Exited -= OnOwnedProcessExited;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            process.Dispose();
            _stoppingOwnedProcess = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
        try { _job?.Dispose(); }
        catch (Exception) { }
        _job = null;
        _jobAssigned = false;
        _startGate.Dispose();
    }
}
