import { api, ensureCommunity, ensureUser, expect, logIn, test } from "./support";

/** Unread badges on channels: counted live for channels you don't have open, cleared when you open them. */

const COMMUNITY = "Chat E2E";
const CHANNEL = "unread-e2e";

test.beforeAll(async () => {
  const communityId = await ensureCommunity(COMMUNITY);
  const alice = await ensureUser("alice");
  const channels = await api<{ name: string }[]>(`/api/communities/${communityId}/Channels`, { token: alice });
  if (!channels.some((c) => c.name === CHANNEL))
    await api(`/api/communities/${communityId}/Channels`, { method: "POST", token: alice, body: JSON.stringify({ name: CHANNEL, topic: null, type: 0 }) });
});

test("Channel unread badges", async ({ browser }) => {
  const alice = await logIn(browser, "alice", COMMUNITY);
  const bob = await logIn(browser, "bob", COMMUNITY);
  for (const page of [alice, bob]) await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const channel = (page: typeof bob) => page.locator(`[data-testid=channel][data-name="${CHANNEL}"]`);
  // Start from read (earlier runs may have left messages Bob never saw).
  await channel(bob).click();
  await expect(bob.locator("main[data-live=true]")).toBeVisible();
  await bob.getByTestId("channel").first().click();
  await expect(bob.locator("main[data-live=true]")).toBeVisible();
  await expect(channel(bob).getByTestId("channel-unread")).toHaveCount(0);

  const run = Date.now().toString(36);
  await channel(alice).click();
  await expect(alice.locator("main[data-live=true]")).toBeVisible();
  const input = alice.getByLabel("Message", { exact: true });
  for (const n of [1, 2]) {
    await input.fill(`unread ${n} ${run}`);
    await input.press("Enter");
  }
  // Bob is in another channel: the badge counts up live; Alice's own badge doesn't.
  await expect(channel(bob).getByTestId("channel-unread")).toHaveText("2");
  await expect(channel(alice).getByTestId("channel-unread")).toHaveCount(0);

  // Opening it clears it, and new messages while it's open don't count.
  await channel(bob).click();
  await expect(channel(bob).getByTestId("channel-unread")).toHaveCount(0);
  await expect(bob.locator("main[data-live=true]")).toBeVisible();
  await input.fill(`unread 3 ${run}`);
  await input.press("Enter");
  await expect(bob.getByTestId("message").filter({ hasText: `unread 3 ${run}` })).toBeVisible();
  await expect(channel(bob).getByTestId("channel-unread")).toHaveCount(0);

  // The server remembers: after a reload Bob still has nothing unread there.
  await bob.reload();
  await bob.getByTitle(COMMUNITY).click();
  await expect(bob.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  await expect(channel(bob).getByTestId("channel-unread")).toHaveCount(0);
});
