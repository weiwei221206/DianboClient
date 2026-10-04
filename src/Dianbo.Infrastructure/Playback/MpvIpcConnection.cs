using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Dianbo.Infrastructure.Playback;

public sealed class MpvIpcException : Exception
{
    public MpvIpcException(string message, bool pipeClosed = false) : base(message) => PipeClosed = pipeClosed;
    public bool PipeClosed { get; }
}

public sealed class MpvIpcConnection : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly TimeSpan _commandTimeout;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly Channel<JsonObject> _events = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _lifetime;
    private Task? _readLoop;
    private long _requestId;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private volatile bool _closed;

    public MpvIpcConnection(string pipeName, TimeSpan commandTimeout)
    {
        _pipeName = pipeName;
        _commandTimeout = commandTimeout;
    }

    public ChannelReader<JsonObject> Events => _events.Reader;
    public bool IsClosed => _closed;

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_pipe is not null) return;
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await pipe.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (TimeoutException)
                {
                    if (Environment.TickCount64 > deadline) throw new MpvIpcException("连接 mpv IPC 超时");
                }
            }
        }
        catch (Exception)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 8192, leaveOpen: true) { AutoFlush = true };
        _lifetime = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_lifetime.Token));
    }

    public async Task<JsonObject> SendAsync(CancellationToken cancellationToken, params object?[] command)
    {
        if (_closed || _writer is null) throw new MpvIpcException("IPC 连接未建立或已断开", pipeClosed: true);
        var requestId = Interlocked.Increment(ref _requestId);
        var payload = new JsonObject
        {
            ["command"] = new JsonArray(command.Select(ToNode).ToArray()),
            ["request_id"] = requestId
        };
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_commandTimeout);
        try
        {
            await _writeGate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(payload.ToJsonString().AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            }
            catch
            {
                Close();
                _pipe?.Dispose();
                throw;
            }
            finally { _writeGate.Release(); }
            return await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _pending.TryRemove(requestId, out _);
            throw new MpvIpcException("mpv 命令超时");
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new MpvIpcException("写入 mpv IPC 失败", pipeClosed: true);
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        _ => JsonValue.Create(value.ToString())
    };

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _reader!.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }
                if (message is null) continue;

                if (message.TryGetPropertyValue("request_id", out var idNode) && idNode is not null)
                {
                    var requestId = idNode.GetValue<long>();
                    if (_pending.TryRemove(requestId, out var completion)) completion.TrySetResult(message);
                    continue;
                }

                _events.Writer.TryWrite(message);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            Close();
        }
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var completion))
                completion.TrySetException(new MpvIpcException("mpv IPC 连接已断开", pipeClosed: true));
        }
        try { _events.Writer.TryComplete(); } catch (Exception) { }
        try { _lifetime?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); }
            catch (Exception) { }
        }
        _lifetime?.Dispose();
        try
        {
            if (_writer is not null) await _writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) { }
        try
        {
            _reader?.Dispose();
        }
        catch (Exception) { }
        try
        {
            if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) { }
    }
}
