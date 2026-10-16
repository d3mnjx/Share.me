#!/usr/bin/env python3
"""Deterministic extractor fixture. Real ffprobe/ffmpeg handle its output."""
import http.client
import json
import os
from pathlib import Path
import shutil
import sys
import time
from urllib.parse import urlsplit

args = sys.argv[1:]
url = args[-1]
mode = urlsplit(url).path.strip('/')
if mode == 'fail':
    print('ERROR: this platform refused the request', file=sys.stderr)
    sys.exit(1)
if mode == 'hang':
    Path('unfinished.part').write_text('incomplete')
    time.sleep(120)
if mode == 'proxy-check':
    proxy = urlsplit(args[args.index('--proxy') + 1])
    for target in ['http://127.0.0.1/', 'http://169.254.169.254/latest/meta-data/', 'http://[::1]/', 'http://localhost/']:
        connection = http.client.HTTPConnection(proxy.hostname, proxy.port, timeout=5)
        connection.request('GET', target)
        assert connection.getresponse().status == 403, f'Proxy allowed {target}'
        connection.close()
    connection = http.client.HTTPConnection(proxy.hostname, proxy.port, timeout=5)
    connection.request('CONNECT', '127.0.0.1:443')
    assert connection.getresponse().status == 403, 'Proxy allowed private CONNECT'
    connection.close()
if '--dump-single-json' in args:
    print(json.dumps({'title': '<img src=x onerror=alert(1)> & a video' if mode == 'xss' else 'A little moment worth sharing',
                      'description': '\"><script>alert("preview")</script>' if mode == 'xss' else 'A sample description with "quotes" & details.',
                      'uploader': 'Sample author',
                      'duration': 99999 if mode == 'too-long' else None if mode == 'unknown-duration' else 2,
                      'is_live': mode == 'live'}))
else:
    if mode == 'oversize':
        Path('source.webm').write_bytes(b'x' * (2 * 1024 * 1024))
    else:
        shutil.copyfile(os.environ['FAKE_MEDIA'], 'source.webm')
    print('PROGRESS:50.0%', flush=True)
    print('PROGRESS:100.0%', flush=True)
