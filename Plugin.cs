using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Plugin;

namespace ShijiaSignIn;

public sealed class Plugin : IDalamudPlugin
{
    public static string Name => "石之家自动签到";

    private const string CommandName = "/shijia";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Configuration configuration;
    private readonly SignInService signInService;
    private readonly CookieBrowserService cookieBrowserService;
    private readonly PluginUI pluginUI;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        pluginInterface.Create<Service>();

        this.configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.configuration.Initialize(pluginInterface);

        this.cookieBrowserService = new CookieBrowserService(this.configuration);
        this.signInService = new SignInService(this.configuration);
        this.signInService.OnLoginRequired = this.AutoLoginAndSignAsync;
        this.pluginUI = new PluginUI(this.configuration, this.signInService, this.cookieBrowserService);

        Service.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "石之家自动签到。用法: /shijia [sign|login|help]",
        });

        Service.PluginInterface.UiBuilder.Draw += this.pluginUI.Draw;
        Service.PluginInterface.UiBuilder.OpenConfigUi += this.pluginUI.Toggle;

        this.signInService.Start();

        // 启动时缺 Cookie 且允许自动 → 打开浏览器登录
        if (this.configuration.AutoOpenBrowser && string.IsNullOrWhiteSpace(this.configuration.CookieString))
            _ = this.AutoLoginAndSignAsync();
    }

    private async Task AutoLoginAndSignAsync()
    {
        var result = await this.cookieBrowserService.GetCookiesViaBrowserAsync();
        if (result == LoginResult.Success)
            await this.signInService.SignInAsync("登录后自动");
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Trim().ToLowerInvariant();
        switch (args)
        {
            case "":
                this.pluginUI.Toggle();
                break;
            case "sign":
            case "签到":
            case "qiandao":
                _ = this.signInService.SignInAsync("手动命令");
                break;
            case "login":
            case "登录":
                _ = this.AutoLoginAndSignAsync();
                break;
            case "help":
            case "帮助":
                Service.ChatGui.Print("[石之家签到] 用法: /shijia 打开设置 | /shijia sign 立即签到 | /shijia login 打开浏览器登录");
                break;
            default:
                Service.ChatGui.PrintError($"[石之家签到] 未知参数: {arguments}。用 /shijia help 查看帮助。");
                break;
        }
    }

    public void Dispose()
    {
        this.signInService.Dispose();
        this.cookieBrowserService.Dispose();
        Service.CommandManager.RemoveHandler(CommandName);
        Service.PluginInterface.UiBuilder.Draw -= this.pluginUI.Draw;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= this.pluginUI.Toggle;
        this.pluginUI.Dispose();
    }
}
