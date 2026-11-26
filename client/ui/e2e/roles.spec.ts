import { api, ensureCommunity, ensureUser, expect, logIn, test } from "./support";

/** Alice (owner) makes Bob an admin: he can manage channels at once, and loses it when demoted. */

const COMMUNITY = "Chat E2E";
const Member = 3;

test.afterAll(async () => {
  // Other specs expect Bob to be a plain member.
  const communityId = await ensureCommunity(COMMUNITY);
  const bobId = (await api<{ id: string }>("/api/Users/me", { token: await ensureUser("bob") })).id;
  await api(`/api/communities/${communityId}/members/${bobId}/role`, { method: "PUT", token: await ensureUser("alice"), body: JSON.stringify({ role: Member }) }).catch(() => {});
});

test("The owner changes roles, and they apply live", async ({ browser }) => {
  await ensureCommunity(COMMUNITY);
  const a = await logIn(browser, "alice", COMMUNITY);
  const b = await logIn(browser, "bob", COMMUNITY);
  for (const page of [a, b]) await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  await expect(b.getByTestId("new-channel")).toHaveCount(0);
  await expect(b.getByTestId("member-role")).toHaveCount(0);

  const bobRole = a.locator('[data-testid=member][data-name="bob"]').getByTestId("member-role");
  await bobRole.selectOption({ label: "Admin" });
  await expect(b.getByTestId("new-channel")).toBeVisible();
  await expect(b.locator('[data-testid=member][data-name="bob"]').getByTestId("role-tag")).toHaveText("Admin");
  await expect(b.locator('[data-testid=member][data-name="alice"]').getByTestId("role-tag")).toHaveText("Owner");

  await bobRole.selectOption({ label: "Member" });
  await expect(b.getByTestId("new-channel")).toHaveCount(0);
  await expect(b.locator('[data-testid=member][data-name="bob"]').getByTestId("role-tag")).toHaveCount(0);
});
