using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dianbo.Core.Models;
using Dianbo.Core.Services;

namespace Dianbo.Infrastructure.Api;

public sealed class BodianApiClient : IBodianApiClient
{
    public const string ApiBase = "https://bd-api.kuwo.cn/api/";
    private const string LyricsBase = "http://mlyric.kuwo.cn/mobi.s";
    private const int MaxResponseBytes = 5_000_000;
    private const int MaxLyricsBytes = 500_000;

    private readonly HttpClient _http;
    private readonly IAuthCredentialSource _credentials;
    private readonly Action<string>? _log;

    public BodianApiClient(HttpClient http, IAuthCredentialSource credentials, Action<string>? log = null)
    {
        _http = http;
        _credentials = credentials;
        _log = log;
    }

    public BodianApiClient WithCredentials(IAuthCredentialSource credentials) => new(_http, credentials, _log);

    public async Task<IReadOnlyList<Song>> SearchAsync(string keyword, int pageIndex, int pageSize, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return Array.Empty<Song>();
        pageSize = Math.Clamp(pageSize, 1, 30);
        var query = new Dictionary<string, string?>
        {
            ["keyword"] = keyword.Trim(),
            ["pn"] = pageIndex.ToString(CultureInfo.InvariantCulture),
            ["rn"] = pageSize.ToString(CultureInfo.InvariantCulture),
            ["correct"] = "1"
        };
        using var payload = await GetAsync("search/music/list", query, attachCredentials: false, cancellationToken).ConfigureAwait(false);
        if (!JsonValue.TryGetProperty(payload.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Object)
            return Array.Empty<Song>();
        if (!JsonValue.TryGetProperty(data, "resultList", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<Song>();

        var songs = new List<Song>(list.GetArrayLength());
        foreach (var item in list.EnumerateArray())
        {
            var song = MapSong(item);
            if (song is not null) songs.Add(song);
        }
        return songs;
    }

    public async Task<bool> CheckPlayRightAsync(long songId, CancellationToken cancellationToken)
    {
        var session = _credentials.Session;
        var query = new Dictionary<string, string?>
        {
            ["musicId"] = songId.ToString(CultureInfo.InvariantCulture),
            ["uid"] = session?.Uid.ToString(CultureInfo.InvariantCulture),
            ["token"] = session?.Token
        };
        using var payload = await GetAsync("play/music/v2/checkRight", query, attachCredentials: false, cancellationToken).ConfigureAwait(false);
        if (!JsonValue.TryGetProperty(payload.RootElement, "data", out var data)) return false;
        var status = JsonValue.AsInt(JsonValue.GetProperty(data, "status"));
        var canPlay = JsonValue.AsBool(JsonValue.GetProperty(data, "canPlay"));
        _log?.Invoke($"api checkRight musicId={songId} status={status?.ToString(CultureInfo.InvariantCulture) ?? "-"} canPlay={canPlay?.ToString() ?? "-"}");
        if (status is 1) return true;
        return canPlay == true;
    }

    public async Task<AudioSource> ResolveAudioSourceAsync(long songId, string bitrate, CancellationToken cancellationToken)
    {
        var session = _credentials.Session;
        var devId = session?.DevId ?? _credentials.DevId;
        var query = new Dictionary<string, string?>
        {
            ["musicId"] = songId.ToString(CultureInfo.InvariantCulture),
            ["br"] = bitrate,
            ["devId"] = devId,
            ["uid"] = session?.Uid.ToString(CultureInfo.InvariantCulture),
            ["token"] = session?.Token
        };
        if (bitrate.Contains("mp3", StringComparison.OrdinalIgnoreCase))
        {
            query["format"] = "mp3";
        }
        else if (bitrate.Contains("aac", StringComparison.OrdinalIgnoreCase))
        {
            query["format"] = "aac";
        }

        using var payload = await GetAsync("play/music/v2/audioUrl", query, attachCredentials: true, cancellationToken).ConfigureAwait(false);
        if (!JsonValue.TryGetProperty(payload.RootElement, "data", out var data))
            throw new BodianApiException("play/music/v2/audioUrl", 200, null, "接口未返回播放地址", retryable: true);

        var https = JsonValue.AsString(JsonValue.GetProperty(data, "audioHttpsUrl"));
        var plain = JsonValue.AsString(JsonValue.GetProperty(data, "audioUrl"));
        var url = !string.IsNullOrWhiteSpace(https) ? https : plain;
        var format = JsonValue.AsString(JsonValue.GetProperty(data, "format"));
        var bitrateValue = JsonValue.AsInt(JsonValue.GetProperty(data, "bitrate"));
        var sizeRaw = JsonValue.AsString(JsonValue.GetProperty(data, "size"));
        var sizeLong = JsonValue.AsLong(JsonValue.GetProperty(data, "size"));
        var durationSeconds = JsonValue.AsInt(JsonValue.GetProperty(data, "duration"));
        var respCode = JsonValue.AsInt(JsonValue.GetProperty(data, "respCode"));
        _log?.Invoke($"api audioUrl musicId={songId} req_br={bitrate} hasUrl={!string.IsNullOrWhiteSpace(url)} format={format ?? "-"} "
                   + $"bitrate={bitrateValue?.ToString(CultureInfo.InvariantCulture) ?? "-"} duration={durationSeconds?.ToString(CultureInfo.InvariantCulture) ?? "-"} "
                   + $"size={sizeRaw ?? "-"} respCode={respCode?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new BodianApiException("play/music/v2/audioUrl", 200, respCode, "该歌曲当前没有可用的播放地址", retryable: respCode is null or >= 500);
        }

        return new AudioSource
        {
            SongId = songId,
            Url = url,
            Format = format,
            Bitrate = bitrateValue,
            Size = sizeLong,
            SizeText = sizeRaw,
            Duration = durationSeconds is > 0 ? TimeSpan.FromSeconds(durationSeconds.Value) : null
        };
    }

    public async Task<string?> GetLyricsAsync(long songId, CancellationToken cancellationToken)
    {
        var inner = $"type=lyric&req=2&lrcx=1&rid={songId.ToString(CultureInfo.InvariantCulture)}&songname=&artist=&corp=kuwo&fromchannel=bodian";
        var encoded = WebUtility.UrlEncode(Convert.ToBase64String(Encoding.UTF8.GetBytes(inner)));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{LyricsBase}?f=bodian&q={encoded}");
        request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/3.10.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        using var body = await ReadBoundedAsync(response, MaxLyricsBytes, cancellationToken).ConfigureAwait(false);
        if (body is null) return null;

        using var document = JsonDocument.Parse(body);
        if (!JsonValue.TryGetProperty(document.RootElement, "data", out var data)) return null;
        var content = JsonValue.AsString(JsonValue.GetProperty(data, "content"));
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(content));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task<Playlist?> GetFondPlaylistAsync(CancellationToken cancellationToken)
    {
        var session = _credentials.Session;
        var uidStr = session?.Uid.ToString(CultureInfo.InvariantCulture);
        var query = new Dictionary<string, string?>
        {
            ["userId"] = uidStr,
            ["uid"] = uidStr,
            ["token"] = session?.Token
        };

        using var payload = await GetAsync("service/playlist/fond", query, attachCredentials: true, cancellationToken).ConfigureAwait(false);
        if (!JsonValue.TryGetProperty(payload.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        var id = JsonValue.AsLong(JsonValue.GetProperty(data, "id"));
        if (id is null or <= 0) return null;

        var name = JsonValue.AsString(JsonValue.GetProperty(data, "name")) ?? "我喜欢的音乐";
        var count = JsonValue.AsInt(JsonValue.GetProperty(data, "musicCount")) ?? 0;
        var pic = JsonValue.AsString(JsonValue.GetProperty(data, "pic"));
        var creatorName = JsonValue.AsString(JsonValue.GetProperty(data, "creatorName"));
        var creatorId = JsonValue.AsLong(JsonValue.GetProperty(data, "creatorId"));
        var desc = JsonValue.AsString(JsonValue.GetProperty(data, "description"));
        var createTime = JsonValue.AsString(JsonValue.GetProperty(data, "createTime"));

        return new Playlist
        {
            Id = id.Value,
            Name = name,
            CoverUrl = pic,
            MusicCount = count,
            IsFond = true,
            CreatorName = creatorName,
            CreatorId = creatorId,
            Description = desc,
            CreateTime = createTime
        };
    }

    public async Task<IReadOnlyList<Playlist>> GetUserPlaylistsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken)
    {
        var session = _credentials.Session;
        var uidStr = session?.Uid.ToString(CultureInfo.InvariantCulture);
        var query = new Dictionary<string, string?>
        {
            ["userId"] = uidStr,
            ["uid"] = uidStr,
            ["token"] = session?.Token,
            ["pn"] = (pageIndex + 1).ToString(CultureInfo.InvariantCulture),
            ["rn"] = Math.Clamp(pageSize, 1, 100).ToString(CultureInfo.InvariantCulture)
        };

        using var payload = await GetAsync("service/playlist/userCreate", query, attachCredentials: true, cancellationToken).ConfigureAwait(false);
        if (!JsonValue.TryGetProperty(payload.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Object)
            return Array.Empty<Playlist>();

        var playlists = new List<Playlist>();
        if (!JsonValue.TryGetProperty(data, "playLists", out var listElement) &&
            !JsonValue.TryGetProperty(data, "playlists", out listElement) &&
            !JsonValue.TryGetProperty(data, "list", out listElement))
        {
            return playlists;
        }

        if (listElement.ValueKind != JsonValueKind.Array) return playlists;

        foreach (var item in listElement.EnumerateArray())
        {
            var id = JsonValue.AsLong(JsonValue.GetProperty(item, "id"));
            if (id is null or <= 0) continue;

            var name = JsonValue.AsString(JsonValue.GetProperty(item, "name")) ?? "未命名歌单";
            var pic = JsonValue.AsString(JsonValue.GetProperty(item, "pic"));
            var musicCount = JsonValue.AsInt(JsonValue.GetProperty(item, "musicCount")) ?? 0;
            var isFond = (JsonValue.AsInt(JsonValue.GetProperty(item, "isFond")) ?? 0) == 1;
            var creatorName = JsonValue.AsString(JsonValue.GetProperty(item, "creatorName"));
            var creatorId = JsonValue.AsLong(JsonValue.GetProperty(item, "creatorId"));
            var desc = JsonValue.AsString(JsonValue.GetProperty(item, "description"));
            var createTime = JsonValue.AsString(JsonValue.GetProperty(item, "createTime"));

            playlists.Add(new Playlist
            {
                Id = id.Value,
                Name = name,
                CoverUrl = pic,
                MusicCount = musicCount,
                IsFond = isFond,
                CreatorName = creatorName,
                CreatorId = creatorId,
                Description = desc,
                CreateTime = createTime
            });
        }

        return playlists;
    }

    private readonly record struct PlaylistPageResult(IReadOnlyList<Song> Songs, int Total);

    public async Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(long playlistId, int pageIndex, int pageSize, CancellationToken cancellationToken, int sourceType = 5)
    {
        var result = await FetchPlaylistSongsInternalWithTotalAsync(playlistId, sourceType, pageIndex, pageSize, cancellationToken).ConfigureAwait(false);
        var songs = result.Songs;
        if (songs.Count == 0 && pageIndex == 0)
        {
            var fallbackSource = sourceType == 5 ? 4 : 5;
            _log?.Invoke($"playlist {playlistId} primary source={sourceType} returned 0 songs, retrying fallback source={fallbackSource}");
            var fallbackResult = await FetchPlaylistSongsInternalWithTotalAsync(playlistId, fallbackSource, pageIndex, pageSize, cancellationToken).ConfigureAwait(false);
            songs = fallbackResult.Songs;
        }
        return songs;
    }

    public async Task<IReadOnlyList<Song>> GetAllPlaylistSongsAsync(long playlistId, CancellationToken cancellationToken, int sourceType = 5)
    {
        var firstPage = await FetchPlaylistSongsInternalWithTotalAsync(playlistId, sourceType, 0, 100, cancellationToken).ConfigureAwait(false);
        var resolvedSource = sourceType;
        if (firstPage.Songs.Count == 0)
        {
            var fallbackSource = sourceType == 5 ? 4 : 5;
            _log?.Invoke($"playlist {playlistId} primary source={sourceType} returned 0 songs, retrying fallback source={fallbackSource}");
            var fallback = await FetchPlaylistSongsInternalWithTotalAsync(playlistId, fallbackSource, 0, 100, cancellationToken).ConfigureAwait(false);
            if (fallback.Songs.Count > 0)
            {
                firstPage = fallback;
                resolvedSource = fallbackSource;
            }
        }

        if (firstPage.Songs.Count == 0) return Array.Empty<Song>();

        var allSongs = new List<Song>(firstPage.Songs);
        var seenIds = new HashSet<long>(firstPage.Songs.Select(s => s.Id));

        if (firstPage.Total <= 0 && firstPage.Songs.Count < 100)
        {
            return allSongs;
        }

        int pageIndex = 1;
        while (pageIndex < 50)
        {
            if (firstPage.Total > 0 && allSongs.Count >= firstPage.Total) break;

            var next = await FetchPlaylistSongsInternalWithTotalAsync(playlistId, resolvedSource, pageIndex, 100, cancellationToken).ConfigureAwait(false);
            if (next.Songs.Count == 0) break;

            int addedCount = 0;
            foreach (var song in next.Songs)
            {
                if (seenIds.Add(song.Id))
                {
                    allSongs.Add(song);
                    addedCount++;
                }
            }

            if (addedCount == 0) break;
            pageIndex++;
        }

        _log?.Invoke($"loaded all playlist songs: playlistId={playlistId}, total={firstPage.Total}, fetched={allSongs.Count}, pages={pageIndex}");
        return allSongs;
    }

    private async Task<PlaylistPageResult> FetchPlaylistSongsInternalWithTotalAsync(long playlistId, int sourceType, int pageIndex, int pageSize, CancellationToken cancellationToken)
    {
        var session = _credentials.Session;
        var uidStr = session?.Uid.ToString(CultureInfo.InvariantCulture);
        var query = new Dictionary<string, string?>
        {
            ["source"] = sourceType.ToString(CultureInfo.InvariantCulture),
            ["pn"] = (pageIndex + 1).ToString(CultureInfo.InvariantCulture),
            ["rn"] = Math.Clamp(pageSize, 1, 100).ToString(CultureInfo.InvariantCulture),
            ["uid"] = uidStr,
            ["token"] = session?.Token
        };

        using var payload = await GetAsync($"service/playlist/{playlistId.ToString(CultureInfo.InvariantCulture)}/musicList", query, attachCredentials: true, cancellationToken).ConfigureAwait(false);
        if (!JsonValue.TryGetProperty(payload.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new PlaylistPageResult(Array.Empty<Song>(), 0);

        var total = JsonValue.AsInt(JsonValue.GetProperty(data, "total")) ?? 0;

        if (!JsonValue.TryGetProperty(data, "list", out var listElement) &&
            !JsonValue.TryGetProperty(data, "musicList", out listElement))
        {
            return new PlaylistPageResult(Array.Empty<Song>(), total);
        }

        if (listElement.ValueKind != JsonValueKind.Array) return new PlaylistPageResult(Array.Empty<Song>(), total);

        var songs = new List<Song>();
        foreach (var item in listElement.EnumerateArray())
        {
            if (MapSong(item) is { } song) songs.Add(song);
        }

        return new PlaylistPageResult(songs, total);
    }

    public async Task<IReadOnlyList<Song>> GetRecommendSongsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken)
    {
        var session = _credentials.Session;
        var uidStr = session?.Uid.ToString(CultureInfo.InvariantCulture);
        var query = new Dictionary<string, string?>
        {
            ["pn"] = Math.Max(1, pageIndex).ToString(CultureInfo.InvariantCulture),
            ["rn"] = Math.Clamp(pageSize, 1, 30).ToString(CultureInfo.InvariantCulture),
            ["uid"] = uidStr,
            ["token"] = session?.Token
        };

        using var payload = await GetAsync("service/music/recommendList", query, attachCredentials: true, cancellationToken).ConfigureAwait(false);
        if (!JsonValue.TryGetProperty(payload.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Object)
            return Array.Empty<Song>();

        if (!JsonValue.TryGetProperty(data, "musicList", out var listElement) &&
            !JsonValue.TryGetProperty(data, "list", out listElement))
        {
            return Array.Empty<Song>();
        }

        if (listElement.ValueKind != JsonValueKind.Array) return Array.Empty<Song>();

        var songs = new List<Song>();
        foreach (var item in listElement.EnumerateArray())
        {
            if (MapSong(item) is { } song) songs.Add(song);
        }

        return songs;
    }

    private static readonly int[] CuratedModuleIds =
    [
        15, 38, 39, 40, 43, 44, 45, 46, 47, 48, 50, 51, 60, 62, 66, 69
    ];

    private static readonly string[] DiscoverPlaylistKeywords =
    [
        "精选", "热门", "华语", "流行", "民谣", "经典", "治愈", "伤感"
    ];

    public async Task<IReadOnlyList<Playlist>> GetRecommendPlaylistsAsync(int count, CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 20);

        bool tryModule = Random.Shared.Next(2) == 0;
        if (tryModule)
        {
            var modulePlaylists = await FetchModulePlaylistsAsync(count, cancellationToken).ConfigureAwait(false);
            if (modulePlaylists.Count >= 3)
            {
                return modulePlaylists;
            }
        }

        var tagPlaylists = await FetchTagPlaylistsAsync(count, cancellationToken).ConfigureAwait(false);
        if (tagPlaylists.Count > 0)
        {
            return tagPlaylists;
        }

        return await FetchModulePlaylistsAsync(count, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Playlist>> FetchModulePlaylistsAsync(int count, CancellationToken cancellationToken)
    {
        var moduleId = CuratedModuleIds[Random.Shared.Next(CuratedModuleIds.Length)];
        var query = new Dictionary<string, string?>
        {
            ["moduleId"] = moduleId.ToString(CultureInfo.InvariantCulture)
        };

        try
        {
            using var payload = await GetAsync("service/finds/module", query, attachCredentials: false, cancellationToken).ConfigureAwait(false);
            if (!JsonValue.TryGetProperty(payload.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Object)
                return Array.Empty<Playlist>();

            if (!JsonValue.TryGetProperty(data, "songList", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<Playlist>();

            var playlists = new List<Playlist>();
            foreach (var item in listElement.EnumerateArray())
            {
                var id = JsonValue.AsLong(JsonValue.GetProperty(item, "id"));
                if (id is null or <= 0) continue;

                var name = JsonValue.AsString(JsonValue.GetProperty(item, "name"));
                if (string.IsNullOrWhiteSpace(name)) continue;

                var pic = JsonValue.AsString(JsonValue.GetProperty(item, "pic"));
                var musicCount = JsonValue.AsInt(JsonValue.GetProperty(item, "musicCount")) ?? 0;
                var playNum = JsonValue.AsLong(JsonValue.GetProperty(item, "playNum"));
                var creatorName = JsonValue.AsString(JsonValue.GetProperty(item, "creatorName"));
                var creatorId = JsonValue.AsLong(JsonValue.GetProperty(item, "creatorId"));
                var desc = JsonValue.AsString(JsonValue.GetProperty(item, "description"));
                var srcType = JsonValue.AsInt(JsonValue.GetProperty(item, "sourceType")) ?? 4;

                playlists.Add(new Playlist
                {
                    Id = id.Value,
                    Name = name.Trim(),
                    CoverUrl = pic,
                    MusicCount = musicCount,
                    PlayCount = playNum,
                    CreatorName = creatorName,
                    CreatorId = creatorId,
                    Description = desc,
                    SourceType = srcType
                });

                if (playlists.Count >= count) break;
            }

            return playlists;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"fetch module playlists error: {ex.Message}");
            return Array.Empty<Playlist>();
        }
    }

    private async Task<IReadOnlyList<Playlist>> FetchTagPlaylistsAsync(int count, CancellationToken cancellationToken)
    {
        var kw = DiscoverPlaylistKeywords[Random.Shared.Next(DiscoverPlaylistKeywords.Length)];
        var pn = Random.Shared.Next(1, 6);
        var query = new Dictionary<string, string?>
        {
            ["keyword"] = kw,
            ["pn"] = pn.ToString(CultureInfo.InvariantCulture),
            ["rn"] = count.ToString(CultureInfo.InvariantCulture)
        };

        try
        {
            using var payload = await GetAsync("search/playlist/list", query, attachCredentials: false, cancellationToken).ConfigureAwait(false);
            if (!JsonValue.TryGetProperty(payload.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Object)
                return Array.Empty<Playlist>();

            if (!JsonValue.TryGetProperty(data, "resultList", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<Playlist>();

            var playlists = new List<Playlist>();
            foreach (var item in listElement.EnumerateArray())
            {
                var id = JsonValue.AsLong(JsonValue.GetProperty(item, "id"));
                if (id is null or <= 0) continue;

                var name = JsonValue.AsString(JsonValue.GetProperty(item, "name"));
                if (string.IsNullOrWhiteSpace(name)) continue;

                var pic = JsonValue.AsString(JsonValue.GetProperty(item, "pic"));
                var musicCount = JsonValue.AsInt(JsonValue.GetProperty(item, "musicnum")) ?? 0;
                var playNum = JsonValue.AsLong(JsonValue.GetProperty(item, "playnum"));
                var creatorName = JsonValue.AsString(JsonValue.GetProperty(item, "creator_name"));
                var creatorId = JsonValue.AsLong(JsonValue.GetProperty(item, "creator_id"));
                var srcType = JsonValue.AsInt(JsonValue.GetProperty(item, "source")) ?? 5;

                playlists.Add(new Playlist
                {
                    Id = id.Value,
                    Name = name.Trim(),
                    CoverUrl = pic,
                    MusicCount = musicCount,
                    PlayCount = playNum,
                    CreatorName = creatorName,
                    CreatorId = creatorId,
                    SourceType = srcType
                });

                if (playlists.Count >= count) break;
            }

            return playlists;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"fetch tag playlists error: {ex.Message}");
            return Array.Empty<Playlist>();
        }
    }

    public async Task<bool> RecordBehaviorAsync(long songId, string operation, CancellationToken cancellationToken = default)
    {
        var session = _credentials.Session;
        if (session is null) return false;

        var uidStr = session.Uid.ToString(CultureInfo.InvariantCulture);
        var query = new Dictionary<string, string?>
        {
            ["uid"] = uidStr,
            ["token"] = session.Token,
            ["devId"] = session.DevId ?? _credentials.DevId
        };

        var behaviorArray = new[]
        {
            new
            {
                id = songId,
                src = new { source = "WINUI" }
            }
        };

        var bodyObj = new
        {
            behavior = JsonSerializer.Serialize(behaviorArray),
            op = operation
        };

        var jsonBody = JsonSerializer.Serialize(bodyObj);

        try
        {
            using var payload = await PostJsonAsync("service/music/record", query, jsonBody, attachCredentials: true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"record behavior failed for songId={songId}, op={operation}: {ex.Message}");
            return false;
        }
    }

    public static string ComputeSign(string path, IEnumerable<KeyValuePair<string, string?>> queryParams, string bodyText = "")
    {
        var normalizedPath = path.StartsWith('/') ? path : "/" + path;
        if (!normalizedPath.StartsWith("/api/"))
        {
            normalizedPath = "/api/" + normalizedPath.TrimStart('/');
        }

        var encodedPairs = new List<string>();
        foreach (var kvp in queryParams)
        {
            if (string.IsNullOrEmpty(kvp.Value) || string.Equals(kvp.Key, "sign", StringComparison.OrdinalIgnoreCase)) continue;
            encodedPairs.Add($"{WebUtility.UrlEncode(kvp.Key)}={WebUtility.UrlEncode(kvp.Value)}");
        }
        var encodedQuery = string.Join("&", encodedPairs);

        var alphanumChars = encodedQuery.Where(char.IsLetterOrDigit).OrderBy(c => c).ToArray();
        var seedBuilder = new StringBuilder("kuwotest").Append(alphanumChars);

        if (!string.IsNullOrEmpty(bodyText))
        {
            using var md5Body = MD5.Create();
            var bodyHash = md5Body.ComputeHash(Encoding.UTF8.GetBytes(bodyText + "kuwotest"));
            seedBuilder.Append(Convert.ToHexString(bodyHash).ToLowerInvariant());
        }

        using var md5Final = MD5.Create();
        var finalHash = md5Final.ComputeHash(Encoding.UTF8.GetBytes(seedBuilder.ToString() + normalizedPath));
        return Convert.ToHexString(finalHash).ToLowerInvariant();
    }

    public async Task<bool> AddPlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default)
    {
        var session = _credentials.Session;
        if (session is null || playlistId <= 0 || songId <= 0) return false;

        var uidStr = session.Uid.ToString(CultureInfo.InvariantCulture);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        var bodyObj = new
        {
            playListId = playlistId,
            musicIdList = new[] { songId }
        };
        var jsonBody = JsonSerializer.Serialize(bodyObj);

        var query = new Dictionary<string, string?>
        {
            ["uid"] = uidStr,
            ["token"] = session.Token,
            ["timestamp"] = timestamp
        };

        var sign = ComputeSign("/api/service/playlist/music", query, jsonBody);
        query["sign"] = sign;

        try
        {
            using var payload = await PostJsonAsync("service/playlist/music", query, jsonBody, attachCredentials: true, cancellationToken).ConfigureAwait(false);
            _log?.Invoke($"add playlist music success: playlistId={playlistId}, songId={songId}");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"add playlist music failed: playlistId={playlistId}, songId={songId}, error={ex.Message}");
            return false;
        }
    }

    public async Task<bool> DeletePlaylistMusicAsync(long playlistId, long songId, CancellationToken cancellationToken = default)
    {
        var session = _credentials.Session;
        if (session is null || playlistId <= 0 || songId <= 0) return false;

        var uidStr = session.Uid.ToString(CultureInfo.InvariantCulture);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        var bodyObj = new
        {
            playListId = playlistId,
            musicIdList = new[] { songId }
        };
        var jsonBody = JsonSerializer.Serialize(bodyObj);

        var query = new Dictionary<string, string?>
        {
            ["uid"] = uidStr,
            ["token"] = session.Token,
            ["timestamp"] = timestamp
        };

        var sign = ComputeSign("/api/service/playlist/music/delete", query, jsonBody);
        query["sign"] = sign;

        try
        {
            using var payload = await PostJsonAsync("service/playlist/music/delete", query, jsonBody, attachCredentials: true, cancellationToken).ConfigureAwait(false);
            _log?.Invoke($"delete playlist music success: playlistId={playlistId}, songId={songId}");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"delete playlist music failed: playlistId={playlistId}, songId={songId}, error={ex.Message}");
            return false;
        }
    }

    public async Task<Playlist?> CreatePlaylistAsync(string name, CancellationToken cancellationToken = default)
    {
        var session = _credentials.Session;
        if (session is null || string.IsNullOrWhiteSpace(name)) return null;

        var uidStr = session.Uid.ToString(CultureInfo.InvariantCulture);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        var bodyObj = new
        {
            name = name.Trim()
        };
        var jsonBody = JsonSerializer.Serialize(bodyObj);

        var query = new Dictionary<string, string?>
        {
            ["uid"] = uidStr,
            ["token"] = session.Token,
            ["timestamp"] = timestamp
        };

        var sign = ComputeSign("/api/service/playlist", query, jsonBody);
        query["sign"] = sign;

        try
        {
            using var payload = await PostJsonAsync("service/playlist", query, jsonBody, attachCredentials: true, cancellationToken).ConfigureAwait(false);
            if (JsonValue.TryGetProperty(payload.RootElement, "data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var id = JsonValue.AsLong(JsonValue.GetProperty(data, "id")) ?? JsonValue.AsLong(JsonValue.GetProperty(data, "playListId"));
                if (id is > 0)
                {
                    _log?.Invoke($"create playlist success: id={id}, name={name}");
                    return new Playlist
                    {
                        Id = id.Value,
                        Name = name.Trim(),
                        MusicCount = 0,
                        CreatorName = session.UserName,
                        CreatorId = session.Uid,
                        CreateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    };
                }
            }
            _log?.Invoke($"create playlist response missing id: {name}");
            return null;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"create playlist failed: name={name}, error={ex.Message}");
            return null;
        }
    }

    public async Task<bool> DeletePlaylistAsync(long playlistId, CancellationToken cancellationToken = default)
    {
        var session = _credentials.Session;
        if (session is null || playlistId <= 0) return false;

        var uidStr = session.Uid.ToString(CultureInfo.InvariantCulture);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        var bodyObj = new
        {
            playListId = playlistId
        };
        var jsonBody = JsonSerializer.Serialize(bodyObj);

        var query = new Dictionary<string, string?>
        {
            ["uid"] = uidStr,
            ["token"] = session.Token,
            ["timestamp"] = timestamp
        };

        var sign = ComputeSign("/api/service/playlist", query, jsonBody);
        query["sign"] = sign;

        try
        {
            var builder = new StringBuilder(ApiBase).Append("service/playlist");
            var first = true;
            foreach (var (key, value) in query)
            {
                if (string.IsNullOrEmpty(value)) continue;
                builder.Append(first ? '?' : '&');
                first = false;
                builder.Append(WebUtility.UrlEncode(key)).Append('=').Append(WebUtility.UrlEncode(value));
            }

            using var request = new HttpRequestMessage(HttpMethod.Delete, builder.ToString())
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("uid", uidStr);
            request.Headers.TryAddWithoutValidation("token", session.Token);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            _log?.Invoke($"delete playlist response: status={response.StatusCode}, id={playlistId}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"delete playlist failed: playlistId={playlistId}, error={ex.Message}");
            return false;
        }
    }

    private Song? MapSong(JsonElement item)
    {
        var id = JsonValue.AsLong(JsonValue.GetProperty(item, "id")) ?? JsonValue.AsLong(JsonValue.GetProperty(item, "musicRid"));
        if (id is null or <= 0) return null;

        var name = JsonValue.AsString(JsonValue.GetProperty(item, "name"));
        if (string.IsNullOrWhiteSpace(name)) name = JsonValue.AsString(JsonValue.GetProperty(item, "FSONGNAME"));
        if (string.IsNullOrWhiteSpace(name)) name = JsonValue.AsString(JsonValue.GetProperty(item, "songName"));
        if (string.IsNullOrWhiteSpace(name)) name = "未知歌曲";

        var artist = JsonValue.AsString(JsonValue.GetProperty(item, "artist"));
        if (string.IsNullOrWhiteSpace(artist) && JsonValue.TryGetProperty(item, "artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var entry in artists.EnumerateArray())
            {
                var artistName = JsonValue.AsString(JsonValue.GetProperty(entry, "name"));
                if (!string.IsNullOrWhiteSpace(artistName)) names.Add(artistName);
            }
            artist = string.Join("、", names);
        }

        var cover = JsonValue.AsString(JsonValue.GetProperty(item, "albumPic"));
        if (string.IsNullOrWhiteSpace(cover)) cover = JsonValue.AsString(JsonValue.GetProperty(item, "albumPic120"));

        TimeSpan? duration = null;
        var rawDuration = JsonValue.AsLong(JsonValue.GetProperty(item, "duration"));
        if (rawDuration is > 0 and < 7_200) duration = TimeSpan.FromSeconds(rawDuration.Value);

        return new Song
        {
            Id = id.Value,
            Name = name,
            Artist = artist ?? string.Empty,
            Album = JsonValue.AsString(JsonValue.GetProperty(item, "album")) ?? string.Empty,
            AlbumId = JsonValue.AsLong(JsonValue.GetProperty(item, "albumId")) ?? 0,
            Duration = duration,
            CoverUrl = string.IsNullOrWhiteSpace(cover) ? null : cover
        };
    }

    private async Task<JsonDocument> GetAsync(string path, IReadOnlyDictionary<string, string?> query, bool attachCredentials, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(ApiBase).Append(path);
        var first = true;
        foreach (var (key, value) in query)
        {
            if (string.IsNullOrEmpty(value)) continue;
            builder.Append(first ? '?' : '&');
            first = false;
            builder.Append(WebUtility.UrlEncode(key)).Append('=').Append(WebUtility.UrlEncode(value));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, builder.ToString());
        if (attachCredentials)
        {
            var session = _credentials.Session;
            if (session is not null)
            {
                request.Headers.TryAddWithoutValidation("uid", session.Uid.ToString(CultureInfo.InvariantCulture));
                request.Headers.TryAddWithoutValidation("token", session.Token);
            }
        }

        var started = Environment.TickCount64;
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        MemoryStream? body;
        try
        {
            body = await ReadBoundedAsync(response, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        var elapsed = Environment.TickCount64 - started;
        if (body is null)
        {
            response.Dispose();
            throw new BodianApiException(path, (int)response.StatusCode, null, "接口返回内容为空或过大");
        }

        var status = (int)response.StatusCode;
        var success = response.IsSuccessStatusCode;
        response.Dispose();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            body.Dispose();
            throw new BodianApiException(path, status, null, "接口返回不是合法 JSON");
        }
        body.Dispose();

        var code = JsonValue.AsInt(JsonValue.GetProperty(document.RootElement, "code"));
        _log?.Invoke($"api path={path} http={status} code={code?.ToString(CultureInfo.InvariantCulture) ?? "-"} ms={elapsed}");

        if (!success)
        {
            document.Dispose();
            throw new BodianApiException(path, status, code, $"接口返回 HTTP {status}");
        }
        if (code is not null and not 200)
        {
            var message = JsonValue.AsString(JsonValue.GetProperty(document.RootElement, "msg")) ?? "接口返回失败";
            document.Dispose();
            throw new BodianApiException(path, status, code, message, retryable: code is not (403 or 11052));
        }
        return document;
    }

    private async Task<JsonDocument> PostJsonAsync(string path, IReadOnlyDictionary<string, string?> query, string jsonBody, bool attachCredentials, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(ApiBase).Append(path);
        var first = true;
        foreach (var (key, value) in query)
        {
            if (string.IsNullOrEmpty(value)) continue;
            builder.Append(first ? '?' : '&');
            first = false;
            builder.Append(WebUtility.UrlEncode(key)).Append('=').Append(WebUtility.UrlEncode(value));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, builder.ToString())
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };

        if (attachCredentials)
        {
            var session = _credentials.Session;
            if (session is not null)
            {
                request.Headers.TryAddWithoutValidation("uid", session.Uid.ToString(CultureInfo.InvariantCulture));
                request.Headers.TryAddWithoutValidation("token", session.Token);
            }
        }

        var started = Environment.TickCount64;
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        MemoryStream? body;
        try
        {
            body = await ReadBoundedAsync(response, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        var elapsed = Environment.TickCount64 - started;
        if (body is null)
        {
            response.Dispose();
            throw new BodianApiException(path, (int)response.StatusCode, null, "接口返回内容为空或过大");
        }

        var status = (int)response.StatusCode;
        var success = response.IsSuccessStatusCode;
        response.Dispose();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            body.Dispose();
            throw new BodianApiException(path, status, null, "接口返回不是合法 JSON");
        }
        body.Dispose();

        var code = JsonValue.AsInt(JsonValue.GetProperty(document.RootElement, "code"));
        _log?.Invoke($"api post path={path} http={status} code={code?.ToString(CultureInfo.InvariantCulture) ?? "-"} ms={elapsed}");

        if (!success)
        {
            document.Dispose();
            throw new BodianApiException(path, status, code, $"接口返回 HTTP {status}");
        }
        if (code is not null and not 200)
        {
            var message = JsonValue.AsString(JsonValue.GetProperty(document.RootElement, "msg")) ?? "接口返回失败";
            document.Dispose();
            throw new BodianApiException(path, status, code, message, retryable: code is not (403 or 11052));
        }
        return document;
    }

    private static async Task<MemoryStream?> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is > 0 && declaredLength > maxBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            if (buffer.Length + read > maxBytes)
            {
                buffer.Dispose();
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        return buffer;
    }
}

public interface IAuthCredentialSource
{
    Session? Session { get; }
    string DevId { get; }
}
