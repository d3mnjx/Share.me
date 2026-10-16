# Share.me

Paste a video link. Get a link anyone can watch, without an account on the original platform.

A standalone, self-hosted Docker app inspired by [Yap’s media caching](https://github.com/urza/Yap). It uses yt-dlp to fetch videos, ffmpeg to prepare browser-compatible MP4s, and ASP.NET Core to serve the app and media. There is no database service, frontend build step, or external API account to configure.

The interface defaults to dark mode, with a light-theme toggle that remembers your choice. Portrait, square, and landscape videos use their native aspect ratio and fit the screen.

The token-authenticated integration API supports submission, polling, listing, downloads, and deletion. Configure `API_TOKEN` through Docker; see [API.md](API.md) for endpoints and curl examples. The web interface remains open.

## Run

Linux (Bash):

```sh
docker run -d --name share-me --restart unless-stopped --init -p 6901:8080 -v shareme-data:/data -e API_TOKEN='replace-with-a-long-random-token' -e PUBLIC_URL='https://your-domain.example' ghcr.io/d3mnjx/share.me:latest
```

Windows (PowerShell, Docker Desktop using Linux containers):

```powershell
docker run -d --name share-me --restart unless-stopped --init -p 6901:8080 -v shareme-data:/data -e API_TOKEN='replace-with-a-long-random-token' -e PUBLIC_URL='https://your-domain.example' ghcr.io/d3mnjx/share.me:latest
```

Replace `https://your-domain.example` with your actual public HTTPS address, served through a reverse proxy to port 6901. For local HTTP testing, omit `-e PUBLIC_URL='https://your-domain.example'`.

Open **http://localhost:6901**. The public image supports Linux AMD64 and ARM64. Set `API_TOKEN` to your own random value if you use the integration API; the UI stays open. Existing Compose users should retain their current `DATA_VOLUME` when switching deployments.

Update an existing container using the same API token, public URL, and data volume as before. The named volume keeps downloaded videos.

Linux (Bash):

```sh
docker pull ghcr.io/d3mnjx/share.me:latest; docker stop share-me; docker rm share-me; docker run -d --name share-me --restart unless-stopped --init -p 6901:8080 -v shareme-data:/data -e API_TOKEN='replace-with-a-long-random-token' -e PUBLIC_URL='https://your-domain.example' ghcr.io/d3mnjx/share.me:latest
```

Windows (PowerShell):

```powershell
docker pull ghcr.io/d3mnjx/share.me:latest; docker stop share-me; docker rm share-me; docker run -d --name share-me --restart unless-stopped --init -p 6901:8080 -v shareme-data:/data -e API_TOKEN='replace-with-a-long-random-token' -e PUBLIC_URL='https://your-domain.example' ghcr.io/d3mnjx/share.me:latest
```

To build from source instead:

```sh
cp .env.example .env
docker compose up -d --build
```

Source Compose defaults to **http://localhost:8080**. Anyone who can reach the app can submit videos; there is no login. Share links look like `/s/012345…`, and direct video links like `/m/012345….mp4`.

For sharing outside your network, point your domain at an HTTPS reverse proxy in front of port 8080 and set `PUBLIC_URL=https://your-domain.example` in `.env`. Re-run `docker compose up -d` after changing configuration. `PUBLIC_URL` supplies absolute social-preview URLs; the copy buttons use the address you opened in your browser. It does not register a domain or make a private server publicly accessible. If your reverse proxy runs on the host, set `BIND_ADDRESS=127.0.0.1` to expose the app through that proxy only.

## What it does

- Downloads one video per submission, with a bounded queue and visible progress.
- Saves the video, title, poster, and share ID to a persistent Docker volume.
- Produces H.264/AAC MP4 with fast-start and HTTP byte ranges for playback and seeking.
- Gives viewers a watch page, direct MP4 link, download button, and original source link.
- Keeps your recent submissions and deletion controls in your browser. Deleting a share removes its media and invalidates its link. Active downloads must finish before deletion.
- Handles failed downloads and interrupted jobs explicitly. A server restart preserves completed shares and marks interrupted jobs as failed so they can be resubmitted.

Anyone with a share URL can watch. There is no public directory of all videos. A random deletion token is returned only when a share is created and kept in browser storage; viewers do not receive it. Clearing that storage removes your history and deletion controls, but does **not** delete the server’s files. The server operator can remove an orphaned share by stopping the app and deleting its `/data/videos/<id>` directory. There is no automatic expiration.

## Platform support

YouTube, X, Instagram, Facebook, TikTok, and other sites are supported **when their yt-dlp extractor can fetch the video from your server**. This is not a guarantee that every link will work. Private posts, login requirements, age/region restrictions, anti-bot checks, and platform changes can prevent a download. This version does not accept platform cookies or credentials and does not bypass DRM. Live streams, playlists/albums, photo slideshows, and audio-only links are outside the initial scope.

The generic webpage extractor is disabled. Native HTTP/HTTPS downloads and supported HLS/DASH streams are allowed; arbitrary files, private-network links, RTMP, and RTSP are not. All yt-dlp HTTP requests use a loopback proxy which rejects non-public IPs and connects to the validated address. Local ffmpeg processing restricts input protocols to files and pipes. Deploy this as a media downloader, not as a general-purpose network isolation boundary.

## Configuration

| Variable                   | Default        | Purpose                                            |
| -------------------------- | -------------- | -------------------------------------------------- |
| `DATA_VOLUME`              | `shareme-data` | Docker volume used for videos and metadata         |
| `PORT`                     | `8080`         | Host port                                          |
| `BIND_ADDRESS`             | `0.0.0.0`      | Host address to listen on                          |
| `PUBLIC_URL`               | empty          | Public origin for link-preview metadata            |
| `API_TOKEN`                | empty          | Bearer token for `/api/v1`; disabled when unset    |
| `MAX_FILE_MB`              | `250`          | Maximum final video size; also passed to yt-dlp    |
| `MAX_STORAGE_MB`           | `10240`        | Media and metadata storage budget                  |
| `MAX_DURATION_MINUTES`     | `20`           | Maximum video duration                             |
| `DOWNLOAD_TIMEOUT_SECONDS` | `900`          | Total time allowed per job, including conversion   |
| `YT_DLP_VERSION`           | `2026.08.19`   | Pinned yt-dlp release used when building the image |

One worker processes up to 20 outstanding submissions. Submissions are limited to 10 per minute per connecting IP. Behind a reverse proxy, clients share that proxy’s limit; forwarded IP headers are deliberately not trusted. Completed and failed records are capped at 10,000. Failed records are small and can be deleted from browser history.

Storage is checked during processing, with a temporary per-job budget of three times the video size limit. Downloads can briefly overshoot between checks; conversion needs room for both input and output. The app refuses new submissions when storage is full and does not silently expire old shares. Compose also limits CPU, memory, processes, and log growth, runs as an unprivileged user, and keeps the application filesystem read-only. `/tmp` permits execution because yt-dlp’s standalone binary unpacks native libraries there.

## Maintenance

```sh
docker compose logs --tail 100 -f
docker compose down          # Stops the app; keeps downloaded media
docker compose up -d         # Starts it again
```

Keep the `shareme-data` volume (or the volume selected by `DATA_VOLUME`) backed up: it contains both media and the records needed for share links. Stop the app while taking a filesystem backup. Do not run `docker compose down -v` unless you intend to delete all stored shares.

When a platform changes, update `YT_DLP_VERSION` in `.env` to an available yt-dlp release and run `docker compose up -d --build`. The Docker build checks the binary against the release’s SHA-256 manifest. Rebuild periodically to pick up .NET and operating-system updates as well.

## Local development

Install .NET 10, yt-dlp, ffmpeg/ffprobe, and Deno (or Node.js for YouTube challenges), then:

```sh
dotnet run --project src/ShareMe --urls http://localhost:8080
```

`DATA_PATH` overrides the data directory for local runs; `YT_DLP_PATH` overrides the executable path. Production defaults are set by the image. Do not run multiple app processes against the same data directory.

## Checks

```sh
python3 tests/integration.py
npm ci
node node_modules/playwright/cli.js install chromium
npm run test:browser
```

On filesystems that prohibit symlinks, use `npm ci --no-bin-links`.

The integration suite uses a deterministic yt-dlp fixture and real ffprobe/ffmpeg. It covers URL validation, private-destination rejection (HTTP and CONNECT), conversion, playback ranges, downloads, posters, persistence, size/duration limits, timeout cleanup, interrupted-job recovery, metadata escaping, deletion authorization, and rate limiting. Browser checks exercise submission, actual video playback, access from a fresh browser, mobile layout, and deletion. Screenshots are written to ignored `test-results/`.

These checks verify the app’s behavior, not ongoing compatibility with each external platform. Live platform checks and their results are recorded in [VALIDATION.md](VALIDATION.md).

Messenger previews are rendered in the watch page's HTML. For Signal, use a reachable HTTPS address and set `PUBLIC_URL` accordingly. A local HTTP address is sufficient to use the app, but not to generate a Signal preview. See [preview details](API.md#messenger-previews).

## Source reference

Yap was inspected at commit `b5ff08be322391db168421aa5d6eb2568e2bd89c`. Its media cache established the approach to yt-dlp, duration/size limits, H.264 conversion, metadata persistence, and ffmpeg posters. The standalone service, queue, storage, proxy, API, and UI are implemented here without Yap’s chat runtime. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Upgrading from share.to

Keep the original Docker volume and set `DATA_VOLUME=shareto_share-data` in `.env` before starting Share.me. Stop the original container first so the new container can bind to the same port. Existing share URLs and video records stay valid. Browser history and deletion tokens are read from the original storage key and migrated on the next history update; use the same host and port to retain access to that browser storage.

## Container releases

[GitHub Actions](https://github.com/d3mnjx/Share.me/actions/workflows/container.yml) runs the integration suite and publishes `ghcr.io/d3mnjx/share.me` for AMD64 and ARM64. Pushes to `main` update `latest`; each published commit also receives a `sha-…` tag. Tags such as `v1.0.0` publish the corresponding version tag. Pull requests run tests without publishing. The workflow uses GitHub's built-in `GITHUB_TOKEN` with package-write permission; no registry password secret is required.

See [preview research and fallbacks](docs/link-previews.md) for the Signal, Apple Messages, Slack, and Open Graph compatibility work.
