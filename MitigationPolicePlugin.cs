// 本文件是减伤警察插件入口，负责初始化服务、窗口、事件捕获与命令注册。
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using MitigationPolice.Chat;
using MitigationPolice.Events;
using MitigationPolice.Mitigations;
using MitigationPolice.Storage;
using MitigationPolice.UI;

namespace MitigationPolice;

public sealed class MitigationPolicePlugin : IDalamudPlugin {
    private const string CommandName = "/mp";

    public Configuration Configuration { get; }
    public JsonEventStore EventStore { get; }
    public MitigationState MitigationState { get; }
    public MitigationEventCapture EventCapture { get; }
    public ChatSender ChatSender { get; }

    public WindowSystem WindowSystem { get; }
    public MainWindow MainWindow { get; }
    public ConfigWindow ConfigWindow { get; }

    public MitigationPolicePlugin(IDalamudPluginInterface pluginInterface) {
        Service.Initialize(pluginInterface);

        Configuration = Configuration.Get(pluginInterface);
        EventStore = new JsonEventStore(pluginInterface, () => Configuration.MaxStoredEvents, () => Configuration.SaveDebounceMilliseconds);
        EventStore.Load();

        MitigationState = new MitigationState(this);
        ChatSender = new ChatSender(() => Configuration.AllowSendingToPartyChat);
        EventCapture = new MitigationEventCapture(this);

        WindowSystem = new WindowSystem("MitigationPolice");
        MainWindow = new MainWindow(this);
        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ConfigWindow);

        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenMainUi += () => MainWindow.Toggle();
        pluginInterface.UiBuilder.OpenConfigUi += () => ConfigWindow.Toggle();

        var commandInfo = new CommandInfo((_, _) => MainWindow.Toggle()) {
            HelpMessage = "打开/关闭 减伤警察 主界面",
        };
        Service.CommandManager.AddHandler(CommandName, commandInfo);
    }

    public void Dispose() {
        Service.CommandManager.RemoveHandler(CommandName);
        Service.PluginInterface.UiBuilder.Draw -= DrawUi;

        EventCapture.Dispose();
        ChatSender.Dispose();
        EventStore.Dispose();
    }

    private void DrawUi() {
        EventStore.Tick();
        WindowSystem.Draw();
    }
}
