"""Exercise the real HTTP app and transcoding pipeline with a local extractor fixture.

No social platform accounts, network downloads or application data are used.
"""
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'src/ShareMe'
subprocess.run(['dotnet', 'build', str(PROJECT / 'ShareMe.csproj'), '-c', 'Release', '--nologo'], check=True)
temporary = tempfile.TemporaryDirectory(prefix='share-me-tests-')
work = Path(temporary.name)
fixture = work / 'fixture.webm'
subprocess.run(['ffmpeg', '-v', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=24', '-f', 'lavfi', '-i', 'sine=frequency=440',
                '-t', '2', '-c:v', 'libvpx-vp9', '-threads', '2', '-c:a', 'libopus', str(fixture)], check=True)
fake = ROOT / 'tests/fake-yt-dlp.py'
fake.chmod(0o755)
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    port = sock.getsockname()[1]
base = f'http://127.0.0.1:{port}'
env = {**os.environ, 'ASPNETCORE_URLS': base, 'DATA_PATH': str(work / 'data'), 'YT_DLP_PATH': str(fake), 'FAKE_MEDIA': str(fixture),
       'MAX_FILE_MB': '1', 'PUBLIC_URL': 'https://share.example', 'DOWNLOAD_TIMEOUT_SECONDS': '10'}
log = open(work / 'server.log', 'w+')
process = None

def request(path, method='GET', data=None, headers=None):
    body = json.dumps(data).encode() if data is not None else None
    req = urllib.request.Request(base + path, data=body, method=method, headers={'Content-Type': 'application/json', **(headers or {})})
    try:
        response = urllib.request.urlopen(req, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    raw = response.read()
    return response.status, dict(response.headers), json.loads(raw) if 'application/json' in response.headers.get('Content-Type', '') else raw

def start():
    global process
    process = subprocess.Popen(['dotnet', str(PROJECT / 'bin/Release/net10.0/ShareMe.dll')], cwd=PROJECT, env=env, stdout=log, stderr=log, start_new_session=True)
    for _ in range(100):
        try:
            if request('/healthz')[0] == 200: return
        except OSError: pass
        time.sleep(.1)
    raise AssertionError('Server did not start')

def stop():
    if process and process.poll() is None:
        os.killpg(process.pid, signal.SIGTERM)
        process.wait(timeout=15)

def submit(mode):
    status, _, result = request('/api/videos', 'POST', {'url': f'https://example.com/{mode}'})
    assert status == 202, (status, result)
    assert 'deleteHash' not in result['video']
    return result

def wait(result):
    for _ in range(150):
        status, _, video = request('/api/videos/' + result['video']['id'])
        assert status == 200
        if video['status'] in ['ready', 'failed']: return video
        time.sleep(.1)
    raise AssertionError('Download did not finish')

try:
    start()
    assert request('/')[0] == 200
    assert request('/api/videos', 'POST', {'url': 'file:///etc/passwd'})[0] == 400
    assert request('/api/videos', 'POST', {'url': 'http://127.0.0.1'})[0] == 400
    assert request('/api/videos', 'POST', {'url': 'http://169.254.169.254'})[0] == 400
    assert request('/api/videos', 'POST', {'url': 'https://name:password@example.com/'})[0] == 400
    assert request('/api/videos', 'POST', {'url': 'https://example.com:8443/'})[0] == 400
    print('PASS URL validation', flush=True)
    ready = submit('proxy-check')
    video = wait(ready)
    assert video['status'] == 'ready', video
    status, headers, payload = request(video['mediaUrl'], headers={'Range': 'bytes=0-99'})
    assert status == 206 and len(payload) == 100 and headers['Content-Type'] == 'video/mp4'
    status, headers, _ = request(video['mediaUrl'] + '?download=1')
    assert status == 200 and 'attachment;' in headers['Content-Disposition']
    assert request(video['posterUrl'])[0] == 200
    assert request(video['shareUrl'])[0] == 200
    assert b'https://share.example/m/' in request(video['shareUrl'])[2]
    assert 'DeleteHash' not in request('/api/videos/' + video['id'])[2]
    media_path = work / 'data/videos' / video['id'] / 'video.mp4'
    codecs = subprocess.check_output(['ffprobe', '-v', 'error', '-show_entries', 'stream=codec_name', '-of', 'json', str(media_path)], text=True)
    assert {s['codec_name'] for s in json.loads(codecs)['streams']} == {'h264', 'aac'}
    print('PASS private HTTP/CONNECT destinations blocked; conversion, poster, range requests and previews', flush=True)
    # Restart also resets the per-process request limiter for the remaining cases.
    stop(); start()
    assert request('/api/videos/' + video['id'])[2]['status'] == 'ready'
    assert request(video['mediaUrl'])[0] == 200
    for mode in ['too-long', 'live', 'fail', 'oversize']:
        result = submit(mode)
        failed = wait(result)
        assert failed['status'] == 'failed', failed
        assert list((work / 'data/videos' / failed['id']).iterdir()) == [work / 'data/videos' / failed['id'] / 'record.json']
    unknown = wait(submit('unknown-duration'))
    assert unknown['status'] == 'ready', unknown
    escaped = wait(submit('xss'))
    assert escaped['status'] == 'ready', escaped
    assert b'&lt;img src=x onerror=alert(1)&gt;' in request(escaped['shareUrl'])[2]
    print('PASS persistence; duration/live/size/failure handling; null duration; metadata escaping', flush=True)
    hang = submit('hang')
    failed = wait(hang)
    assert failed['status'] == 'failed' and 'timed out' in failed['error'], failed
    interrupted = submit('hang')
    time.sleep(.3)
    os.killpg(process.pid, signal.SIGKILL); process.wait()
    start()
    failed = request('/api/videos/' + interrupted['video']['id'])[2]
    assert failed['status'] == 'failed' and 'restarted' in failed['error'], failed
    assert not (work / 'data/videos' / failed['id'] / 'unfinished.part').exists()
    assert request('/api/videos/' + video['id'], 'DELETE')[0] == 403
    assert request('/api/videos/' + video['id'], 'DELETE', headers={'Authorization': 'Bearer wrong'})[0] == 403
    assert request('/api/videos/' + video['id'], 'DELETE', headers={'Authorization': 'Bearer ' + ready['deleteToken']})[0] == 204
    assert request(video['mediaUrl'])[0] == 404 and request(video['shareUrl'])[0] == 404
    print('PASS timeout cleanup, crash recovery and deletion ownership', flush=True)
    if os.getenv('BROWSER_TESTS') == '1':
        subprocess.run(['node', 'tests/browser.mjs', base], cwd=ROOT, check=True)
    for _ in range(11): result = request('/api/videos', 'POST', {'url': 'invalid'})
    assert result[0] == 429
    print('PASS submission rate limit', flush=True)
finally:
    stop()
    log.flush(); log.seek(0)
    if __import__('sys').exc_info()[0]: print(log.read()[-12000:])
    log.close()
    temporary.cleanup()
