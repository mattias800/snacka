import { api, ensureCommunity, ensureUser, expect, logIn, solidPng, test } from "./support";

/**
 * Alice creates a channel (Bob sees it appear), then shares formatted text, an image and a file
 * in it; Bob sees the formatting, the image actually loads, and the file is downloadable.
 */

const COMMUNITY = "Chat E2E";
let communityId = "";
let channelId: string | null = null;

test.beforeAll(async () => {
  communityId = await ensureCommunity(COMMUNITY);
});

test.afterAll(async () => {
  // Don't let test channels pile up in the dev database.
  if (channelId) await api(`/api/communities/${communityId}/Channels/${channelId}`, { method: "DELETE", token: await ensureUser("alice") }).catch(() => {});
});

const PNG = solidPng(2, 2, [255, 0, 0]);

test("New channel, formatting, image and file attachments", async ({ browser }) => {
  const alice = await logIn(browser, "alice", COMMUNITY);
  const bob = await logIn(browser, "bob", COMMUNITY);
  for (const page of [alice, bob]) await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const run = Date.now().toString(36);
  const name = `e2e-${run}`;

  // Alice (owner) creates a channel; it shows up for Bob live.
  await alice.getByTestId("new-channel").click();
  await alice.getByLabel("Channel name").fill(name);
  await alice.getByRole("button", { name: "Create" }).click();
  await expect(alice.locator("main header")).toHaveText(`# ${name}`);
  await expect(bob.getByRole("button", { name: `# ${name}` })).toBeVisible({ timeout: 10_000 });
  channelId = await alice.evaluate(async ([server, cid, n]) => {
    const token = JSON.parse(localStorage.getItem("snacka.auth") ?? "{}").accessToken as string;
    const list = (await (await fetch(`${server}/api/communities/${cid}/Channels`, { headers: { Authorization: `Bearer ${token}` } })).json()) as { id: string; name: string }[];
    return list.find((c) => c.name === n)?.id ?? null;
  }, [process.env.SNACKA_SERVER ?? "http://localhost:5117", communityId, name] as const);
  await bob.getByRole("button", { name: `# ${name}` }).click();
  await expect(bob.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  await expect(alice.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });

  // Formatting: rendered as elements, not raw markup.
  const input = alice.getByLabel("Message", { exact: true });
  await input.fill(`**gg** wp \`/kick bot\` https://example.com/x?y=1 ${run}`);
  await input.press("Enter");
  const formatted = bob.getByTestId("message").filter({ hasText: run }).first();
  await expect(formatted.locator("strong", { hasText: "gg" })).toBeVisible({ timeout: 10_000 });
  await expect(formatted.locator("code", { hasText: "/kick bot" })).toBeVisible();
  await expect(formatted.locator('a[href="https://example.com/x?y=1"]')).toBeVisible();

  // Multi-line: Shift+Enter makes a new line; a code block keeps its lines.
  await input.pressSequentially("```", { delay: 5 });
  await input.press("Shift+Enter");
  await input.pressSequentially(`line one ${run}`, { delay: 5 });
  await input.press("Shift+Enter");
  await input.pressSequentially("line two", { delay: 5 });
  await input.press("Shift+Enter");
  await input.pressSequentially("```", { delay: 5 });
  await input.press("Enter");
  const block = bob.getByTestId("message").filter({ hasText: `line one ${run}` }).locator("pre");
  await expect(block).toHaveText(`line one ${run}\nline two`, { timeout: 10_000 });

  // Attachments: an image and a text file, with a caption.
  await alice.getByTestId("file-input").setInputFiles([
    { name: "screenshot.png", mimeType: "image/png", buffer: PNG },
    { name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from(`meet at 8 ${run}`) },
  ]);
  await expect(alice.getByTestId("pending-file")).toHaveCount(2);
  await input.fill(`files ${run}`);
  await input.press("Enter");
  await expect(alice.getByTestId("pending-file")).toHaveCount(0);

  const shared = bob.getByTestId("message").filter({ hasText: `files ${run}` });
  const image = shared.getByTestId("attachment-image");
  await expect(image).toBeVisible({ timeout: 10_000 });
  await expect.poll(() => image.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBe(2);
  const file = shared.getByTestId("attachment-file");
  await expect(file).toContainText("notes.txt");
  const download = bob.waitForEvent("download");
  await file.click();
  const saved = await (await download).path();
  const { readFileSync } = await import("node:fs");
  expect(readFileSync(saved, "utf8")).toBe(`meet at 8 ${run}`);
});
