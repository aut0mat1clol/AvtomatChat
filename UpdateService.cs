using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AvtomatChat;

/// <summary>
/// Автообновление через GitHub Releases:
/// 1) сравнивает версию сборки с тегом последнего релиза;
/// 2) скачивает zip-ассет и распаковывает новый exe во временную папку;
/// 3) запускает скрытый PowerShell, который после выхода приложения
///    заменяет exe и запускает новую версию.
/// </summary>
public class UpdateService
{
    private const string Owner = "aut0mat1clol";
    private const string Repo = "AvtomatChat";
    private const string AssetName = "AvtomatChat-win-x64.zip";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub API требует User-Agent
        c.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("AvtomatChat", CurrentVersion.ToString()));
        return c;
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Версия для показа пользователю («1.3»).</summary>
    public static string CurrentVersionText
    {
        get
        {
            var v = CurrentVersion;
            return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
        }
    }

    public record UpdateInfo(Version Version, string TagName, string ZipUrl);

    /// <summary>Возвращает информацию о новой версии или null, если обновлений нет.</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        // Основной способ: страница /releases/latest отвечает редиректом на /releases/tag/X.Y.
        // Это НЕ API — не попадает под лимит 60 запросов/час на IP.
        var info = await CheckViaRedirectAsync();
        // Запасной способ — API (вдруг редирект-поведение изменится)
        info ??= await CheckViaApiAsync();

        if (info == null) return null;
        return Normalize(info.Version) > Normalize(CurrentVersion) ? info : null;
    }

    private static async Task<UpdateInfo?> CheckViaRedirectAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("AvtomatChat", CurrentVersion.ToString()));

            using var resp = await client.GetAsync(
                $"https://github.com/{Owner}/{Repo}/releases/latest",
                HttpCompletionOption.ResponseHeadersRead);

            // Ожидаем 302 -> https://github.com/{owner}/{repo}/releases/tag/{tag}
            var location = resp.Headers.Location?.ToString();
            if (location == null) return null;

            var marker = "/releases/tag/";
            var idx = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var tag = Uri.UnescapeDataString(location[(idx + marker.Length)..].Trim('/'));
            if (!TryParseTag(tag, out var ver)) return null;

            // Имя ассета фиксированное — ссылка на скачивание предсказуема
            var zipUrl = $"https://github.com/{Owner}/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{AssetName}";
            return new UpdateInfo(ver, tag, zipUrl);
        }
        catch
        {
            return null; // перейдём к запасному способу
        }
    }

    private static async Task<UpdateInfo?> CheckViaApiAsync()
    {
        var json = await Http.GetStringAsync(
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);

        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseTag(tag, out var ver)) return null;

        // Ищем zip-ассет (предпочтительно с известным именем)
        string? url = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    break;
                }
                if (url == null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    url = a.GetProperty("browser_download_url").GetString();
            }
        }

        return url == null ? null : new UpdateInfo(ver, tag, url);
    }

    private static bool TryParseTag(string tag, out Version version)
    {
        var verText = tag.TrimStart('v', 'V');
        if (!verText.Contains('.')) verText += ".0";
        return Version.TryParse(verText, out version!);
    }

    /// <summary>«1.2» и «1.2.0.0» должны быть равны.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>
    /// Скачивает обновление и запускает установщик-скрипт.
    /// После вызова нужно закрыть приложение — замена произойдёт автоматически.
    /// </summary>
    public async Task DownloadAndPrepareAsync(UpdateInfo info, Action<string>? status = null)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("не удалось определить путь к exe");

        status?.Invoke($"Скачивание обновления {info.TagName}…");
        var tmpDir = Path.Combine(Path.GetTempPath(), "AvtomatChat_update");
        Directory.CreateDirectory(tmpDir);

        var zipPath = Path.Combine(tmpDir, "update.zip");
        var bytes = await Http.GetByteArrayAsync(info.ZipUrl);
        await File.WriteAllBytesAsync(zipPath, bytes);

        status?.Invoke("Распаковка…");
        var extractDir = Path.Combine(tmpDir, "extracted");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var newExe = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("в архиве обновления нет exe");

        // PowerShell через -EncodedCommand: никаких проблем с кириллицей в путях
        // (cmd-скрипты ломаются на не-ASCII путях из-за OEM-кодировки).
        var ps = $$"""
            while ($true) {
              Start-Sleep -Milliseconds 500
              try {
                Copy-Item -LiteralPath '{{Escape(newExe)}}' -Destination '{{Escape(exePath)}}' -Force -ErrorAction Stop
                break
              } catch { }
            }
            Start-Process -FilePath '{{Escape(exePath)}}'
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}",
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        status?.Invoke("Перезапуск…");
    }

    private static string Escape(string path) => path.Replace("'", "''");
}
