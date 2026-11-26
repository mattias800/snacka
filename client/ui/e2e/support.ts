import { crc32, deflateSync } from "node:zlib";
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

/** A community owned by `ownerName` (default Alice) with `memberName` (default Bob) as a member. Returns its id. */
export async function ensureCommunity(name: string, ownerName = "alice", memberName = "bob"): Promise<string> {
  const alice = await ensureUser(ownerName);
  const bob = await ensureUser(memberName);
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

/** A fresh browser context logged in as `name`, with `community` open. `setup` runs before the app loads. */
export async function logIn(browser: Browser, name: string, community: string, setup?: (page: Page) => Promise<void>): Promise<Page> {
  const page = await newPage(browser);
  await setup?.(page);
  await page.goto("/");
  await page.getByLabel("Server").fill(SERVER);
  await page.getByLabel("Email").fill(`${name}@test.com`);
  await page.getByLabel("Password").fill(PASSWORD);
  await page.getByRole("button", { name: "Log in" }).click();
  await page.getByTitle(community).click();
  return page;
}

/** A valid PNG of one colour (browsers forgive broken checksums; the server's decoder doesn't). */
export function solidPng(width: number, height: number, [r, g, b]: [number, number, number]): Buffer {
  const chunk = (type: string, data: Buffer) => {
    const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const out = Buffer.alloc(body.length + 8);
    out.writeUInt32BE(data.length, 0);
    body.copy(out, 4);
    out.writeUInt32BE(crc32(body), body.length + 4);
    return out;
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 2, 0, 0, 0], 8); // 8-bit RGB
  const row = Buffer.concat([Buffer.from([0]), Buffer.from(Array.from({ length: width }, () => [r, g, b]).flat())]);
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", header),
    chunk("IDAT", deflateSync(Buffer.concat(Array.from({ length: height }, () => row)))),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}
