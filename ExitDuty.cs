namespace MentorRoulette;

using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

/// <summary>
/// 退本。流程照 DailyRoutines 的 AutoLeaveDuty：
/// 默认等脱战再退，勾选「立即退本」则只等复活/过场结束；
/// 每次尝试间留间隔，超时自动重试。
/// </summary>
internal sealed unsafe class ExitDuty
{
    private uint     territory;
    private DateTime startedAt;
    private DateTime lastAttempt;
    private int      attempts;

    internal bool Running { get; private set; }

    internal TimeSpan Elapsed => this.Running ? DateTime.UtcNow - this.startedAt : TimeSpan.Zero;

    internal int Attempts => this.attempts;

    internal void Start(bool force = false)
    {
        if (this.Running || Service.ClientState.TerritoryType == 0)
            return;

        this.Force = force;

        this.territory   = Service.ClientState.TerritoryType;
        this.startedAt   = DateTime.UtcNow;
        this.lastAttempt = DateTime.MinValue;
        this.attempts    = 0;
        this.Running     = true;

        Service.Log.Debug($"[ExitDuty] start for territory {this.territory}");
        DiagLog.Add("开始退本");
    }

    internal void Stop()
    {
        this.Running   = false;
        this.territory = 0;
    }

    internal void Tick()
    {
        if (!this.Running)
            return;

        if (Service.ClientState.TerritoryType != this.territory || this.territory == 0)
        {
            DiagLog.Add($"已离开副本（{this.attempts} 次尝试）");
            this.Stop();
            return;
        }

        // 副本里死亡/过场/战斗中都不发请求（立即退本只跳过战斗判断）
        if (Service.Condition[ConditionFlag.Unconscious] || Service.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            return;

        if (!Service.Condition[ConditionFlag.InCombat] || this.Force)
        {
            if ((DateTime.UtcNow - this.lastAttempt).TotalSeconds >= 2)
            {
                this.lastAttempt = DateTime.UtcNow;
                this.attempts++;
                this.TryLeave();
            }
        }

        if (this.Elapsed.TotalSeconds > 45)
        {
            Service.Log.Warning("[ExitDuty] 45 秒还没退出去，放弃");
            DiagLog.Add("退本超时，放弃");
            this.Stop();
        }
    }

    /// <summary>是否立即退本（不等脱战）。</summary>
    internal bool Force { get; set; }

    private void TryLeave()
    {
        if (NativeCommand.Available && NativeCommand.Execute(NativeCommand.LeaveDuty))
        {
            Service.Log.Debug($"[ExitDuty] native leave #{this.attempts}");
            DiagLog.Add($"退本请求 #{this.attempts}（原生）");
            return;
        }

        AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentsFinderMenu)->Show();

        if (!AddonHelper.TryGetReady("ContentsFinderMenu", out AtkUnitBase* menu))
            return;

        AddonHelper.Fire(menu, 0);
        AddonHelper.Fire(menu, false, -2);

        if (AddonHelper.TryGetReady("SelectYesno", out AtkUnitBase* yesno))
            AddonHelper.Fire(yesno, 0);

        Service.Log.Debug($"[ExitDuty] attempt {this.attempts}");
        DiagLog.Add($"退本请求 #{this.attempts}");
    }
}
