namespace MentorRoulette;

using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using MentorRoulette.Sync;

public enum RunMode : byte
{
    AllSupported = 0,
    Selected     = 1,
}

public enum LaunchMode : byte
{
    Group = 0,
    Solo  = 1,
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

    /// <summary>运行方式：组队同步 / 单人导随。UI 只切这一个开关。</summary>
    public LaunchMode LaunchMode { get; set; } = LaunchMode.Group;

    /// <summary>AllSupported = 按 AutoDuty 是否支持来跑；Selected = 完全自定义，只留勾选的副本。</summary>
    public RunMode Mode { get; set; } = RunMode.AllSupported;

    /// <summary>自定义模式下的白名单（TerritoryType）。</summary>
    public List<uint> Whitelist { get; set; } = [];

    /// <summary>AutoDuty 支持模式下的排除项（取消勾选的副本）。</summary>
    public List<uint> Excluded { get; set; } = [];

    /// <summary>
    /// 自定义模式下是否也自动启动 AutoDuty。默认关：自定义模式只负责「留下这些副本」，不接管战斗。
    /// </summary>
    public bool StartAdInCustomMode { get; set; } = false;

    /// <summary>排到不支持的副本时：退本，或者原地挂着。</summary>
    public UnsupportedAction Unsupported { get; set; } = UnsupportedAction.LeaveDuty;

    // ---------- 两种模式共用的行为 ----------

    /// <summary>进对副本后做什么（命令 / AutoDuty / 两者）。</summary>
    public PostEntry PostEntry { get; set; } = new();

    /// <summary>惩罚等待：all = 全员一起等，leaver = 只有退本端等。</summary>
    public string PenaltyMode { get; set; } = "all";

    public bool AutoLoop         { get; set; }
    /// <summary>轮次上限；0 = 不限。</summary>
    public int  MaxRounds        { get; set; }
    public int  RoundIntervalSec { get; set; } = 10;
    public int  SuccessMin       { get; set; } = 1;

    // ---------- 排队目标 ----------

    /// <summary>副本清单。空 = 放行全部 AutoDuty 支持的本；非空 = 只用清单里的。</summary>
    public List<uint> DutyPool { get; set; } = [];

    /// <summary>组队端要排的那个副本（TerritoryType，必须是清单里的）。</summary>
    public uint PartyDutyTerritory { get; set; }

    /// <summary>立即退本：不等脱战（DailyRoutines 的 ForceToLeave）。</summary>
    public bool ForceLeaveDuty { get; set; }

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

    // ---------- 组队同步 ----------

    public bool   SyncEnabled    { get; set; }
    public string SyncServerUrl  { get; set; } = SyncDefaults.ServerUrl;
    public string SyncRoomCode   { get; set; } = string.Empty;
    /// <summary>当前客户端身份（= 游戏角色名），由插件自动维护，不用手填。</summary>
    public string SyncClientId   { get; set; } = string.Empty;
    public string SyncHostToken  { get; set; } = string.Empty;
    public bool   SyncIsHost     { get; set; }

    public Dictionary<string, Assignment> SyncAssignments { get; set; } = [];

    /// <summary>「已选副本」列表（TerritoryType），供穿梭框使用。</summary>
    public List<uint> SyncSelectedDuties { get; set; } = [];
    public RoomOptions                    SyncOptions     { get; set; } = new();
    public Stagger                        SyncStagger     { get; set; } = new();

    /// <summary>把旧版本的白名单/已选副本合并进 DutyPool。</summary>
    public void Migrate()
    {
        if (this.DutyPool.Count == 0)
        {
            foreach (uint t in this.Whitelist)
                if (!this.DutyPool.Contains(t))
                    this.DutyPool.Add(t);
        }

        if (this.DutyPool.Count == 0)
        {
            foreach (uint t in this.SyncSelectedDuties)
                if (!this.DutyPool.Contains(t))
                    this.DutyPool.Add(t);
        }

        if (this.PartyDutyTerritory == 0 && this.DutyPool.Count > 0)
            this.PartyDutyTerritory = this.DutyPool[0];
    }

    /// <summary>把客户端身份同步成当前角色名。</summary>
    public string EnsureClientId(string characterName)
    {
        if (!string.IsNullOrWhiteSpace(characterName))
            this.SyncClientId = characterName;

        return this.SyncClientId;
    }

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
