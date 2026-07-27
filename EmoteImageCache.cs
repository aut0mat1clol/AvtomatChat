using System.Collections.Concurrent;
using System.Net.Http;

namespace AvtomatChat;

/// <summary>
/// Кэш картинок эмоутов: скачиваем сами через HttpClient и держим байты в памяти.
/// Это обходит баг WPF (гонка в LateBoundBitmapDecoder при загрузке BitmapImage по URL,
/// падает с ArgumentOutOfRangeException) и экономит трафик — каждый эмоут качается один раз.
/// </summary>
public static class EmoteImageCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // Кэшируем Task, чтобы параллельные запросы одного URL не качали дважды
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> Cache = new();

    public static Task<byte[]?> GetBytesAsync(string url) =>
        Cache.GetOrAdd(url, DownloadAsync);

    private static async Task<byte[]?> DownloadAsync(string url)
    {
        try
        {
            return await Http.GetByteArrayAsync(url);
        }
        catch
        {
            // Не скачалось — уберём из кэша, чтобы можно было попробовать ещё раз позже
            Cache.TryRemove(url, out _);
            return null;
        }
    }
}
