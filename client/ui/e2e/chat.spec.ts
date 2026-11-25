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
  const input = page.getByLabel("Message", { exact: true });
  await input.fill(text);
  await input.press("Enter");
}

test("Alice and Bob see each other's messages exactly once", async ({ browser }) => {
  const alice = await logIn(browser, "alice", COMMUNITY);
  const bob = await logIn(browser, "bob", COMMUNITY);
  for (const page of [alice, bob]) await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });

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

  await expect(bob.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const after = `back again ${run}`;
  await send(alice, after);
  await expect(messages(bob, after)).toHaveCount(1, { timeout: 10_000 });
});

test("Reactions, edits and deletes show up on the other screen", async ({ browser }) => {
  const alice = await logIn(browser, "alice", COMMUNITY);
  const bob = await logIn(browser, "bob", COMMUNITY);
  for (const page of [alice, bob]) await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const run = Date.now().toString(36);
  const text = `pizza tonight? ${run}`;
  await send(alice, text);
  const onBob = messages(bob, text);
  await expect(onBob).toHaveCount(1, { timeout: 10_000 });

  // Bob reacts; Alice sees the count.
  await onBob.hover();
  await onBob.getByTitle("React 👍").click();
  const chip = messages(alice, text).locator('[data-testid="reaction"][data-emoji="👍"]');
  await expect(chip).toHaveAttribute("data-count", "1", { timeout: 10_000 });

  // Alice reacts too (count 2), then Bob takes his back (count 1).
  await chip.click();
  await expect(onBob.locator('[data-testid="reaction"][data-emoji="👍"]')).toHaveAttribute("data-count", "2", { timeout: 10_000 });
  await onBob.locator('[data-testid="reaction"][data-emoji="👍"]').click();
  await expect(chip).toHaveAttribute("data-count", "1", { timeout: 10_000 });

  // Alice edits; Bob sees the new text, marked edited.
  const edited = `pizza at 7? ${run}`;
  const mine = messages(alice, text);
  await mine.hover();
  await mine.getByTitle("Edit").click();
  await alice.getByLabel("Edit message").fill(edited);
  await alice.getByLabel("Edit message").press("Enter");
  await expect(messages(bob, edited)).toHaveCount(1, { timeout: 10_000 });
  await expect(messages(bob, edited)).toContainText("(edited)");
  await expect(messages(bob, text)).toHaveCount(0);

  // Alice deletes; it disappears for Bob.
  alice.on("dialog", (d) => void d.accept());
  await messages(alice, edited).hover();
  await messages(alice, edited).getByTitle("Delete").click();
  await expect(messages(bob, edited)).toHaveCount(0, { timeout: 10_000 });
  await expect(messages(alice, edited)).toHaveCount(0);
});

test("Typing indicator, replies and online status", async ({ browser }) => {
  const alice = await logIn(browser, "alice", COMMUNITY);
  const bob = await logIn(browser, "bob", COMMUNITY);
  for (const page of [alice, bob]) await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const run = Date.now().toString(36);

  // Alice sees Bob online.
  const bobOnAlice = alice.locator('[data-testid="member"][data-name="bob"] .dot');
  await expect(bobOnAlice).toHaveClass(/connected/, { timeout: 10_000 });

  // Alice types: Bob sees it; when she sends, it goes away.
  const question = `anyone up for a game? ${run}`;
  await alice.getByLabel("Message", { exact: true }).pressSequentially("anyone up", { delay: 20 });
  await expect(bob.getByTestId("typing")).toContainText("alice is typing", { timeout: 10_000 });
  await alice.getByLabel("Message", { exact: true }).fill(question);
  await alice.getByLabel("Message", { exact: true }).press("Enter");
  await expect(messages(bob, question)).toHaveCount(1, { timeout: 10_000 });
  await expect(bob.getByTestId("typing")).toHaveText("");

  // Bob replies to that message: Alice sees the quote.
  await messages(bob, question).hover();
  await messages(bob, question).getByTitle("Reply").click();
  await expect(bob.getByTestId("replying")).toContainText("alice");
  const answer = `me! ${run}`;
  await send(bob, answer);
  const onAlice = messages(alice, answer);
  await expect(onAlice).toHaveCount(1, { timeout: 10_000 });
  await expect(onAlice.getByTestId("reply-quote")).toContainText(question);
  await expect(bob.getByTestId("replying")).toHaveCount(0);

  // Bob closes the app: Alice sees him go offline.
  await bob.context().close();
  await expect(bobOnAlice).not.toHaveClass(/connected/, { timeout: 15_000 });
});
