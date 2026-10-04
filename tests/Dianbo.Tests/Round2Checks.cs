using System.Reflection;
using Dianbo.Infrastructure.Api;
using Dianbo.Core.Models;
using Dianbo.Core.Services;
using Dianbo.Infrastructure.Storage;
using Dianbo.App.ViewModels;

// SearchViewModel 的真实源码参与测试；只替换与 WinUI 画刷有关的行展示对象。
namespace Dianbo.App.ViewModels
{
    public sealed class SongViewModel(Song song, bool isFavorite)
    {
        public long Id => song.Id;
        public bool IsFavorite { get; set; } = isFavorite;
    }
}

namespace Dianbo.Tests
{
    public static class Round2Checks
    {
        public class ApiProxy : DispatchProxy
        {
            public Func<string, object?[], object> Call = (_, _) => throw new NotSupportedException();
            protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args!);
        }
        private sealed class Credentials : IAuthCredentialSource
        {
            public Session? Session { get; } = new() { Uid = 9, Token = "test", DevId = "test" };
            public string DevId => "test";
        }
        private static Song Song(long id) => new() { Id = id, Name = "test", Artist = "test" };
        private static void Check(bool ok) { if (!ok) throw new Exception("回归断言失败"); }
        public static async Task<List<(string Name, string? Error)>> RunAsync()
        {
            var results = new List<(string, string?)>();
            async Task Test(string name, Func<Task> test)
            {
                try { await test().WaitAsync(TimeSpan.FromSeconds(10)); results.Add((name, null)); }
                catch (Exception e) { results.Add((name, e.ToString())); }
            }
            await Test("清空关键词后忽略不响应取消的旧分页", async () =>
            {
                var api = DispatchProxy.Create<IBodianApiClient, ApiProxy>();
                var late = new TaskCompletionSource<IReadOnlyList<Song>>();
                CancellationToken pageToken = default;
                ((ApiProxy)api).Call = (name, args) =>
                {
                    if (name != "SearchAsync") throw new NotSupportedException(name);
                    if ((int)args[1]! == 0) return Task.FromResult<IReadOnlyList<Song>>([Song(1)]);
                    pageToken = (CancellationToken)args[^1]!;
                    return late.Task;
                };
                var vm = new SearchViewModel(api);
                await vm.SearchAsync("old", true, default);
                var more = vm.LoadMoreAsync(default);
                await vm.SearchAsync("", true, default);
                Check(pageToken.IsCancellationRequested);
                late.SetResult([Song(2)]);
                await more;
                Check(vm.Results.Count == 0 && vm.Keyword == "" && !vm.IsBusy && !vm.HasMore);
            });
            await Test("新输入立即使旧分页失效而非等待防抖结束", async () =>
            {
                var api = DispatchProxy.Create<IBodianApiClient, ApiProxy>();
                var late = new TaskCompletionSource<IReadOnlyList<Song>>();
                ((ApiProxy)api).Call = (_, args) => (int)args[1]! == 0
                    ? Task.FromResult<IReadOnlyList<Song>>([Song((string)args[0]! == "old" ? 1 : 3)]) : late.Task;
                var vm = new SearchViewModel(api);
                await vm.SearchAsync("old", true, default);
                var more = vm.LoadMoreAsync(default);
                vm.OnQueryChanged("new");
                late.SetResult([Song(2)]);
                await more;
                Check(vm.Results.Count == 0 && vm.Keyword == "new");
                await Task.Delay(400);
                Check(vm.Results.Count == 1 && vm.Results[0].Id == 3);
            });
            await Test("删除拒绝或断网不隐藏云端歌单且成功才返回成功", async () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                try
                {
                    var api = DispatchProxy.Create<IBodianApiClient, ApiProxy>();
                    var mode = 0;
                    ((ApiProxy)api).Call = (name, _) => name switch
                    {
                        "GetUserPlaylistsAsync" => Task.FromResult<IReadOnlyList<Playlist>>(mode == 2 ? [] : [new() { Id = 5, Name = "cloud" }]),
                        "DeletePlaylistAsync" => mode == 1 ? Task.FromException<bool>(new IOException("offline")) : Task.FromResult(mode == 2),
                        _ => throw new NotSupportedException(name)
                    };
                    var path = Path.Combine(dir, "playlists.json");
                    var service = new UserPlaylistService(path, api, new Credentials());
                    Check(!await service.DeletePlaylistAsync(5));
                    Check((await service.GetPlaylistsAsync()).Count == 1);
                    mode = 1;
                    try { await service.DeletePlaylistAsync(5); throw new Exception("应抛出网络错误"); }
                    catch (IOException) { }
                    Check((await new UserPlaylistService(path, api, new Credentials()).GetPlaylistsAsync()).Count == 1);
                    mode = 2;
                    Check(await service.DeletePlaylistAsync(5));
                    Check((await service.GetPlaylistsAsync()).Count == 0);
                }
                finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            });
            await Test("完整收藏快照移除手机取消项而离线仍保留缓存", async () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                try
                {
                    var api = DispatchProxy.Create<IBodianApiClient, ApiProxy>();
                    IReadOnlyList<Song> songs = [Song(1), Song(2)];
                    bool offline = false;
                    ((ApiProxy)api).Call = (name, _) => name switch
                    {
                        "GetFondPlaylistAsync" => Task.FromResult<Playlist?>(new() { Id = 7, Name = "fond" }),
                        "AddPlaylistMusicAsync" or "RecordBehaviorAsync" => Task.FromResult(true),
                        "GetAllPlaylistSongsAsync" => offline ? Task.FromException<IReadOnlyList<Song>>(new IOException()) : Task.FromResult(songs),
                        _ => throw new NotSupportedException(name)
                    };
                    var path = Path.Combine(dir, "favorites.json");
                    var service = new FavoriteService(path, api, new Credentials());
                    await service.SyncAsync();
                    Check(service.IsFavorite(1) && service.IsFavorite(2));
                    // 本机刚成功添加，手机随即取消；成功的添加不能永久留在待同步集合。
                    await service.AddFavoriteAsync(Song(3));
                    songs = [Song(2)];
                    await service.SyncAsync();
                    Check(!service.IsFavorite(3));
                    Check(!service.IsFavorite(1) && service.IsFavorite(2));
                    offline = true;
                    await service.SyncAsync();
                    Check(service.IsFavorite(2));
                    var restored = new FavoriteService(path, api, new Credentials());
                    Check(!restored.IsFavorite(1) && restored.IsFavorite(2));
                }
                finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            });
            return results;
        }
    }
}
