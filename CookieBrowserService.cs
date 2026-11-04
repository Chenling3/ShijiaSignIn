using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShijiaSignIn;

public enum LoginResult
{
    Success,
    Timeout,
    Failed,
    Busy,
}

/// <summary>
/// 通过 Chrome DevTools Protocol(CDP) 打开浏览器让用户登录石之家，
/// 然后自动读取盛大通行证 Cookie 保存到配置。
/// </summary>
public sealed class CookieBrowserService : IDisposable
{
    // 盛大通行证登录页（appId=6788 为石之家；登录成功后按 returnURL 回跳 GHome/login 建立石之家会话）
    private const string TargetUrl = "https://login.u.sdo.com/sdo/iframe/?appId=6788&areaId=1&thirdParty=wegame%7C310&returnURL=https%3A%2F%2Fapiff14risingstones.web.sdo.com%2Fapi%2Fhome%2FGHome%2Flogin%3FredirectUrl%3Dhttps%3A%2F%2Fff14risingstones.web.sdo.com%2Fpc%2Findex.html#/post";

    private readonly Configuration configuration;
    private readonly SemaphoreSlim loginLock = new(1, 1);

    private Process? browserProcess;
    private ClientWebSocket? ws;
    private CancellationTokenSource? cts;
    private int nextId;
    private volatile bool disposed;
    private volatile bool loginInProgress;

    private sealed record CookieEntry(string Name, string Value, string Domain);

    public bool LoginInProgress => this.loginInProgress;

    public CookieBrowserService(Configuration configuration)
    {
        this.configuration = configuration;
    }

    /// <summary>打开浏览器让用户登录，自动读取 Cookie 保存到配置。</summary>
    public async Task<LoginResult> GetCookiesViaBrowserAsync()
    {
        if (this.disposed)
            return LoginResult.Failed;

        if (!await this.loginLock.WaitAsync(0).ConfigureAwait(false))
        {
            Notify("已有一次登录流程正在进行中，请在弹出的浏览器窗口完成登录。");
            return LoginResult.Busy;
        }

        string? userDataDir = null;
        try
        {
            this.loginInProgress = true;
            this.cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            var browserPath = ResolveBrowserPath();
            if (browserPath == null)
            {
                Notify("未找到 Chrome/Edge 浏览器。请在设置里指定浏览器路径，或手动粘贴 Cookie。", error: true);
                return LoginResult.Failed;
            }

            var port = GetFreePort();
            userDataDir = Path.Combine(Path.GetTempPath(), "shijia-signin-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            Notify("正在打开浏览器登录石之家，请在窗口中完成登录（扫码或账号密码）...");

            var args = $"--remote-debugging-port={port} --user-data-dir=\"{userDataDir}\" --no-first-run --no-default-browser-check --new-window {TargetUrl}";
            this.browserProcess = Process.Start(new ProcessStartInfo(browserPath, args)
            {
                UseShellExecute = false,
            });

            var wsUrl = await WaitForPageWebSocketAsync(port).ConfigureAwait(false);
            if (wsUrl == null)
            {
                Notify("无法连接浏览器调试端口，登录失败。", error: true);
                return LoginResult.Failed;
            }

            this.ws = new ClientWebSocket();
            await this.ws.ConnectAsync(new Uri(wsUrl), this.cts.Token).ConfigureAwait(false);

            var cookieString = await PollForCookiesAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(cookieString))
            {
                Notify("未能读取到石之家 Cookie（可能浏览器已关闭或登录超时）。", error: true);
                return LoginResult.Timeout;
            }

            // 读取浏览器真实 UA，供签到请求使用（石之家会话可能与 UA 绑定，需保持一致）
            var ua = await GetUserAgentAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(ua))
            {
                this.configuration.UserAgent = ua;
                Service.Log.Information($"[ShijiaSignIn] 已读取浏览器 UA: {ua}");
            }

            this.configuration.CookieString = cookieString;
            this.configuration.Save();
            Notify("已成功读取 Cookie 并保存，正在自动签到...");
            return LoginResult.Success;
        }
        catch (OperationCanceledException)
        {
            Notify("登录流程已取消或超时。", error: true);
            return LoginResult.Timeout;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[ShijiaSignIn] 浏览器登录异常: {Sanitize(ex.Message)}");
            Notify($"浏览器登录异常：{ex.Message}", error: true);
            return LoginResult.Failed;
        }
        finally
        {
            this.loginInProgress = false;
            CleanupBrowser(userDataDir);
            this.cts?.Dispose();
            this.cts = null;
            this.loginLock.Release();
        }
    }

    public void CancelLogin()
    {
        try { this.cts?.Cancel(); } catch { }
    }

    /// <summary>等待某个 page 目标的 WebSocket 调试地址就绪。</summary>
    private async Task<string?> WaitForPageWebSocketAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var i = 0; i < 60; i++)
        {
            if (this.cts!.IsCancellationRequested)
                return null;
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json").ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var type = el.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : null;
                    var wsUrl = el.TryGetProperty("webSocketDebuggerUrl", out var wEl) && wEl.ValueKind == JsonValueKind.String ? wEl.GetString() : null;
                    if (type == "page" && !string.IsNullOrEmpty(wsUrl))
                        return wsUrl;
                }
            }
            catch
            {
                // 浏览器还没就绪，继续等待
            }
            await Task.Delay(250, this.cts.Token).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// 轮询浏览器 Cookie。核心原则：只有在“石之家会话已真正建立”时才收集，避免只拿到 CASTGC 而缺会话 Cookie。
    /// 会话建立的判定：
    ///   1) 页面地址已跳到 ff14risingstones / apiff14risingstones（returnURL 回跳链已完成，最强信号）；
    ///   2) 或 出现了 ff14risingstones / apiff14risingstones 域上的任意 Cookie（会话 Cookie 已下发）。
    /// CASTGC 只作为“用户已完成盛大通行证登录”的参考：出现后最多再等 25 秒，
    /// 若迟迟没有石之家会话 Cookie 则按现状兜底收集（并打警告日志）。
    /// </summary>
    private async Task<string?> PollForCookiesAsync()
    {
        try
        {
            DateTime? castgcSeenAt = null;
            var lastCookieSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!this.cts!.IsCancellationRequested)
            {
                var cookies = await GetAllCookiesAsync().ConfigureAwait(false);
                var url = await GetCurrentUrlAsync().ConfigureAwait(false);

                // 诊断日志：Cookie 集合变化时打印全部 name@domain
                var cookieSet = cookies.Select(c => $"{c.Name}@{c.Domain}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!cookieSet.SetEquals(lastCookieSet))
                {
                    lastCookieSet = cookieSet;
                    Service.Log.Information($"[ShijiaSignIn] 浏览器 Cookie({cookies.Count}): {string.Join(", ", cookieSet)}");
                }

                var hasCastgc = cookies.Any(c => c.Name.Equals("CASTGC", StringComparison.OrdinalIgnoreCase));
                var urlOnShijia = url != null &&
                    Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) &&
                    IsShijiaDomain(parsedUrl.Host);
                var hasShijiaCookie = cookies.Any(c => IsShijiaDomain(c.Domain));

                if (hasCastgc && castgcSeenAt == null)
                {
                    castgcSeenAt = DateTime.UtcNow;
                    Notify("检测到盛大通行证登录成功，正在等待石之家会话建立...");
                }

                var sessionReady = urlOnShijia || hasShijiaCookie;
                var fallback = castgcSeenAt != null && (DateTime.UtcNow - castgcSeenAt.Value) > TimeSpan.FromSeconds(25);

                if (sessionReady || fallback)
                {
                    if (fallback && !sessionReady)
                        Service.Log.Warning("[ShijiaSignIn] 未检测到 ff14risingstones 会话 Cookie，按现有 sdo.com Cookie 兜底收集");

                    var selected = cookies
                        .Where(c => c.Domain.Contains("sdo.com", StringComparison.OrdinalIgnoreCase)
                                 || c.Domain.Contains("ff14risingstones", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToList();

                    var cookieString = string.Join("; ", selected.Select(c => $"{c.Name}={c.Value}"));
                    Service.Log.Information($"[ShijiaSignIn] 已读取 {selected.Count} 个 Cookie");
                    return cookieString;
                }

                await Task.Delay(2000, this.cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 浏览器被关闭等导致的连接中断
            Service.Log.Warning($"[ShijiaSignIn] 轮询中断: {Sanitize(ex.Message)}");
        }
        return null;
    }

    /// <summary>发送一条 CDP 命令，并读取匹配 id 的响应（忽略事件）。可带可选 params 对象。</summary>
    private async Task<JsonElement> SendAsync(string method, string? paramsJson = null)
    {
        if (this.ws == null)
            throw new InvalidOperationException("WebSocket 未连接");

        var id = ++this.nextId;
        var json = paramsJson == null
            ? $"{{\"id\":{id},\"method\":\"{method}\"}}"
            : $"{{\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";
        var bytes = Encoding.UTF8.GetBytes(json);
        await this.ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, this.cts!.Token).ConfigureAwait(false);

        var buffer = new byte[65536];
        var sb = new StringBuilder();
        while (true)
        {
            sb.Clear();
            WebSocketReceiveResult receive;
            do
            {
                receive = await this.ws.ReceiveAsync(new ArraySegment<byte>(buffer), this.cts.Token).ConfigureAwait(false);
                if (receive.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("WebSocket 已关闭");
                sb.Append(Encoding.UTF8.GetString(buffer, 0, receive.Count));
            } while (!receive.EndOfMessage);

            using var doc = JsonDocument.Parse(sb.ToString());
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.GetInt32() == id)
                return root.Clone();
        }
    }

    /// <summary>读取浏览器全部 Cookie（优先 Network.getAllCookies，失败退回 Storage.getCookies）。</summary>
    private async Task<List<CookieEntry>> GetAllCookiesAsync()
    {
        var list = new List<CookieEntry>();
        var parsed = false;
        try
        {
            var result = await SendAsync("Network.getAllCookies").ConfigureAwait(false);
            parsed = TryExtractCookies(result, list);
        }
        catch
        {
            parsed = false;
        }

        if (!parsed)
        {
            try
            {
                var result = await SendAsync("Storage.getCookies").ConfigureAwait(false);
                TryExtractCookies(result, list);
            }
            catch
            {
            }
        }

        return list;
    }

    /// <summary>读取当前页面地址（用于判断是否已回跳到石之家）。失败返回 null。</summary>
    private async Task<string?> GetCurrentUrlAsync()
    {
        try
        {
            var result = await SendAsync("Runtime.evaluate", "{\"expression\":\"location.href\",\"returnByValue\":true}").ConfigureAwait(false);
            if (result.TryGetProperty("result", out var r) &&
                r.TryGetProperty("result", out var inner) &&
                inner.TryGetProperty("value", out var v) &&
                v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch
        {
        }
        return null;
    }

    /// <summary>读取浏览器实际的 User-Agent（供签到请求复用，保持会话 UA 一致）。失败返回 null。</summary>
    private async Task<string?> GetUserAgentAsync()
    {
        try
        {
            var result = await SendAsync("Runtime.evaluate", "{\"expression\":\"navigator.userAgent\",\"returnByValue\":true}").ConfigureAwait(false);
            if (result.TryGetProperty("result", out var r) &&
                r.TryGetProperty("result", out var inner) &&
                inner.TryGetProperty("value", out var v) &&
                v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch
        {
        }
        return null;
    }

    /// <summary>判断域名/主机是否属于石之家（ff14risingstones 或 apiff14risingstones 的 web.sdo.com）。</summary>
    private static bool IsShijiaDomain(string hostOrDomain)
    {
        var d = hostOrDomain.TrimStart('.');
        return d.Equals("ff14risingstones.web.sdo.com", StringComparison.OrdinalIgnoreCase)
            || d.Equals("apiff14risingstones.web.sdo.com", StringComparison.OrdinalIgnoreCase)
            || d.EndsWith(".ff14risingstones.web.sdo.com", StringComparison.OrdinalIgnoreCase)
            || d.EndsWith(".apiff14risingstones.web.sdo.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractCookies(JsonElement result, List<CookieEntry> list)
    {
        if (!result.TryGetProperty("result", out var r) ||
            !r.TryGetProperty("cookies", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var c in arr.EnumerateArray())
        {
            var name = c.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : null;
            var value = c.TryGetProperty("value", out var vEl) && vEl.ValueKind == JsonValueKind.String ? vEl.GetString() : null;
            var domain = c.TryGetProperty("domain", out var dEl) && dEl.ValueKind == JsonValueKind.String ? dEl.GetString() : null;

            if (string.IsNullOrEmpty(name) || value == null || domain == null)
                continue;
            list.Add(new CookieEntry(name, value, domain));
        }
        return true;
    }

    private string? ResolveBrowserPath()
    {
        if (!string.IsNullOrWhiteSpace(this.configuration.BrowserPath) && File.Exists(this.configuration.BrowserPath))
            return this.configuration.BrowserPath;

        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void CleanupBrowser(string? userDataDir)
    {
        try
        {
            if (this.ws != null && this.ws.State == WebSocketState.Open)
                this.ws.Dispose();
        }
        catch { }
        this.ws = null;
        this.nextId = 0;

        try
        {
            if (this.browserProcess != null && !this.browserProcess.HasExited)
                this.browserProcess.Kill(entireProcessTree: true);
        }
        catch { }
        try
        {
            this.browserProcess?.Dispose();
        }
        catch { }
        this.browserProcess = null;

        // 清理临时 user-data-dir（尽力而为，失败忽略）
        if (userDataDir != null)
        {
            _ = Task.Run(() =>
            {
                try { if (Directory.Exists(userDataDir)) Directory.Delete(userDataDir, true); }
                catch { }
            });
        }
    }

    private static string Sanitize(string s) => s.Replace('{', '[').Replace('}', ']');

    private void Notify(string message, bool error = false)
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
            if (error)
                Service.ChatGui.PrintError(text);
            else
                Service.ChatGui.Print(text);
        });
    }

    public void Dispose()
    {
        this.disposed = true;
        this.CancelLogin();
        this.loginLock.Dispose();
    }
}
