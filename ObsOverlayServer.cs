using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AvtomatChat;

/// <summary>
/// Мини-HTTP-сервер для OBS: отдаёт страницу-оверлей с прозрачным фоном.
/// В OBS добавляется как источник «Браузер» с адресом http://localhost:8085/
/// Страница раз в секунду забирает последние сообщения с /messages (JSON).
/// </summary>
public class ObsOverlayServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly object _lock = new();
    private readonly List<ChatMessage> _messages = new();
    private CancellationTokenSource? _cts;

    // Очередь озвучки для оверлея: id -> wav
    private readonly object _audioLock = new();
    private readonly List<(long Id, byte[] Wav)> _audioClips = new();
    private long _nextClipId = 1;
    private const int MaxClips = 20;

    public int Port { get; }
    public string Url => $"http://localhost:{Port}/";
    public bool IsRunning { get; private set; }

    private const int MaxMessages = 150; // храним с запасом: оверлей показывает последние 30, окно стримера — все

    /// <summary>Показывать «зашёл/вышел» в окне стримера.</summary>
    public volatile bool ShowJoinsLocal;

    /// <summary>Показывать «зашёл/вышел» в OBS-оверлее.</summary>
    public volatile bool ShowJoinsObs;

    public ObsOverlayServer(int port = 8085)
    {
        Port = port;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _listener.Start();
        IsRunning = true;
        _ = Task.Run(() => ListenLoop(_cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
    }

    public void AddMessage(ChatMessage msg)
    {
        lock (_lock)
        {
            _messages.Add(msg);
            while (_messages.Count > MaxMessages)
                _messages.RemoveAt(0);
        }
    }

    public void Clear()
    {
        lock (_lock) _messages.Clear();
    }

    /// <summary>Удалить одно сообщение по id (CLEARMSG): пометка, контент остаётся для стримера.</summary>
    public void MarkDeleted(string msgId)
    {
        lock (_lock)
            foreach (var m in _messages)
                if (m.MsgId == msgId) m.IsDeleted = true;
    }

    /// <summary>Бан/таймаут: пометить все сообщения пользователя удалёнными.</summary>
    public void MarkUserDeleted(string username)
    {
        lock (_lock)
            foreach (var m in _messages)
                if (!m.IsSystem && !m.IsAlert &&
                    m.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                    m.IsDeleted = true;
    }

    /// <summary>Добавить WAV-клип озвучки для проигрывания в оверлее.</summary>
    public void AddSpeech(byte[] wav)
    {
        lock (_audioLock)
        {
            _audioClips.Add((_nextClipId++, wav));
            while (_audioClips.Count > MaxClips)
                _audioClips.RemoveAt(0);
        }
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; } // listener остановлен

            _ = Task.Run(() => Handle(ctx), token);
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";

            // Файлы шрифтов: из папки приложения и из пользовательских шрифтов Windows
            // (%LOCALAPPDATA%\Microsoft\Windows\Fonts) — работают в OBS без установки «для всех»
            if (path.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase))
            {
                var fileName = Uri.UnescapeDataString(path["/fonts/".Length..]);
                // защита от выхода из папки
                if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    return;
                }

                if (!CollectFontFiles().TryGetValue(fileName, out var filePath) || !File.Exists(filePath))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                var fontBytes = File.ReadAllBytes(filePath);
                ctx.Response.ContentType = Path.GetExtension(fileName).ToLowerInvariant() switch
                {
                    ".woff2" => "font/woff2",
                    ".woff" => "font/woff",
                    ".otf" => "font/otf",
                    _ => "font/ttf",
                };
                ctx.Response.Headers["Cache-Control"] = "max-age=3600";
                ctx.Response.ContentLength64 = fontBytes.Length;
                ctx.Response.OutputStream.Write(fontBytes);
                ctx.Response.OutputStream.Close();
                return;
            }

            // Бинарный ответ: WAV-клип озвучки по id
            if (path == "/speech")
            {
                var idStr = ctx.Request.QueryString["id"];
                byte[]? wav = null;
                if (long.TryParse(idStr, out var id))
                {
                    lock (_audioLock)
                        wav = _audioClips.FirstOrDefault(c => c.Id == id).Wav;
                }

                if (wav == null)
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                ctx.Response.ContentType = "audio/wav";
                ctx.Response.Headers["Cache-Control"] = "no-store";
                ctx.Response.ContentLength64 = wav.Length;
                ctx.Response.OutputStream.Write(wav);
                ctx.Response.OutputStream.Close();
                return;
            }

            string body;
            string contentType;

            if (path == "/messages")
            {
                // Режим стримера (?view=streamer): все сообщения, отметки времени,
                // текст удалённых сохраняется. Для OBS — последние 30, удалённые скрыты.
                var streamer = ctx.Request.QueryString["view"] == "streamer";
                var showJoins = streamer ? ShowJoinsLocal : ShowJoinsObs;

                List<object> snapshot;
                lock (_lock)
                {
                    snapshot = _messages
                        .Where(m => !m.IsSystem || showJoins)
                        .TakeLast(streamer ? MaxMessages : 30)
                        .Select(m => (object)new
                        {
                            u = m.Username,
                            t = m.Text,
                            c = m.ColorHex,
                            ts = streamer ? m.TimeString : null, // время — только стримеру
                            sys = m.IsSystem, // системное событие (зашёл/вышел)
                            al = m.IsAlert,   // алерт (саб/рейд)
                            del = m.IsDeleted, // удалено модератором
                            // части сообщения: текст или эмоут (e = URL картинки)
                            p = m.Parts?.Select(part => new
                            {
                                t = part.Text,
                                e = part.Emote?.WebpUrl,
                            }),
                        })
                        .ToList();
                }
                body = JsonSerializer.Serialize(snapshot);
                contentType = "application/json; charset=utf-8";
            }
            else if (path == "/speech-list")
            {
                // Список id доступных клипов озвучки
                List<long> ids;
                lock (_audioLock)
                    ids = _audioClips.Select(c => c.Id).ToList();
                body = JsonSerializer.Serialize(ids);
                contentType = "application/json; charset=utf-8";
            }
            else
            {
                // Подставляем CSS выбранного пресета + пользовательский CSS
                // Пресет — в общий <style>, пользовательский CSS — в отдельный,
                // чтобы @import (веб-шрифты) не игнорировался браузером
                var html = OverlayHtml
                    .Replace("/*EXTRA_CSS*/", GetPresetCss(LayoutPreset))
                    .Replace("/*USER_CSS*/", BuildFontFaceCss() + (CustomCss ?? ""));

                // /streamer — окно приложения: тот же стиль, но с фичами для стримера
                // (время, текст удалённых сообщений, прокрутка, тёмный фон).
                // /preview — тестовые сообщения и тёмный фон (в OBS фон прозрачный).
                if (path == "/streamer")
                    html = html
                        .Replace("const MODE = 'obs';", "const MODE = 'streamer';")
                        .Replace("background: transparent;", "background: #18181B;")
                        // прокрутка вместо «прибитого» к низу оверлея
                        .Replace("overflow: hidden;", "overflow-y: auto;")
                        .Replace("position: absolute;\n    bottom: 0; left: 0; right: 0;", "min-height: 100vh;");
                else if (path == "/preview")
                    html = html
                        .Replace("const MODE = 'obs';", "const MODE = 'preview';")
                        .Replace("background: transparent;", "background: #18181B;");

                body = html;
                contentType = "text/html; charset=utf-8";
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = contentType;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.OutputStream.Close();
        }
        catch { /* клиент отвалился — не страшно */ }
    }

    // Оверлей: прозрачный фон, сообщения снизу, плавное появление.
    private const string OverlayHtml = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<style>
  html, body {
    margin: 0; padding: 0;
    background: transparent;
    overflow: hidden;
    font-family: 'Segoe UI', Arial, sans-serif;
    font-size: 20px;
  }
  #chat {
    position: absolute;
    bottom: 0; left: 0; right: 0;
    padding: 8px;
    display: flex;
    flex-direction: column;
    justify-content: flex-end;
  }
  .msg {
    color: #fff;
    margin: 3px 0;
    line-height: 1.35;
    word-wrap: break-word;
    text-shadow: 0 1px 3px rgba(0,0,0,.9), 0 0 6px rgba(0,0,0,.7);
    animation: fadein .25s ease-out;
  }
  .msg .nick { font-weight: 700; }
  .msg.sysmsg {
    color: #adadb8;
    font-style: italic;
    font-size: 0.85em;
  }
  .msg.alert {
    color: #00E701;
    font-weight: 700;
    border-left: 3px solid #00E701;
    padding-left: 8px;
  }
  .msg.deleted {
    color: #7a7a85;
    font-style: italic;
    font-size: 0.85em;
  }
  /* Стримерский режим: время и удалённые с содержимым */
  .msg .time { color: #7a7a85; font-size: 0.75em; }
  .msg.deleted-full { color: #7a7a85; }
  .msg.deleted-full s { color: #7a7a85; }
  .msg.deleted-full em { font-size: 0.85em; }
  .msg img.emote {
    height: 26px;
    vertical-align: middle;
    margin: 0 1px;
  }
  @keyframes fadein {
    from { opacity: 0; transform: translateY(8px); }
    to   { opacity: 1; transform: none; }
  }
  #unlock {
    display: none;
    position: fixed;
    top: 10px; left: 10px;
    padding: 10px 16px;
    background: #00E701;
    color: #0e0e10;
    
    border-radius: 6px;
    font-weight: 700;
    cursor: pointer;
    z-index: 10;
  }
/*EXTRA_CSS*/
</style>
<!-- Отдельный блок для CSS пользователя: @import (веб-шрифты и т.п.) работает
     только в начале таблицы стилей, поэтому свой <style>, а не общий -->
<style>/*USER_CSS*/</style>
</head>
<body>
<div id="unlock">🔊 Нажми, чтобы включить звук TTS</div>
<div id="chat"></div>
<script>
  const MODE = 'obs'; // 'obs' | 'streamer' (окно приложения) | 'preview' (тестовые сообщения)
  const chat = document.getElementById('chat');
  let lastJson = '';

  function esc(s) {
    const d = document.createElement('div');
    d.textContent = s;
    return d.innerHTML;
  }

  function renderBody(m) {
    // p — части сообщения (текст/эмоут); если их нет, просто текст
    if (!m.p) return esc(m.t);
    return m.p.map(part =>
      part.e
        ? `<img class="emote" src="${esc(part.e)}" alt="${esc(part.t)}" title="${esc(part.t)}">`
        : esc(part.t)
    ).join('');
  }

  function render(msgs) {
    // Держим прокрутку внизу, только если пользователь и так был внизу
    const nearBottom = window.innerHeight + window.scrollY >= document.body.scrollHeight - 60;

    chat.innerHTML = msgs.map(m => {
      const time = (MODE === 'streamer' && m.ts) ? `<span class="time">${esc(m.ts)}</span> ` : '';
      if (m.del) {
        // Стримеру — контент с зачёркиванием и пометкой, зрителям — заглушка
        return MODE === 'streamer'
          ? `<div class="msg deleted-full">${time}<span class="nick" style="color:${esc(m.c)}">${esc(m.u)}</span>: <s>${renderBody(m)}</s> <em>— Deleted</em></div>`
          : `<div class="msg deleted">Сообщение удалено</div>`;
      }
      if (m.al)  return `<div class="msg alert">${time}${esc(m.u)} ${esc(m.t)}</div>`;
      if (m.sys) return `<div class="msg sysmsg">${time}${esc(m.u)} ${esc(m.t)}</div>`;
      return `<div class="msg">${time}<span class="nick" style="color:${esc(m.c)}">${esc(m.u)}</span>: ${renderBody(m)}</div>`;
    }).join('');

    if (nearBottom) window.scrollTo(0, document.body.scrollHeight);
  }

  // Тестовые сообщения для предпросмотра лайаута
  const SAMPLE = [
    {u:'Streamer', c:'#00E701', t:'Привет, чат! Начинаем стрим'},
    {u:'Viewer42', c:'#FF6BD6', t:'привет PogChamp', p:[{t:'привет '},{t:'PogChamp', e:'https://static-cdn.jtvnw.net/emoticons/v2/305954156/default/dark/2.0'}]},
    {u:'CoolNick', c:'#4BA1FF', t:'ооо, новый оверлей, выглядит топово'},
    {u:'🎉', t:'CoolUser subscribed at Tier 1.', al:true},
    {u:'toxic_user', t:'тут было плохое сообщение', del:true},
    {u:'lurker99', t:'зашёл в чат', sys:true},
    {u:'Модератор', c:'#00E701', t:'длинное сообщение, чтобы проверить перенос строк и то, как лайаут ведёт себя с многострочным текстом'},
  ];

  async function tick() {
    if (MODE === 'preview') { render(SAMPLE); return; }
    try {
      const r = await fetch('/messages' + (MODE === 'streamer' ? '?view=streamer' : ''));
      const txt = await r.text();
      if (txt === lastJson) return; // ничего нового
      lastJson = txt;
      render(JSON.parse(txt));
    } catch (e) { /* приложение закрыто — просто ждём */ }
  }

  setInterval(tick, MODE === 'streamer' ? 500 : 1000); // окну стримера — пошустрее
  tick();

  // ---------- Озвучка TTS через оверлей ----------
  // Опрашиваем /speech-list; новые клипы проигрываем по очереди.
  // При первой загрузке страницы запоминаем существующие id, чтобы не читать старое.
  let knownMax = -1;      // максимальный id, который мы уже видели/проиграли
  let firstPoll = true;
  const playQueue = [];
  let playing = false;

  async function pollSpeech() {
    try {
      const r = await fetch('/speech-list');
      const ids = await r.json();
      if (firstPoll) {
        // пропускаем всё, что было до открытия страницы
        knownMax = ids.length ? Math.max(...ids) : 0;
        firstPoll = false;
        return;
      }
      for (const id of ids) {
        if (id > knownMax) {
          knownMax = id;
          playQueue.push(id);
        }
      }
      playNext();
    } catch (e) { /* приложение закрыто — ждём */ }
  }

  function playNext() {
    if (playing || playQueue.length === 0) return;
    playing = true;
    const id = playQueue.shift();
    const audio = new Audio('/speech?id=' + id);
    audio.onended = audio.onerror = () => { playing = false; playNext(); };
    audio.play().catch(err => {
      playing = false;
      if (err && err.name === 'NotAllowedError') {
        // Браузер блокирует автовоспроизведение до клика по странице
        // (в OBS такого нет). Возвращаем клип в очередь и показываем кнопку.
        playQueue.unshift(id);
        document.getElementById('unlock').style.display = 'block';
      } else {
        playNext(); // битый клип — пропускаем
      }
    });
  }

  // Клик в любом месте страницы разблокирует звук
  document.addEventListener('click', () => {
    document.getElementById('unlock').style.display = 'none';
    playNext();
  });

  setInterval(pollSpeech, 700);
  pollSpeech();
</script>
</body>
</html>
""";

    public void Dispose()
    {
        Stop();
        try { _listener.Close(); } catch { }
    }

    // ---------- Лайауты ----------

    /// <summary>Имя пресета лайаута: classic, compact, bubbles, big.</summary>
    public volatile string LayoutPreset = "classic";

    /// <summary>Дополнительный CSS пользователя (добавляется после пресета).</summary>
    public volatile string? CustomCss;

    /// <summary>Пресет лайаута (record — для биндинга в WPF).</summary>
    public sealed record LayoutPresetInfo(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    public static readonly LayoutPresetInfo[] Presets =
    {
        new("classic", "Классика"),
        new("compact", "Компактный"),
        new("bubbles", "Пузыри"),
        new("big", "Крупный текст"),
    };

    // ---------- Локальные шрифты ----------

    /// <summary>
    /// Шрифты, установленные в Windows «только для меня»:
    /// %LOCALAPPDATA%\Microsoft\Windows\Fonts. Браузер OBS их не видит,
    /// поэтому сервер оверлея раздаёт их сам через @font-face.
    /// </summary>
    public static string UserWindowsFontsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Fonts");

    private static readonly string[] FontExtensions = { ".ttf", ".otf", ".woff", ".woff2", ".ttc" };

    /// <summary>Доступные файлы шрифтов: имя файла -> полный путь.</summary>
    private static Dictionary<string, string> CollectFontFiles()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(UserWindowsFontsDir))
                foreach (var file in Directory.GetFiles(UserWindowsFontsDir))
                    if (FontExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                        result[Path.GetFileName(file)] = file;
        }
        catch { /* нет доступа — работаем без локальных шрифтов */ }
        return result;
    }

    /// <summary>
    /// @font-face для каждого доступного шрифта. Имя семейства = имя файла
    /// без расширения (например MyFont.ttf -> font-family: 'MyFont').
    /// Шрифты раздаются самим сервером, поэтому работают в OBS без установки «для всех».
    /// </summary>
    private static string BuildFontFaceCss()
    {
        try
        {
            var css = new StringBuilder();
            foreach (var (fileName, _) in CollectFontFiles())
            {
                var family = Path.GetFileNameWithoutExtension(fileName).Replace("'", "");
                css.AppendLine(
                    $"@font-face {{ font-family: '{family}'; src: url('/fonts/{Uri.EscapeDataString(fileName)}'); }}");
            }
            return css.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static string GetPresetCss(string preset) => preset switch
    {
        "compact" => """
            body { font-size: 15px; }
            .msg { margin: 1px 0; line-height: 1.2; }
            .msg img.emote { height: 18px; }
            """,
        "bubbles" => """
            .msg {
              background: rgba(20,20,23,.85);
              border-radius: 12px;
              padding: 6px 12px;
              margin: 4px 0;
              width: fit-content;
              max-width: 92%;
              text-shadow: none;
            }
            .msg.alert { border: 1px solid #00E701; }
            """,
        "big" => """
            body { font-size: 28px; }
            .msg { margin: 5px 0; }
            .msg img.emote { height: 34px; }
            """,
        _ => "", // classic — стили по умолчанию
    };
}
