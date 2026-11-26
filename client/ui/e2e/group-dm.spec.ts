import { ensureCommunity, expect, logIn, test } from "./support";

/** Kim starts a group with Lea and Max; a message reaches both, and the group shows up for them. */

const COMMUNITY = "Group E2E";

test.beforeAll(async () => {
  await ensureCommunity(COMMUNITY, "kim", "lea");
  await ensureCommunity(COMMUNITY, "kim", "max");
});

test("Starting a group conversation", async ({ browser }) => {
  const run = Date.now().toString(36);
  const kim = await logIn(browser, "kim", COMMUNITY);
  const lea = await logIn(browser, "lea", COMMUNITY);
  const max = await logIn(browser, "max", COMMUNITY);
  for (const page of [kim, lea, max]) await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });

  await kim.getByTestId("home").click();
  await kim.getByTestId("new-group").click();
  const panel = kim.getByTestId("new-group-panel");
  await panel.getByLabel("Group name").fill(`raid ${run}`);
  await panel.getByLabel("lea", { exact: true }).check();
  await panel.getByLabel("max", { exact: true }).check();
  await panel.getByRole("button", { name: "Start group with 2" }).click();
  await expect(kim.getByTestId("view-title")).toHaveText(`@ raid ${run}`);

  const input = kim.getByLabel("Message", { exact: true });
  await input.fill(`ready? ${run}`);
  await input.press("Enter");

  for (const page of [lea, max]) {
    await page.getByTestId("home").click();
    const group = page.locator(`[data-testid=conversation][data-name="raid ${run}"]`);
    await expect(group).toBeVisible();
    await group.click();
    await expect(page.getByTestId("message").filter({ hasText: `ready? ${run}` })).toBeVisible();
  }
});
