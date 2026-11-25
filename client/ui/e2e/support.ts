import { test as base, type Browser, type BrowserContext, type Page } from "@playwright/test";

export { expect } from "@playwright/test";

/** Shared setup for multi-user e2e tests: Alice and Bob against a running dev server. */

export const SERVER = process.env.SNACKA_SERVER ?? "http://localhost:5117";
export const PASSWORD = "Password123!";

export async function api<T>(path: string, init: RequestInit & { token?: string } = {}): Promise<T> {
  const res = await fetch(`${SERVER}${path}`, {
    ...init,
    headers: { "Content-Type": "application/json", ...(init.token ? { Authorization: `Bearer ${init.token}` } : {}) },
  });
  if (!res.ok) throw new Error(`${init.method ?? "GET"} ${path}: ${res.status} ${await res.text()}`);
  return (res.status === 204 ? undefined : await res.json()) as T;
}

/** Logs in, registering first if needed (dev servers expose a bootstrap invite). Returns an access token. */
export async function ensureUser(name: string): Promise<string> {
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

/** A community owned by Alice with Bob as a member. Returns its id. */
export async function ensureCommunity(name: string): Promise<string> {
  const alice = await ensureUser("alice");
  const bob = await ensureUser("bob");
  type Community = { id: string; name: string };
  let community = (await api<Community[]>("/api/communities", { token: alice })).find((c) => c.name === name);
  community ??= await api<Community>("/api/communities", { method: "POST", token: alice, body: JSON.stringify({ name, description: null }) });
  const bobs = await api<Community[]>("/api/communities", { token: bob });
  if (!bobs.some((c) => c.id === community!.id)) await api(`/api/communities/${community.id}/join`, { method: "POST", token: bob });
  return community.id;
}

/** Contexts opened by a test; closed after it so users go offline (otherwise they'd stay "online" across tests). */
const openContexts: BrowserContext[] = [];

export async function newPage(browser: Browser): Promise<Page> {
  const context = await browser.newContext();
  openContexts.push(context);
  return context.newPage();
}

/**
 * Use this `test` in every spec: an automatic fixture closes the test's browsers afterwards.
 * (A plain afterEach in this module would only attach to the first spec file importing it.)
 */
export const test = base.extend<{ closeBrowsers: void }>({
  closeBrowsers: [
    // eslint-disable-next-line no-empty-pattern -- Playwright fixtures must destructure
    async ({}, use) => {
      await use();
      await Promise.all(openContexts.splice(0).map((c) => c.close()));
    },
    { auto: true },
  ],
});

/** A fresh browser context logged in as `name`, with `community` open. */
export async function logIn(browser: Browser, name: string, community: string): Promise<Page> {
  const page = await newPage(browser);
  await page.goto("/");
  await page.getByLabel("Server").fill(SERVER);
  await page.getByLabel("Email").fill(`${name}@test.com`);
  await page.getByLabel("Password").fill(PASSWORD);
  await page.getByRole("button", { name: "Log in" }).click();
  await page.getByTitle(community).click();
  return page;
}
