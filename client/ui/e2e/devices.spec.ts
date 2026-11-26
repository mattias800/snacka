import type { Room } from "livekit-client";
import { ensureCommunity, expect, logIn, test } from "./support";

/** Choosing a microphone in Settings: remembered, and used by the next call (Chrome's fake devices). */

const COMMUNITY = "Voice E2E";

test("The chosen microphone is remembered and used in voice", async ({ browser }) => {
  await ensureCommunity(COMMUNITY);
  const page = await logIn(browser, "alice", COMMUNITY);
  await expect(page.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });

  await page.getByTestId("open-settings").click();
  const mic = page.getByLabel("Microphone");
  // Chrome's fake devices: a default and at least one more.
  await expect.poll(() => mic.locator("option").count()).toBeGreaterThan(1);
  const options = await mic.locator("option").evaluateAll((els) => els.map((e) => (e as HTMLOptionElement).value));
  const chosen = options[options.length - 1];
  await mic.selectOption(chosen);

  await page.reload();
  await page.getByTestId("open-settings").click();
  await expect(page.getByLabel("Microphone")).toHaveValue(chosen);

  await page.getByTitle(COMMUNITY).click();
  await page.getByTestId("voice-channel").first().click();
  await expect(page.getByTestId("voice-panel")).toHaveAttribute("data-status", "connected", { timeout: 15_000 });
  const active = await page.evaluate(() => (window as unknown as { __snackaRoom: Room }).__snackaRoom.getActiveDevice("audioinput"));
  expect(active).toBe(chosen);
});
