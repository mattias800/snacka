import type { Page } from "@playwright/test";
import { api, ensureCommunity, ensureUser, expect, logIn, test } from "./support";

/**
 * Ivy is reading #general of one community while Jon writes elsewhere: mentions, replies and DMs
 * notify her, plain chatter only marks things unread. Own users and communities, so counts don't
 * mix with other specs.
 */

const HOME = "Notify A";
const OTHER = "Notify B";
const SIDE = "side-chat";
type Channel = { id: string; name: string; type: number };
type Message = { id: string };
const ids: Record<string, string> = {};

test.beforeAll(async () => {
  ids.home = await ensureCommunity(HOME, "ivy", "jon");
  ids.other = await ensureCommunity(OTHER, "ivy", "jon");
  const ivy = await ensureUser("ivy");
  const channels = await api<Channel[]>(`/api/communities/${ids.home}/Channels`, { token: ivy });
  ids.side = channels.find((c) => c.name === SIDE)?.id ?? (await api<Channel>(`/api/communities/${ids.home}/Channels`, { method: "POST", token: ivy, body: JSON.stringify({ name: SIDE, topic: null, type: 0 }) })).id;
  ids.otherGeneral = (await api<Channel[]>(`/api/communities/${ids.other}/Channels`, { token: ivy })).find((c) => c.type === 0)!.id;
});

/** Records notifications instead of showing them; permission is granted. */
async function fakeNotifications(page: Page) {
  await page.addInitScript(() => {
    type Note = { title: string; body: string; onclick: (() => void) | null };
    const w = window as unknown as { __notes: Note[]; Notification: unknown };
    w.__notes = [];
    w.Notification = class {
      static permission = "granted";
      static requestPermission = async () => "granted";
      title: string;
      body: string;
      onclick: (() => void) | null = null;
      constructor(title: string, options?: { body?: string }) {
        this.title = title;
        this.body = options?.body ?? "";
        w.__notes.push(this);
      }
      close() {}
    };
  });
}
const notes = (page: Page) => page.evaluate(() => (window as unknown as { __notes: { title: string; body: string }[] }).__notes.map((n) => `${n.title}: ${n.body}`));
const clickLastNote = (page: Page) => page.evaluate(() => (window as unknown as { __notes: { onclick: () => void }[] }).__notes.at(-1)!.onclick());

const post = async (channelId: string, content: string, replyToId: string | null = null) =>
  api<Message>(`/api/channels/${channelId}/messages`, { method: "POST", token: await ensureUser("jon"), body: JSON.stringify({ content, replyToId }) });

async function ivyReadingGeneral(browser: Parameters<typeof logIn>[0]) {
  const ivy = await logIn(browser, "ivy", HOME, fakeNotifications);
  await expect(ivy.locator("main[data-live=true]")).toBeVisible({ timeout: 15_000 });
  await expect(ivy.getByTestId("view-title")).toHaveText("# general");
  return ivy;
}

test("Mentions and replies notify; other messages only count as unread", async ({ browser }) => {
  const ivy = await ivyReadingGeneral(browser);
  const run = Date.now().toString(36);
  const side = ivy.locator(`[data-testid=channel][data-name="${SIDE}"]`);

  await post(ids.side, `just chatting ${run}`);
  await expect(side.getByTestId("channel-unread")).toHaveText("1");

  const ivyToken = await ensureUser("ivy");
  const mine = await api<Message>(`/api/channels/${ids.side}/messages`, { method: "POST", token: ivyToken, body: JSON.stringify({ content: `ivy asks ${run}`, replyToId: null }) });
  await post(ids.side, `@ivy look ${run}`);
  await post(ids.side, `answer ${run}`, mine.id);

  await expect.poll(() => notes(ivy)).toEqual([`jon mentioned you in #${SIDE}: @ivy look ${run}`, `jon replied to you in #${SIDE}: answer ${run}`]);
  await expect(side.getByTestId("channel-mentions")).toHaveText("@2");
  await expect(ivy).toHaveTitle("(2) Snacka");

  // Clicking the notification opens the channel, which reads it.
  await clickLastNote(ivy);
  await expect(ivy.getByTestId("view-title")).toHaveText(`# ${SIDE}`);
  await expect(side.getByTestId("channel-mentions")).toHaveCount(0);
  await expect(ivy).toHaveTitle("Snacka");
  const mention = ivy.getByTestId("message").filter({ hasText: `@ivy look ${run}` });
  await expect(mention).toHaveClass(/mentions-me/);
  await expect(mention.locator(".mention.me")).toHaveText("@ivy");
});

test("Other communities show unread and mentions on their icon", async ({ browser }) => {
  const ivy = await ivyReadingGeneral(browser);
  const other = ivy.locator(`[data-testid=community][data-name="${OTHER}"]`);
  const run = Date.now().toString(36);

  await post(ids.otherGeneral, `over here ${run}`);
  await expect(other.getByTestId("community-unread")).toBeVisible();
  // The server knows too: still there after a reload.
  await ivy.reload();
  await expect(other.getByTestId("community-unread")).toBeVisible();

  await post(ids.otherGeneral, `@everyone games tonight ${run}`);
  await expect(other.getByTestId("community-mentions")).toHaveText("1");

  await other.click();
  await expect(ivy.getByTestId("view-title")).toHaveText("# general");
  await expect(other.getByTestId("community-mentions")).toHaveCount(0);
  await expect(other.getByTestId("community-unread")).toHaveCount(0);
});

test("Direct messages notify unless the conversation is open", async ({ browser }) => {
  const ivy = await ivyReadingGeneral(browser);
  const jon = await ensureUser("jon");
  const ivyId = (await api<{ id: string }>("/api/Users/me", { token: await ensureUser("ivy") })).id;
  const conversation = await api<{ id: string }>(`/api/conversations/direct/${ivyId}`, { token: jon });
  const run = Date.now().toString(36);

  await api(`/api/conversations/${conversation.id}/messages`, { method: "POST", token: jon, body: JSON.stringify({ content: `psst ${run}` }) });

  await expect.poll(() => notes(ivy)).toEqual([`jon: psst ${run}`]);
  await clickLastNote(ivy);
  await expect(ivy.getByTestId("view-title")).toHaveText("@ jon");
  await expect(ivy.getByTestId("message").filter({ hasText: `psst ${run}` })).toBeVisible();

  // Now that she's looking at it, the next one just appears.
  await api(`/api/conversations/${conversation.id}/messages`, { method: "POST", token: jon, body: JSON.stringify({ content: `still there? ${run}` }) });
  await expect(ivy.getByTestId("message").filter({ hasText: `still there? ${run}` })).toBeVisible();
  expect(await notes(ivy)).toEqual([`jon: psst ${run}`]);
});
