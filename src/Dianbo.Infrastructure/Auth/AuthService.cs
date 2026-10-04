using System.Text;
using System.Text.Json;
using Dianbo.Core.Models;
using Dianbo.Core.Services;
using Dianbo.Infrastructure.Api;

namespace Dianbo.Infrastructure.Auth;

public sealed class AuthServiceOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    public string ApiBase { get; init; } = "https://bd-api.kuwo.cn/api/";
}

public sealed class AuthService : IAuthService, IAuthCredentialSource
{
    private readonly HttpClient _http;
    private readonly ISessionStore _store;
    private readonly IDeviceIdentity _identity;
    private readonly AuthServiceOptions _options;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private volatile Session? _session;
    private volatile UserProfile? _profile;

    public event EventHandler<UserProfile?>? ProfileChanged;

    public AuthService(HttpClient http, ISessionStore store, IDeviceIdentity identity, AuthServiceOptions? options = null, Action<string>? log = null)
    {
        _http = http;
        _store = store;
        _identity = identity;
        _options = options ?? new AuthServiceOptions();
        _log = log;
    }

    public Session? Current => _session;
    public Session? Session => _session;
    public UserProfile? Profile => _profile;
    public bool IsSignedIn => _session is not null;
    public string DevId => _session?.DevId ?? _identity.DevId;

    public bool SessionPersistenceFailed { get; private set; }

    private async Task<bool> SaveGenericAsync(Session session, CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (BodianApiException)
        {
            return false;
        }
    }

    public async Task<Session?> RestoreAsync(CancellationToken cancellationToken)
    {
        var saved = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            _log?.Invoke("auth restore skipped: no saved session");
            return null;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));
            var body = new { authType = 5, token = saved.Token, uid = saved.Uid };
            using var response = await PostAsync("ucenter/users/login", body, timeoutCts.Token).ConfigureAwait(false);
            if (!TryReadSession(response.RootElement, out var uid, out var token))
            {
                _session = saved;
                _profile = new UserProfile { Uid = saved.Uid, Nickname = $"UID {saved.Uid} (离线)" };
                ProfileChanged?.Invoke(this, _profile);
                _log?.Invoke($"auth restore offline fallback (unrecognized response): uid={saved.Uid}");
                return saved;
            }

            if (uid != saved.Uid)
            {
                _log?.Invoke("auth restore rejected: account mismatch");
                await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var restored = saved with { Token = token };
            _session = restored;
            _profile = ParseUserProfile(response.RootElement, uid);
            ProfileChanged?.Invoke(this, _profile);
            if (!string.Equals(token, saved.Token, StringComparison.Ordinal))
            {
                await _store.SaveAsync(restored, cancellationToken).ConfigureAwait(false);
                _log?.Invoke("auth restore refreshed token");
            }
            else
            {
                _log?.Invoke("auth restore succeeded");
            }
            return restored;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BodianApiException apiEx) when (apiEx.HttpStatus is 401 or 403 || apiEx.BusinessCode is 401 or 403 or 11052 or 20018)
        {
            _log?.Invoke($"auth restore rejected by server: status={apiEx.HttpStatus} code={apiEx.BusinessCode} msg={apiEx.Message}");
            await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
            _session = null;
            _profile = null;
            ProfileChanged?.Invoke(this, null);
            return null;
        }
        catch (Exception exception)
        {
            _session = saved;
            _profile = new UserProfile { Uid = saved.Uid, Nickname = $"UID {saved.Uid} (离线)" };
            ProfileChanged?.Invoke(this, _profile);
            _log?.Invoke($"auth restore offline fallback: {exception.GetType().Name}: {exception.Message}");
            return saved;
        }
    }

    public async Task<QrCodeTicket> CreateQrCodeAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("ucenter/login/qrCode", cancellationToken).ConfigureAwait(false);
        var qrCode = ReadDataString(document.RootElement, "qrCode");
        if (string.IsNullOrWhiteSpace(qrCode))
            throw new BodianApiException("ucenter/login/qrCode", 200, null, "未能取得二维码");

        var scanUrl = $"https://bodian-oia.kuwo.cn/bodian/download.html?pageName=login_pc&pt=3&id={Uri.EscapeDataString(qrCode)}";
        return new QrCodeTicket(qrCode, scanUrl);
    }

    public async Task<QrPollState> PollQrCodeAsync(string qrCode, CancellationToken cancellationToken)
    {
        using var document = await GetAsync($"ucenter/login/qrCodeStatus?qrCode={Uri.EscapeDataString(qrCode)}", cancellationToken).ConfigureAwait(false);
        if (!JsonValueDataType(document.RootElement, "data", out var data))
        {
            _log?.Invoke("auth qr poll: data 不是对象");
            return QrPollState.Unusable;
        }
        var status = ReadInt(data, "status");
        _log?.Invoke($"auth qr poll status={status?.ToString() ?? "缺失"}");
        return status switch
        {
            1 => QrPollState.Waiting,
            3 => QrPollState.Confirmed,
            _ => QrPollState.Unusable
        };
    }

    public async Task<Session?> ExchangeQrCodeAsync(string qrCode, CancellationToken cancellationToken)
    {
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var body = new { authType = 10, qrCode };
            using var response = await PostAsync("ucenter/users/login", body, cancellationToken).ConfigureAwait(false);
            if (!TryReadSession(response.RootElement, out var uid, out var token)) return null;

            var session = new Session { Uid = uid, Token = token, DevId = _identity.DevId };
            _session = session;
            _profile = ParseUserProfile(response.RootElement, uid);
            ProfileChanged?.Invoke(this, _profile);
            var persisted = _store is DpapiSessionStore dpapi
                ? await dpapi.TrySaveAsync(session, cancellationToken).ConfigureAwait(false)
                : await SaveGenericAsync(session, cancellationToken).ConfigureAwait(false);
            _identity.EnsurePersisted();
            _log?.Invoke($"auth qr exchange succeeded persisted={persisted}");
            SessionPersistenceFailed = !persisted;
            return session;
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    public async Task<UserProfile?> GetProfileAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null) return null;

        try
        {
            var path = $"ucenter/users/pub/{session.Uid}?fromUid={session.Uid}&token={Uri.EscapeDataString(session.Token)}&uid={session.Uid}";
            using var document = await GetAsync(path, cancellationToken).ConfigureAwait(false);
            var profile = ParseUserProfile(document.RootElement, session.Uid);
            if (profile is not null)
            {
                _profile = profile;
                ProfileChanged?.Invoke(this, profile);
            }
            return profile;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.Invoke($"auth get profile failed: {exception.GetType().Name}");
            return _profile;
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        _session = null;
        _profile = null;
        ProfileChanged?.Invoke(this, null);
        await _store.ClearAsync(cancellationToken).ConfigureAwait(false);
        _log?.Invoke("auth signed out locally");
    }

    private static bool TryReadSession(JsonElement root, out long uid, out string token)
    {
        uid = 0;
        token = string.Empty;
        if (!JsonValueDataType(root, "data", out var data)) return false;
        var id = ReadLong(data, "id");
        var value = ReadString(data, "token");
        if (id is null or <= 0 || string.IsNullOrEmpty(value)) return false;
        uid = id.Value;
        token = value;
        return true;
    }

    private async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
        => await SendAsync(new HttpRequestMessage(HttpMethod.Get, _options.ApiBase + pathAndQuery), pathAndQuery, cancellationToken).ConfigureAwait(false);

    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, _options.ApiBase + path)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return await SendAsync(request, path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, string path, CancellationToken cancellationToken)
    {
        using (request)
        {
            var started = Environment.TickCount64;
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var elapsed = Environment.TickCount64 - started;
            var code = ReadInt(bytes, "code");
            var logPath = path.Split('?')[0];
            if (logPath.StartsWith("ucenter/users/pub/", StringComparison.Ordinal)) logPath = "ucenter/users/pub/[account]";
            _log?.Invoke($"auth path={logPath} http={(int)response.StatusCode} code={code?.ToString() ?? "-"} ms={elapsed}");

            if (bytes.Length == 0) throw new BodianApiException(path, (int)response.StatusCode, null, "登录接口未返回内容");
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                throw new BodianApiException(path, (int)response.StatusCode, null, "登录接口返回不是合法 JSON");
            }

            if (!response.IsSuccessStatusCode)
            {
                document.Dispose();
                throw new BodianApiException(path, (int)response.StatusCode, code, $"登录接口返回 HTTP {(int)response.StatusCode}");
            }
            if (code is not null and not 200)
            {
                var message = ReadString(document.RootElement, "msg") ?? "登录接口返回失败";
                document.Dispose();
                throw new BodianApiException(path, (int)response.StatusCode, code, message, retryable: code != 11052);
            }
            return document;
        }
    }

    private static int? ReadInt(byte[] bytes, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return ReadInt(document.RootElement, name);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool JsonValueDataType(JsonElement root, string name, out JsonElement data)
    {
        if (root.TryGetProperty(name, out data) && data.ValueKind == JsonValueKind.Object) return true;
        data = default;
        return false;
    }

    private static string? ReadDataString(JsonElement root, string name)
        => JsonValueDataType(root, "data", out var data) ? ReadString(data, name) : null;

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var n) => n != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
            _ => null
        };
    }

    public static UserProfile? ParseUserProfile(JsonElement root, long fallbackUid)
    {
        if (!JsonValueDataType(root, "data", out var data)) return null;

        var uid = ReadLong(data, "id") ?? fallbackUid;
        string? nickname = null;
        string? avatarUrl = null;
        var isVip = false;
        var vipType = 0;
        DateTimeOffset? expireDate = null;

        if (JsonValueDataType(data, "userInfo", out var userInfo))
        {
            nickname = ReadString(userInfo, "nickname") ?? ReadString(userInfo, "name") ?? ReadString(userInfo, "userName");
            avatarUrl = ReadString(userInfo, "headImg") ?? ReadString(userInfo, "pic") ?? ReadString(userInfo, "avatar") ?? ReadString(userInfo, "headPic");
            if (ReadInt(userInfo, "isVip") is 1)
            {
                isVip = true;
            }
            vipType = ReadInt(userInfo, "vipType") ?? ReadInt(userInfo, "payVipType") ?? 0;
        }

        if (JsonValueDataType(data, "payInfo", out var payInfo))
        {
            var payVipBool = ReadBool(payInfo, "isVipBoolean") ?? ReadBool(payInfo, "isPayVipBoolean") ?? ReadBool(payInfo, "isBigVipBoolean");
            var payVipInt = ReadInt(payInfo, "isVip");
            if (payVipBool == true || payVipInt is 1)
            {
                isVip = true;
            }

            var type = ReadInt(payInfo, "vipType") ?? ReadInt(payInfo, "payVipType");
            if (type is not null and > 0)
            {
                vipType = type.Value;
            }

            var rawExpire = ReadLong(payInfo, "expireDate")
                         ?? ReadLong(payInfo, "payExpireDate")
                         ?? ReadLong(payInfo, "bigExpireDate")
                         ?? ReadLong(payInfo, "actExpireDate");

            if (rawExpire is not null and > 0)
            {
                try
                {
                    expireDate = rawExpire.Value > 10_000_000_000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(rawExpire.Value).ToLocalTime()
                        : DateTimeOffset.FromUnixTimeSeconds(rawExpire.Value).ToLocalTime();
                }
                catch
                {
                }
            }
        }

        return new UserProfile
        {
            Uid = uid,
            Nickname = nickname,
            AvatarUrl = avatarUrl,
            IsVip = isVip,
            VipType = vipType,
            VipExpireDate = expireDate
        };
    }
}
