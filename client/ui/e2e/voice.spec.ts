import { expect, test, type Page } from "@playwright/test";
import { ensureCommunity, logIn } from "./support";

/**
 * "Alice & Bob as code" (#23) for voice (#52): two browsers with fake microphones join the same
 * voice channel. Each must see the other and detect them speaking (LiveKit audio levels from
 * Chrome's fake-mic beep), which proves server tokens, LiveKit, WebRTC and the UI end to end.
 */

const COMMUNITY = "Voice E2E";

test.beforeAll(() => ensureCommunity(COMMUNITY));

async function joinVoice(page: Page) {
  await page.getByTestId("voice-channel").first().click();
  await expect(page.getByTestId("voice-panel")).toHaveAttribute("data-status", "connected", { timeout: 15_000 });
}

test("Alice and Bob hear each other in a voice channel", async ({ browser }) => {
  const alice = await logIn(browser, "alice", COMMUNITY);
  const bob = await logIn(browser, "bob", COMMUNITY);
  // Presence: Bob, not in voice, sees Alice appear in the channel list (LiveKit webhook -> server -> hub).
  await expect(bob.getByTestId("voice-presence")).toHaveCount(0, { timeout: 15_000 });
  await joinVoice(alice);
  await expect(bob.locator('[data-testid="voice-presence"][data-name="alice"]')).toBeVisible({ timeout: 15_000 });
  await joinVoice(bob);

  for (const [page, other] of [[alice, "bob"], [bob, "alice"]] as const) {
    await expect(page.getByTestId("voice-member")).toHaveCount(2, { timeout: 15_000 });
    // The other participant's fake-mic tone must register as speech on this side.
    await expect(page.locator(`[data-testid="voice-member"][data-name="${other}"][data-speaking="true"]`)).toBeVisible({ timeout: 20_000 });
  }

  // Bob leaves: Alice sees only herself, and Bob's channel list shows Alice still in voice.
  await bob.getByRole("button", { name: "Leave" }).click();
  await expect(alice.getByTestId("voice-member")).toHaveCount(1, { timeout: 15_000 });
  await expect(bob.locator('[data-testid="voice-presence"][data-name="alice"]')).toBeVisible({ timeout: 15_000 });
  await expect(bob.locator('[data-testid="voice-presence"][data-name="bob"]')).toHaveCount(0, { timeout: 15_000 });

  // Alice leaves: nobody is in voice any more.
  await alice.getByRole("button", { name: "Leave" }).click();
  await expect(bob.getByTestId("voice-presence")).toHaveCount(0, { timeout: 15_000 });
});
