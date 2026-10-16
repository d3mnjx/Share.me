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
  await page.getByRole("button", { name: "Download & share" }).click();
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
  await page.getByText("No videos submitted from this browser yet.").waitFor();
  assert.deepEqual(errors, []);
  console.log(
    "PASS browser submission, playback, visitor access, mobile layout and deletion; screenshots in test-results/",
  );
} finally {
  await browser.close();
}
