import { expect, test } from "@playwright/test";
import { ensureCommunity, logIn, newPage } from "./support";

/**
 * A new friend from invite to first message: Alice (server admin) creates an invite link, the
 * friend opens it, signs up, joins Alice's community and says hi; Alice sees them arrive live.
 * (Alice is the dev server's first user, so she's the admin.)
 */

const COMMUNITY = "Chat E2E";

test.beforeAll(() => ensureCommunity(COMMUNITY));

test("A friend joins with an invite link and says hi", async ({ browser }) => {
  const alice = await logIn(browser, "alice", COMMUNITY);
  await expect(alice.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });

  await alice.getByRole("button", { name: "Invite" }).click();
  await alice.getByRole("button", { name: "Create invite" }).click();
  const link = await alice.getByTestId("invite-link").inputValue();
  expect(link).toContain("invite=");
  await alice.getByTitle(COMMUNITY).click(); // back to the community to watch for the newcomer
  await expect(alice.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });

  // The friend opens the link: the sign-up form is prefilled with server and code.
  const name = `dave${Date.now().toString(36)}`;
  const dave = await newPage(browser);
  await dave.goto(link);
  await expect(dave.getByLabel("Invite code")).not.toHaveValue("");
  await dave.getByLabel("Username").fill(name);
  await dave.getByLabel("Email").fill(`${name}@test.com`);
  await dave.getByLabel("Password").fill("Password123!");
  await dave.getByRole("button", { name: "Create account" }).click();

  // Browse communities and join Alice's.
  await dave.getByTestId("browse").click();
  await dave.locator(`[data-testid="discover-community"][data-name="${COMMUNITY}"]`).getByRole("button", { name: "Join" }).click();
  await expect(dave.locator("main header")).toHaveText("# general", { timeout: 10_000 });
  await expect(dave.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });

  // Alice sees the newcomer in the member list, live, and their first message.
  await expect(alice.locator(`[data-testid="member"][data-name="${name}"]`)).toBeVisible({ timeout: 10_000 });
  const hi = `hi everyone, ${name} here`;
  await dave.getByLabel("Message", { exact: true }).fill(hi);
  await dave.getByLabel("Message", { exact: true }).press("Enter");
  await expect(alice.getByTestId("message").filter({ hasText: hi })).toHaveCount(1, { timeout: 10_000 });

  // The invite was single-use: it can't create a second account.
  const again = await newPage(browser);
  await again.goto(link);
  await again.getByLabel("Username").fill(`${name}x`);
  await again.getByLabel("Email").fill(`${name}x@test.com`);
  await again.getByLabel("Password").fill("Password123!");
  await again.getByRole("button", { name: "Create account" }).click();
  await expect(again.locator(".error")).toBeVisible();
});
