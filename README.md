# 导随助手（MentorRoulette）

把 AutoDuty 里的「导随」功能单独拆出来的插件：**自动排导师随机任务，进本后判断这个副本 AutoDuty 支不支持——支持就交给 AutoDuty 自动跑，不支持就按配置退出重排。**

AutoDuty 本体不再负责排队和退本，只负责「按路径跑这一趟」。

## 依赖

- **AutoDuty**（必须安装并启用，本插件通过 IPC 调用它）
  - `AutoDuty.Run(territoryType, loops, bareMode)`
  - `AutoDuty.ContentHasPath(territoryType)`
  - `AutoDuty.Stop()` / `AutoDuty.IsStopped()`
- vnavmesh / BossMod / 循环插件（AutoDuty 本身的要求）

## 功能

- **两种策略**
  - **AutoDuty 支持的副本**：AutoDuty 有路径就自动攻略；没有的退出（或挂机）
  - **完全自定义副本**：只保留你勾选的副本，其余一律退出。**默认不启动 AutoDuty**（战斗交给你自己），
    需要它接管时再勾选「自定义模式下也自动启动 AutoDuty」；勾选后只对 AD 有路径的副本生效
- **不支持时**：退本（可选原地挂机，不吃放弃惩罚）
- **退本前宽限**：可配置秒数，期间点「取消退出」可以留下自己打
- **惩罚保护**：退本后 25 秒还没排上就判定处于放弃惩罚，等到惩罚结束再排
- **自动收尾**：副本打完后如果 AutoDuty 没退本，15 秒后替用户退出；AutoDuty 中途停了 2 分钟也会退本重排
- **统计**：已排 / 进本 / 完成 / 退出 / 连续退本

## 组队同步（多端）

多个客户端一起排，让导随端尽量被补进指定的简单副本。

**前置**：一台跑中继服务器的机器（见 [server/README.md](server/README.md)），所有端能访问到它。

**步骤**

1. 每个端在「组队同步」窗口填服务器地址，队长点「创建房间」拿到 6 位组队码，其余端填码「加入房间」
2. 队长给每个成员分配角色：
   - `导随` — 单人排导师随机（id 9）
   - `组队排本` — 你们在游戏里已经组好队，这里选同一个副本和一个相同的 partyId
3. 点「下发配置」→ 各端自动切到对应排队目标
4. 队长点「开始本轮」→ 服务器记录 `startAt`，各端按错开时间**同时**点排队
5. 各端自动上报状态，队长端汇总：
   - 导随端进错本 → 该端自动退本，按设置（全员 / 仅本端）等待惩罚
   - 全部进对 → 本轮成功，可「下一轮」或开自动循环
   - 5 分钟没人匹配 → 自动中止本轮

**进本后动作**可配：什么都不做 / 执行命令（宏）/ 启动 AutoDuty / 两者都做。

**命令**：`/mentor sync` 打开组队同步窗口。

## 命令

- `/mentor` 打开设置
- `/mentor start` / `stop` / `status` / `cancel`（取消本次退出）
- `/mentor dump` 把任务搜索器列表打到日志（排查排本问题用）

## 构建

```bash
# 国服
build-cn.cmd

# 通用
dotnet build MentorRoulette.csproj -c Release -p:DalamudLibPath="<Dalamud dev libs 目录>/"
```

产物：`bin/Release/MentorRoulette/latest.zip`（Dalamud SDK 已经打好包）

## 发布

推 `v*` tag 触发 CI：

```bash
git tag -a v0.0.0.2 -m "v0.0.0.2"
git push origin v0.0.0.2
```

CI 会构建、在 `destesasinsh/DalamudPlugins` 建 `MentorRoulette-v<ver>` 的 release、更新清单和 pluginmaster。

> ⚠️ 需要在**本仓库** Actions secrets 里配置 `DALAMUDPLUGINS_PAT`（有 DalamudPlugins 写权限的 PAT），否则发布会跳过、只留构建产物。
> `gh secret set DALAMUDPLUGINS_PAT --repo destesasinsh/MentorRoulette`

## 安装

把 `latest.zip` 解压到 `%APPDATA%\XIVLauncherCN\devPlugins\MentorRoulette\`，或在插件仓库里添加：

```
https://raw.githubusercontent.com/destesasinsh/DalamudPlugins/main/pluginmaster.json
```
