import { expect, test, type Page } from "@playwright/test";
import { ensureCommunity, logIn } from "./support";

/**
 * Alice & Bob chat in the new client (#23): each message shows up exactly once on both screens,
 * live, and survives a reload (reconnect + history) without duplicates.
 */

const COMMUNITY = "Chat E2E";

test.beforeAll(() => ensureCommunity(COMMUNITY));

const messages = (page: Page, text: string) => page.getByTestId("message").filter({ hasText: text });

async function send(page: Page, text: string) {
  const input = page.getByPlaceholder(/^Message #/);
  await input.fill(text);
  await input.press("Enter");
}

test("Alice and Bob see each other's messages exactly once", async ({ browser }) => {
  const alice = await logIn(browser, "alice", COMMUNITY);
  const bob = await logIn(browser, "bob", COMMUNITY);
  for (const page of [alice, bob]) await expect(page.locator(".dot.connected")).toBeVisible({ timeout: 15_000 });

  const run = Date.now().toString(36);
  const hello = `hello bob ${run}`;
  const reply = `hi alice ${run}`;

  await send(alice, hello);
  await expect(messages(bob, hello)).toHaveCount(1, { timeout: 10_000 });
  await send(bob, reply);
  await expect(messages(alice, reply)).toHaveCount(1, { timeout: 10_000 });

  // Own messages: the POST response and the realtime echo must not both show.
  await alice.waitForTimeout(500);
  for (const page of [alice, bob]) {
    await expect(messages(page, hello)).toHaveCount(1);
    await expect(messages(page, reply)).toHaveCount(1);
  }

  // Bob reloads (reconnect): history has both, once, in order, and live delivery still works.
  await bob.reload();
  await bob.getByTitle(COMMUNITY).click();
  await expect(messages(bob, reply)).toHaveCount(1, { timeout: 10_000 });
  await expect(messages(bob, hello)).toHaveCount(1);
  const ids = await bob.getByTestId("message").evaluateAll((els) => els.map((e) => e.textContent ?? ""));
  expect(ids.findIndex((t) => t.includes(hello))).toBeLessThan(ids.findIndex((t) => t.includes(reply)));

  await expect(bob.locator(".dot.connected")).toBeVisible({ timeout: 15_000 });
  const after = `back again ${run}`;
  await send(alice, after);
  await expect(messages(bob, after)).toHaveCount(1, { timeout: 10_000 });
});
