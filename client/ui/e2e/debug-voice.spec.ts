import { test } from "@playwright/test";
// Temporary diagnostics for the voice e2e test.
test.skip(!process.env.VOICE_DEBUG, "debug only");
test("debug levels", async ({ browser }) => {
  const SERVER = process.env.SNACKA_SERVER ?? "http://localhost:5117";
  const open = async (name: string) => {
    const page = await (await browser.newContext()).newPage();
    await page.goto("/");
    await page.getByLabel("Server").fill(SERVER);
    await page.getByLabel("Email").fill(`${name}@test.com`);
    await page.getByLabel("Password").fill("Password123!");
    await page.getByRole("button", { name: "Log in" }).click();
    await page.getByTitle("Voice E2E").click();
    await page.getByTestId("voice-channel").first().click();
    return page;
  };
  const alice = await open("alice");
  const bob = await open("bob");
  for (let i = 0; i < 6; i++) {
    await alice.waitForTimeout(1000);
    for (const [n, p] of [["alice", alice], ["bob", bob]] as const) {
      const info = await p.evaluate(() => {
        const r = (window as any).__snackaRoom;
        if (!r) return "no room";
        const remote = [...r.remoteParticipants.values()].map((x: any) => `${x.name}:lvl=${x.audioLevel.toFixed(3)},spk=${x.isSpeaking},tracks=${x.trackPublications.size}`);
        return `vis=${document.visibilityState} local:lvl=${r.localParticipant.audioLevel.toFixed(3)},spk=${r.localParticipant.isSpeaking},mic=${r.localParticipant.isMicrophoneEnabled} | ${remote.join(" ")}`;
      });
      console.log(`${i} ${n}: ${info}`);
    }
  }
});
