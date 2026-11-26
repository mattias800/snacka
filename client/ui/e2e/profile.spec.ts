import { api, ensureCommunity, ensureUser, expect, logIn, solidPng, test } from "./support";

/**
 * Dana changes her display name and picture in settings; Erik sees both change on her message and
 * in the member list without reloading. Own users, so other specs' "alice"/"bob" names stay put.
 */

const COMMUNITY = "Profile E2E";

test.beforeAll(() => ensureCommunity(COMMUNITY, "dana", "erik"));

test.afterAll(async () => {
  const token = await ensureUser("dana");
  await api("/api/Users/me", { method: "PUT", token, body: JSON.stringify({ username: null, displayName: "", status: null }) }).catch(() => {});
  await api("/api/Users/me/avatar", { method: "DELETE", token }).catch(() => {});
});

const PNG = solidPng(2, 2, [255, 0, 0]);

test("Name and picture changes reach others live", async ({ browser }) => {
  const dana = await logIn(browser, "dana", COMMUNITY);
  const erik = await logIn(browser, "erik", COMMUNITY);
  for (const page of [dana, erik]) await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const run = Date.now().toString(36);

  const input = dana.getByLabel("Message", { exact: true });
  await input.fill(`hello ${run}`);
  await input.press("Enter");
  const message = erik.getByTestId("message").filter({ hasText: `hello ${run}` });
  await expect(message.getByTestId("author")).toHaveText("dana");
  const member = erik.locator('[data-testid=member][data-name="dana"]');

  // Display name
  await dana.getByTestId("open-settings").click();
  await dana.getByLabel("Display name").fill(`Dana ${run}`);
  await dana.getByRole("button", { name: "Save name" }).click();
  await expect(message.getByTestId("author")).toHaveText(`Dana ${run}`);
  await expect(member).toContainText(`Dana ${run}`);
  await expect(dana.getByTestId("open-settings")).toContainText(`Dana ${run}`);

  // Picture: processed by the server, then loaded by Erik's client with his token.
  await dana.getByTestId("avatar-input").setInputFiles({ name: "me.png", mimeType: "image/png", buffer: PNG });
  await expect(dana.getByTestId("settings-status")).toHaveText("Picture updated.");
  const picture = message.getByTestId("avatar-image");
  await expect(picture).toBeVisible();
  await expect.poll(() => picture.evaluate((img) => (img as HTMLImageElement).naturalWidth)).toBeGreaterThan(2); // scaled up to the avatar size
  await expect(member.getByTestId("avatar-image")).toBeVisible();

  // Removing it brings the initial back.
  await dana.getByRole("button", { name: "Remove" }).click();
  await expect(picture).toHaveCount(0);
  await expect(message.getByTestId("author")).toHaveText(`Dana ${run}`);
});
