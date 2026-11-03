namespace MentorRoulette.Sync;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// 组队同步：轮询房间状态 → 按角色自动排队 / 退本 / 上报，队长端负责开排、中止、下一轮。
/// </summary>
internal sealed class SyncCoordinator
{
    private readonly Config         cfg;
    private readonly AutoDutyIpc    ipc;
    private readonly SyncClient     client = new();
    private readonly ExitDuty       exit   = new();

    private CancellationTokenSource? cts;
    private RoomState? pending;
    private RoomState? room;

    private long appliedSeq;
    private bool commandsInitialized;
    private int  appliedRound;
    private bool hasAssignment;

    private string myRole      = "none";
    private uint   myTarget    = 0;   // 目标 territory
    private uint   myCfc       = 0;
    private DateTime myStartAt = DateTime.MinValue;

    private DateTime lastReport   = DateTime.MinValue;
    private DateTime lastHostTick = DateTime.MinValue;
    private DateTime lastSignalAt = DateTime.MinValue;
    private DateTime lastQueueAttempt = DateTime.MinValue;
    private DateTime roundEndedAt = DateTime.MinValue;
    private bool     pendingStart;
    private bool     roundClosed  = false;

    private readonly System.Collections.Generic.Queue<string> pendingCommands = new();
    private DateTime nextCommandAt = DateTime.MinValue;

    private bool   inDuty;
    private uint   enteredTerritory;
    private bool   dutyCompleted;
    private bool   postEntryDone;
    private bool   queuedByUs;
    private bool   leftEarly;
    private DateTime penaltyUntil = DateTime.MinValue;

    internal SyncCoordinator(Config cfg, AutoDutyIpc ipc)
    {
        this.cfg = cfg;
        this.ipc = ipc;
    }

    internal RoomState? Room => this.room;

    /// <summary>已点开始，正在等全员就绪。</summary>
    internal bool PendingStart => this.pendingStart;
    internal string Status { get; private set; } = "未连接";
    internal bool   Connected => this.client.Connected && this.room != null;
    internal string LastError => this.client.LastError;
    internal DateTime PenaltyUntil => this.penaltyUntil;

    internal bool IsHost => this.cfg.SyncIsHost;

    /// <summary>当前游戏角色名，作为同步身份。</summary>
    internal string Identity => Service.Objects.LocalPlayer?.Name.TextValue ?? string.Empty;

    /// <summary>配置文件里的地址优先，没有就用内置默认值。</summary>
    private string ServerUrl =>
        string.IsNullOrWhiteSpace(this.cfg.SyncServerUrl) ? SyncDefaults.ServerUrl : this.cfg.SyncServerUrl;

    internal string MyRole => this.myRole;

    // ---------- 连接 ----------

    internal async Task<(bool ok, string msg)> CreateRoomAsync()
    {
        this.SyncIdentity();

        if (string.IsNullOrWhiteSpace(this.cfg.SyncClientId))
            return (false, "还没登录角色");

        this.client.SetServer(this.ServerUrl);
        CreateRoomResponse? res = await this.client.CreateRoomAsync(this.cfg.SyncClientId).ConfigureAwait(false);
        if (res == null || string.IsNullOrEmpty(res.Code))
            return (false, this.client.LastError.Length > 0 ? this.client.LastError : "创建失败");

        this.cfg.SyncRoomCode  = res.Code;
        this.cfg.SyncHostToken = res.HostToken;
        this.cfg.SyncIsHost    = true;
        this.cfg.Save();

        await this.client.JoinAsync(res.Code, this.cfg.SyncClientId, this.cfg.SyncClientId).ConfigureAwait(false);
        this.StartLoop();
        return (true, $"房间 {res.Code}");
    }

    internal async Task<(bool ok, string msg)> JoinRoomAsync(string code)
    {
        this.SyncIdentity();

        if (string.IsNullOrWhiteSpace(this.cfg.SyncClientId))
            return (false, "还没登录角色");

        this.client.SetServer(this.ServerUrl);
        RoomState? st = await this.client.JoinAsync(code, this.cfg.SyncClientId, this.cfg.SyncClientId)
                                 .ConfigureAwait(false);
        if (st == null)
            return (false, this.client.LastError.Length > 0 ? this.client.LastError : "加入失败");

        this.cfg.SyncRoomCode = code;
        this.cfg.SyncIsHost   = false;
        this.cfg.Save();

        this.room = st;
        this.StartLoop();
        return (true, $"已加入 {code}");
    }

    internal void Disconnect()
    {
        this.cts?.Cancel();
        this.cts?.Dispose();
        this.cts = null;

        if (this.cfg.SyncRoomCode.Length > 0)
            _ = this.client.LeaveAsync(this.cfg.SyncRoomCode, this.cfg.SyncClientId);

        this.room = null;
        this.Status = "未连接";
    }

    private void StartLoop()
    {
        this.cts?.Cancel();
        this.cts = new CancellationTokenSource();

        CancellationToken token = this.cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                if (this.cfg.SyncRoomCode.Length > 0)
                {
                    RoomState? st = await this.client.PollAsync(this.cfg.SyncRoomCode, this.cfg.SyncClientId)
                                                     .ConfigureAwait(false);
                    if (st != null)
                        Interlocked.Exchange(ref this.pending, st);
                }

                try
                {
                    await Task.Delay(2000, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }, token);
    }

    // ---------- 每帧 ----------

    internal void Tick()
    {
        if (this.cfg.LaunchMode != LaunchMode.Group)
        {
            this.Status = "单人模式";
            return;
        }

        this.SyncIdentity();

        RoomState? next = Interlocked.Exchange(ref this.pending, null);
        if (next != null)
            this.ApplyState(next);

        if (this.room == null)
        {
            this.Status = "未连接";
            return;
        }

        // 匹配弹出框：只要连上房间就无条件确认（队长/队友、任何阶段都一样），避免漏确认被踢出队列
        QueueController.AcceptPop();

        this.TrackDutyState();
        this.EvaluateQueue();
        this.UpdateStatus();

        if ((DateTime.UtcNow - this.lastReport).TotalMilliseconds > 2000)
        {
            this.lastReport = DateTime.UtcNow;
            this.SendReport();
        }

        this.FlushCommands();

        if (this.IsHost && (DateTime.UtcNow - this.lastHostTick).TotalMilliseconds > 1000)
        {
            this.lastHostTick = DateTime.UtcNow;
            this.HostTick();
        }
    }

    /// <summary>把身份同步成当前角色；换角色时自动用新身份重新加入房间。</summary>
    private void SyncIdentity()
    {
        string id = this.Identity;
        if (string.IsNullOrWhiteSpace(id) || id == this.cfg.SyncClientId)
            return;

        bool hadRoom = !string.IsNullOrEmpty(this.cfg.SyncRoomCode) && this.room != null;

        Service.Log.Info($"[Sync] identity -> {id}");
        this.cfg.SyncClientId = id;
        this.cfg.Save();

        if (hadRoom)
            _ = this.client.JoinAsync(this.cfg.SyncRoomCode, id, id);
    }

    private void ApplyState(RoomState st)
    {
        this.room = st;

        if (st.Round != this.appliedRound)
        {
            this.appliedRound = st.Round;
            this.postEntryDone = false;
            this.leftEarly     = false;
        }

        // 分配
        this.hasAssignment = st.Assignments.TryGetValue(this.cfg.SyncClientId, out Assignment? a);
        if (this.hasAssignment && a != null)
        {
            this.myRole   = a.Role;
            this.myTarget = a.Territory;
            this.myCfc    = a.Cfc;
        }
        else
        {
            this.myRole = "none";
        }

        // 命令（插件刚加载时跳过 2 分钟前的旧命令，避免重放旧的 start）
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (Command cmd in st.Commands.Where(c => c.Seq > this.appliedSeq).OrderBy(c => c.Seq))
        {
            this.appliedSeq = cmd.Seq;

            if (!this.commandsInitialized && cmd.At < nowMs - 120_000)
                continue;

            this.commandsInitialized = true;
            this.ApplyCommand(cmd);
        }

        this.UpdateStartTime();
    }

    private void ApplyCommand(Command cmd)
    {
        switch (cmd.Type)
        {
            case "start":
                this.myStartAt     = DateTimeOffset.FromUnixTimeMilliseconds(this.room!.StartAt).UtcDateTime;
                this.queuedByUs    = false;
                this.postEntryDone = false;
                this.leftEarly     = false;
                DiagLog.Add($"收到开始信号，{this.myStartAt:HH:mm:ss} 排队");
                Service.Log.Info($"[Sync] start received, phase={this.room.Phase}, startAt={this.myStartAt:HH:mm:ss}");
                break;

            case "abort":
            case "stop":
            case "reset":
                this.CancelMyQueue();
                this.queuedByUs = false;
                break;

            case "next":
                this.queuedByUs    = false;
                this.postEntryDone = false;
                this.myStartAt     = DateTime.MinValue;
                break;

            case "leave":
                if (cmd.Target == null || cmd.Target == this.cfg.SyncClientId)
                {
                    if (this.inDuty && !this.exit.Running)
                        this.exit.Start(this.cfg.ForceLeaveDuty);
                }

                break;
        }
    }

    private void UpdateStartTime()
    {
        if (this.room == null || this.room.Phase != "queueing")
        {
            this.myStartAt = DateTime.MinValue;
            return;
        }

        int stagger = this.myRole == "party" ? this.room.Stagger.Party : this.room.Stagger.Roulette;
        this.myStartAt = DateTimeOffset.FromUnixTimeMilliseconds(this.room.StartAt + stagger).UtcDateTime;
    }

    // ---------- 排队 ----------

    private DateTime EffectivePenalty()
    {
        if (this.room == null)
            return this.penaltyUntil;

        if (this.room.Options.PenaltyMode == "leaver")
            return this.penaltyUntil;

        // 全员等待：取所有在线端里最晚的惩罚时间
        long max = this.room.Clients.Values
                       .Where(c => c.Online && c.Report != null)
                       .Select(c => c.Report!.PenaltyUntil)
                       .DefaultIfEmpty(0)
                       .Max();

        return max > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(max).UtcDateTime : DateTime.MinValue;
    }

    private void EvaluateQueue()
    {
        if (this.room == null)
            return;

        bool queueingPhase = this.room.Phase == "queueing";

        if (!queueingPhase)
        {
            if (this.queuedByUs && QueueController.InQueue)
                this.CancelMyQueue();

            return;
        }

        if (this.myRole == "none" || !this.hasAssignment)
            return;

        DateTime waitUntil = this.EffectivePenalty();
        if (DateTime.UtcNow < waitUntil)
        {
            if (this.queuedByUs && QueueController.InQueue)
                this.CancelMyQueue();

            return;
        }

        if (DateTime.UtcNow < this.myStartAt)
            return;

        if (this.inDuty)
            return;

        // 队伍里非队长不主动排队，跟着队长进队列
        if (this.myRole == "party" && QueueController.PartySize > 0 && !QueueController.IsPartyLeader)
        {
            if (QueueController.InQueue)
                this.queuedByUs = true;

            return;
        }

        if (QueueController.InQueue)
        {
            this.queuedByUs = true;
            return;
        }

        if (this.queuedByUs)
        {
            // 发过请求但还没进队列 → 2.5 秒后重试（之前是 10 秒，第二个端会明显晚开始）
            if ((DateTime.UtcNow - this.lastQueueAttempt).TotalSeconds < 2.5)
                return;

            this.queuedByUs = false;
            DiagLog.Add("排队没生效，重试");
        }

        this.lastQueueAttempt = DateTime.UtcNow;
        this.queuedByUs       = true;

        uint rouletteId = this.room.Assignments[this.cfg.SyncClientId].RouletteId;
        bool ok = this.myRole == "party"
                      ? QueueController.QueueDuty(this.myCfc)
                      : QueueController.QueueRoulette(rouletteId);

        DiagLog.Add(this.myRole == "party"
                        ? $"开始排队：副本 {this.myCfc}（{ok}）"
                        : $"开始排队：随机 {rouletteId}（{ok}）");
    }

    private void CancelMyQueue()
    {
        if (!QueueController.InQueue)
            return;

        Service.Log.Info("[Sync] cancel queue");
        QueueController.CancelQueue();
    }

    // ---------- 进本 / 退本 ----------

    private void TrackDutyState()
    {
        uint territory = Service.ClientState.TerritoryType;
        bool nowInDuty = territory != 0 && (DutyCatalog.IsKnownDuty(territory) || Service.DutyState.IsDutyStarted);

        if (nowInDuty && !this.inDuty)
        {
            this.inDuty           = true;
            this.enteredTerritory = territory;
            this.dutyCompleted    = false;
            this.postEntryDone    = false;
            this.queuedByUs       = false;
            DiagLog.Add($"进入副本 {DutyCatalog.Get(territory)?.Name ?? territory.ToString()} ({territory})");
            Service.Log.Info($"[Sync] entered duty {territory}");
            this.RunPostEntry(territory);
        }
        else if (!nowInDuty && this.inDuty)
        {
            this.inDuty = false;

            if (!this.dutyCompleted && this.myRole == "roulette")
            {
                this.penaltyUntil = DateTime.UtcNow.AddMinutes(30);
                this.leftEarly    = true;
                Service.Log.Info($"[Sync] left duty early, penalty until {this.penaltyUntil:HH:mm:ss}");
            }

            this.enteredTerritory = 0;
        }

        // 导随端进错本 → 自动退
        if (this.inDuty && this.myRole == "roulette" && this.myTarget != 0 &&
            this.enteredTerritory != 0 && this.enteredTerritory != this.myTarget && !this.exit.Running)
        {
            DiagLog.Add($"进错副本 {this.enteredTerritory}（目标 {this.myTarget}），退本");
            Service.Log.Info($"[Sync] wrong duty {this.enteredTerritory} != target {this.myTarget}, leaving");
            this.exit.Start(this.cfg.ForceLeaveDuty);
        }

        if (this.exit.Running)
        {
            this.exit.Tick();
            if (!this.exit.Running && !this.inDuty)
                this.queuedByUs = false;
        }
    }

    private void RunPostEntry(uint territory)
    {
        if (this.postEntryDone || this.room == null)
            return;

        // 只有进对了本才执行（没设目标就都算对）
        if (this.myTarget != 0 && territory != this.myTarget)
            return;

        this.postEntryDone = true;
        PostEntry post = this.room.Options.PostEntry;

        if (post.Mode is "command" or "both" || post.Commands.Length > 0 || post.Command.Length > 0)
        {
            string raw = post.Commands.Length > 0 ? post.Commands : post.Command;

            foreach (string line in raw.Split('\n'))
            {
                string cmd = line.Trim();
                if (cmd.Length == 0 || cmd.StartsWith('#'))
                    continue;

                if (!cmd.StartsWith('/'))
                    cmd = "/" + cmd;

                this.pendingCommands.Enqueue(cmd);
            }

            if (this.pendingCommands.Count > 0)
                Service.Log.Info($"[Sync] post-entry: {this.pendingCommands.Count} 条命令排队");
        }

        if (post.Mode is "autoduty" or "both")
        {
            Service.Log.Info($"[Sync] post-entry AutoDuty.Run({territory})");
            this.ipc.Run(territory, 1, this.cfg.BareMode);
        }
    }

    private void FlushCommands()
    {
        if (this.pendingCommands.Count == 0 || DateTime.UtcNow < this.nextCommandAt)
            return;

        string cmd = this.pendingCommands.Dequeue();
        this.nextCommandAt = DateTime.UtcNow.AddMilliseconds(600);
        Service.Log.Info($"[Sync] send command: {cmd}");
        Service.Commands.ProcessCommand(cmd);
    }

    internal void OnDutyCompleted() => this.dutyCompleted = true;

    // ---------- 上报 ----------

    private void SendReport()
    {
        if (this.room == null || this.cfg.SyncRoomCode.Length == 0)
            return;

        bool busy = Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
                    Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent];

        ClientReport report = new()
        {
            State        = this.inDuty ? "in_duty"
                                : QueueController.InQueue ? "queued"
                                : busy ? "busy"
                                : "idle",
            Role         = this.myRole,
            Territory    = this.enteredTerritory,
            Target       = this.myTarget,
            Round        = this.room.Round,
            Party        = QueueController.PartyNames().ToList(),
            PenaltyUntil = this.penaltyUntil > DateTime.UtcNow
                               ? new DateTimeOffset(this.penaltyUntil).ToUnixTimeMilliseconds()
                               : 0,
            QueueStartAt = this.queuedByUs ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0,
        };

        _ = this.client.ReportAsync(this.cfg.SyncRoomCode, this.cfg.SyncClientId, report);
    }

    // ---------- 队长逻辑 ----------

    private void HostTick()
    {
        if (this.room == null || !this.cfg.SyncIsHost)
            return;

        if ((DateTime.UtcNow - this.lastSignalAt).TotalSeconds < 3)
            return;

        RoomOptions opt = this.room.Options;

        // 等待全员就绪再开排
        if (this.pendingStart)
        {
            if (this.room.Phase == "queueing")
            {
                this.pendingStart = false;
            }
            else if (this.AllMembersReady(out _) && DateTime.UtcNow >= this.EffectivePenalty())
            {
                this.pendingStart = false;
                this.SendStart();
                return;
            }
            else
            {
                return;
            }
        }

        // 队长端的判定用本地配置，改完立刻生效，不用等下一轮下发
        int  maxRounds   = this.cfg.MaxRounds;      // 0 = 不限
        int  intervalSec = this.cfg.RoundIntervalSec;
        bool autoLoop    = this.cfg.AutoLoop;
        int  successMin  = this.cfg.SuccessMin;

        if (this.room.Phase == "queueing")
        {
            if (this.roundClosed)
                return;

            int okCount = this.room.Clients.Values.Count(c =>
                              c.Online && c.Report is { Role: "roulette", State: "in_duty" } &&
                              c.Report.Territory != 0 && c.Report.Territory == c.Report.Target);

            bool anyoneInDuty = this.room.Clients.Values.Any(c => c.Online && c.Report is { State: "in_duty" });

            long maxPenaltyMs = this.room.Clients.Values
                                    .Where(c => c.Online && c.Report != null)
                                    .Select(c => c.Report!.PenaltyUntil)
                                    .DefaultIfEmpty(0).Max();

            // 1) 有人进错本退了（出现惩罚），且场上已经没人在本里 → 本轮判失败，收尾
            if (maxPenaltyMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() && !anyoneInDuty)
            {
                Service.Log.Info("[Sync] round failed (someone left early), aborting");
                this.CloseRound("abort");
                return;
            }

            // 2) 成功
            if (okCount >= Math.Max(1, successMin))
            {
                DiagLog.Add($"第 {this.room.Round} 轮成功");
                Service.Log.Info("[Sync] round success");
                this.CloseRound("next");
                return;
            }

            // 3) 超时且没有任何进展
            DateTime startAt = DateTimeOffset.FromUnixTimeMilliseconds(this.room.StartAt).UtcDateTime;
            bool timeout = DateTime.UtcNow > startAt.AddSeconds(opt.QueueTimeoutSec);
            bool nothingHappening = this.room.Clients.Values.All(c => c.Report is null or { State: "idle" or "queued" });

            if (timeout && nothingHappening)
            {
                Service.Log.Info("[Sync] queue timeout, aborting round");
                this.CloseRound("abort");
            }

            return;
        }

        // 空闲：自动下一轮
        if (this.room.Phase != "idle" || !this.roundClosed)
            return;

        if (!autoLoop)
            return;

        if (maxRounds > 0 && this.room.Round > maxRounds)
        {
            if ((DateTime.UtcNow - this.lastSignalAt).TotalSeconds > 30)
            {
                this.lastSignalAt = DateTime.UtcNow;
                Service.Log.Info($"[Sync] 已到轮次上限 {maxRounds}，停止自动循环");
                DiagLog.Add($"已到轮次上限 {maxRounds}，停止自动循环");
            }

            return;
        }

        DateTime earliest = this.roundEndedAt.AddSeconds(Math.Max(0, intervalSec));
        DateTime penalty  = this.EffectivePenalty();

        if (penalty > earliest)
            earliest = penalty;

        if (DateTime.UtcNow < earliest)
            return;

        bool allReady = this.room.Clients.Values.Where(c => c.Online).All(c => c.Report is { State: "idle" });
        if (!allReady)
            return;

        // 注意：本轮结束时已经发过 next（轮次 +1），这里只发 start。
        // 之前多发一次 next，导致每打一轮轮次 +2，20 轮上限实际只能打 10 轮。
        Service.Log.Info($"[Sync] auto loop: start round {this.room.Round}");
        DiagLog.Add($"自动开始第 {this.room.Round} 轮");
        this.lastSignalAt = DateTime.UtcNow;
        this.roundClosed  = false;

        string code  = this.cfg.SyncRoomCode;
        string token = this.cfg.SyncHostToken;

        // 先下发配置（轮次上限/间隔可能刚改过），再开排，保证顺序
        _ = Task.Run(async () =>
        {
            await this.client.ConfigAsync(code, token, this.BuildAssignments(), this.BuildOptions(), this.cfg.SyncStagger)
                      .ConfigureAwait(false);
            await this.client.SignalAsync(code, token, "start").ConfigureAwait(false);
        });
    }

    private void CloseRound(string signal)
    {
        this.lastSignalAt = DateTime.UtcNow;
        this.roundClosed  = true;
        this.roundEndedAt = DateTime.UtcNow;
        _ = this.client.SignalAsync(this.cfg.SyncRoomCode, this.cfg.SyncHostToken, signal);
    }

    // ---------- 状态 ----------

    private void UpdateStatus()
    {
        if (this.room == null)
        {
            this.Status = "未连接";
            return;
        }

        DateTime penalty = this.EffectivePenalty();
        string phase = this.room.Phase switch
        {
            "queueing" => "排队中",
            "armed"    => "已下发配置",
            "idle"     => "待机",
            _          => this.room.Phase,
        };

        this.Status = DateTime.UtcNow < penalty
                          ? $"{phase} · 惩罚等待 {(penalty - DateTime.UtcNow):mm\\:ss}"
                          : $"{phase} · 第 {this.room.Round} 轮 · 我：{this.myRole}";
    }

    // ---------- 队长操作 ----------

    internal void PushConfig()
    {
        if (!this.cfg.SyncIsHost)
            return;

        _ = this.client.ConfigAsync(this.cfg.SyncRoomCode, this.cfg.SyncHostToken,
                                    this.BuildAssignments(), this.BuildOptions(), this.cfg.SyncStagger);
    }

    /// <summary>把共享配置打包成房间选项。</summary>
    private RoomOptions BuildOptions() => new()
    {
        PenaltyMode      = this.cfg.PenaltyMode,
        SuccessMin       = this.cfg.SuccessMin,
        QueueTimeoutSec  = this.cfg.SyncOptions.QueueTimeoutSec,
        AutoLoop         = this.cfg.AutoLoop,
        MaxRounds        = this.cfg.MaxRounds,
        RoundIntervalSec = this.cfg.RoundIntervalSec,
        PostEntry        = this.cfg.PostEntry,
    };

    /// <summary>所有「组队排本」成员都用同一个副本（排队目标里的「使用中」）。</summary>
    private Dictionary<string, Assignment> BuildAssignments()
    {
        uint territory = this.cfg.PartyDutyTerritory;
        uint cfc       = DutyCatalog.Get(territory)?.CfcId ?? 0;

        Dictionary<string, Assignment> result = [];

        foreach ((string id, Assignment a) in this.cfg.SyncAssignments)
        {
            result[id] = new Assignment
            {
                Role       = a.Role,
                PartyId    = a.PartyId,
                Territory  = territory,
                Cfc        = cfc,
                RouletteId = this.cfg.RouletteId,
            };
        }

        return result;
    }

    /// <summary>所有在线成员都处于空闲（不在本里 / 不在队列 / 不在过场）才允许开排。</summary>
    private bool AllMembersReady(out string waiting)
    {
        waiting = string.Empty;

        if (this.room == null)
            return false;

        List<string> notReady = [];

        foreach (ClientBox c in this.room.Clients.Values)
        {
            if (!c.Online)
                continue;

            string state = c.Report?.State ?? "unknown";

            if (state != "idle")
                notReady.Add($"{c.Id}({state})");
        }

        waiting = string.Join("、", notReady);
        return notReady.Count == 0;
    }

    internal void StartRound()
    {
        if (this.room == null || !this.cfg.SyncIsHost)
            return;

        if (!this.AllMembersReady(out string waiting))
        {
            this.pendingStart = true;
            DiagLog.Add($"等待成员就绪：{waiting}");
            Service.Log.Info($"[Sync] start pending, waiting for: {waiting}");
            return;
        }

        this.pendingStart = false;
        this.SendStart();
    }

    private void SendStart()
    {
        this.roundClosed = false;

        string code  = this.cfg.SyncRoomCode;
        string token = this.cfg.SyncHostToken;

        _ = Task.Run(async () =>
        {
            await this.client.ConfigAsync(code, token, this.BuildAssignments(), this.BuildOptions(), this.cfg.SyncStagger)
                      .ConfigureAwait(false);
            await this.client.SignalAsync(code, token, "start").ConfigureAwait(false);
        });

        this.lastSignalAt = DateTime.UtcNow;
        DiagLog.Add($"开始第 {this.room?.Round} 轮");
        Service.Log.Info($"[Sync] start round {this.room?.Round}");
    }

    internal void Abort()
    {
        if (this.room == null || !this.cfg.SyncIsHost)
            return;

        this.pendingStart = false;
        this.CancelMyQueue();
        this.queuedByUs = false;
        DiagLog.Add("中止本轮");
        _ = this.client.SignalAsync(this.cfg.SyncRoomCode, this.cfg.SyncHostToken, "abort");
    }

    internal void NextRound()
    {
        if (this.room == null || !this.cfg.SyncIsHost)
            return;

        _ = this.client.SignalAsync(this.cfg.SyncRoomCode, this.cfg.SyncHostToken, "next");
    }
}
