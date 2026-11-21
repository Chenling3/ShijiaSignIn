using System;
using System.Collections.Generic;
using System.Linq;
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
    private const string SignRewardListUrl = "https://apiff14risingstones.web.sdo.com/api/home/sign/signRewardList";
    private const string GetSignRewardUrl = "https://apiff14risingstones.web.sdo.com/api/home/sign/getSignReward";
    private const string GetUserInfoUrl = "https://apiff14risingstones.web.sdo.com/api/home/userInfo/getUserInfo";
    private const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private const string MsgSuccess = "帮你签到好啦小仓鼠~";
    private const string MsgFail = "哎呀，好像哪里出了问题，快带着截图找我吧？";
    private const string MsgAlready = "签过到啦笨蛋仓鼠！";
    private const string MsgNoCharacter = "还没有绑定角色，先去石之家网页绑定一下游戏角色吧~";
    private const string MsgRewardClaimed = "签到奖励也帮你领好啦小仓鼠~";

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
    private readonly SemaphoreSlim rewardLock = new(1, 1);

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
            if (result == SignInResult.Success || result == SignInResult.AlreadySigned)
                _ = ClaimRewardsAsync(); // 签到成功/已签后，自动领取当月可领的签到奖励
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
            var info = ExtractSignInfo(body);
            if (info.Continuous != null)
            {
                this.configuration.LastContinuousDays = int.TryParse(info.Continuous, out var c) ? c : 0;
                this.configuration.LastTotalDays = info.Total ?? string.Empty;
                this.configuration.Save();
            }
            var successMsg = info.Continuous != null ? $"{MsgSuccess} 已连续签到 {info.Continuous} 天~" : MsgSuccess;
            Notify(successMsg, NotifyKind.Success);
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

    private sealed record RewardItem(int Id, int IsGet, string Name, int Num);

    /// <summary>
    /// 签到成功后自动领取当月可领的签到奖励。
    /// 独立于签到锁运行，避免阻塞签到；有独立的奖励锁防止并发重复领取。
    /// </summary>
    private async Task ClaimRewardsAsync()
    {
        if (this.disposed)
            return;

        if (!await this.rewardLock.WaitAsync(0).ConfigureAwait(false))
            return; // 已有领奖在进行

        try
        {
            var month = DateTime.Now.ToString("yyyy-MM");
            var items = await FetchRewardListAsync(month).ConfigureAwait(false);
            if (items == null || items.Count == 0)
                return; // 查询失败或无奖励数据

            var available = items.Where(i => i.IsGet == 0).ToList();
            if (available.Count == 0)
            {
                Service.Log.Information("[ShijiaSignIn] 当月没有可领取的签到奖励");
                return;
            }

            Service.Log.Information($"[ShijiaSignIn] 开始领取 {available.Count} 份签到奖励");
            var claimedNames = new List<string>();
            foreach (var item in available)
            {
                if (await ClaimSingleRewardAsync(item.Id, month).ConfigureAwait(false))
                    claimedNames.Add($"{item.Name} x{item.Num}");
            }

            if (claimedNames.Count > 0)
                Notify($"{MsgRewardClaimed} 领取了：{string.Join("、", claimedNames)}", NotifyKind.Success);
            else
                Service.Log.Warning("[ShijiaSignIn] 签到奖励全部领取失败");
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[ShijiaSignIn] 领奖异常: {Sanitize(ex.Message)}");
        }
        finally
        {
            this.rewardLock.Release();
        }
    }

    private async Task<List<RewardItem>?> FetchRewardListAsync(string month)
    {
        var tempsuid = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{SignRewardListUrl}?month={month}&tempsuid={tempsuid}");
        request.Headers.TryAddWithoutValidation("Referer", "https://ff14risingstones.web.sdo.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", this.EffectiveUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Cookie", this.configuration.CookieString);

        using var response = await this.httpClient.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Service.Log.Information($"[ShijiaSignIn] rewardList HTTP {(int)response.StatusCode} body={Sanitize(body)}");

        if (response.StatusCode != HttpStatusCode.OK)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                Service.Log.Warning($"[ShijiaSignIn] 领奖列表无 data 数组: {Sanitize(body)}");
                return null;
            }

            var list = new List<RewardItem>();
            foreach (var el in data.EnumerateArray())
            {
                var id = ReadInt(el, "id");
                var isGet = ReadInt(el, "is_get") ?? -1;
                if (id.HasValue)
                    list.Add(new RewardItem(id.Value, isGet, ReadString(el, "item_name") ?? "未知奖励", ReadInt(el, "num") ?? 1));
            }
            return list;
        }
        catch (Exception ex)
        {
            Service.Log.Warning($"[ShijiaSignIn] 无法解析领奖列表: {Sanitize(ex.Message)}");
            return null;
        }
    }

    private async Task<bool> ClaimSingleRewardAsync(int id, string month)
    {
        var tempsuid = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GetSignRewardUrl}?tempsuid={tempsuid}")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("id", id.ToString()),
                new KeyValuePair<string, string>("month", month),
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
        Service.Log.Information($"[ShijiaSignIn] getSignReward id={id} HTTP {(int)response.StatusCode} body={Sanitize(body)}");

        if (response.StatusCode != HttpStatusCode.OK)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var code = ReadLong(doc.RootElement, "code");
            // 石之家标准成功码 10000；兼容 0/200/1/null
            return code is 10000 or 0 or 200 or 1 or null;
        }
        catch
        {
            return false;
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

    private static int? ReadInt(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        return null;
    }

    /// <summary>签到天数信息（连续 + 累计）。</summary>
    private sealed record SignDaysInfo(string? Continuous, string? Total);

    /// <summary>
    /// 从签到成功响应里提取「连续签到天数」与「累计天数」。
    /// 真实响应示例：data={sqMsg:"...", continuousDays:2, totalDays:"2", sqExp:5, shopExp:200}
    /// </summary>
    private static SignDaysInfo ExtractSignInfo(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                string? continuous = null, total = null;
                if (data.TryGetProperty("continuousDays", out var cd) && cd.ValueKind == JsonValueKind.Number && cd.TryGetInt32(out var c))
                    continuous = c.ToString();
                if (data.TryGetProperty("totalDays", out var td))
                {
                    var s = td.ValueKind == JsonValueKind.String ? td.GetString() : td.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        total = s;
                }
                return new SignDaysInfo(continuous, total);
            }
        }
        catch
        {
            // 解析失败则不显示天数
        }
        return new SignDaysInfo(null, null);
    }

    /// <summary>
    /// 主动查询签到天数（/shijia status）。优先实时调 getUserInfo 递归找签到天数字段；找不到则回退到上次签到成功时缓存的连续天数。
    /// </summary>
    public async Task QuerySignStatusAsync()
    {
        if (this.disposed)
            return;

        if (string.IsNullOrWhiteSpace(this.configuration.CookieString))
        {
            Notify("还没配置 Cookie 呢，用 /shijia login 登录获取~", NotifyKind.Error);
            return;
        }

        try
        {
            var info = await FetchSignDaysFromUserInfoAsync().ConfigureAwait(false);

            if (info.Continuous != null || info.Total != null)
            {
                var parts = new List<string>();
                if (info.Continuous != null)
                    parts.Add($"连续签到 {info.Continuous} 天");
                if (info.Total != null)
                    parts.Add($"累计签到 {info.Total} 天");
                Notify($"查到啦：{string.Join("，", parts)}~", NotifyKind.Success);
            }
            else if (this.configuration.LastContinuousDays > 0)
            {
                var total = string.IsNullOrWhiteSpace(this.configuration.LastTotalDays)
                    ? string.Empty
                    : $"（累计签到 {this.configuration.LastTotalDays} 天）";
                Notify($"上次签到已连续 {this.configuration.LastContinuousDays} 天{total}~", NotifyKind.Info);
            }
            else
            {
                Notify("暂时查不到签到天数，去石之家网页看一眼吧~", NotifyKind.Info);
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[ShijiaSignIn] 查询签到天数异常: {Sanitize(ex.Message)}");
            Notify(MsgFail, NotifyKind.Error);
        }
    }

    private async Task<SignDaysInfo> FetchSignDaysFromUserInfoAsync()
    {
        var tempsuid = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GetUserInfoUrl}?page=1&tempsuid={tempsuid}");
        request.Headers.TryAddWithoutValidation("Referer", "https://ff14risingstones.web.sdo.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", this.EffectiveUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Cookie", this.configuration.CookieString);

        using var response = await this.httpClient.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Service.Log.Information($"[ShijiaSignIn] getUserInfo HTTP {(int)response.StatusCode} body={Sanitize(body)}");

        if (response.StatusCode != HttpStatusCode.OK)
            return new SignDaysInfo(null, null);

        return FindSignDays(body);
    }

    /// <summary>在响应 JSON 里递归搜索「连续/累计签到天数」字段（字段名不固定时兜底）。</summary>
    private static SignDaysInfo FindSignDays(string body)
    {
        string? continuous = null, total = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            SearchSignDays(doc.RootElement, ref continuous, ref total, 0);
        }
        catch
        {
            // 忽略解析失败
        }
        return new SignDaysInfo(continuous, total);
    }

    private static void SearchSignDays(JsonElement el, ref string? continuous, ref string? total, int depth)
    {
        if (depth > 5 || (continuous != null && total != null))
            return;

        if (el.ValueKind == JsonValueKind.Object)
        {
            continuous ??= ReadAny(el, "continuousDays", "continuous_days", "continuousSignDays", "signDays");
            total ??= ReadAny(el, "totalDays", "total_days", "totalSignDays", "accumulateDays", "totalSignDay");

            if (continuous == null || total == null)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if (continuous != null && total != null)
                        break;
                    SearchSignDays(prop.Value, ref continuous, ref total, depth + 1);
                }
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (continuous != null && total != null)
                    break;
                SearchSignDays(item, ref continuous, ref total, depth + 1);
            }
        }
    }

    private static string? ReadAny(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.Number)
                return v.ToString();
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
        }
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
        this.rewardLock.Dispose();
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
