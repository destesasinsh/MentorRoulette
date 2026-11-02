# 导随助手（MentorRoulette）

把 AutoDuty 里的「导随」功能单独拆出来的插件：**自动排导师随机任务，进本后判断这个副本 AutoDuty 支不支持——支持就交给 AutoDuty 自动跑，不支持就按配置退出重排。**

AutoDuty 本体不再负责排队和退本，只负责「按路径跑这一趟」。

## 依赖

- **AutoDuty**（必须安装并启用，本插件通过 IPC 调用它）
  - `AutoDuty.Run(territoryType, loops, bareMode)`
  - `AutoDuty.ContentHasPath(territoryType)`
  - `AutoDuty.Stop()` / `AutoDuty.IsStopped()`
- vnavmesh / BossMod / 循环插件（AutoDuty 本身的要求）

## 界面

只有一个窗口，顶部两个 tab：**设置** / **日志**。

设置页最上面选运行方式，两种方式共用下面的所有规则：

```
运行方式   (•) 组队同步   ( ) 单人导随

▾ 房间          队长建房 / 成员输码加入 / 复制房间号        （单人隐藏）
▾ 成员与分工     每个角色设成 导随 或 组队排本               （单人隐藏）
▾ 排队目标       随机任务 + 副本清单（左右穿梭）
▾ 进本后         无 / 宏 / AutoDuty / 两者
▾ 进错/不支持时   退本 或 挂机；惩罚全员等 或 仅本端等
▾ 循环          自动下一轮、每轮间隔、轮次上限、成功判定
▾ 其它          游戏内提示、统计
```

**排队目标**是唯一跟玩法相关的设置，两种模式共用：

- **随机任务**：排哪个随机任务（默认导师随机 9）。导随端 / 单人单开排这个。
- **副本清单**：左边待选，点一下加到右边。
  - 清单为空 → 放行全部 AutoDuty 支持的本
  - 清单非空 → **只放行清单里的本**（单人即白名单）
  - 组队端要排的是清单里标「组队端使用中」的那条（点「把选中的副本设为组队端副本」）

## 功能

- **进本后**：什么都不做 / 执行宏（一行一条，按顺序发）/ 启动 AutoDuty / 两者都做
- **进错或不在清单里**：退本（吃 30 分钟惩罚）或原地挂机
- **惩罚保护**：退本后 25 秒还没排上就判定处于放弃惩罚，等到惩罚结束再排
- **自动收尾**：副本打完后如果 AutoDuty 没退本，15 秒后替用户退出
- **组队同步**：队长下发分工、同步开排（服务器对齐时间 + 错开毫秒）、汇总各端状态、
  进错自动退、按设置等待惩罚、自动下一轮
- **统计**：已排 / 进本 / 完成 / 退出

## 组队同步

**前置**：中继服务器已内置（http://121.40.159.61/mr），开箱即用。
想自己搭看 [server/README.md](server/README.md)，然后改配置文件的 SyncServerUrl。

1. 队长「创建房间」拿 6 位码（可复制），其余端输码「加入房间」；身份就是当前游戏角色名
2. 队长在「成员与分工」里给每个端设成 `导随` 或 `组队排本`（组队排本的端在游戏内要同队）
3. 排队目标里选好副本 → 点「下发分工」
4. 队长点「开始本轮」：各端按服务器时间 + 错开同时排
5. 可选自动下一轮：每轮结束（成功 / 惩罚结束 / 超时）后等间隔再开

## 组队同步（多端）

多个客户端一起排，让导随端尽量被补进指定的简单副本。

**前置**：中继服务器已内置（http://121.40.159.61/mr），开箱即用，不需要配置。
想自己搭的话看 [server/README.md](server/README.md)，然后改配置文件的 SyncServerUrl（UI 里不显示地址）。

**步骤**

1. 每个端打开「组队同步」窗口（服务器地址内置，不用填；身份就是当前游戏角色名），
   队长点「创建房间」拿到 6 位组队码（可点「复制房间号」发出去），其余端填码「加入房间」
   > 副本用左右穿梭框选：左边待选副本（默认列出全部，含行会令；可勾「只看 AutoDuty 支持」过滤），
   > 点一下加到右边「已选副本」，再点「把选中的副本设为组队端副本」。

2. 队长给每个成员分配角色：
   - `导随` — 单人排导师随机（id 9）
   - `组队排本` — 你们在游戏里已经组好队，这里选同一个副本和一个相同的 partyId
3. 点「下发配置」→ 各端自动切到对应排队目标
4. 队长点「开始本轮」→ 服务器记录 `startAt`，各端按错开时间**同时**点排队
5. 队长可开「自动下一轮」：设置**每轮间隔**和**轮次上限**，一轮结束（成功 / 退本惩罚结束 / 超时）
   后自动等间隔时间再开下一轮，到达上限自动停
6. 各端自动上报状态，队长端汇总：
   - 导随端进错本 → 该端自动退本，按设置（全员 / 仅本端）等待惩罚
   - 全部进对 → 本轮成功，可「下一轮」或开自动循环
   - 5 分钟没人匹配 → 自动中止本轮

**进本后动作**可配：什么都不做 / 执行命令（宏）/ 启动 AutoDuty / 两者都做。

## 命令

- `/mentor` 打开设置窗口
- `/mentor solo` / `/mentor group` 切换运行方式并打开窗口
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
