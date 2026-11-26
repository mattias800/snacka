import type { Page } from "@playwright/test";
import { SERVER, ensureCommunity, expect, logIn, test } from "./support";

/**
 * Staying signed in (#15): access tokens live an hour, so the client renews them with the refresh
 * token, which the server rotates on every use. Its own user, since these tests sign out.
 */

const COMMUNITY = "Session E2E";

test.beforeAll(() => ensureCommunity(COMMUNITY, "gus", "hal"));

type Stored = { accessToken: string; refreshToken: string; expiresAt: string };
const stored = (page: Page) => page.evaluate(() => JSON.parse(localStorage.getItem("snacka.auth") ?? "null") as Stored | null);
const tamper = (page: Page, change: Partial<Stored>) =>
  page.evaluate((c) => {
    const a = JSON.parse(localStorage.getItem("snacka.auth")!) as Record<string, unknown>;
    localStorage.setItem("snacka.auth", JSON.stringify({ ...a, ...c }));
  }, change);
const refresh = (refreshToken: string) =>
  fetch(`${SERVER}/api/Auth/refresh`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ refreshToken }) });

async function signedIn(page: Page) {
  await page.getByTitle(COMMUNITY).click();
  await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
}

test("An access token the server rejects is renewed without signing out", async ({ browser }) => {
  // What a server restart with a new signing key looks like to the client.
  const page = await logIn(browser, "gus", COMMUNITY);
  await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const before = (await stored(page))!;

  await tamper(page, { accessToken: `${before.accessToken}x` });
  await page.reload();

  await signedIn(page);
  expect((await stored(page))!.refreshToken).not.toBe(before.refreshToken);
});

test("An expiring access token is renewed before it's used", async ({ browser }) => {
  const page = await logIn(browser, "gus", COMMUNITY);
  await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const before = (await stored(page))!;
  const unauthorized: string[] = [];
  page.on("response", (r) => r.status() === 401 && unauthorized.push(r.url()));

  await tamper(page, { expiresAt: new Date(Date.now() + 10_000).toISOString() });
  await page.reload();

  await signedIn(page);
  expect((await stored(page))!.accessToken).not.toBe(before.accessToken);
  expect(unauthorized).toEqual([]);
});

test("Logging out ends the session on the server", async ({ browser }) => {
  const page = await logIn(browser, "gus", COMMUNITY);
  await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const { refreshToken } = (await stored(page))!;

  await page.getByRole("button", { name: "Log out" }).click();

  await expect(page.getByRole("button", { name: "Log in" })).toBeVisible();
  expect(await stored(page)).toBeNull();
  expect((await refresh(refreshToken)).status).toBe(401);
});

test("A revoked session goes back to the login screen", async ({ browser }) => {
  const page = await logIn(browser, "gus", COMMUNITY);
  await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });

  await tamper(page, { accessToken: "invalid", refreshToken: "revoked" });
  await page.reload();

  await expect(page.getByRole("button", { name: "Log in" })).toBeVisible({ timeout: 15_000 });
});
