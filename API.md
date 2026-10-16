# API

Share.me uses ASP.NET Core 10 Minimal APIs. The integration API is under `/api/v1` and authenticates requests with `Authorization: Bearer <token>`.

Set `API_TOKEN` in `.env` and run `docker compose up -d`, or pass it to Docker:

```sh
export API_TOKEN="$(openssl rand -hex 32)"
docker run -d --name shareme \
  -p 7543:8080 \
  -e API_TOKEN="$API_TOKEN" \
  -v shareme-data:/data \
  share-me:local
```

Use your existing data volume when upgrading an instance. Compose reads its volume name from `DATA_VOLUME`.

The token grants access to all videos on the instance, including listing and deletion. **The web UI and its browser endpoints remain open**, as do shared watch pages and media URLs. This token does not restrict who can submit through the website. An unset token disables `/api/v1` with HTTP 503. An absent or incorrect bearer token returns HTTP 401. Use HTTPS when sending the token over a network you do not trust.

## Endpoints

| Method | Path                               | Result                                                                             |
| ------ | ---------------------------------- | ---------------------------------------------------------------------------------- |
| GET    | `/api/v1/config`                   | Download limits and configured public origin                                       |
| POST   | `/api/v1/videos`                   | Queue a URL; HTTP 202 with `{ video, deleteToken }` and a status URL in `Location` |
| GET    | `/api/v1/videos?offset=0&limit=50` | Newest-first `{ videos, total, offset, limit }`; maximum limit 100                 |
| GET    | `/api/v1/videos/{id}`              | Video metadata and download status                                                 |
| GET    | `/api/v1/videos/{id}/media`        | Download MP4; supports HTTP Range requests                                         |
| GET    | `/api/v1/videos/{id}/poster`       | JPEG thumbnail                                                                     |
| DELETE | `/api/v1/videos/{id}`              | Delete a completed or failed job and its files; HTTP 204                           |

## Submit, poll, share

```sh
BASE_URL=http://localhost:7543

curl --fail-with-body "$BASE_URL/api/v1/videos" \
  -H "Authorization: Bearer $API_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"url":"https://www.youtube.com/watch?v=jNQXAC9IVRw"}'
```

Take the ID from `video.id` and poll its status:

```sh
VIDEO_ID=the_id_from_the_response
curl --fail-with-body "$BASE_URL/api/v1/videos/$VIDEO_ID" \
  -H "Authorization: Bearer $API_TOKEN"
```

`status` is `queued`, `downloading`, `processing`, `ready`, or `failed`. `progress` is download progress; `error` explains a failed job. Metadata includes `title`, `description`, `author`, `duration` in seconds, `bytes`, `width`, and `height` when known.

When ready, `shareUrl` is the watch page, `mediaUrl` is the MP4, and `posterUrl` is the JPEG. These paths are relative to your externally reachable Share.me origin. Send the **watch page** for messenger preview cards. The MP4 URL is for direct playback/downloads. Viewers do not need the API token.

```sh
curl --fail-with-body "$BASE_URL/api/v1/videos/$VIDEO_ID/media" \
  -H "Authorization: Bearer $API_TOKEN" -o video.mp4

curl --fail-with-body -X DELETE "$BASE_URL/api/v1/videos/$VIDEO_ID" \
  -H "Authorization: Bearer $API_TOKEN"
```

The `deleteToken` returned at creation is for the open browser API; integrations can delete with the instance API token instead. Neither token is included in the watch page or public video metadata.

## Errors

Errors use `{ "error": "..." }`. Invalid input returns 400, a missing video returns 404, and media requests or deletion before a job finishes return 409. Rate limits return 429; full queues/storage return 503. Failed downloads remain inspectable and can be deleted or retried by submitting the URL again.

## Messenger previews

Watch pages include their title, description, canonical URL, Open Graph metadata, Twitter/X card metadata, local JPEG thumbnail, video URL, and known dimensions in the **initial HTML**, without JavaScript. Source descriptions/authors are saved for new downloads. Existing downloads use their saved title, a video frame, and a description based on the original source. The app identifies itself as Share.me rather than impersonating the original platform.

Set `PUBLIC_URL=https://your-public-host` behind an HTTPS reverse proxy so the page advertises the correct public image and video URLs. Without it, URLs use the request's scheme and Host. Untrusted forwarded headers are not used. The copy buttons use the browser's current origin, so open the app at its public HTTPS address when sharing externally.

Signal's Android client reads `og:title`, `og:description`, and `og:image`, requires HTTPS preview URLs, and rejects private/local IP addresses. A plain HTTP or private sandbox address will not generate a Signal card. Signal also needs link previews enabled. Messengers decide how to display a card and may cache it; share after a download finishes. `og:video` does not guarantee inline playback in a messenger.

References checked on 2026-10-16: [Signal preview parsing](https://github.com/signalapp/Signal-Android/blob/main/app/src/main/java/org/thoughtcrime/securesms/linkpreview/LinkPreviewUtil.java) and [URL eligibility](https://github.com/signalapp/Signal-Android/blob/main/app/src/main/java/org/thoughtcrime/securesms/util/LinkUtil.kt).
