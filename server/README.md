# MentorRoulette 中继服务器

零依赖中继，只做房间/信令/状态中转。两份等价实现：`server.js`（Node，本地开发）和 `server.py`（Python 3.6+，生产部署，ECS 上只有 python3）。

## 当前部署（已上线）

```
ECS     i-bp14l55p4tmcczyalsp7 (cn-hangzhou, 121.40.159.61)
路径    /opt/mentor-roulette/server.py
服务    systemd: mentor-roulette.service  (127.0.0.1:8787, Restart=always)
反代    nginx: http://121.40.159.61/mr/  ->  127.0.0.1:8787/
```

部署脚本见 [mentor-roulette.service](mentor-roulette.service) 和 [patch_nginx.py](patch_nginx.py)。

## 启动

```bash
node server.js                 # 默认 8787
PORT=9000 node server.js       # 自定义端口
```

建议用 pm2 / systemd / docker 常驻。Docker 示例：

```dockerfile
FROM node:20-alpine
WORKDIR /app
COPY server.js .
EXPOSE 8787
CMD ["node", "server.js"]
```

## 房间回收

- 客户端 30 秒没上报 → 视为离线
- 房间 **10 分钟**没有任何在线客户端 → 自动删除（`ROOM_EMPTY_MS` 可调）
- 房间存在超过 6 小时 → 无条件删除

## 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | /room/create | 创建房间 → `{code, hostToken}` |
| POST | /room/:code/join | 成员加入 |
| POST | /room/:code/leave | 离开 |
| POST | /room/:code/config | 队长下发分配/选项（需 hostToken） |
| POST | /room/:code/signal | 队长发信号 start/abort/next/stop（需 hostToken） |
| POST | /room/:code/report | 成员上报状态 |
| GET | /room/:code/state | 拉取房间状态（含 commands） |
| GET | /healthz | 健康检查 |

## 状态示例

```json
{
  "code": "K7Q2M9",
  "rev": 12,
  "phase": "queueing",
  "round": 3,
  "startAt": 1793646591000,
  "stagger": { "party": 0, "roulette": 2000 },
  "assignments": {
    "c-1b33": { "role": "party", "partyId": "A", "territory": 1234, "cfc": 567 },
    "c-8f2a": { "role": "roulette", "rouletteId": 9 }
  },
  "clients": { },
  "commands": [ { "seq": 3, "type": "start", "startAt": 1793646591000 } ]
}
```

## 安全

- 只有 `hostToken` 能改配置/发信号，成员只能写自己的 report
- 不要裸奔公网，建议前面挂个反代加 basic auth，或用内网/组网（Tailscale 之类）
