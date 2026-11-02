namespace MentorRoulette;

using System;
using System.Collections.Generic;
using Dalamud.Configuration;

public enum RunMode : byte
{
    AllSupported = 0,
    Selected     = 1,
}

public enum UnsupportedAction : byte
{
    LeaveDuty = 0,
    KeepDuty  = 1,
}

[Serializable]
public sealed class Config : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; }

    /// <summary>AllSupported = AutoDuty 有路径就跑；Selected = 只有勾选的副本才跑。</summary>
    public RunMode Mode { get; set; } = RunMode.AllSupported;

    /// <summary>Selected 模式使用的白名单（TerritoryType）。</summary>
    public List<uint> Whitelist { get; set; } = [];

    /// <summary>排到不支持的副本时：退本，或者原地挂着。</summary>
    public UnsupportedAction Unsupported { get; set; } = UnsupportedAction.LeaveDuty;

    public int JudgeDelayMs      { get; set; } = 3000;
    public int GraceSeconds      { get; set; } = 3;
    public int CombatTimeoutSec  { get; set; } = 20;
    public int LeaveTimeoutSec   { get; set; } = 90;
    public int RequeueDelaySec   { get; set; } = 5;
    public bool PenaltyGuard     { get; set; } = true;
    public int PenaltyWaitMinutes{ get; set; } = 30;
    public int MaxLeavesBeforeStop { get; set; }
    public bool BareMode         { get; set; } = true;
    public bool Notify           { get; set; } = true;

    /// <summary>要排的随机任务（ContentRoulette）。导师随机 = 9。</summary>
    public uint RouletteId { get; set; } = 9;

    // 本次运行统计，不存盘
    [NonSerialized] public int QueuedCount;
    [NonSerialized] public int JoinedCount;
    [NonSerialized] public int CompletedCount;
    [NonSerialized] public int LeftCount;
    [NonSerialized] public DateTime LastLeaveAt = DateTime.MinValue;

    public void Save() => Service.PluginInterface.SavePluginConfig(this);

    public void ResetSession()
    {
        this.QueuedCount    = 0;
        this.JoinedCount    = 0;
        this.CompletedCount = 0;
        this.LeftCount      = 0;
    }
}
