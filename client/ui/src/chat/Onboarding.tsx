import { useEffect, useState } from "react";
import { ok, type Api, type Schemas } from "../api/client";
import { inNativeApp } from "../StreamTest";

type Community = Schemas["CommunityResponse"];
type Invite = Schemas["ServerInviteResponse"];

/**
 * Communities on this server the user can join, and creating a new one. Everyone with an
 * account may join any community: the server itself is invite-only (see InviteFriends).
 */
export function BrowseCommunities(props: { api: Api; onOpen: (c: Community) => void }) {
  const { api } = props;
  const [list, setList] = useState<Community[] | null>(null);
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api
      .GET("/api/communities/discover")
      .then(ok)
      .then(setList)
      .catch((e: Error) => setError(e.message));
  }, [api]);

  const join = async (c: Community) => {
    try {
      const res = await api.POST("/api/communities/{communityId}/join", { params: { path: { communityId: c.id } } });
      if (!res.response.ok) throw new Error(`Couldn't join: ${res.response.status}`);
      props.onOpen(c);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const create = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;
    try {
      props.onOpen(ok(await api.POST("/api/communities", { body: { name: name.trim(), description: null } })));
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <div className="panel">
      <h2>Join a community</h2>
      {error && <p className="error">{error}</p>}
      {list === null ? (
        <p className="muted">Loading…</p>
      ) : list.length === 0 ? (
        <p className="muted">You're in every community on this server.</p>
      ) : (
        <ul className="community-list">
          {list.map((c) => (
            <li key={c.id} data-testid="discover-community" data-name={c.name}>
              <span className="avatar">{c.name.slice(0, 2).toUpperCase()}</span>
              <span className="grow">
                <strong>{c.name}</strong>
                <span className="muted"> · {c.memberCount} {c.memberCount === 1 ? "member" : "members"}</span>
                {c.description && <span className="muted block">{c.description}</span>}
              </span>
              <button className="primary" onClick={() => void join(c)}>Join</button>
            </li>
          ))}
        </ul>
      )}
      <h2>Or create one</h2>
      <form className="row" onSubmit={create}>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Community name" aria-label="Community name" />
        <button className="primary" type="submit">Create</button>
      </form>
    </div>
  );
}

/** For server admins: a single-use invite code (7 days) to send to a friend. */
export function InviteFriends(props: { api: Api; serverUrl: string }) {
  const { api, serverUrl } = props;
  const [invite, setInvite] = useState<Invite | null>(null);
  const [error, setError] = useState<string | null>(null);

  const create = async () => {
    try {
      const expiresAt = new Date(Date.now() + 7 * 24 * 3600 * 1000).toISOString();
      setInvite(ok(await api.POST("/api/Admin/invites", { body: { maxUses: 1, expiresAt } })));
    } catch (e) {
      setError((e as Error).message);
    }
  };

  // The web client can carry server and code in a link; the desktop app has no web address.
  const link = invite && !inNativeApp
    ? `${window.location.origin}${window.location.pathname}?${new URLSearchParams({ server: serverUrl, invite: invite.code })}`
    : null;

  return (
    <div className="panel">
      <h2>Invite a friend</h2>
      <p className="muted">Each invite works for one account and expires in 7 days.</p>
      {error && <p className="error">{error}</p>}
      {invite ? (
        <dl className="invite">
          <dt>Server</dt>
          <dd>{serverUrl}</dd>
          <dt>Invite code</dt>
          <dd data-testid="invite-code">{invite.code}</dd>
          {link && (
            <>
              <dt>Link</dt>
              <dd>
                <input readOnly value={link} data-testid="invite-link" onFocus={(e) => e.currentTarget.select()} aria-label="Invite link" />
                <button className="link" onClick={() => void navigator.clipboard?.writeText(link)}>Copy</button>
              </dd>
            </>
          )}
        </dl>
      ) : (
        <button className="primary" onClick={() => void create()}>Create invite</button>
      )}
    </div>
  );
}
