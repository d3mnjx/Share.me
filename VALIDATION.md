# Validation

Checked on 2026-10-16 on Linux x86-64 with .NET 10.0.401, yt-dlp 2026.08.19, and Chromium through Playwright 1.64.1.

## Automated checks

`BROWSER_TESTS=1 python3 tests/integration.py` passed:

- Rejects invalid schemes, credentials in URLs, nonstandard ports, and private input addresses.
- The downloader proxy rejects private HTTP destinations and private HTTPS CONNECT tunnels, including localhost, IPv6 loopback, and cloud metadata addresses.
- A deterministic extractor fixture supplies VP9/Opus video; the real ffmpeg pipeline produces H.264/AAC MP4 and a JPEG poster.
- Watch pages, public metadata, direct downloads, attachment headers, and byte-range responses work.
- Completed shares and media survive application restart.
- Overlong videos, live streams, oversized files, and extractor failures fail cleanly and remove partial media.
- Unknown source duration is handled; duration is checked again using the downloaded file.
- Titles are escaped in page metadata.
- Timed-out subprocesses are killed; interrupted jobs become failed after restart and partial files are removed.
- Deletion requires the submission’s token; deleted watch and media links return 404.
- A browser submits a link, observes completion, plays the resulting video, and deletes it.
- A fresh browser without submission history can watch; 390px mobile pages have no horizontal overflow.
- Submission rate limiting returns HTTP 429.

The .NET Release build completed with zero warnings and errors. Prettier checks passed. Docker Compose built the image and started a healthy container using the unprivileged user, read-only application filesystem, persistent volume, and configured resource limits. yt-dlp and Deno execute inside that container.

## Live platform check

The running Docker app downloaded `https://www.youtube.com/watch?v=jNQXAC9IVRw` ("Me at the zoo") using the real yt-dlp executable and checked outbound proxy. The resulting video was 744,361 bytes, 19.064 seconds, and 320 × 240 pixels. Chromium played it successfully and sought to 10 seconds. The final live check used the production image after the protocol restrictions and proxy address fallback were applied. Test shares were deleted afterward using their deletion tokens.

The older yt-dlp test URL `https://www.youtube.com/watch?v=BaW_jenozKc` returned “This video is unavailable”; the app showed a failed download rather than an unusable share.

X, Instagram, Facebook, and TikTok were not independently tested with live posts. Their support depends on yt-dlp extractors and platform access from the deployment’s IP. No claim of universal platform compatibility is made.

Browser screenshots are generated under ignored `test-results/`; they include desktop/mobile submission pages, the fixture watch page, and the live YouTube watch page. The deterministic tests use isolated temporary storage and do not contact social platforms.

## Share.me rename, themes, and portrait playback

The renamed .NET application and Docker image built successfully, and the full integration/browser suite passed again. The running service remains bound to `0.0.0.0:7543`. Checksums of the existing video records matched before and after the container migration; the original volume was retained.

Browser checks confirmed dark mode is the default even with a light system preference, light mode persists across reloads and navigation to watch pages, theme switching works when browser storage is blocked, and CSS defaults to dark when JavaScript is disabled. Legacy browser history loaded successfully, and its original deletion token was retained when migrating to the new storage key.

Generated portrait (9:16), landscape (16:9), and square videos kept their intrinsic aspect ratios and stayed centered within page and viewport bounds on desktop, phone, and rotated-phone layouts. The portrait checks were repeated after the theme update. These layout checks use local video fixtures and do not establish additional platform-download compatibility.
