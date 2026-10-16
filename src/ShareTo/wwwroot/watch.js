import { api, copyLink, stateLabel, mediaInfo } from "./shared.js";
const $ = (id) => document.getElementById(id);
const id = location.pathname.split("/")[2];
let video;
$("copy-share").onclick = () => copyLink(video.shareUrl, $("copy-status"));
$("copy-direct").onclick = () => copyLink(video.mediaUrl, $("copy-status"));
$("player").addEventListener("error", () => {
  $("watch-status").hidden = false;
  $("watch-status").textContent =
    "Your browser couldn’t play this video. Try downloading it using the button below.";
});
async function refresh() {
  try {
    video = await api(`/api/videos/${encodeURIComponent(id || "")}`);
    $("video-title").textContent = video.title || "A video for you.";
    document.title = `${video.title || "A video for you"} — share.to`;
    if (video.status === "ready") {
      $("watch-status").hidden = true;
      $("player").hidden = false;
      $("watch-details").hidden = false;
      $("player").poster = video.posterUrl;
      $("player").src = video.mediaUrl;
      $("source-link").href = video.sourceUrl;
      $("video-info").textContent = mediaInfo(video);
      $("download-link").href = `${video.mediaUrl}?download=1`;
      return;
    }
    $("watch-status").textContent =
      video.status === "failed"
        ? video.error
        : `${stateLabel(video)}…\nYou’re in the right place. The video will appear here when it’s ready.`;
    if (video.status !== "failed") setTimeout(refresh, 2000);
  } catch (error) {
    $("watch-status").textContent =
      error.status === 404 ? error.message : `${error.message} Reconnecting…`;
    if (error.status !== 404) setTimeout(refresh, 3000);
  }
}
refresh();
