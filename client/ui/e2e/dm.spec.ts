import { expect, test } from "@playwright/test";
import { ensureCommunity, logIn } from "./support";

/**
 * Alice & Bob direct messages in the new client: start a DM from the member list, the other
 * side gets an unread badge live, opening clears it, and replies arrive exactly once.
 */

const COMMUNITY = "Chat E2E";

test.beforeAll(() => ensureCommunity(COMMUNITY));

test("Alice DMs Bob: unread badge, delivery and reply", async ({ browser }) => {
  const alice = await logIn(browser, "alice", COMMUNITY);
  const bob = await logIn(browser, "bob", COMMUNITY);
  for (const page of [alice, bob]) await expect(page.locator(".dot.connected").last()).toBeVisible({ timeout: 15_000 });
  const run = Date.now().toString(36);

  // Alice opens a DM from the member list and writes.
  const bobRow = alice.locator('[data-testid="member"][data-name="bob"]');
  await bobRow.hover();
  await bobRow.getByRole("button", { name: /Message/ }).click();
  await expect(alice.locator("main header")).toHaveText(/@ bob/);
  const hello = `psst bob ${run}`;
  await alice.getByLabel("Message", { exact: true }).fill(hello);
  await alice.getByLabel("Message", { exact: true }).press("Enter");
  await expect(alice.getByTestId("message").filter({ hasText: hello })).toHaveCount(1);

  // Bob, still in the community, sees an unread badge on Home, then the conversation.
  await expect(bob.getByTestId("unread-total")).toBeVisible({ timeout: 10_000 });
  await bob.getByTestId("home").click();
  const conversation = bob.locator('[data-testid="conversation"][data-name="alice"]');
  await expect(conversation.getByTestId("unread")).toBeVisible();
  await conversation.click();
  await expect(bob.getByTestId("message").filter({ hasText: hello })).toHaveCount(1);
  await expect(conversation.getByTestId("unread")).toHaveCount(0);
  await expect(bob.getByTestId("unread-total")).toHaveCount(0);

  // Bob replies; Alice sees it once, live.
  const reply = `hey alice ${run}`;
  await bob.getByLabel("Message", { exact: true }).fill(reply);
  await bob.getByLabel("Message", { exact: true }).press("Enter");
  await expect(alice.getByTestId("message").filter({ hasText: reply })).toHaveCount(1, { timeout: 10_000 });
  await bob.waitForTimeout(500);
  await expect(bob.getByTestId("message").filter({ hasText: reply })).toHaveCount(1);
});
