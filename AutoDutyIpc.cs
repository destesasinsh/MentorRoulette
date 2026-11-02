namespace MentorRoulette;

using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

/// <summary>
/// AutoDuty 的 IPC 封装（ECommons EzIPC，名字形如 "AutoDuty.方法名"）。
/// </summary>
internal sealed class AutoDutyIpc
{
    private readonly ICallGateSubscriber<uint, int, bool, object> run;
    private readonly ICallGateSubscriber<object>                   stop;
    private readonly ICallGateSubscriber<uint, bool>               hasPath;
    private readonly ICallGateSubscriber<bool>                     isStopped;

    internal AutoDutyIpc(IDalamudPluginInterface pi)
    {
        this.run       = pi.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run");
        this.stop      = pi.GetIpcSubscriber<object>("AutoDuty.Stop");
        this.hasPath   = pi.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath");
        this.isStopped = pi.GetIpcSubscriber<bool>("AutoDuty.IsStopped");
    }

    /// <summary>AutoDuty 是否已加载并注册了 IPC。</summary>
    internal bool Available => this.run.HasAction && this.hasPath.HasFunction;

    internal bool HasPath(uint territoryType)
    {
        try
        {
            return this.hasPath.HasFunction && this.hasPath.InvokeFunc(territoryType);
        }
        catch (Exception ex)
        {
            Service.Log.Debug($"AutoDuty.ContentHasPath({territoryType}) failed: {ex.Message}");
            return false;
        }
    }

    internal bool IsStopped(out bool ok)
    {
        ok = this.isStopped.HasFunction;

        try
        {
            return !ok || this.isStopped.InvokeFunc();
        }
        catch
        {
            return true;
        }
    }

    internal void Run(uint territoryType, int loops = 1, bool bareMode = false)
    {
        try
        {
            this.run.InvokeAction(territoryType, loops, bareMode);
        }
        catch (Exception ex)
        {
            Service.Log.Error($"AutoDuty.Run({territoryType}) failed: {ex}");
        }
    }

    internal void Stop()
    {
        try
        {
            if (this.stop.HasAction)
                this.stop.InvokeAction();
        }
        catch (Exception ex)
        {
            Service.Log.Error($"AutoDuty.Stop failed: {ex}");
        }
    }
}
