using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling;

namespace ShijiaSignIn;

/// <summary>
/// 石之家(ff14risingstones) 签到核心逻辑。
/// 接口信息参考社区逆向: https://github.com/SakagamiJun/ff14checkin
/// </summary>
public sealed class SignInService : IDisposable
{
    private const string PreflightUrl = "https://ff14risingstones.web.sdo.com/";
    private const string SignInUrl = "https://apiff14risingstones.web.sdo.com/api/home/sign/signIn";
    private const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private const string MsgSuccess = "帮你签到好啦小仓鼠~";
    private const string MsgFail = "哎呀，好像哪里出了问题，快带着截图找我吧？";
    private const string MsgAlready = "签过到啦笨蛋仓鼠！";
    private const string MsgNoCharacter = "还没有绑定角色，先去石之家网页绑定一下游戏角色吧~";

    // UIColor row id：healer green / tank blue（来自 Dalamud.Colors 职业色）
    private const ushort ColorGreen = 504;
    private const ushort ColorBlue = 37;

    private enum NotifyKind
    {
        Info,    // 白色默认
        Success, // 绿色
        Error,   // 红色
        Already, // 蓝色
    }

    private readonly Configuration configuration;
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim signInLock = new(1, 1);

    private Timer? dailyTimer;
    private string? lastSignDate;   // yyyy-MM-dd，仅成功/已签时写入
    private DateTime? lastAttempt;  // 防止失败后频繁重试
    private DateTime? lastBrowserLoginTrigger; // 自动打开浏览器登录的防抖冷却
    private volatile bool disposed;

    /// <summary>Cookie 缺失或失效时触发；由插件接线到"打开浏览器登录"流程。</summary>
    public Func<Task>? OnLoginRequired { get; set; }

    /// <summary>签到请求使用的 User-Agent。优先用登录浏览器读到的真实 UA（石之家会话可能与 UA 绑定），否则回退 Chrome124。</summary>
    private string EffectiveUserAgent =>
        string.IsNullOrWhiteSpace(this.configuration.UserAgent) ? ChromeUserAgent : this.configuration.UserAgent;

    public SignInService(Configuration configuration)
    {
        this.configuration = configuration;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false, // Cookie 由我们手动放入 Header
        };
        this.httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public void Start()
    {
        // 每分钟检查一次：到点且今天未签时触发
        this.dailyTimer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
    }

    private void Tick()
    {
        if (this.disposed || !this.configuration.AutoSignEnabled)
            return;

        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd");

        if (today == this.lastSignDate)
            return; // 今天已成功/已签

        if (this.lastAttempt.HasValue && (now - this.lastAttempt.Value) < TimeSpan.FromMinutes(10))
            return; // 冷却，避免失败时刷屏

        var target = ParseTime(this.configuration.AutoSignTime) ?? new TimeSpan(0, 5, 0);
        if (now.TimeOfDay < target)
            return; // 还没到设定时间

        this.lastAttempt = now;
        _ = SignInAsync("每日自动");
    }

    public async Task<SignInResult> SignInAsync(string reason)
    {
        if (this.disposed)
            return SignInResult.Blocked;

        if (string.IsNullOrWhiteSpace(this.configuration.CookieString))
        {
            if (this.configuration.AutoOpenBrowser && this.OnLoginRequired != null)
                _ = this.OnLoginRequired();
            else
                Notify("尚未配置 Cookie。用 /shijia 打开设置填入盛大通行证 Cookie。", NotifyKind.Error);
            return SignInResult.NeedsLogin;
        }

        if (!await this.signInLock.WaitAsync(0).ConfigureAwait(false))
        {
            Notify("已有一个签到请求正在进行中，请稍候。");
            return SignInResult.Blocked;
        }

        try
        {
            Service.Log.Information("[ShijiaSignIn] 开始签到, reason={Reason}", reason);

            // 1) 前置请求，让服务端刷新/维持会话 Cookie
            await PreflightAsync().ConfigureAwait(false);

            // 2) 发起签到请求
            var tempsuid = Guid.NewGuid().ToString("N");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{SignInUrl}?tempsuid={tempsuid}")
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("tempsuid", tempsuid),
                }),
            };
            request.Headers.TryAddWithoutValidation("Referer", "https://ff14risingstones.web.sdo.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://ff14risingstones.web.sdo.com");
            request.Headers.TryAddWithoutValidation("User-Agent", this.EffectiveUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.TryAddWithoutValidation("Cookie", this.configuration.CookieString);

            using var response = await this.httpClient.SendAsync(request).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            Service.Log.Information($"[ShijiaSignIn] signIn HTTP {(int)response.StatusCode} body={Sanitize(body)}");

            if (response.StatusCode != HttpStatusCode.OK)
            {
                Service.Log.Warning($"[ShijiaSignIn] signIn HTTP {(int)response.StatusCode}");
                Notify(MsgFail, NotifyKind.Error);
                return SignInResult.Blocked;
            }

            var result = InterpretResponse(body);
            if (result == SignInResult.NeedsLogin)
                this.MaybeTriggerBrowserLogin();
            return result;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[ShijiaSignIn] 签到异常: {Sanitize(ex.Message)}");
            Notify(MsgFail, NotifyKind.Error);
            return SignInResult.Blocked;
        }
        finally
        {
            this.signInLock.Release();
        }
    }

    private async Task PreflightAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, PreflightUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", this.EffectiveUserAgent);
            request.Headers.TryAddWithoutValidation("Cookie", this.configuration.CookieString);

            using var response = await this.httpClient.SendAsync(request).ConfigureAwait(false);
            Service.Log.Information($"[ShijiaSignIn] preflight HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            // 前置请求失败不致命，记录即可
            Service.Log.Warning($"[ShijiaSignIn] preflight 失败: {Sanitize(ex.Message)}");
        }
    }

    private SignInResult InterpretResponse(string body)
    {
        long? code = null;
        string msg = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            code = ReadLong(root, "code") ?? ReadLong(root, "resultCode");
            msg = ReadString(root, "msg")
                ?? ReadString(root, "message")
                ?? ReadString(root, "desc")
                ?? string.Empty;
        }
        catch
        {
            Service.Log.Warning($"[ShijiaSignIn] 无法解析响应: {Sanitize(body)}");
            Notify(MsgFail, NotifyKind.Error);
            return SignInResult.Unknown;
        }

        // 登录失效/需要验证的已知错误码 (来自 ff14checkin 逆向)
        if (code is 10105 or 10403 or -10350174)
        {
            Service.Log.Warning($"[ShijiaSignIn] 登录失效 code={code} msg={msg}");
            Notify(MsgFail, NotifyKind.Error);
            return SignInResult.NeedsLogin;
        }

        // 未绑定游戏角色
        if (code == 10103)
        {
            Service.Log.Warning($"[ShijiaSignIn] 未绑定角色 code={code} msg={msg}");
            Notify(MsgNoCharacter, NotifyKind.Error);
            return SignInResult.Blocked;
        }

        // 已签到：10001=今天已签过；10301=重复请求被限流（说明已有签到请求在处理/已成功）
        if (code == 10001 || code == 10301 || msg.Contains("已签") || msg.Contains("已经"))
        {
            this.lastSignDate = DateTime.Now.ToString("yyyy-MM-dd");
            Notify(MsgAlready, NotifyKind.Already);
            return SignInResult.AlreadySigned;
        }

        // 成功：石之家标准成功码 10000，兼容 0/200/1/null
        if (code == 10000 || code == 0 || code == 200 || code == 1 || code is null)
        {
            this.lastSignDate = DateTime.Now.ToString("yyyy-MM-dd");
            Notify(MsgSuccess, NotifyKind.Success);
            return SignInResult.Success;
        }

        Service.Log.Warning($"[ShijiaSignIn] 未知响应 code={code} msg={msg}");
        Notify(MsgFail, NotifyKind.Error);
        return SignInResult.Unknown;
    }

    private void MaybeTriggerBrowserLogin()
    {
        if (this.configuration.AutoOpenBrowser && this.OnLoginRequired != null)
        {
            // 防抖：2 分钟内不重复自动打开浏览器，避免登录后签到失败导致的反复弹窗
            var now = DateTime.UtcNow;
            if (this.lastBrowserLoginTrigger.HasValue &&
                (now - this.lastBrowserLoginTrigger.Value) < TimeSpan.FromMinutes(2))
            {
                Notify("刚刚已尝试自动登录，请稍候再试，或手动输入 /shijia login。", NotifyKind.Error);
                return;
            }
            this.lastBrowserLoginTrigger = now;
            _ = this.OnLoginRequired();
        }
    }

    private static long? ReadLong(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            return n;
        return null;
    }

    private static string? ReadString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private static TimeSpan? ParseTime(string hhmm)
    {
        if (TimeSpan.TryParse(hhmm, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
            return t;
        return null;
    }

    private static string Sanitize(string s) => s.Replace('{', '[').Replace('}', ']');

    private void Notify(string message, NotifyKind kind = NotifyKind.Info)
    {
        if (this.disposed)
            return;

        Service.Log.Information($"[ShijiaSignIn] {Sanitize(message)}");
        if (!this.configuration.ChatNotification)
            return;

        Service.Framework.RunOnTick(() =>
        {
            if (this.disposed)
                return;
            var text = $"[石之家签到] {message}";
            switch (kind)
            {
                case NotifyKind.Success:
                    Service.ChatGui.Print(Colored(text, ColorGreen));
                    break;
                case NotifyKind.Already:
                    Service.ChatGui.Print(Colored(text, ColorBlue));
                    break;
                case NotifyKind.Error:
                    Service.ChatGui.PrintError(text);
                    break;
                default:
                    Service.ChatGui.Print(text);
                    break;
            }
        });
    }

    private static SeString Colored(string text, ushort colorKey)
    {
        var sb = new SeStringBuilder();
        sb.AddUiForeground(colorKey);
        sb.AddText(text);
        sb.AddUiForegroundOff();
        return sb.BuiltString;
    }

    public void Dispose()
    {
        this.disposed = true;
        this.dailyTimer?.Dispose();
        this.signInLock.Dispose();
        this.httpClient.Dispose();
    }
}

public enum SignInResult
{
    Success,
    AlreadySigned,
    NeedsLogin,
    Blocked,
    Unknown,
}
