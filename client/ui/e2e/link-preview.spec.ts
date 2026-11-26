import { ensureCommunity, expect, logIn, test } from "./support";

/**
 * A link in a message gets a preview card (fetched by the server, which only reaches public
 * addresses, #18). Uses example.com, which is stable and public; links to internal addresses
 * get no card.
 */

const COMMUNITY = "Chat E2E";

test("Links get a preview, internal ones don't", async ({ browser }) => {
  await ensureCommunity(COMMUNITY);
  const a = await logIn(browser, "alice", COMMUNITY);
  const b = await logIn(browser, "bob", COMMUNITY);
  for (const page of [a, b]) await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  const run = Date.now().toString(36);

  const input = a.getByLabel("Message", { exact: true });
  await input.fill(`look ${run} https://example.com/`);
  await input.press("Enter");
  await input.fill(`internal ${run} http://169.254.169.254/latest/meta-data/`);
  await input.press("Enter");

  const card = b.getByTestId("message").filter({ hasText: `look ${run}` }).getByTestId("link-preview");
  await expect(card).toContainText("Example Domain", { timeout: 15_000 });
  const internal = b.getByTestId("message").filter({ hasText: `internal ${run}` });
  await expect(internal).toBeVisible();
  // Give its preview request time to come back empty.
  await b.waitForTimeout(1000);
  await expect(internal.getByTestId("link-preview")).toHaveCount(0);
});
