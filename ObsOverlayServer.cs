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

    private const int MaxMessages = 30; // сколько последних сообщений отдаём оверлею

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

    /// <summary>Удалить одно сообщение по id (CLEARMSG).</summary>
    public void RemoveMessage(string msgId)
    {
        lock (_lock) _messages.RemoveAll(m => m.MsgId == msgId);
    }

    /// <summary>Удалить все сообщения пользователя (бан/таймаут).</summary>
    public void RemoveUserMessages(string username)
    {
        lock (_lock) _messages.RemoveAll(m =>
            m.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
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
                List<object> snapshot;
                lock (_lock)
                {
                    snapshot = _messages
                        .Select(m => (object)new
                        {
                            u = m.Username,
                            t = m.Text,
                            c = m.ColorHex,
                            ts = m.TimeString,
                            sys = m.IsSystem, // системное событие (зашёл/вышел)
                            al = m.IsAlert,   // алерт (саб/рейд)
                            del = m.IsDeleted, // удалено модератором — в оверлее заглушка
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
                body = OverlayHtml.Replace("/*EXTRA_CSS*/", GetPresetCss(LayoutPreset) + "\n" + (CustomCss ?? ""));
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
</head>
<body>
<div id="unlock">🔊 Нажми, чтобы включить звук TTS</div>
<div id="chat"></div>
<script>
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

  async function tick() {
    try {
      const r = await fetch('/messages');
      const txt = await r.text();
      if (txt === lastJson) return; // ничего нового
      lastJson = txt;
      const msgs = JSON.parse(txt);
      chat.innerHTML = msgs.map(m =>
        m.del
          ? `<div class="msg deleted">Сообщение удалено</div>`
          : m.al
            ? `<div class="msg alert">${esc(m.u)} ${esc(m.t)}</div>`
            : m.sys
              ? `<div class="msg sysmsg">${esc(m.u)} ${esc(m.t)}</div>`
              : `<div class="msg"><span class="nick" style="color:${esc(m.c)}">${esc(m.u)}</span>: ${renderBody(m)}</div>`
      ).join('');
    } catch (e) { /* приложение закрыто — просто ждём */ }
  }

  setInterval(tick, 1000);
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
