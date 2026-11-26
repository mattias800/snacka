import { useState } from "react";
import { ok, type Api, type Schemas } from "../api/client";
import { Avatar } from "./Avatar";
import type { FileLoader } from "./files";

export type Profile = Schemas["UserProfileResponse"];

/** The user's own profile: picture, display name and password. Changes reach others live (UserProfileUpdated). */
export function Settings(props: {
  api: Api;
  serverUrl: string;
  getToken: () => string | null;
  loadFile: FileLoader;
  profile: Profile;
  onChange: (p: Profile) => void;
}) {
  const { api, serverUrl, getToken, loadFile, profile, onChange } = props;
  const [displayName, setDisplayName] = useState(profile.displayName ?? "");
  const [passwords, setPasswords] = useState({ current: "", next: "" });
  const [status, setStatus] = useState<{ error: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  const act = async (f: () => Promise<string>) => {
    setBusy(true);
    try {
      setStatus({ error: false, text: await f() });
    } catch (e) {
      setStatus({ error: true, text: (e as Error).message });
    } finally {
      setBusy(false);
    }
  };

  const reload = async () => onChange(ok(await api.GET("/api/Users/me")));

  // Multipart upload: not expressible in the generated client. The server crops to the centre square.
  const uploadAvatar = (file: File) =>
    act(async () => {
      const form = new FormData();
      form.append("file", file, file.name);
      const res = await fetch(`${serverUrl}/api/users/me/avatar`, { method: "PUT", headers: { Authorization: `Bearer ${getToken() ?? ""}` }, body: form });
      if (!res.ok) {
        const body = (await res.json().catch(() => null)) as { error?: string } | null;
        throw new Error(body?.error ?? `Upload failed: ${res.status}`);
      }
      await reload();
      return "Picture updated.";
    });

  const removeAvatar = () =>
    act(async () => {
      const res = await api.DELETE("/api/Users/me/avatar");
      if (!res.response.ok) throw new Error(`Couldn't remove the picture: ${res.response.status}`);
      await reload();
      return "Picture removed.";
    });

  const saveName = (e: React.FormEvent) => {
    e.preventDefault();
    void act(async () => {
      // Blank clears the display name: others then see the username.
      onChange(ok(await api.PUT("/api/Users/me", { body: { username: null, displayName: displayName.trim(), status: null } })));
      return "Name saved.";
    });
  };

  const changePassword = (e: React.FormEvent) => {
    e.preventDefault();
    void act(async () => {
      const res = await api.PUT("/api/Users/me/password", { body: { currentPassword: passwords.current, newPassword: passwords.next } });
      if (!res.response.ok) throw new Error((res.error as { error?: string } | undefined)?.error ?? `Couldn't change the password: ${res.response.status}`);
      setPasswords({ current: "", next: "" });
      return "Password changed.";
    });
  };

  return (
    <div className="panel settings" data-testid="settings">
      <h2>Profile</h2>
      {status && (
        <p className={status.error ? "error" : "muted"} data-testid="settings-status">
          {status.text}
        </p>
      )}
      <div className="row avatar-row">
        <Avatar userId={profile.id} name={profile.effectiveDisplayName} avatar={profile.avatar} loadFile={loadFile} size={80} />
        <label className="button primary">
          Upload picture
          <input
            type="file"
            accept="image/png,image/jpeg,image/gif,image/webp"
            hidden
            data-testid="avatar-input"
            disabled={busy}
            onChange={(e) => {
              const file = e.target.files?.[0];
              e.target.value = "";
              if (file) void uploadAvatar(file);
            }}
          />
        </label>
        {profile.avatar && (
          <button className="link" disabled={busy} onClick={() => void removeAvatar()}>
            Remove
          </button>
        )}
      </div>
      <form className="row" onSubmit={saveName}>
        <input value={displayName} maxLength={32} onChange={(e) => setDisplayName(e.target.value)} placeholder={profile.username} aria-label="Display name" />
        <button className="primary" type="submit" disabled={busy}>Save name</button>
      </form>
      <p className="muted">
        Username <strong>{profile.username}</strong> · {profile.email}
      </p>
      <h2>Password</h2>
      <form className="column" onSubmit={changePassword}>
        <input type="password" autoComplete="current-password" value={passwords.current} onChange={(e) => setPasswords({ ...passwords, current: e.target.value })} placeholder="Current password" aria-label="Current password" />
        <input type="password" autoComplete="new-password" minLength={8} value={passwords.next} onChange={(e) => setPasswords({ ...passwords, next: e.target.value })} placeholder="New password (8+ characters)" aria-label="New password" />
        <button className="primary" type="submit" disabled={busy || !passwords.current || passwords.next.length < 8}>Change password</button>
      </form>
    </div>
  );
}
