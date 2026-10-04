using System.Net.Http.Headers;
using Dianbo.Infrastructure.Auth;

namespace Dianbo.Infrastructure.Api;

public sealed class PublicHeaderHandler : DelegatingHandler
{
    public const string Plat = "win";
    public const string Ver = "1.1.7";
    public const string Channel = "W1";

    private readonly IDeviceIdentity _identity;

    public PublicHeaderHandler(IDeviceIdentity identity) => _identity = identity;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers;
        headers.TryAddWithoutValidation("plat", Plat);
        headers.TryAddWithoutValidation("ver", Ver);
        headers.TryAddWithoutValidation("channel", Channel);
        headers.TryAddWithoutValidation("devid", _identity.DevId);
        headers.TryAddWithoutValidation("api-ver", "application/json");
        headers.TryAddWithoutValidation("svrver", "13");
        headers.TryAddWithoutValidation("net", "wifi");
        if (headers.UserAgent.Count == 0 && headers.Accept.Count == 0)
        {
            headers.UserAgent.Add(new ProductInfoHeaderValue("DianboClient", "0.1"));
        }
        return base.SendAsync(request, cancellationToken);
    }
}

public interface IDeviceIdentity
{
    string DevId { get; }
    void EnsurePersisted();
}

public sealed class DeviceIdentity : IDeviceIdentity
{
    private string _path;
    private readonly object _sync = new();
    private string? _devId;

    public DeviceIdentity(string path) => _path = path;

    public string Path => _path;

    public void SetPath(string path)
    {
        lock (_sync)
        {
            _path = path;
            EnsurePersisted();
        }
    }

    public string DevId
    {
        get
        {
            lock (_sync)
            {
                if (_devId is not null) return _devId;
                _devId = Load() ?? Create();
                return _devId;
            }
        }
    }

    public void EnsurePersisted()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(_path)) return;
                Persist(DevId);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private string? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var value = File.ReadAllText(_path).Trim();
            return IsValid(value) ? value : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string Create()
    {
        var value = Guid.NewGuid().ToString("N");
        try
        {
            Persist(value);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return value;
    }

    private void Persist(string value)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(_path, value);
    }

    public static bool IsValid(string? value)
        => value is { Length: 32 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
