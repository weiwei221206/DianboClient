using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dianbo.Core.Models;
using Dianbo.Core.Services;

namespace Dianbo.Infrastructure.Auth;

public sealed partial class DpapiSessionStore : ISessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DianboClient.session.v1");
    private const long MaxFileBytes = 65536;

    public DpapiSessionStore(string path) => Location = path;

    public string Location { get; set; }

    public Task<Session?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(Location);
            if (!info.Exists || info.Length is <= 0 or > MaxFileBytes) return Task.FromResult<Session?>(null);
            var encrypted = File.ReadAllBytes(Location);
            var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var session = Deserialize(plain);
            return Task.FromResult(session);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return Task.FromResult<Session?>(null);
        }
    }

    public async Task SaveAsync(Session session, CancellationToken cancellationToken)
    {
        Validate(session);
        var plain = Serialize(session);
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var directory = Path.GetDirectoryName(Location);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temporary = Path.Combine(directory ?? ".", $".session-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await stream.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, Location, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BodianApiException("storage", null, null, "无法保存会话（本机数据目录不可写）", retryable: false);
        }
    }

    public async Task<bool> TrySaveAsync(Session session, CancellationToken cancellationToken)
    {
        try
        {
            await SaveAsync(session, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (BodianApiException)
        {
            return false;
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        TryDelete(Location);
        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static byte[] Serialize(Session session) => JsonSerializer.SerializeToUtf8Bytes(new SessionFile
    {
        Version = session.Version,
        Uid = session.Uid,
        Token = session.Token,
        DevId = session.DevId
    });

    private static Session? Deserialize(byte[] plain)
    {
        var file = JsonSerializer.Deserialize<SessionFile>(plain);
        if (file is null || file.Version != Session.CurrentVersion) return null;
        var session = new Session { Version = file.Version, Uid = file.Uid, Token = file.Token ?? string.Empty, DevId = file.DevId ?? string.Empty };
        Validate(session);
        return session;
    }

    public static void Validate(Session session)
    {
        if (session.Version != Session.CurrentVersion) throw new BodianApiException("storage", null, null, "会话版本不受支持", retryable: false);
        if (session.Uid <= 0) throw new BodianApiException("storage", null, null, "会话缺少有效账号", retryable: false);
        if (string.IsNullOrEmpty(session.Token) || session.Token.Length > 8192) throw new BodianApiException("storage", null, null, "会话票据无效", retryable: false);
        if (!DeviceIdRegex().IsMatch(session.DevId)) throw new BodianApiException("storage", null, null, "设备标识无效", retryable: false);
    }

    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex DeviceIdRegex();

    private sealed class SessionFile
    {
        public int Version { get; set; }
        public long Uid { get; set; }
        public string? Token { get; set; }
        public string? DevId { get; set; }
    }
}
