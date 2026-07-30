using System.Net.Http;
using System.Text.Json;

namespace AvtomatChat;

/// <summary>
/// OAuth-авторизация Twitch по Device Code Flow:
/// пользователь один раз вводит Client ID своего Twitch-приложения,
/// жмёт «Войти», подтверждает код в браузере — и приложение получает токен
/// для EventSub (алерты фоловов). Токен обновляется автоматически.
/// </summary>
public class TwitchAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string ClientId { get; set; } = "";
    public string AccessToken { get; private set; } = "";
    public string RefreshToken { get; private set; } = "";
    public string UserId { get; private set; } = "";
    public string UserLogin { get; private set; } = "";

    public bool IsLoggedIn => AccessToken.Length > 0 && UserId.Length > 0;

    public record DeviceCode(string UserCode, string VerificationUri, string DeviceCodeValue, int Interval, int ExpiresIn);

    /// <summary>Шаг 1: получить код для подтверждения в браузере.</summary>
    public async Task<DeviceCode> StartDeviceFlowAsync()
    {
        var resp = await Http.PostAsync("https://id.twitch.tv/oauth2/device",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scopes"] = "moderator:read:followers moderator:read:shoutouts",
            }));
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("Twitch отклонил запрос: " + json);

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new DeviceCode(
            r.GetProperty("user_code").GetString()!,
            r.GetProperty("verification_uri").GetString()!,
            r.GetProperty("device_code").GetString()!,
            r.TryGetProperty("interval", out var i) ? i.GetInt32() : 5,
            r.GetProperty("expires_in").GetInt32());
    }

    /// <summary>Шаг 2: ждать, пока пользователь подтвердит код в браузере.</summary>
    public async Task WaitForTokenAsync(DeviceCode code, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(code.Interval), ct);

            var resp = await Http.PostAsync("https://id.twitch.tv/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = code.DeviceCodeValue,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }));
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (resp.IsSuccessStatusCode)
            {
                AccessToken = doc.RootElement.GetProperty("access_token").GetString()!;
                RefreshToken = doc.RootElement.GetProperty("refresh_token").GetString() ?? "";
                await LoadUserAsync();
                return;
            }

            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "";
            if (msg == "authorization_pending") continue; // ждём подтверждения
            throw new InvalidOperationException("Авторизация не удалась: " + msg);
        }
        throw new TimeoutException("Код истёк — попробуй войти ещё раз.");
    }

    /// <summary>Восстановление сессии из сохранённых токенов.</summary>
    public async Task<bool> TryRestoreAsync(string accessToken, string refreshToken)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        if (AccessToken.Length == 0) return false;

        try
        {
            await LoadUserAsync();
            return true;
        }
        catch
        {
            return await TryRefreshAsync();
        }
    }

    /// <summary>Обновление access-токена по refresh-токену.</summary>
    public async Task<bool> TryRefreshAsync()
    {
        if (RefreshToken.Length == 0) return false;
        try
        {
            var resp = await Http.PostAsync("https://id.twitch.tv/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = RefreshToken,
                }));
            if (!resp.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            AccessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            RefreshToken = doc.RootElement.GetProperty("refresh_token").GetString() ?? RefreshToken;
            await LoadUserAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task LoadUserAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/users");
        req.Headers.Add("Authorization", "Bearer " + AccessToken);
        req.Headers.Add("Client-Id", ClientId);
        var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var user = doc.RootElement.GetProperty("data")[0];
        UserId = user.GetProperty("id").GetString()!;
        UserLogin = user.GetProperty("login").GetString()!;
    }

    public void Logout()
    {
        AccessToken = "";
        RefreshToken = "";
        UserId = "";
        UserLogin = "";
    }
}
