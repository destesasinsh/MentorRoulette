namespace MentorRoulette;

using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

public enum State : byte
{
    Stopped  = 0,
    Queueing = 1,
    Judging  = 2,
    Grace    = 3,
    Leaving  = 4,
    Cooldown = 5,
    Running  = 6,
    Keeping  = 7,
}

/// <summary>
/// 导随主流程：排队 → 进本判定 → 支持就 IPC 让 AutoDuty 跑，不支持就退本 → 冷却 → 再排。
/// </summary>
internal sealed class Manager
{
    private readonly Config      cfg;
    private readonly AutoDutyIpc ipc;
    private readonly RouletteQueue queue = new();
    private readonly ExitDuty      exit  = new();

    private DateTime stateAt;
    private DateTime judgeAt;
    private DateTime leaveAt;
    private DateTime queueAttemptAt;
    private DateTime? completedAt;
    private int  leaveStep;
    private bool joinedQueue;
    private bool lastLeaveWasAbandon;
    private bool dutyCompleted;

    internal Manager(Config cfg, AutoDutyIpc ipc)
    {
        this.cfg = cfg;
        this.ipc = ipc;
    }

    internal State    State           { get; private set; } = State.Stopped;
    internal string   Status          { get; private set; } = string.Empty;
    internal DateTime CooldownUntil   { get; private set; } = DateTime.MinValue;
    internal int      ConsecutiveLeaves { get; private set; }
    internal bool     CancelRequested { get; set; }

    internal Config Config => this.cfg;

    private static uint Territory => Service.ClientState.TerritoryType;

    private static bool InDuty =>
        Territory != 0 && (DutyCatalog.IsKnownDuty(Territory) || Service.DutyState.IsDutyStarted);

    internal void Start()
    {
        if (!this.ipc.Available)
        {
            this.Notify("没有检测到 AutoDuty，请先安装并启用 AutoDuty。");
            this.cfg.Enabled = false;
            return;
        }

        if (this.State != State.Stopped)
            return;

        this.cfg.Enabled = true;
        this.cfg.ResetSession();
        this.ConsecutiveLeaves = 0;
        this.lastLeaveWasAbandon = false;
        this.joinedQueue = false;
        this.CancelRequested = false;

        DutyCatalog.EnsureLoaded();
        DutyCatalog.RefreshSupport(this.ipc);

        if (InDuty)
        {
            this.judgeAt = DateTime.UtcNow;
            this.SetState(State.Judging, "已在本内，直接判定");
        }
        else
        {
            this.BeginQueueing();
        }

        this.cfg.Save();
        Service.Log.Info("[Manager] started");
    }

    internal void Stop(string reason)
    {
        this.queue.Reset();
        this.exit.Stop();
        this.cfg.Enabled = false;
        this.SetState(State.Stopped, string.IsNullOrEmpty(reason) ? "已停止" : reason);
        this.cfg.Save();
        Service.Log.Info($"[Manager] stopped: {reason}");
    }

    internal void CancelLeave()
    {
        if (this.State == State.Grace)
            this.CancelRequested = true;
    }

    internal void OnDutyCompleted()
    {
        this.dutyCompleted = true;
    }

    private void SetState(State state, string status)
    {
        this.State   = state;
        this.Status  = status;
        this.stateAt = DateTime.UtcNow;
        Service.Log.Debug($"[Manager] => {state}: {status}");
    }

    private void Notify(string message)
    {
        Service.Log.Info($"[MentorRoulette] {message}");

        if (this.cfg.Notify)
            Service.Chat.Print(new XivChatEntry { Message = $"[导随] {message}" });
    }

    /// <summary>这个副本要不要留下来（跑或挂机），返回 false 就退本。</summary>
    internal bool IsAllowed(uint territory) =>
        this.cfg.Mode switch
        {
            RunMode.AllSupported => this.ipc.HasPath(territory),
            _                    => this.cfg.Whitelist.Contains(territory),
        };

    /// <summary>这个副本要不要交给 AutoDuty 自动跑。</summary>
    private bool ShouldStartAutoDuty(uint territory) =>
        this.cfg.Mode == RunMode.AllSupported
            ? this.ipc.HasPath(territory)
            : this.cfg.StartAdInCustomMode && this.ipc.HasPath(territory);

    internal void Tick()
    {
        if (!this.cfg.Enabled || this.State == State.Stopped)
            return;

        try
        {
            switch (this.State)
            {
                case State.Queueing: this.UpdateQueueing(); break;
                case State.Judging:  this.UpdateJudging();  break;
                case State.Grace:    this.UpdateGrace();    break;
                case State.Leaving:  this.UpdateLeaving();  break;
                case State.Cooldown: this.UpdateCooldown(); break;
                case State.Running:  this.UpdateRunning();  break;
                case State.Keeping:  this.UpdateKeeping();  break;
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"[Manager] tick failed: {ex}");
        }
    }

    private void BeginQueueing()
    {
        this.queueAttemptAt = DateTime.UtcNow;
        this.joinedQueue    = false;
        this.SetState(State.Queueing, "排队中…");
    }

    private void UpdateQueueing()
    {
        if (InDuty)
        {
            this.OnEnteredDuty();
            return;
        }

        if (RouletteQueue.InQueue)
        {
            if (!this.joinedQueue)
            {
                this.joinedQueue = true;
                this.cfg.QueuedCount++;
                this.SetState(State.Queueing, $"已进队列（随机 {RouletteQueue.QueuedRouletteId}）");
            }

            return;
        }

        this.queue.Tick(this.cfg.RouletteId);

        // 惩罚看门狗：试着排了 25 秒还没进队列，就假定还在放弃惩罚里
        if (this.cfg.PenaltyGuard && this.lastLeaveWasAbandon &&
            (DateTime.UtcNow - this.queueAttemptAt).TotalSeconds > 25)
        {
            DateTime until = this.cfg.LastLeaveAt.AddMinutes(this.cfg.PenaltyWaitMinutes);

            if (until > DateTime.UtcNow)
            {
                this.queue.Reset();
                this.CooldownUntil = until;
                this.SetState(State.Cooldown, $"检测到排本惩罚，等待至 {until:HH:mm:ss}");
                this.Notify($"检测到排本惩罚，等到 {until:HH:mm} 再排。");
            }
        }
    }

    private void OnEnteredDuty()
    {
        this.queue.Reset();
        this.cfg.JoinedCount++;
        this.judgeAt       = DateTime.UtcNow;
        this.completedAt   = null;
        this.dutyCompleted = false;
        this.SetState(State.Judging, "进本判定中…");
    }

    private void UpdateJudging()
    {
        if (!InDuty)
        {
            this.EnterCooldown("不在副本内，回到排队");
            return;
        }

        if ((DateTime.UtcNow - this.judgeAt).TotalMilliseconds < this.cfg.JudgeDelayMs)
            return;

        uint territory = Territory;
        string name    = DutyCatalog.Get(territory)?.Name ?? territory.ToString();

        if (this.IsAllowed(territory))
        {
            if (this.ShouldStartAutoDuty(territory))
            {
                this.Notify($"支持 {name}，交给 AutoDuty。");
                this.ipc.Run(territory, 1, this.cfg.BareMode);
                this.SetState(State.Running, $"攻略中：{name}");
                return;
            }

            // 自定义模式（或没开自动启动）：留下副本，但不接管战斗
            this.Notify(this.cfg.Mode == RunMode.Selected
                            ? $"自定义副本 {name} 已保留，不启动 AutoDuty。"
                            : $"{name} 没有 AutoDuty 路径，保留但不会自动攻略。");

            this.SetState(State.Keeping, $"保留：{name}（未启动 AutoDuty）");
            return;
        }

        if (this.cfg.Unsupported == UnsupportedAction.KeepDuty)
        {
            this.Notify($"{name} 不支持，原地挂机。");
            this.SetState(State.Keeping, $"挂机中：{name}");
            return;
        }

        this.CancelRequested = false;
        this.Notify($"{name} 不支持，{this.cfg.GraceSeconds} 秒后退出（点“取消退出”可留下）。");
        this.SetState(State.Grace, $"{name} 不支持，{this.cfg.GraceSeconds} 秒后退出");
    }

    private void UpdateGrace()
    {
        if (!InDuty)
        {
            this.EnterCooldown("已经离开副本");
            return;
        }

        if (this.CancelRequested)
        {
            this.CancelRequested = false;
            this.Notify("已取消退出，这个本交给你手动处理。");
            this.SetState(State.Keeping, "手动处理中（已取消退出）");
            return;
        }

        if ((DateTime.UtcNow - this.stateAt).TotalSeconds < this.cfg.GraceSeconds)
            return;

        this.leaveAt   = DateTime.UtcNow;
        this.leaveStep = 0;
        this.SetState(State.Leaving, "退出副本中…");
    }

    private void UpdateLeaving()
    {
        if (!InDuty)
        {
            this.FinishLeave();
            return;
        }

        if ((DateTime.UtcNow - this.leaveAt).TotalSeconds > this.cfg.LeaveTimeoutSec)
        {
            this.Stop("退本超时");
            this.Notify("多次尝试仍未退出副本，已停止。");
            return;
        }

        if (this.leaveStep == 0)
        {
            if (Service.Condition[ConditionFlag.InCombat] &&
                (DateTime.UtcNow - this.leaveAt).TotalSeconds < this.cfg.CombatTimeoutSec)
                return;

            this.leaveStep = 1;
        }

        this.exit.Tick();

        if (!this.exit.Running && InDuty)
            this.exit.Start();
    }

    private void FinishLeave()
    {
        this.exit.Stop();

        this.cfg.LeftCount++;
        this.ConsecutiveLeaves++;
        this.lastLeaveWasAbandon = true;
        this.cfg.LastLeaveAt     = DateTime.UtcNow;

        if (this.cfg.MaxLeavesBeforeStop > 0 && this.ConsecutiveLeaves >= this.cfg.MaxLeavesBeforeStop)
        {
            this.Notify($"连续退本 {this.ConsecutiveLeaves} 次，达到上限，停止。");
            this.Stop("连续退本达到上限");
            return;
        }

        this.EnterCooldown("已退出不支持的副本，准备重排");
    }

    private void EnterCooldown(string status)
    {
        this.CooldownUntil = DateTime.UtcNow.AddSeconds(Math.Max(0, this.cfg.RequeueDelaySec));
        this.SetState(State.Cooldown, status);
    }

    private void UpdateCooldown()
    {
        if (InDuty)
        {
            this.OnEnteredDuty();
            return;
        }

        if (DateTime.UtcNow < this.CooldownUntil)
            return;

        this.BeginQueueing();
    }

    private void UpdateRunning()
    {
        if (!InDuty)
        {
            if (this.dutyCompleted)
                this.cfg.CompletedCount++;

            this.ConsecutiveLeaves   = 0;
            this.lastLeaveWasAbandon = false;
            this.completedAt         = null;
            this.EnterCooldown("副本结束，准备重排");
            return;
        }

        if (this.dutyCompleted)
        {
            this.completedAt ??= DateTime.UtcNow;

            // 打完了还站在里面（AutoDuty 的 AutoExitDuty 关着），替用户退本
            if ((DateTime.UtcNow - this.completedAt.Value).TotalSeconds > 15 && !this.exit.Running)
                this.exit.Start();

            this.exit.Tick();
            return;
        }

        // AutoDuty 已经停了但人还在本里，超过 2 分钟就退本重排
        bool adStopped = this.ipc.IsStopped(out bool ok);
        if (ok && adStopped)
        {
            this.completedAt ??= DateTime.UtcNow;

            if ((DateTime.UtcNow - this.completedAt.Value).TotalSeconds > 120 && !this.exit.Running)
            {
                this.Notify("AutoDuty 已经停下但还在本里，退出这个副本重排。");
                this.exit.Start();
            }

            this.exit.Tick();
        }
        else
        {
            this.completedAt = null;
        }
    }

    private void UpdateKeeping()
    {
        if (InDuty)
        {
            if (this.dutyCompleted)
                this.completedAt ??= DateTime.UtcNow;
            return;
        }

        if (this.dutyCompleted)
            this.cfg.CompletedCount++;

        this.ConsecutiveLeaves   = 0;
        this.lastLeaveWasAbandon = false;
        this.EnterCooldown("离开副本，准备重排");
    }

    internal string StateText =>
        this.State switch
        {
            State.Stopped  => "已停止",
            State.Queueing => "排队中",
            State.Judging  => "判定中",
            State.Grace    => "即将退出",
            State.Leaving  => "退本中",
            State.Cooldown => "冷却中",
            State.Running  => "攻略中",
            State.Keeping  => "挂机 / 手动",
            _              => this.State.ToString(),
        };
}
