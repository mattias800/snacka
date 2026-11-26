import { useEffect, useState } from "react";
import { ok, type Api, type Schemas } from "../api/client";
import { Avatar } from "./Avatar";
import type { FileLoader } from "./files";

type Conversation = Schemas["ConversationResponse"];
type Person = { userId: string; name: string; username: string; avatar: string | null };

/** Starting a group conversation with people from the user's communities. */
export function NewGroup(props: { api: Api; communityIds: string[]; myId: string; loadFile: FileLoader; onCreated: (c: Conversation) => void }) {
  const { api, communityIds, myId, loadFile, onCreated } = props;
  const [people, setPeople] = useState<Person[] | null>(null);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    void Promise.all(communityIds.map((communityId) => api.GET("/api/communities/{communityId}/members", { params: { path: { communityId } } }).then(ok)))
      .then((lists) => {
        const byId = new Map<string, Person>();
        for (const m of lists.flat())
          if (m.userId !== myId && !byId.has(m.userId)) byId.set(m.userId, { userId: m.userId, name: m.effectiveDisplayName, username: m.username, avatar: m.avatar ?? null });
        if (live) setPeople([...byId.values()].sort((a, b) => a.name.localeCompare(b.name)));
      })
      .catch((e: Error) => live && setError(e.message));
    return () => {
      live = false;
    };
  }, [api, communityIds, myId]);

  const toggle = (id: string) =>
    setPicked((prev) => {
      const next = new Set(prev);
      if (!next.delete(id)) next.add(id);
      return next;
    });

  const create = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      setError(null);
      onCreated(ok(await api.POST("/api/conversations", { body: { participantIds: [...picked], name: name.trim() || null } })));
    } catch (err) {
      setError((err as Error).message);
    }
  };

  return (
    <form className="panel new-group" data-testid="new-group-panel" onSubmit={(e) => void create(e)}>
      <h2>New group</h2>
      <p className="muted">Pick two or more people from your communities.</p>
      {error && <p className="error">{error}</p>}
      <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Group name (optional)" aria-label="Group name" maxLength={100} />
      {people === null ? (
        <p className="muted">Loading…</p>
      ) : (
        <ul className="pick-people">
          {people.map((p) => (
            <li key={p.userId}>
              <label>
                <input type="checkbox" checked={picked.has(p.userId)} onChange={() => toggle(p.userId)} aria-label={p.username} />
                <Avatar userId={p.userId} name={p.name} avatar={p.avatar} loadFile={loadFile} size={24} />
                {p.name}
              </label>
            </li>
          ))}
        </ul>
      )}
      <button className="primary" type="submit" disabled={picked.size < 2}>
        Start group{picked.size >= 2 ? ` with ${picked.size}` : ""}
      </button>
    </form>
  );
}
