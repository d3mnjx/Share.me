import { chromium } from "playwright";
import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
const base = process.argv[2];
await mkdir("test-results", { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 1120 },
  });
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  page.on("console", (msg) => {
    if (msg.type() === "error") errors.push(msg.text());
  });
  await page.goto(base);
  await page.screenshot({
    path: "test-results/home-desktop.png",
    fullPage: true,
  });
  await page.getByLabel("Video URL").fill("https://example.com/browser");
  await page.getByRole("button", { name: "Download", exact: true }).click();
  await page
    .getByRole("heading", { name: "Ready to share" })
    .waitFor({ timeout: 20000 });
  await page.screenshot({
    path: "test-results/share-ready.png",
    fullPage: true,
  });
  const sharePath = await page
    .getByRole("link", { name: "Watch video" })
    .getAttribute("href");
  await page.getByRole("link", { name: "Watch video" }).click();
  await page.locator("video").waitFor();
  await page.waitForFunction(
    () => document.querySelector("video").readyState >= 2,
  );
  assert.ok(
    await page.locator("video").evaluate(async (video) => {
      video.muted = true;
      await video.play();
      return video.videoWidth > 0;
    }),
  );
  assert.equal(
    await page.locator("video").evaluate((video) => video.loop),
    true,
  );
  await page.locator("video").evaluate((video) => {
    video.currentTime = video.duration - 0.2;
  });
  await page.waitForFunction(() => {
    const video = document.querySelector("video");
    return video.currentTime < 0.5 && !video.paused;
  });
  await page.screenshot({
    path: "test-results/watch-desktop.png",
    fullPage: true,
  });
  // A fresh visitor has no local management token and can still view the share.
  const mobile = await browser.newContext({
    viewport: { width: 390, height: 844 },
    isMobile: true,
    hasTouch: true,
  });
  const phone = await mobile.newPage();
  await phone.goto(base + sharePath);
  await phone.locator("video").waitFor();
  await phone.waitForFunction(
    () => document.querySelector("video").readyState >= 2,
  );
  for (const viewport of [
    { width: 320, height: 568 },
    { width: 390, height: 844 },
    { width: 520, height: 900 },
    { width: 844, height: 390 },
  ]) {
    await phone.setViewportSize(viewport);
    const layout = await phone.locator("video").evaluate((video) => {
      const bounds = video.getBoundingClientRect();
      return {
        left: bounds.left,
        right: bounds.right,
        ratio: bounds.width / bounds.height,
        mediaRatio: video.videoWidth / video.videoHeight,
        overflow: document.documentElement.scrollWidth > innerWidth,
      };
    });
    assert.ok(Math.abs(layout.left) < 1);
    assert.ok(Math.abs(layout.right - viewport.width) < 1);
    assert.ok(Math.abs(layout.ratio - layout.mediaRatio) < 0.01);
    assert.equal(layout.overflow, false);
  }
  await phone.setViewportSize({ width: 390, height: 844 });
  assert.ok(
    await phone.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  );
  await phone.screenshot({
    path: "test-results/watch-mobile.png",
    fullPage: true,
  });
  await phone.goto(base);
  assert.ok(
    await phone.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  );
  await phone.screenshot({
    path: "test-results/home-mobile.png",
    fullPage: true,
  });
  await page.goto(base);
  await page
    .getByRole("link", { name: "A little moment worth sharing" })
    .waitFor();
  page.on("dialog", (dialog) => dialog.accept());
  await page
    .getByRole("button", { name: "Delete A little moment worth sharing" })
    .click();
  await page.getByText("No downloads yet.").waitFor();
  assert.deepEqual(errors, []);
  console.log(
    "PASS browser submission, playback, visitor access, mobile layout and deletion; screenshots in test-results/",
  );
} finally {
  await browser.close();
}
