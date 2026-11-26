import { ensureCommunity, expect, logIn, test } from "./support";

/** The emoji picker: any emoji as a reaction (seen by the other side), and into the composer. */

const COMMUNITY = "Chat E2E";

test("Reacting and writing with the emoji picker", async ({ browser }) => {
  await ensureCommunity(COMMUNITY);
  const a = await logIn(browser, "alice", COMMUNITY);
  const b = await logIn(browser, "bob", COMMUNITY);
  for (const page of [a, b]) await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const run = Date.now().toString(36);

  // Composer: the emoji lands at the cursor.
  const input = a.getByLabel("Message", { exact: true });
  await input.fill(`gg ${run}`);
  await a.getByTestId("composer-emoji").click();
  await a.getByTestId("emoji-picker").getByRole("button", { name: "🎮" }).click();
  await expect(input).toHaveValue(`gg ${run}🎮`);
  await input.press("Enter");

  // Reaction from the picker, seen by Bob.
  const onBob = b.getByTestId("message").filter({ hasText: `gg ${run}🎮` });
  await expect(onBob).toBeVisible();
  await onBob.hover();
  await onBob.getByTestId("add-reaction").click();
  await b.getByTestId("emoji-picker").getByRole("button", { name: "🏆" }).click();
  await expect(b.getByTestId("emoji-picker")).toHaveCount(0);
  await expect(a.getByTestId("message").filter({ hasText: `gg ${run}🎮` }).getByRole("button", { name: /🏆\s*1/ })).toBeVisible();
});
