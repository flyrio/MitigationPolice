// 本文件集中管理 Dalamud 的依赖注入服务，供插件各模块统一访问。
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MitigationPolice;

#pragma warning disable 8618
internal sealed class Service {
    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; }

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; }

    [PluginService]
    internal static IDataManager DataManager { get; private set; }

    [PluginService]
    internal static IClientState ClientState { get; private set; }

    [PluginService]
    internal static IObjectTable ObjectTable { get; private set; }

    [PluginService]
    internal static IPartyList PartyList { get; private set; }

    [PluginService]
    internal static ICondition Condition { get; private set; }

    [PluginService]
    internal static IFramework Framework { get; private set; }

    [PluginService]
    internal static IPluginLog PluginLog { get; private set; }

    [PluginService]
    internal static IGameInteropProvider GameInteropProvider { get; private set; }

    [PluginService]
    internal static ISigScanner SigScanner { get; private set; }

    internal static void Initialize(IDalamudPluginInterface pluginInterface) {
        pluginInterface.Create<Service>();
    }
}
#pragma warning restore 8618

