import { expect, test, type Browser, type Page } from "@playwright/test";

/**
 * "Alice & Bob as code" (#23) for voice (#52): two browsers with fake microphones join the same
 * voice channel. Each must see the other and detect them speaking (LiveKit audio levels from
 * Chrome's fake-mic beep), which proves server tokens, LiveKit, WebRTC and the UI end to end.
 */

const SERVER = process.env.SNACKA_SERVER ?? "http://localhost:5117";
const PASSWORD = "Password123!";
const COMMUNITY = "Voice E2E";

async function api<T>(path: string, init: RequestInit & { token?: string } = {}): Promise<T> {
  const res = await fetch(`${SERVER}${path}`, {
    ...init,
    headers: { "Content-Type": "application/json", ...(init.token ? { Authorization: `Bearer ${init.token}` } : {}) },
  });
  if (!res.ok) throw new Error(`${init.method ?? "GET"} ${path}: ${res.status} ${await res.text()}`);
  return (res.status === 204 ? undefined : await res.json()) as T;
}

/** Logs in, registering first if needed (dev servers expose a bootstrap invite). */
async function ensureUser(name: string): Promise<string> {
  const email = `${name}@test.com`;
  const login = async () => (await api<{ accessToken: string }>("/api/Auth/login", { method: "POST", body: JSON.stringify({ email, password: PASSWORD }) })).accessToken;
  try {
    return await login();
  } catch {
    const { bootstrapInviteCode } = await api<{ bootstrapInviteCode: string | null }>("/api/health");
    await api("/api/Auth/register", { method: "POST", body: JSON.stringify({ username: name, email, password: PASSWORD, inviteCode: bootstrapInviteCode }) });
    return await login();
  }
}

test.beforeAll(async () => {
  const alice = await ensureUser("alice");
  const bob = await ensureUser("bob");
  type Community = { id: string; name: string };
  let community = (await api<Community[]>("/api/communities", { token: alice })).find((c) => c.name === COMMUNITY);
  community ??= await api<Community>("/api/communities", { method: "POST", token: alice, body: JSON.stringify({ name: COMMUNITY, description: null }) });
  const bobs = await api<Community[]>("/api/communities", { token: bob });
  if (!bobs.some((c) => c.id === community!.id)) await api(`/api/communities/${community.id}/join`, { method: "POST", token: bob });
});

async function openAs(browser: Browser, name: string): Promise<Page> {
  const page = await (await browser.newContext()).newPage();
  await page.goto("/");
  await page.getByLabel("Server").fill(SERVER);
  await page.getByLabel("Email").fill(`${name}@test.com`);
  await page.getByLabel("Password").fill(PASSWORD);
  await page.getByRole("button", { name: "Log in" }).click();
  await page.getByTitle(COMMUNITY).click();
  await page.getByTestId("voice-channel").first().click();
  await expect(page.getByTestId("voice-panel")).toHaveAttribute("data-status", "connected", { timeout: 15_000 });
  return page;
}

test("Alice and Bob hear each other in a voice channel", async ({ browser }) => {
  const alice = await openAs(browser, "alice");
  const bob = await openAs(browser, "bob");

  for (const [page, other] of [[alice, "bob"], [bob, "alice"]] as const) {
    await expect(page.getByTestId("voice-member")).toHaveCount(2, { timeout: 15_000 });
    // The other participant's fake-mic beep must register as speech on this side.
    await expect(page.locator(`[data-testid="voice-member"][data-name="${other}"][data-speaking="true"]`)).toBeVisible({ timeout: 20_000 });
  }

  // Bob leaves: Alice sees only herself.
  await bob.getByRole("button", { name: "Leave" }).click();
  await expect(alice.getByTestId("voice-member")).toHaveCount(1, { timeout: 15_000 });
});
