namespace MentorRoulette;

using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.DutyState;
using MentorRoulette.Sync;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

public sealed class Plugin : IDalamudPlugin
{
    internal static Plugin Instance { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("MentorRoulette");

    internal Config          Config     { get; }
    internal AutoDutyIpc     Ipc        { get; }
    internal Manager         Manager    { get; }
    internal ConfigWindow    Window     { get; }
    internal SyncCoordinator Sync       { get; }
    internal SyncWindow      SyncWindow { get; }

    private DateTime lastTick = DateTime.MinValue;

    public Plugin(IDalamudPluginInterface pi)
    {
        Instance = this;

        pi.Create<Service>();

        this.Config = pi.GetPluginConfig() as Config ?? new Config();
        this.Ipc    = new AutoDutyIpc(pi);

        this.Manager    = new Manager(this.Config, this.Ipc);
        this.Window     = new ConfigWindow(this);
        this.Sync       = new SyncCoordinator(this.Config, this.Ipc);
        this.SyncWindow = new SyncWindow(this);

        this.Config.EnsureClientId();

        this.windowSystem.AddWindow(this.Window);
        this.windowSystem.AddWindow(this.SyncWindow);

        pi.UiBuilder.Draw         += this.Draw;
        pi.UiBuilder.OpenConfigUi += this.OpenConfig;

        Service.Framework.Update      += this.OnFrameworkUpdate;
        Service.DutyState.DutyCompleted += this.OnDutyCompleted;

        Service.Commands.AddHandler("/mentor", new CommandInfo(this.OnCommand)
        {
            HelpMessage = "导随助手：/mentor 打开设置；/mentor start|stop|status。",
        });

        if (this.Config.Enabled)
            this.Manager.Start();

        Service.Log.Info("[MentorRoulette] loaded");
    }

    public void Dispose()
    {
        Service.Framework.Update        -= this.OnFrameworkUpdate;
        Service.DutyState.DutyCompleted -= this.OnDutyCompleted;
        Service.Commands.RemoveHandler("/mentor");

        Service.PluginInterface.UiBuilder.Draw         -= this.Draw;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfig;

        this.windowSystem.RemoveAllWindows();
        this.Manager.Stop("插件卸载");
    }

    private void Draw() => this.windowSystem.Draw();

    private void OpenConfig() => this.Window.IsOpen = true;

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        this.Manager.OnDutyCompleted();
        this.Sync.OnDutyCompleted();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if ((DateTime.UtcNow - this.lastTick).TotalMilliseconds < 200)
            return;

        this.lastTick = DateTime.UtcNow;
        this.Manager.Tick();
        this.Sync.Tick();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":
            case "config":
                this.Window.IsOpen = true;
                break;
            case "start":
                this.Manager.Start();
                break;
            case "stop":
                this.Manager.Stop("命令停止");
                break;
            case "cancel":
                this.Manager.CancelLeave();
                break;
            case "sync":
                this.SyncWindow.IsOpen = !this.SyncWindow.IsOpen;
                break;
            case "dump":
                Service.Log.Info(RouletteQueue.DumpList());
                break;
            default:
                Service.Chat.Print(new()
                {
                    Message = $"[导随] {this.Manager.StateText} / {this.Manager.Status}",
                });
                break;
        }
    }
}
