using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace ShijiaSignIn;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>石之家签到所需的整条 Cookie 字符串("name=value; name2=value2" 形式)。可手动填，也可用浏览器自动获取。</summary>
    public string CookieString { get; set; } = string.Empty;

    /// <summary>每日自动签到时间(本地时间)，24 小时制 "HH:mm"。</summary>
    public string AutoSignTime { get; set; } = "00:05";

    /// <summary>是否启用每日自动签到。</summary>
    public bool AutoSignEnabled { get; set; } = true;

    /// <summary>是否在聊天框显示签到结果提示。</summary>
    public bool ChatNotification { get; set; } = true;

    /// <summary>Cookie 缺失或失效时，是否自动打开浏览器登录获取。</summary>
    public bool AutoOpenBrowser { get; set; } = true;

    /// <summary>自定义浏览器可执行文件路径；留空则自动检测 Chrome/Edge。</summary>
    public string BrowserPath { get; set; } = string.Empty;

    /// <summary>登录浏览器实际的 User-Agent。自动登录时读取并用于签到请求（石之家会话可能与 UA 绑定）。留空则回退 Chrome124。</summary>
    public string UserAgent { get; set; } = string.Empty;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public void Save()
    {
        this.pluginInterface?.SavePluginConfig(this);
    }
}
