namespace MentorRoulette;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

internal sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string search = string.Empty;

    internal ConfigWindow(Plugin plugin) : base("导随助手###MentorRouletteConfig")
    {
        this.plugin = plugin;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 380),
            MaximumSize = new Vector2(1000, 1000),
        };

        this.Size           = new Vector2(580, 640);
        this.SizeCondition  = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    private Config Config => this.plugin.Config;

    public override void Draw()
    {
        Manager manager = this.plugin.Manager;
        Config  cfg     = this.Config;

        ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f), "导随助手");
        ImGui.SameLine();
        ImGui.TextDisabled($"{manager.StateText}");
        ImGui.TextWrapped(manager.Status);

        ImGui.TextDisabled($"已排 {cfg.QueuedCount} / 进本 {cfg.JoinedCount} / 完成 {cfg.CompletedCount} / 退出 {cfg.LeftCount} / 连续退本 {manager.ConsecutiveLeaves}");

        if (!this.plugin.Ipc.Available)
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "没有检测到 AutoDuty：请先安装并启用 AutoDuty。");

        bool enabled = cfg.Enabled;
        if (ImGui.Checkbox("启用导随自动排队", ref enabled))
        {
            if (enabled)
                manager.Start();
            else
                manager.Stop("界面关闭");
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(cfg.Enabled))
            if (ImGui.Button("开始"))
                manager.Start();

        ImGui.SameLine();

        using (ImRaii.Disabled(!cfg.Enabled))
            if (ImGui.Button("停止"))
                manager.Stop("界面停止");

        ImGui.SameLine();

        if (ImGui.Button("组队同步…"))
            this.plugin.SyncWindow.IsOpen = true;

        if (manager.State == State.Grace)
        {
            ImGui.SameLine();
            if (ImGui.Button("取消退出（这个本我自己打）"))
                manager.CancelLeave();
        }

        if (manager.State == State.Cooldown && manager.CooldownUntil > DateTime.UtcNow)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), $"等待至 {manager.CooldownUntil:HH:mm:ss}");

        ImGui.Separator();

        // 模式
        ImGui.Text("策略");

        bool all = cfg.Mode == RunMode.AllSupported;
        if (ImGui.RadioButton("AutoDuty 支持的副本", all))
        {
            cfg.Mode = RunMode.AllSupported;
            cfg.Save();
        }

        ImGui.TextDisabled("AutoDuty 有路径的副本自动攻略，没有的退出（或挂机）。");

        bool custom = cfg.Mode == RunMode.Selected;
        if (ImGui.RadioButton("完全自定义副本", custom))
        {
            cfg.Mode = RunMode.Selected;
            cfg.Save();
        }

        ImGui.TextDisabled("只保留下面勾选的副本，其余一律退出；默认不接管战斗。");

        using (ImRaii.Disabled(!custom))
        {
            bool startAd = cfg.StartAdInCustomMode;
            if (ImGui.Checkbox("自定义模式下也自动启动 AutoDuty", ref startAd))
            {
                cfg.StartAdInCustomMode = startAd;
                cfg.Save();
            }
        }

        ImGui.TextDisabled("不勾选时：保留下来的副本只是「不走」，战斗交给你自己。");

        // 不支持时
        ImGui.Text("排到不支持的副本时");
        bool leave = cfg.Unsupported == UnsupportedAction.LeaveDuty;
        if (ImGui.RadioButton("退出副本", leave))
        {
            cfg.Unsupported = UnsupportedAction.LeaveDuty;
            cfg.Save();
        }

        ImGui.SameLine();

        bool keep = cfg.Unsupported == UnsupportedAction.KeepDuty;
        if (ImGui.RadioButton("原地挂机", keep))
        {
            cfg.Unsupported = UnsupportedAction.KeepDuty;
            cfg.Save();
        }

        ImGui.TextDisabled("退本会触发 30 分钟放弃惩罚；挂机不吃惩罚但一轮很久。");

        ImGui.Separator();
        this.DrawWhitelist(cfg);

        ImGui.Separator();
        this.DrawSettings(cfg);
    }

    private void DrawWhitelist(Config cfg)
    {
        bool custom = cfg.Mode == RunMode.Selected;

        // 注意：### 后面的 ID 必须固定，否则数量一变折叠状态就丢（会自己收起）
        int enabledCount = DutyCatalog.SupportedEntries.Count(e => !cfg.Excluded.Contains(e.TerritoryId));

        string header = custom
                            ? $"自定义副本列表（已选 {cfg.Whitelist.Count}）###dutyList"
                            : $"AutoDuty 支持的副本（{enabledCount}/{DutyCatalog.SupportedCount}）###dutyList";

        if (!ImGui.CollapsingHeader(header))
            return;

        if (ImGui.Button("刷新支持状态"))
        {
            DutyCatalog.EnsureLoaded();
            DutyCatalog.RefreshSupport(this.plugin.Ipc);
        }

        if (custom)
        {
            ImGui.SameLine();

            if (ImGui.Button("全选支持 AD 的"))
            {
                cfg.Whitelist = DutyCatalog.SupportedEntries.Select(e => e.TerritoryId).ToList();
                cfg.Save();
            }

            ImGui.SameLine();

            if (ImGui.Button("清空"))
            {
                cfg.Whitelist.Clear();
                cfg.Save();
            }
        }
        else
        {
            ImGui.SameLine();

            if (ImGui.Button("全选"))
            {
                cfg.Excluded.Clear();
                cfg.Save();
            }

            ImGui.SameLine();

            if (ImGui.Button("全不选"))
            {
                cfg.Excluded = DutyCatalog.SupportedEntries.Select(e => e.TerritoryId).ToList();
                cfg.Save();
            }
        }

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##search", "搜索副本…", ref this.search, 128);

        using (ImRaii.Child("##dutyList", new Vector2(0, 200), true))
        {
            IEnumerable<DutyEntry> list = custom ? DutyCatalog.Entries : DutyCatalog.SupportedEntries;

            foreach (DutyEntry duty in list)
            {
                if (this.search.Length > 0 && !duty.Name.Contains(this.search, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool picked = custom
                                  ? cfg.Whitelist.Contains(duty.TerritoryId)
                                  : !cfg.Excluded.Contains(duty.TerritoryId);

                if (ImGui.Checkbox($"##duty{duty.TerritoryId}", ref picked))
                {
                    if (custom)
                    {
                        if (picked)
                        {
                            if (!cfg.Whitelist.Contains(duty.TerritoryId))
                                cfg.Whitelist.Add(duty.TerritoryId);
                        }
                        else
                        {
                            cfg.Whitelist.Remove(duty.TerritoryId);
                        }
                    }
                    else
                    {
                        if (picked)
                            cfg.Excluded.Remove(duty.TerritoryId);
                        else if (!cfg.Excluded.Contains(duty.TerritoryId))
                            cfg.Excluded.Add(duty.TerritoryId);
                    }

                    cfg.Save();
                }

                ImGui.SameLine();

                ImGui.TextUnformatted(duty.Name);
                ImGui.SameLine();
                ImGui.TextDisabled($"Lv.{duty.Level} ({duty.TerritoryId})");

                if (custom)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(duty.HasPath ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f),
                                      duty.HasPath ? "AD 支持" : "AD 无路径");
                }
                else if (cfg.Excluded.Contains(duty.TerritoryId))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("已排除");
                }
            }
        }
    }

    private void DrawSettings(Config cfg)
    {
        if (!ImGui.CollapsingHeader("行为设置"))
            return;

        int judge = cfg.JudgeDelayMs;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("进本后判定延迟 (ms)", ref judge))
        {
            cfg.JudgeDelayMs = Math.Clamp(judge, 0, 60000);
            cfg.Save();
        }

        int grace = cfg.GraceSeconds;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("退本前宽限 (秒)", ref grace))
        {
            cfg.GraceSeconds = Math.Clamp(grace, 0, 60);
            cfg.Save();
        }

        int combat = cfg.CombatTimeoutSec;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("等脱战超时 (秒)", ref combat))
        {
            cfg.CombatTimeoutSec = Math.Clamp(combat, 0, 300);
            cfg.Save();
        }

        int leaveTimeout = cfg.LeaveTimeoutSec;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("退本超时 (秒)", ref leaveTimeout))
        {
            cfg.LeaveTimeoutSec = Math.Clamp(leaveTimeout, 10, 600);
            cfg.Save();
        }

        int requeue = cfg.RequeueDelaySec;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("重排延迟 (秒)", ref requeue))
        {
            cfg.RequeueDelaySec = Math.Clamp(requeue, 0, 600);
            cfg.Save();
        }

        int maxLeaves = cfg.MaxLeavesBeforeStop;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("连续退本上限 (0 = 不限)", ref maxLeaves))
        {
            cfg.MaxLeavesBeforeStop = Math.Max(0, maxLeaves);
            cfg.Save();
        }

        bool guard = cfg.PenaltyGuard;
        if (ImGui.Checkbox("惩罚保护（检测到排本惩罚时暂停）", ref guard))
        {
            cfg.PenaltyGuard = guard;
            cfg.Save();
        }

        int penalty = cfg.PenaltyWaitMinutes;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("惩罚等待 (分钟)", ref penalty))
        {
            cfg.PenaltyWaitMinutes = Math.Clamp(penalty, 1, 120);
            cfg.Save();
        }

        bool bare = cfg.BareMode;
        if (ImGui.Checkbox("让 AutoDuty 用 BareMode 跑（跳过前置/轮次间/结束动作）", ref bare))
        {
            cfg.BareMode = bare;
            cfg.Save();
        }

        bool notify = cfg.Notify;
        if (ImGui.Checkbox("聊天窗口提示", ref notify))
        {
            cfg.Notify = notify;
            cfg.Save();
        }

        int roulette = (int)cfg.RouletteId;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("随机任务 ID（导师随机 = 9）", ref roulette))
        {
            cfg.RouletteId = (uint)Math.Clamp(roulette, 1, 100);
            cfg.Save();
        }
    }
}
