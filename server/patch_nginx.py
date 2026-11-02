#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import re, shutil, subprocess, sys, time

P = '/www/server/panel/vhost/nginx/quickfileshare-relay.conf'
bak = P + '.bak.' + time.strftime('%Y%m%d%H%M%S')

src = open(P, encoding='utf-8').read()
if 'location /mr/' in src:
    print('already patched')
    sys.exit(0)

block = """
    location = /mr {
        return 302 /mr/healthz;
    }
    location /mr/ {
        access_log /www/wwwlogs/mr-relay.log combined;
        limit_except GET POST OPTIONS { deny all; }
        client_max_body_size 256k;
        proxy_pass http://127.0.0.1:8787/;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Connection "";
        proxy_buffering off;
        proxy_cache off;
        proxy_read_timeout 60s;
    }
"""

pattern = re.compile(r"(location /qfs-relay/ \{.*?\n    \}\n)", re.S)
new, n = pattern.subn(lambda m: m.group(1) + block, src)
if n == 0:
    print('anchor not found, abort')
    sys.exit(1)

shutil.copy2(P, bak)
open(P, 'w', encoding='utf-8').write(new)
print('patched %d blocks, backup %s' % (n, bak))

r = subprocess.run(['nginx', '-t'], capture_output=True, text=True)
print(r.stderr[-1500:])
if r.returncode != 0:
    shutil.copy2(bak, P)
    print('nginx -t failed, restored backup')
    sys.exit(2)

subprocess.run(['nginx', '-s', 'reload'], check=False)
print('nginx reloaded')
