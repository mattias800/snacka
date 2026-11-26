import { ensureCommunity, expect, logIn, test } from "./support";

/** Threads and pins between Alice and Bob, live on both screens. */

const COMMUNITY = "Chat E2E";

test.beforeAll(() => ensureCommunity(COMMUNITY));

test("Thread replies and pins show up live", async ({ browser }) => {
  const alice = await logIn(browser, "alice", COMMUNITY);
  const bob = await logIn(browser, "bob", COMMUNITY);
  for (const page of [alice, bob]) await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const run = Date.now().toString(36);

  const input = alice.getByLabel("Message", { exact: true });
  await input.fill(`thread root ${run}`);
  await input.press("Enter");
  const bobsRoot = bob.locator("main").getByTestId("message").filter({ hasText: `thread root ${run}` });
  await expect(bobsRoot).toBeVisible();

  // Bob starts a thread on Alice's message.
  await bobsRoot.hover();
  await bobsRoot.getByTitle("Reply in thread").click();
  const bobsThread = bob.getByTestId("thread");
  await bobsThread.getByLabel("Thread reply").fill(`first reply ${run}`);
  await bobsThread.getByLabel("Thread reply").press("Enter");
  await expect(bobsThread.getByTestId("message").filter({ hasText: `first reply ${run}` })).toBeVisible();

  // Alice sees the reply count appear, but not the reply itself in the channel.
  const alicesRoot = alice.locator("main").getByTestId("message").filter({ hasText: `thread root ${run}` });
  await expect(alicesRoot.getByTestId("thread-link")).toHaveText("💬 1 reply");
  await expect(alice.locator("main").getByText(`first reply ${run}`)).toHaveCount(0);

  // She opens the thread and answers; Bob's open thread updates live.
  await alicesRoot.getByTestId("thread-link").click();
  const alicesThread = alice.getByTestId("thread");
  await expect(alicesThread.getByTestId("message").filter({ hasText: `first reply ${run}` })).toBeVisible();
  await alicesThread.getByLabel("Thread reply").fill(`second reply ${run}`);
  await alicesThread.getByLabel("Thread reply").press("Enter");
  await expect(bobsThread.getByTestId("message").filter({ hasText: `second reply ${run}` })).toBeVisible();
  await expect(bobsRoot.getByTestId("thread-link")).toHaveText("💬 2 replies");

  // Editing a thread reply updates it in the thread, and doesn't leak it into the channel list.
  // (By id: while editing, the text is in an input and no longer matches hasText.)
  const replyId = await bobsThread.getByTestId("message").filter({ hasText: `first reply ${run}` }).getAttribute("data-message-id");
  const reply = bobsThread.locator(`[data-message-id="${replyId}"]`);
  await reply.hover();
  await reply.getByTitle("Edit").click();
  await reply.getByLabel("Edit message").fill(`first reply edited ${run}`);
  await reply.getByLabel("Edit message").press("Enter");
  await expect(alicesThread.getByTestId("message").filter({ hasText: `first reply edited ${run}` })).toBeVisible();
  await expect(alice.locator("main").getByText(`first reply edited ${run}`)).toHaveCount(0);

  // Alice pins her message: Bob sees the pin mark and finds it under 📌.
  await alicesRoot.hover();
  await alicesRoot.getByTitle("Pin", { exact: true }).click();
  await expect(bobsRoot.getByTestId("pinned-mark")).toBeVisible();
  await bob.getByTestId("open-pins").click();
  const pins = bob.getByTestId("pins");
  await expect(pins.getByTestId("message").filter({ hasText: `thread root ${run}` })).toBeVisible();
  // Bob (a member) may not pin Alice's message.
  await bobsRoot.hover();
  await expect(bobsRoot.getByTitle("Unpin")).toHaveCount(0);

  // Unpinning removes it from Bob's open pin list.
  await alicesRoot.hover();
  await alicesRoot.getByTitle("Unpin").click();
  await expect(pins.getByTestId("message").filter({ hasText: `thread root ${run}` })).toHaveCount(0);
  await expect(bobsRoot.getByTestId("pinned-mark")).toHaveCount(0);
});
