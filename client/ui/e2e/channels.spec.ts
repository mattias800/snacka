import { api, ensureCommunity, ensureUser, expect, logIn, test } from "./support";

/**
 * Alice (owner) renames and deletes a channel; Bob, who has it open, sees the new name and is
 * moved off it when it goes. Bob (a member) can't manage channels.
 */

const COMMUNITY = "Chat E2E";
type Channel = { id: string; name: string };

test("Renaming and deleting a channel reach everyone live", async ({ browser }) => {
  const communityId = await ensureCommunity(COMMUNITY);
  const run = Date.now().toString(36);
  const alice = await ensureUser("alice");
  const channel = await api<Channel>(`/api/communities/${communityId}/Channels`, { method: "POST", token: alice, body: JSON.stringify({ name: `temp-${run}`, topic: null, type: 0 }) });

  const a = await logIn(browser, "alice", COMMUNITY);
  const b = await logIn(browser, "bob", COMMUNITY);
  for (const page of [a, b]) {
    await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
    await page.locator(`[data-testid=channel][data-name="temp-${run}"]`).click();
    await expect(page.getByTestId("view-title")).toHaveText(`# temp-${run}`);
  }
  await expect(b.getByTestId("rename-channel")).toHaveCount(0);
  await expect(b.getByTestId("new-channel")).toHaveCount(0);

  await a.getByTestId("rename-channel").click();
  await a.getByLabel("Channel name").fill(`renamed-${run}`);
  await a.getByRole("button", { name: "Save" }).click();
  for (const page of [a, b]) {
    await expect(page.getByTestId("view-title")).toHaveText(`# renamed-${run}`);
    await expect(page.locator(`[data-testid=channel][data-name="renamed-${run}"]`)).toBeVisible();
  }

  a.once("dialog", (d) => void d.accept());
  await a.getByTestId("delete-channel").click();
  for (const page of [a, b]) {
    await expect(page.locator(`[data-testid=channel][data-name="renamed-${run}"]`)).toHaveCount(0);
    await expect(page.getByTestId("view-title")).toHaveText("# general");
  }
  expect((await api<Channel[]>(`/api/communities/${communityId}/Channels`, { token: alice })).some((c) => c.id === channel.id)).toBe(false);
});
