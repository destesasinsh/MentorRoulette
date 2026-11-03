#!/usr/bin/env node
/**
 * MentorRoulette 组队同步 —— 中继服务器
 *
 * 零依赖，只用 Node 内置模块。职责只有三件：
 *   1. 房间（组队码）管理
 *   2. 队长下发配置 / 命令信号
 *   3. 成员上报状态，聚合给队长
 *
 * 启动: node server.js         （默认端口 8787，可用 PORT 环境变量覆盖）
 */
'use strict';

const http = require('node:http');
const crypto = require('node:crypto');

const PORT = Number(process.env.PORT || 8787);
const CLIENT_TIMEOUT_MS = 30_000;   // 超过这个时间没上报就当掉线
const ROOM_IDLE_MS = 6 * 60 * 60_000; // 硬上限：房间存在 6 小时直接回收
const ROOM_EMPTY_MS = Number(process.env.ROOM_EMPTY_MS || 10 * 60_000); // 没有在线客户端超过这个时间就回收

/** @type {Map<string, Room>} */
const rooms = new Map();

function now() {
  return Date.now();
}

function newRoomCode() {
  const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  let code = '';
  for (let i = 0; i < 6; i++) code += alphabet[crypto.randomInt(alphabet.length)];
  return rooms.has(code) ? newRoomCode() : code;
}

function createRoom(name) {
  const code = newRoomCode();
  const room = {
    code,
    name: name || '',
    hostToken: crypto.randomBytes(16).toString('hex'),
    rev: 0,
    seq: 0,
    createdAt: now(),
    touchedAt: now(),
    activeAt: now(),
    phase: 'idle',
    round: 1,
    startAt: 0,
    stagger: { party: 0, roulette: 2000 },
    assignments: {},
    options: defaultOptions(),
    commands: [],
    clients: {},
  };
  rooms.set(code, room);
  return room;
}

function defaultOptions() {
  return {
    penaltyMode: 'all',        // all = 全员等 / leaver = 只有退本端等
    successMin: 1,             // 至少几个导随端进目标本算成功
    queueTimeoutSec: 300,      // 排队多久没匹配就放弃本轮
    autoLoop: false,           // 打完自动下一轮
    maxRounds: 0,
    postEntry: { mode: 'nothing', autoDuty: false, command: '' },
    allowLateJoin: true,
  };
}

function getRoom(code) {
  const room = rooms.get(String(code || '').toUpperCase());
  if (room) room.touchedAt = now();
  return room;
}

function bump(room) {
  room.rev += 1;
  room.touchedAt = now();
}

function addCommand(room, cmd) {
  room.seq += 1;
  room.commands.push({ seq: room.seq, at: now(), ...cmd });
  if (room.commands.length > 50) room.commands.splice(0, room.commands.length - 50);
  bump(room);
}

function publicState(room, sinceRev, sinceSeq) {
  const clients = {};
  for (const [id, c] of Object.entries(room.clients)) {
    const stale = now() - c.lastSeen > CLIENT_TIMEOUT_MS;
    clients[id] = {
      id,
      name: c.name,
      online: !stale,
      lastSeen: c.lastSeen,
      report: c.report || null,
    };
  }

  return {
    serverTime: now(),
    code: room.code,
    name: room.name,
    rev: room.rev,
    phase: room.phase,
    round: room.round,
    startAt: room.startAt,
    stagger: room.stagger,
    assignments: room.assignments,
    options: room.options,
    clients,
    commands: room.commands.filter((c) => c.seq > (sinceSeq || 0)),
    sinceRev: sinceRev || 0,
  };
}

function send(res, status, body) {
  const data = Buffer.from(JSON.stringify(body), 'utf8');
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': data.length,
    'access-control-allow-origin': '*',
  });
  res.end(data);
}

function readBody(req, limit = 256 * 1024) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    req.on('data', (c) => {
      size += c.length;
      if (size > limit) {
        reject(new Error('body too large'));
        req.destroy();
        return;
      }
      chunks.push(c);
    });
    req.on('end', () => {
      if (chunks.length === 0) return resolve({});
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')));
      } catch (e) {
        reject(new Error('invalid json'));
      }
    });
    req.on('error', reject);
  });
}

function isHost(room, token) {
  return Boolean(room && token && String(token) === room.hostToken);
}

function clientOf(room, id) {
  if (!room || !id) return null;
  if (!room.clients[id]) room.clients[id] = { id, name: id, lastSeen: 0, report: null };
  return room.clients[id];
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const parts = url.pathname.split('/').filter(Boolean);

  try {
    if (url.pathname === '/healthz') return send(res, 200, { ok: true, rooms: rooms.size });

    // POST /room/create
    if (req.method === 'POST' && parts[0] === 'room' && parts[1] === 'create') {
      const body = await readBody(req);
      const room = createRoom(body.name);
      return send(res, 200, { code: room.code, hostToken: room.hostToken, rev: room.rev });
    }

    if (parts[0] !== 'room' || !parts[1]) return send(res, 404, { error: 'not found' });
    const room = getRoom(parts[1]);
    const action = parts[2] || 'state';
    if (!room) return send(res, 404, { error: 'room not found' });

    // POST /room/:code/join
    if (req.method === 'POST' && action === 'join') {
      const body = await readBody(req);
      const id = String(body.clientId || '').slice(0, 64);
      if (!id) return send(res, 400, { error: 'clientId required' });
      const c = clientOf(room, id);
      c.name = String(body.name || id).slice(0, 64);
      c.lastSeen = now();
      bump(room);
      return send(res, 200, { ok: true, rev: room.rev, state: publicState(room, 0, 0) });
    }

    // POST /room/:code/leave
    if (req.method === 'POST' && action === 'leave') {
      const body = await readBody(req);
      const id = String(body.clientId || '');
      if (room.clients[id]) delete room.clients[id];
      bump(room);
      return send(res, 200, { ok: true });
    }

    // POST /room/:code/config   (host)
    if (req.method === 'POST' && action === 'config') {
      const body = await readBody(req);
      if (!isHost(room, body.hostToken)) return send(res, 403, { error: 'bad host token' });
      if (body.assignments && typeof body.assignments === 'object') room.assignments = body.assignments;
      if (body.options && typeof body.options === 'object') room.options = { ...room.options, ...body.options };
      if (body.stagger && typeof body.stagger === 'object') room.stagger = { ...room.stagger, ...body.stagger };
      room.phase = 'armed';
      addCommand(room, { type: 'config' });
      return send(res, 200, { ok: true, rev: room.rev });
    }

    // POST /room/:code/signal   (host)
    if (req.method === 'POST' && action === 'signal') {
      const body = await readBody(req);
      if (!isHost(room, body.hostToken)) return send(res, 403, { error: 'bad host token' });
      const type = String(body.type || '');
      const allowed = ['start', 'abort', 'leave', 'next', 'stop', 'reset'];
      if (!allowed.includes(type)) return send(res, 400, { error: 'unknown signal' });

      if (type === 'start') {
        room.phase = 'queueing';
        room.startAt = Number(body.startAt) || now() + 3000;
        if (body.stagger) room.stagger = { ...room.stagger, ...body.stagger };
      } else if (type === 'abort') {
        room.phase = 'idle';
        room.startAt = 0;
      } else if (type === 'next') {
        room.round += 1;
        room.phase = 'idle';
        room.startAt = 0;
      } else if (type === 'stop') {
        room.phase = 'idle';
        room.startAt = 0;
      } else if (type === 'reset') {
        room.round = 1;
        room.phase = 'idle';
        room.startAt = 0;
      }

      addCommand(room, {
        type,
        target: body.target ? String(body.target) : null,
        startAt: room.startAt,
        stagger: room.stagger,
        round: room.round,
      });
      return send(res, 200, { ok: true, rev: room.rev, seq: room.seq });
    }

    // POST /room/:code/report   (member)
    if (req.method === 'POST' && action === 'report') {
      const body = await readBody(req);
      const id = String(body.clientId || '');
      if (!id) return send(res, 400, { error: 'clientId required' });
      const c = clientOf(room, id);
      c.lastSeen = now();
      c.report = {
        state: String(body.state || 'idle'),
        role: String(body.role || ''),
        territory: Number(body.territory) || 0,
        target: Number(body.target) || 0,
        round: Number(body.round) || 0,
        party: Array.isArray(body.party) ? body.party.slice(0, 8) : [],
        penaltyUntil: Number(body.penaltyUntil) || 0,
        queueStartAt: Number(body.queueStartAt) || 0,
        at: now(),
      };
      // 有人上报进本/退本时推进 phase（队长端也会自己判断，这里只做粗粒度）
      if (body.phase && typeof body.phase === 'string') room.phase = body.phase;
      bump(room);
      return send(res, 200, { ok: true, rev: room.rev });
    }

    // GET /room/:code/state
    if (req.method === 'GET' && action === 'state') {
      const sinceRev = Number(url.searchParams.get('since') || 0);
      const sinceSeq = Number(url.searchParams.get('seq') || 0);
      const clientId = url.searchParams.get('clientId');
      if (clientId) {
        const c = clientOf(room, clientId);
        c.lastSeen = now();
      }
      return send(res, 200, publicState(room, sinceRev, sinceSeq));
    }

    return send(res, 404, { error: 'unknown action' });
  } catch (e) {
    return send(res, 400, { error: e.message });
  }
});

setInterval(() => {
  const t = now();
  for (const [code, room] of rooms) {
    const online = Object.values(room.clients)
      .some((c) => t - (c.lastSeen || 0) <= CLIENT_TIMEOUT_MS);

    if (online) {
      room.activeAt = t;
      continue;
    }

    const idle = t - (room.activeAt || room.createdAt);
    if (idle > ROOM_EMPTY_MS || t - room.createdAt > ROOM_IDLE_MS) rooms.delete(code);
  }
}, 60_000).unref();

server.listen(PORT, '0.0.0.0', () => {
  console.log(`[MentorRoulette] relay listening on http://0.0.0.0:${PORT}`);
});
