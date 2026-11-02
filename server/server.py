#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
MentorRoulette 组队同步 —— 中继服务器（Python 版，仅用标准库，兼容 3.6+）

与 server.js 协议完全一致，用于没有 Node 的机器（例如只装了 python3 的 ECS）。

启动: python3 server.py            （默认 127.0.0.1:8787，环境变量 HOST/PORT 可覆盖）
"""
import json
import os
import random
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from socketserver import ThreadingMixIn
from urllib.parse import urlparse, parse_qs

HOST = os.environ.get('HOST', '127.0.0.1')
PORT = int(os.environ.get('PORT', '8787'))
CLIENT_TIMEOUT_MS = 30_000
ROOM_IDLE_MS = 6 * 60 * 60_000          # 硬上限：房间存在超过 6 小时直接回收
ROOM_EMPTY_MS = int(os.environ.get('ROOM_EMPTY_MS', str(10 * 60_000)))  # 没有在线客户端超过这个时间就回收
GC_INTERVAL_MS = int(os.environ.get('GC_INTERVAL_MS', '60000'))

rooms = {}
lock = threading.Lock()


def now_ms():
    return int(time.time() * 1000)


def default_options():
    return {
        'penaltyMode': 'all',
        'successMin': 1,
        'queueTimeoutSec': 300,
        'autoLoop': False,
        'maxRounds': 20,
        'roundIntervalSec': 10,
        'postEntry': {'mode': 'nothing', 'command': ''},
        'allowLateJoin': True,
    }


def new_code():
    alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'
    while True:
        code = ''.join(random.choice(alphabet) for _ in range(6))
        if code not in rooms:
            return code


def new_room(name):
    code = new_code()
    room = {
        'code': code,
        'name': name or '',
        'hostToken': os.urandom(16).hex(),
        'rev': 0,
        'seq': 0,
        'createdAt': now_ms(),
        'touchedAt': now_ms(),
        'activeAt': now_ms(),
        'phase': 'idle',
        'round': 1,
        'startAt': 0,
        'stagger': {'party': 0, 'roulette': 2000},
        'assignments': {},
        'options': default_options(),
        'commands': [],
        'clients': {},
    }
    rooms[code] = room
    return room


def get_room(code):
    if not code:
        return None
    room = rooms.get(str(code).upper())
    if room:
        room['touchedAt'] = now_ms()
    return room


def bump(room):
    room['rev'] += 1
    room['touchedAt'] = now_ms()


def add_command(room, cmd):
    room['seq'] += 1
    item = {'seq': room['seq'], 'at': now_ms()}
    item.update(cmd)
    room['commands'].append(item)
    if len(room['commands']) > 50:
        del room['commands'][:-50]
    bump(room)


def public_state(room):
    clients = {}
    t = now_ms()
    for cid, c in room['clients'].items():
        clients[cid] = {
            'id': cid,
            'name': c.get('name') or cid,
            'online': (t - c.get('lastSeen', 0)) <= CLIENT_TIMEOUT_MS,
            'lastSeen': c.get('lastSeen', 0),
            'report': c.get('report'),
        }
    return {
        'code': room['code'],
        'name': room['name'],
        'rev': room['rev'],
        'phase': room['phase'],
        'round': room['round'],
        'startAt': room['startAt'],
        'stagger': room['stagger'],
        'assignments': room['assignments'],
        'options': room['options'],
        'clients': clients,
        'commands': room['commands'],
    }


def client_of(room, cid):
    if not room or not cid:
        return None
    c = room['clients'].get(cid)
    if not c:
        c = {'id': cid, 'name': cid, 'lastSeen': 0, 'report': None}
        room['clients'][cid] = c
    return c


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, fmt, *args):
        pass

    def _send(self, status, payload):
        data = json.dumps(payload, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('content-type', 'application/json; charset=utf-8')
        self.send_header('content-length', str(len(data)))
        self.send_header('access-control-allow-origin', '*')
        self.end_headers()
        self.wfile.write(data)

    def _body(self):
        length = int(self.headers.get('content-length') or 0)
        if length <= 0:
            return {}
        raw = self.rfile.read(length)
        try:
            return json.loads(raw.decode('utf-8'))
        except Exception:
            return {}

    @staticmethod
    def _route(parts):
        """兼容 /room/<code>/<action> 和带前缀的 /mr/room/<code>/<action>。"""
        if 'room' not in parts:
            return None, None
        i = parts.index('room')
        if len(parts) < i + 3:
            return None, None
        return parts[i + 1], parts[i + 2]

    def do_GET(self):
        url = urlparse(self.path)
        parts = [p for p in url.path.split('/') if p]

        if parts and parts[-1] == 'healthz':
            return self._send(200, {'ok': True, 'rooms': len(rooms)})

        code, action = self._route(parts)
        if not code or action != 'state':
            return self._send(404, {'error': 'not found'})

        room = get_room(code)
        if not room:
            return self._send(404, {'error': 'room not found'})

        qs = parse_qs(url.query)
        cid = (qs.get('clientId') or [None])[0]

        with lock:
            if cid:
                c = client_of(room, cid)
                c['lastSeen'] = now_ms()
            return self._send(200, public_state(room))

    def do_POST(self):
        url = urlparse(self.path)
        parts = [p for p in url.path.split('/') if p]

        if parts[:2] == ['room', 'create']:
            body = self._body()
            with lock:
                room = new_room(body.get('name'))
                return self._send(200, {'code': room['code'], 'hostToken': room['hostToken'], 'rev': room['rev']})

        code, action = self._route(parts)
        if not code:
            return self._send(404, {'error': 'not found'})

        body = self._body()

        with lock:
            room = get_room(code)
            if not room:
                return self._send(404, {'error': 'room not found'})

            if action == 'join':
                cid = str(body.get('clientId') or '')[:64]
                if not cid:
                    return self._send(400, {'error': 'clientId required'})
                c = client_of(room, cid)
                c['name'] = str(body.get('name') or cid)[:64]
                c['lastSeen'] = now_ms()
                bump(room)
                return self._send(200, {'ok': True, 'rev': room['rev']})

            if action == 'leave':
                cid = str(body.get('clientId') or '')
                room['clients'].pop(cid, None)
                bump(room)
                return self._send(200, {'ok': True})

            if action == 'config':
                if body.get('hostToken') != room['hostToken']:
                    return self._send(403, {'error': 'bad host token'})
                if isinstance(body.get('assignments'), dict):
                    room['assignments'] = body['assignments']
                if isinstance(body.get('options'), dict):
                    room['options'].update(body['options'])
                if isinstance(body.get('stagger'), dict):
                    room['stagger'].update(body['stagger'])
                room['phase'] = 'armed'
                add_command(room, {'type': 'config'})
                return self._send(200, {'ok': True, 'rev': room['rev']})

            if action == 'signal':
                if body.get('hostToken') != room['hostToken']:
                    return self._send(403, {'error': 'bad host token'})
                stype = str(body.get('type') or '')
                if stype not in ('start', 'abort', 'leave', 'next', 'stop', 'reset'):
                    return self._send(400, {'error': 'unknown signal'})

                if stype == 'start':
                    room['phase'] = 'queueing'
                    room['startAt'] = int(body.get('startAt') or (now_ms() + 3000))
                    if isinstance(body.get('stagger'), dict):
                        room['stagger'].update(body['stagger'])
                elif stype == 'abort':
                    room['phase'] = 'idle'
                    room['startAt'] = 0
                elif stype == 'next':
                    room['round'] += 1
                    room['phase'] = 'idle'
                    room['startAt'] = 0
                elif stype == 'stop':
                    room['phase'] = 'idle'
                    room['startAt'] = 0
                elif stype == 'reset':
                    room['round'] = 1
                    room['phase'] = 'idle'
                    room['startAt'] = 0

                add_command(room, {
                    'type': stype,
                    'target': body.get('target'),
                    'startAt': room['startAt'],
                    'stagger': room['stagger'],
                    'round': room['round'],
                })
                return self._send(200, {'ok': True, 'rev': room['rev'], 'seq': room['seq']})

            if action == 'report':
                cid = str(body.get('clientId') or '')
                if not cid:
                    return self._send(400, {'error': 'clientId required'})
                c = client_of(room, cid)
                c['lastSeen'] = now_ms()
                c['report'] = {
                    'state': str(body.get('state') or 'idle'),
                    'role': str(body.get('role') or ''),
                    'territory': int(body.get('territory') or 0),
                    'target': int(body.get('target') or 0),
                    'round': int(body.get('round') or 0),
                    'party': body.get('party') or [],
                    'penaltyUntil': int(body.get('penaltyUntil') or 0),
                    'queueStartAt': int(body.get('queueStartAt') or 0),
                    'at': now_ms(),
                }
                bump(room)
                return self._send(200, {'ok': True, 'rev': room['rev']})

            return self._send(404, {'error': 'unknown action'})

    def do_OPTIONS(self):
        self.send_response(204)
        self.send_header('access-control-allow-origin', '*')
        self.send_header('access-control-allow-methods', 'GET,POST,OPTIONS')
        self.send_header('access-control-allow-headers', 'content-type')
        self.send_header('content-length', '0')
        self.end_headers()


class ThreadedHTTPServer(ThreadingMixIn, HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def gc_loop():
    while True:
        time.sleep(GC_INTERVAL_MS / 1000.0)
        t = now_ms()
        with lock:
            for code in list(rooms.keys()):
                room = rooms.get(code)
                if not room:
                    continue

                online = any((t - c.get('lastSeen', 0)) <= CLIENT_TIMEOUT_MS
                             for c in room['clients'].values())

                if online:
                    room['activeAt'] = t
                    continue

                idle = t - room.get('activeAt', room['createdAt'])
                too_old = t - room['createdAt'] > ROOM_IDLE_MS

                if idle > ROOM_EMPTY_MS or too_old:
                    rooms.pop(code, None)


if __name__ == '__main__':
    threading.Thread(target=gc_loop, daemon=True).start()
    print('mentor-roulette relay listening on http://%s:%d' % (HOST, PORT), flush=True)
    ThreadedHTTPServer((HOST, PORT), Handler).serve_forever()
