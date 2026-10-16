export async function api(path, options) {
  const response = await fetch(path, options);
  if (response.status === 204) return null;
  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    const error = new Error(
      data.error ||
        (response.status === 429
          ? "A few too many requests. Please wait a minute and try again."
          : response.status === 404
            ? "This share does not exist or has been deleted."
            : "The server could not complete the request. Please try again."),
    );
    error.status = response.status;
    throw error;
  }
  return data;
}
export async function copyLink(path, status) {
  const url = new URL(path, location.origin).href;
  try {
    await navigator.clipboard.writeText(url);
    status.textContent = "Link copied. Pass it along!";
  } catch {
    status.textContent = `Copy this link: ${url}`;
  }
}
export const stateLabel = (video) =>
  ({
    queued: "Waiting in the queue",
    downloading: `Downloading${video.progress > 0 ? ` · ${Math.round(video.progress)}%` : " your video"}`,
    processing: "Getting it ready to play",
    ready: "Ready to share",
    failed: "Couldn’t save this video",
  })[video.status] || "Loading";
export const done = (video) => ["ready", "failed"].includes(video.status);
export function element(tag, className, text) {
  const el = document.createElement(tag);
  if (className) el.className = className;
  if (text !== undefined) el.textContent = text;
  return el;
}
export function mediaInfo(video) {
  const seconds = Math.round(video.duration || 0);
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")} · ${(video.bytes / 1024 / 1024).toFixed(1)} MB`;
}
