"""Exercise the real HTTP app and transcoding pipeline with a local extractor fixture.

No social platform accounts, network downloads or application data are used.
"""
import json
import os
from pathlib import Path
import signal
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from html.parser import HTMLParser

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
       'MAX_FILE_MB': '1', 'PUBLIC_URL': 'https://share.example', 'DOWNLOAD_TIMEOUT_SECONDS': '10', 'API_TOKEN': 'integration-token',
       'MAX_VIDEOS_PER_DAY': '23', 'MAX_STORED_VIDEOS': '66'}
log = open(work / 'server.log', 'w+')
process = None
auth = {'Authorization': 'Bearer integration-token'}

class Preview(HTMLParser):
    def __init__(self, html):
        super().__init__()
        self.meta = {}
        self.canonical = None
        self.scripts = []
        self.feed(html.decode())

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'meta': self.meta[attrs.get('property') or attrs.get('name')] = attrs.get('content')
        if tag == 'link' and attrs.get('rel') == 'canonical': self.canonical = attrs['href']
        if tag == 'script': self.scripts.append(attrs.get('src'))

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
    assert request('/api/config')[2]['maxVideosPerDay'] == 23
    assert request('/api/config')[2]['maxStoredVideos'] == 66
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
    assert request(video['previewUrl'])[0] == 200
    assert 8 < len(request(video['previewUrl'])[2]) <= 512 * 1024
    assert request(video['previewUrl'], 'HEAD')[0] == 200
    for agent in ['Signal-Desktop', 'facebookexternalhit/1.1', 'Slackbot-LinkExpanding 1.0', 'TelegramBot', 'WhatsApp/2', 'Applebot']:
        card = Preview(request(video['shareUrl'], headers={'User-Agent': agent})[2])
        assert card.meta['og:image'] == 'https://share.example' + video['previewUrl']
        assert card.meta['og:title'] == video['title']
    assert request(video['shareUrl'])[0] == 200
    assert b'https://share.example/m/' in request(video['shareUrl'])[2]
    preview = Preview(request(video['shareUrl'])[2])
    assert preview.meta['og:title'] == video['title']
    assert preview.meta['og:description'] == 'A sample description with "quotes" & details.'
    assert preview.meta['author'] == 'Sample author'
    assert preview.canonical == preview.meta['og:url'] == 'https://share.example' + video['shareUrl']
    assert preview.meta['og:image'] == preview.meta['og:image:secure_url'] == preview.meta['twitter:image'] == 'https://share.example' + video['previewUrl']
    assert preview.meta['og:image:type'] == 'image/jpeg' and preview.meta['og:image:width'] == '1200' and preview.meta['og:image:height'] == '630'
    assert preview.meta['og:video:width'] == '320' and preview.meta['og:video:height'] == '180'
    assert preview.meta['og:video:secure_url'] == 'https://share.example' + video['mediaUrl']
    assert preview.meta['twitter:card'] == 'summary_large_image'
    assert video['title'].encode() in request(video['shareUrl'])[2].split(b'</title>')[0]
    assert request(video['shareUrl'], 'HEAD')[0] == 200
    assert request(video['posterUrl'], 'HEAD')[0] == 200
    assert request(video['mediaUrl'], 'HEAD')[0] == 200
    assert 'og:image' not in Preview(request('/s/missing')[2]).meta
    print('PASS server-rendered preview metadata, absolute URLs, media dimensions and HEAD requests', flush=True)
    assert 'DeleteHash' not in request('/api/videos/' + video['id'])[2]
    media_path = work / 'data/videos' / video['id'] / 'video.mp4'
    codecs = subprocess.check_output(['ffprobe', '-v', 'error', '-show_entries', 'stream=codec_name', '-of', 'json', str(media_path)], text=True)
    assert {s['codec_name'] for s in json.loads(codecs)['streams']} == {'h264', 'aac'}
    print('PASS private HTTP/CONNECT destinations blocked; conversion, poster, range requests and previews', flush=True)
    # Restart also resets the per-process request limiter for the remaining cases.
    stop(); start()
    assert request('/api/videos/' + video['id'])[2]['status'] == 'ready'
    assert Preview(request(video['shareUrl'])[2]).meta['og:description'] == preview.meta['og:description']
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
    malicious = Preview(request(escaped['shareUrl'])[2])
    assert malicious.meta['og:description'] == '\"><script>alert("preview")</script>'
    assert all(malicious.scripts), 'Source text injected an inline script'
    # Simulate a record written before preview fields were introduced.
    legacy_path = work / 'data/videos' / unknown['id'] / 'record.json'
    legacy = json.loads(legacy_path.read_text())
    for field in ['Description', 'Author', 'Width', 'Height', 'PosterWidth', 'PosterHeight']: legacy.pop(field, None)
    legacy_path.write_text(json.dumps(legacy))
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
    assert request('/api/v1/videos')[0] == 401
    assert request('/api/v1/videos', headers={'Authorization': 'Bearer incorrect'})[0] == 401
    assert request('/api/v1/videos', headers=auth)[2]['total'] > 0
    listing = request('/api/v1/videos?limit=1&offset=0', headers=auth)[2]
    assert len(listing['videos']) == 1 and 'deleteHash' not in listing['videos'][0]
    assert request('/api/v1/videos?limit=0', headers=auth)[0] == 400
    assert request('/api/v1/config', headers=auth)[2]['maxFileMb'] == 1
    code, headers, created = request('/api/v1/videos', 'POST', {'url': 'https://example.com/api'}, headers=auth)
    assert code == 202 and headers['Location'] == '/api/v1/videos/' + created['video']['id']
    api_video = wait(created)
    assert api_video['status'] == 'ready'
    api_path = '/api/v1/videos/' + api_video['id']
    assert request(api_path, headers=auth)[2]['status'] == 'ready'
    assert request(api_path + '/media', headers=auth)[0] == 200
    assert request(api_path + '/poster', headers=auth)[0] == 200
    assert request(api_path + '/media', headers={**auth, 'Range': 'bytes=0-99'})[0] == 206
    assert request(api_path, 'DELETE')[0] == 401
    assert request(api_path, 'DELETE', headers=auth)[0] == 204
    assert request(api_video['shareUrl'])[0] == 404
    print('PASS authenticated API submission, listing, status, downloads, ranges and deletion; public UI remains open', flush=True)
    if os.getenv('BROWSER_TESTS') == '1':
        subprocess.run(['node', 'tests/browser.mjs', base], cwd=ROOT, check=True)
    for _ in range(11): result = request('/api/videos', 'POST', {'url': 'invalid'})
    assert result[0] == 429
    print('PASS submission rate limit', flush=True)
    stop()
    env['PUBLIC_URL'] = ''
    env['API_TOKEN'] = ''
    start()
    legacy_page = Preview(request(unknown['shareUrl'], headers={'Host': 'share.example:7543', 'X-Forwarded-Host': 'untrusted.invalid', 'X-Forwarded-Proto': 'https'})[2])
    assert legacy_page.meta['og:url'] == 'http://share.example:7543' + unknown['shareUrl']
    assert legacy_page.meta['og:image'].startswith('http://share.example:7543/preview/')
    assert 'og:image:secure_url' not in legacy_page.meta
    assert legacy_page.meta['og:description'].startswith('Video from example.com.')
    assert request('/api/v1/videos', headers=auth)[0] == 503
    assert request('/api/videos/' + unknown['id'])[0] == 200
    print('PASS legacy records, request-origin preview fallback and disabled-token API with public UI', flush=True)
    # Simulate an ffmpeg thumbnail failure while real transcoding still works.
    stop()
    wrappers = work / 'wrappers'
    wrappers.mkdir()
    real_ffmpeg = shutil.which('ffmpeg')
    wrapper = wrappers / 'ffmpeg'
    wrapper.write_text('#!/usr/bin/env python3\nimport os,sys\nif "-frames:v" in sys.argv: sys.exit(1)\nos.execv(' + repr(real_ffmpeg) + ', [' + repr(real_ffmpeg) + '] + sys.argv[1:])\n')
    wrapper.chmod(0o755)
    env['PATH'] = str(wrappers) + os.pathsep + os.environ['PATH']
    start()
    without_preview = wait(submit('poster-failure'))
    assert without_preview['status'] == 'ready', without_preview
    assert request(without_preview['mediaUrl'])[0] == 200
    fallback_card = Preview(request(without_preview['shareUrl'])[2])
    assert fallback_card.meta['og:image'] == base + '/icon-180.png'
    assert fallback_card.meta['og:image:type'] == 'image/png'
    assert fallback_card.meta['twitter:card'] == 'summary'
    assert request(without_preview['posterUrl'])[1]['Content-Type'] == 'image/png'
    assert request('/icon-180.png')[0] == 200
    print('PASS optional preview failures retain playable downloads and fall back to a raster icon', flush=True)
    stop()
    env.update(DATA_PATH=str(work / 'daily-data'), MAX_VIDEOS_PER_DAY='3', API_TOKEN='integration-token', PATH=os.environ['PATH'])
    start()
    assert request('/api/videos', 'POST', {'url': 'invalid'})[0] == 400
    assert request('/api/config')[2]['videosSubmittedToday'] == 0
    def concurrent_submit(i):
        return request('/api/v1/videos' if i % 2 else '/api/videos', 'POST', {'url': 'https://example.com/fail'}, headers=auth)
    with ThreadPoolExecutor(max_workers=6) as pool:
        submissions = list(pool.map(concurrent_submit, range(6)))
    accepted = [body for code, _, body in submissions if code == 202]
    rejected = [(headers, body) for code, headers, body in submissions if code == 429]
    assert len(accepted) == len(rejected) == 3, submissions
    assert all(int(headers['Retry-After']) > 0 and body['retryAt'].endswith('+00:00') for headers, body in rejected)
    for created in accepted:
        assert wait(created)['status'] == 'failed'
        assert request('/api/v1/videos/' + created['video']['id'], 'DELETE', headers=auth)[0] == 204
    assert request('/api/config')[2]['videosSubmittedToday'] == 3
    assert request('/api/v1/config', headers=auth)[2]['videosSubmittedToday'] == 3
    stop(); start()
    assert request('/api/videos', 'POST', {'url': 'https://example.com/test'})[0] == 429
    assert request('/api/config')[2]['videosSubmittedToday'] == 3
    stop()
    usage_path = work / 'daily-data/daily-usage.json'
    usage = json.loads(usage_path.read_text())
    usage['Day'] = (datetime.now(timezone.utc) - timedelta(days=1)).date().isoformat()
    usage_path.write_text(json.dumps(usage))
    start()
    assert request('/api/config')[2]['videosSubmittedToday'] == 0
    assert wait(submit('new-day'))['status'] == 'ready'
    assert request('/api/config')[2]['videosSubmittedToday'] == 1
    print('PASS global daily quota across UI/API, concurrent admission, failed/deleted jobs, restart persistence and UTC rollover', flush=True)
    stop()
    env.update(DATA_PATH=str(work / 'retention-data'), MAX_VIDEOS_PER_DAY='23', MAX_STORED_VIDEOS='2')
    start()
    oldest = wait(submit('oldest'))
    second = wait(submit('second'))
    assert oldest['status'] == second['status'] == 'ready'
    active = submit('hang')['video']
    for _ in range(100):
        if request(oldest['shareUrl'])[0] == 404: break
        time.sleep(.05)
    assert request(oldest['shareUrl'])[0] == request(oldest['mediaUrl'])[0] == 404
    assert not (work / 'retention-data/videos' / oldest['id']).exists()
    assert request(second['shareUrl'])[0] == 200
    assert request('/api/videos/' + active['id'])[2]['status'] in ['queued', 'downloading']
    newest = submit('fail')['video']
    for _ in range(100):
        if request(second['shareUrl'])[0] == 404: break
        time.sleep(.05)
    assert request(second['shareUrl'])[0] == 404
    assert request('/api/videos/' + active['id'])[0] == 200
    # Lowering the limit applies on restart; active jobs recover as failed first.
    stop()
    env['MAX_STORED_VIDEOS'] = '1'
    start()
    for _ in range(100):
        listing = request('/api/v1/videos', headers=auth)[2]
        if listing['total'] == 1: break
        time.sleep(.05)
    assert [v['id'] for v in listing['videos']] == [newest['id']]
    assert request('/api/config')[2]['videosSubmittedToday'] == 4
    # With only active jobs, temporarily exceed retention until they finish.
    stop()
    env['DATA_PATH'] = str(work / 'active-retention-data')
    start()
    active = submit('hang')['video']
    newest = submit('fail')['video']
    assert request('/api/v1/videos', headers=auth)[2]['total'] == 2
    for _ in range(150):
        listing = request('/api/v1/videos', headers=auth)[2]
        if listing['total'] == 1 and listing['videos'][0]['status'] == 'failed': break
        time.sleep(.1)
    assert [v['id'] for v in listing['videos']] == [newest['id']]
    assert request('/api/videos/' + active['id'])[0] == 404
    assert request('/api/config')[2]['videosSubmittedToday'] == 2
    print('PASS oldest-first background retention, media removal, active-job protection, completion cleanup and reduced limit on restart', flush=True)
    stop()
    env.update(DATA_PATH=str(work / 'disk-data'), MAX_STORAGE_MB='10')
    start()
    filler = work / 'disk-data/videos/filler.bin'
    with filler.open('wb') as output: output.truncate(10 * 1024 * 1024)
    assert request('/api/videos', 'POST', {'url': 'https://example.com/test'})[0] == 503
    assert request('/api/config')[2]['videosSubmittedToday'] == 0
    filler.unlink()
    assert wait(submit('has-space'))['status'] == 'ready'
    print('PASS disk budget remains independent and rejected submissions do not consume daily allowance', flush=True)
finally:
    stop()
    log.flush(); log.seek(0)
    if __import__('sys').exc_info()[0]: print(log.read()[-12000:])
    log.close()
    temporary.cleanup()
