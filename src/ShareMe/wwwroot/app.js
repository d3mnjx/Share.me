import {
  api,
  copyLink,
  stateLabel,
  done,
  element,
  mediaInfo,
} from "./shared.js";
const $ = (id) => document.getElementById(id);
let history;
try {
  history = JSON.parse(
    localStorage.getItem("share.me.history") ||
      localStorage.getItem("share.to.history") ||
      "[]",
  );
  if (!Array.isArray(history)) history = [];
} catch {
  history = [];
}
history = history.filter(
  (item) => /^[a-f0-9]{32}$/.test(item?.id) && typeof item.token === "string",
);
let currentId = null;
let currentVideo = null;
const cache = new Map();
function saveHistory() {
  try {
    // Retain submission ownership when upgrading from the original app name.
    localStorage.setItem("share.me.history", JSON.stringify(history));
    localStorage.removeItem("share.to.history");
  } catch {
    $("form-error").textContent =
      "Browser storage is unavailable. Keep your share link; history and deletion controls may be lost when this page closes.";
    $("form-error").hidden = false;
  }
}
function showCurrent(video) {
  const panel = $("current-share");
  // Avoid replacing a focused copy button on every polling tick.
  if (currentVideo && JSON.stringify(currentVideo) === JSON.stringify(video))
    return;
  currentVideo = video;
  panel.replaceChildren();
  panel.hidden = false;
  panel.append(
    element("h2", "", stateLabel(video)),
    element(
      "p",
      "",
      video.error ||
        video.title ||
        "The link will show the video when the download finishes.",
    ),
  );
  if (!done(video)) {
    const track = element("div", "progress-track");
    const progress = element(
      "div",
      `progress-bar${video.progress ? "" : " busy"}`,
    );
    progress.style.width = video.progress ? `${video.progress}%` : "";
    track.append(progress);
    panel.append(track);
  }
  const actions = element("div", "actions");
  const open = element(
    "a",
    "button secondary",
    video.status === "ready" ? "Watch video" : "Open page",
  );
  open.href = video.shareUrl;
  const copy = element("button", "", "Copy link");
  const status = element("p", "copy-status");
  status.setAttribute("role", "status");
  copy.addEventListener("click", () => copyLink(video.shareUrl, status));
  actions.append(copy, open);
  if (video.status === "failed") {
    const retry = element("button", "secondary", "Try again");
    retry.onclick = () => {
      $("video-url").value = video.sourceUrl;
      $("share-form").requestSubmit();
    };
    actions.replaceChildren(retry);
  }
  panel.append(actions, status);
}
function renderRecent() {
  $("recent-count").textContent = history.length;
  if (!history.length) {
    const empty = element("div", "empty-state");
    empty.append(element("p", "", "No downloads yet."));
    $("recent-list").replaceChildren(empty);
    return;
  }
  const rows = history.map((item) => {
    const video = cache.get(item.id);
    const row = element("article", "recent-row");
    const thumb =
      video?.status === "ready"
        ? element("img", "recent-thumb")
        : element("span", "recent-thumb recent-placeholder", "↗");
    if (thumb.tagName === "IMG") {
      thumb.src = video.posterUrl;
      thumb.alt = "";
      thumb.loading = "lazy";
    }
    const description = element("div", "recent-description");
    const link = element("a", "", video?.title || "Shared video");
    link.href = `/s/${item.id}`;
    description.append(
      link,
      element(
        "p",
        "",
        video
          ? `${new URL(video.sourceUrl).hostname.replace(/^www\./, "")} · ${video.status === "ready" ? mediaInfo(video) : stateLabel(video)}`
          : "Loading…",
      ),
    );
    row.append(thumb, description);
    const remove = element("button", "secondary danger", "Delete");
    remove.disabled = !!video && !done(video);
    remove.setAttribute(
      "aria-label",
      `Delete ${video?.title || "shared video"}`,
    );
    remove.onclick = async () => {
      if (
        !confirm(
          "Delete this video? Its share link will stop working for everyone.",
        )
      )
        return;
      remove.disabled = true;
      try {
        await api(`/api/videos/${item.id}`, {
          method: "DELETE",
          headers: { Authorization: `Bearer ${item.token}` },
        });
        history = history.filter((h) => h.id !== item.id);
        cache.delete(item.id);
        saveHistory();
        renderRecent();
        if (currentId === item.id) {
          currentId = null;
          currentVideo = null;
          $("current-share").hidden = true;
        }
      } catch (error) {
        $("form-error").textContent = error.message;
        $("form-error").hidden = false;
        remove.disabled = false;
      }
    };
    row.append(remove);
    return row;
  });
  $("recent-list").replaceChildren(...rows);
}
async function refresh() {
  const pending = history.filter(
    (item) => !cache.has(item.id) || !done(cache.get(item.id)),
  );
  await Promise.all(
    pending.map(async (item) => {
      try {
        const video = await api(`/api/videos/${item.id}`);
        cache.set(item.id, video);
        if (item.id === currentId) showCurrent(video);
      } catch (error) {
        if (error.status === 404) {
          history = history.filter((h) => h.id !== item.id);
          cache.delete(item.id);
          saveHistory();
          if (item.id === currentId) {
            currentId = null;
            currentVideo = null;
            $("current-share").hidden = true;
          }
        } else if (item.id === currentId) {
          $("current-share").replaceChildren(
            element("p", "error", `${error.message} Retrying…`),
          );
          currentVideo = null;
        }
      }
    }),
  );
  if (pending.length) renderRecent();
  setTimeout(refresh, 2000);
}
$("share-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  $("form-error").hidden = true;
  $("submit-button").disabled = true;
  $("submit-button").textContent = "Adding…";
  try {
    const result = await api("/api/videos", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url: $("video-url").value }),
    });
    currentId = result.video.id;
    currentVideo = null;
    history.unshift({ id: currentId, token: result.deleteToken });
    saveHistory();
    cache.set(currentId, result.video);
    showCurrent(result.video);
    renderRecent();
    $("video-url").value = "";
    $("current-share").scrollIntoView({
      behavior: matchMedia("(prefers-reduced-motion: reduce)").matches
        ? "instant"
        : "smooth",
      block: "nearest",
    });
  } catch (error) {
    $("form-error").textContent = error.message;
    $("form-error").hidden = false;
  } finally {
    $("submit-button").disabled = false;
    $("submit-button").textContent = "Download";
  }
});
api("/api/config")
  .then((config) => {
    $("limits").textContent =
      `Up to ${config.maxDurationMinutes} min · ${config.maxFileMb} MB · ${config.maxVideosPerDay} downloads/day across this server (UTC) · Keeps the newest ${config.maxStoredVideos}`;
  })
  .catch(() => {});
renderRecent();
refresh();
