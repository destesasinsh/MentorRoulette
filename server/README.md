# MentorRoulette 中继服务器

零依赖 Node 服务，只做房间/信令/状态中转。

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
