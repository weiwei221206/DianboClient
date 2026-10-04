namespace Dianbo.Core.Models;

public sealed record Session
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public required long Uid { get; init; }
    public required string Token { get; init; }
    public required string DevId { get; init; }
    public string? UserName { get; init; }
}

public enum QrLoginState
{
    Waiting,
    Confirmed,
    Unusable
}

public sealed record QrLoginResult(QrLoginState State, Session? Session)
{
    public static QrLoginResult Waiting { get; } = new(QrLoginState.Waiting, null);
    public static QrLoginResult Unusable { get; } = new(QrLoginState.Unusable, null);
    public static QrLoginResult Confirmed(Session session) => new(QrLoginState.Confirmed, session);
}
