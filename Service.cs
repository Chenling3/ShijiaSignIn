using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ShijiaSignIn;

/// <summary>Dalamud 服务定位器，由 pluginInterface.Create&lt;Service&gt;() 注入。</summary>
internal class Service
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; set; } = null!;
    [PluginService] internal static IFramework Framework { get; set; } = null!;
    [PluginService] internal static IPluginLog Log { get; set; } = null!;
}
