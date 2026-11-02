namespace MentorRoulette;

using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

/// <summary>退本：打开任务搜索器菜单 → 点「退出任务」→ 确认。</summary>
internal sealed unsafe class ExitDuty
{
    private uint     territory;
    private DateTime startedAt;

    internal bool Running { get; private set; }

    internal TimeSpan Elapsed => this.Running ? DateTime.UtcNow - this.startedAt : TimeSpan.Zero;

    internal void Start()
    {
        if (this.Running || Service.ClientState.TerritoryType == 0)
            return;

        this.territory = Service.ClientState.TerritoryType;
        this.startedAt = DateTime.UtcNow;
        this.Running   = true;
        Service.Log.Debug($"[ExitDuty] start for territory {this.territory}");
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
            this.Stop();
            return;
        }

        if (Service.Condition[ConditionFlag.InCombat])
            return;

        AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentsFinderMenu)->Show();

        if (!AddonHelper.TryGetReady("ContentsFinderMenu", out AtkUnitBase* menu))
            return;

        AddonHelper.Fire(menu, 0);
        AddonHelper.Fire(menu, false, -2);

        if (AddonHelper.TryGetReady("SelectYesno", out AtkUnitBase* yesno))
            AddonHelper.Fire(yesno, 0);
    }
}
