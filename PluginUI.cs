using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace ShijiaSignIn;

public sealed class PluginUI : IDisposable
{
    private readonly Configuration configuration;
    private readonly SignInService signInService;
    private readonly CookieBrowserService cookieBrowserService;
    private bool visible;

    public PluginUI(Configuration configuration, SignInService signInService, CookieBrowserService cookieBrowserService)
    {
        this.configuration = configuration;
        this.signInService = signInService;
        this.cookieBrowserService = cookieBrowserService;
    }

    public void Toggle() => this.visible = !this.visible;

    public void Draw()
    {
        if (!this.visible)
            return;

        ImGui.SetNextWindowSize(new Vector2(620, 520), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("石之家自动签到", ref this.visible))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("自动完成 FF14 石之家(ff14risingstones)每日签到。");
        ImGui.Spacing();

        // 登录获取 Cookie
        if (this.cookieBrowserService.LoginInProgress)
        {
            ImGui.TextColored(new Vector4(1f, 0.7f, 0f, 1f), "登录进行中：请在打开的浏览器窗口完成登录（扫码或账号密码），插件会自动读取 Cookie。");
            if (ImGui.Button("取消登录", new Vector2(120, 0)))
                this.cookieBrowserService.CancelLogin();
        }
        else
        {
            if (ImGui.Button("打开浏览器登录获取 Cookie", new Vector2(230, 0)))
            {
                _ = this.LoginAndSignAsync();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("也可用 /shijia login");
        }

        ImGui.Spacing();

        ImGui.Text("盛大通行证 Cookie（可自动获取，也可手动粘贴）:");
        var cookie = this.configuration.CookieString;
        if (ImGui.InputTextMultiline("##cookie", ref cookie, 16384, new Vector2(-1, 90)))
        {
            this.configuration.CookieString = cookie;
            this.configuration.Save();
        }
        ImGui.TextDisabled("手动方式：浏览器登录后按 F12 → Network → 复制任一请求的 Cookie 头粘贴到这里。");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var auto = this.configuration.AutoSignEnabled;
        if (ImGui.Checkbox("启用每日自动签到", ref auto))
        {
            this.configuration.AutoSignEnabled = auto;
            this.configuration.Save();
        }

        var time = this.configuration.AutoSignTime;
        if (ImGui.InputText("每日签到时间 (HH:mm)", ref time, 8))
        {
            this.configuration.AutoSignTime = time;
            this.configuration.Save();
        }

        var notify = this.configuration.ChatNotification;
        if (ImGui.Checkbox("在聊天框显示提示", ref notify))
        {
            this.configuration.ChatNotification = notify;
            this.configuration.Save();
        }

        var autoOpen = this.configuration.AutoOpenBrowser;
        if (ImGui.Checkbox("Cookie 缺失/失效时自动打开浏览器登录", ref autoOpen))
        {
            this.configuration.AutoOpenBrowser = autoOpen;
            this.configuration.Save();
        }

        var browserPath = this.configuration.BrowserPath;
        ImGui.Text("浏览器路径（留空自动检测 Chrome/Edge）:");
        if (ImGui.InputText("##browserpath", ref browserPath, 512))
        {
            this.configuration.BrowserPath = browserPath;
            this.configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("立即签到", new Vector2(120, 0)))
        {
            _ = this.signInService.SignInAsync("手动按钮");
        }
        ImGui.SameLine();
        ImGui.TextDisabled("命令: /shijia sign");

        ImGui.End();
    }

    private async Task LoginAndSignAsync()
    {
        var result = await this.cookieBrowserService.GetCookiesViaBrowserAsync();
        if (result == LoginResult.Success)
            await this.signInService.SignInAsync("登录后自动");
    }

    public void Dispose()
    {
    }
}
