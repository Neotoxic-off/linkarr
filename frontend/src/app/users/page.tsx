"use client";

import { useCallback, useEffect, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { useSession } from "@/components/AuthGate";
import { SessionRow } from "@/components/Overview";
import { useToast } from "@/components/Toasts";
import { Alert, Avatar, Badge, Button, Checkbox, Confirm, Empty, Field, IconButton, Input, Menu, MenuItem, Modal, PageHeader, Section, Skeleton, Switch, useAction, WellList } from "@/components/ui";
import { ago, api, displayName, errorMessage, when } from "@/lib/api";
import type { Library, ManagedUser, Rights, SessionInfo, UnmanagedUser } from "@/lib/api";

const defaultRights: Rights = { admin: false, watch: true, allLibraries: true, libraries: [], request: true, manageDownloads: false, modularrAccess: false };

type Dialog =
  | { type: "create" }
  | { type: "edit" | "password" | "resend" | "delete" | "sessions" | "signout" | "disable"; user: ManagedUser }
  | { type: "remove"; user: UnmanagedUser };

const syncNote = (user: ManagedUser, what: string): string =>
  user.syncStatus === "OutOfSync" ? `${what} in linkarr; an app did not take it yet (${user.syncError ?? "out of sync"}).` : `${what} in linkarr and both apps.`;

export default function UsersPage(): ReactNode {
  const toast = useToast();
  const [users, setUsers] = useState<ManagedUser[] | null>(null);
  const [unmanaged, setUnmanaged] = useState<UnmanagedUser[]>([]);
  const [libraries, setLibraries] = useState<Library[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [syncing, setSyncing] = useState(false);
  const [adopting, setAdopting] = useState<string | null>(null);
  const [dialog, setDialog] = useState<Dialog | null>(null);

  const load = useCallback((): void => {
    api.users().then((list) => { setUsers(list); setError(null); }).catch((e: unknown) => setError(errorMessage(e)));
    api.unmanaged().then(setUnmanaged).catch(() => undefined);
  }, []);

  useEffect(() => {
    load();
    api.libraries().then(setLibraries).catch(() => undefined);
  }, [load]);

  const replace = (user: ManagedUser): void => setUsers((list) => list?.map((u) => (u.id === user.id ? user : u)) ?? [user]);
  const done = (user: { id: number; username: string }, message: string): void => toast({ key: `user-${user.id}`, icon: "check", tone: "ok", title: user.username, message });
  const failed = (user: ManagedUser, e: unknown): void => toast({ key: `user-${user.id}`, icon: "alert", tone: "bad", title: user.username, message: errorMessage(e) });
  const close = (): void => setDialog(null);

  async function syncAll(): Promise<void> {
    setSyncing(true);
    try {
      const result = await api.syncUsers();
      setUsers(result.users);
      setUnmanaged(result.unmanaged);
      toast({ key: "users-sync", icon: "check", tone: "ok", title: "Users synced" });
    } catch (e) {
      toast({ key: "users-sync", icon: "alert", tone: "bad", title: "Sync failed", message: errorMessage(e) });
    } finally {
      setSyncing(false);
    }
  }

  // A retried sync can come back still out of sync: the toast says which.
  async function retry(user: ManagedUser): Promise<void> {
    try {
      const synced = await api.syncUser(user.id, null);
      replace(synced);
      if (synced.syncStatus === "OutOfSync") toast({ key: `user-${user.id}`, icon: "alert", tone: "warn", title: user.username, message: synced.syncError ?? "Still out of sync." });
      else done(synced, "In sync with both apps.");
    } catch (e) {
      failed(user, e);
    }
  }

  async function enable(user: ManagedUser): Promise<void> {
    try {
      replace(await api.updateUser(user.id, { disabled: false }));
      done(user, "Enabled: the user can sign in again.");
    } catch (e) {
      failed(user, e);
    }
  }

  async function adopt(user: UnmanagedUser): Promise<void> {
    setAdopting(`${user.app}-${user.remoteId}`);
    try {
      await api.adopt(user.app, user.remoteId);
      toast({ key: `remote-${user.remoteId}`, icon: "check", tone: "ok", title: user.name, message: "Now managed by linkarr. Reset its password so it can sign in." });
      load();
    } catch (e) {
      toast({ key: `remote-${user.remoteId}`, icon: "alert", tone: "bad", title: user.name, message: errorMessage(e) });
    } finally {
      setAdopting(null);
    }
  }

  return (
    <>
      <PageHeader title="Users" description="linkarr is the source of truth: every change goes to visularr and modularr.">
        <div className="flex flex-wrap gap-2">
          <Button icon="refresh" loading={syncing} onClick={syncAll}>Sync now</Button>
          <Button variant="primary" icon="plus" onClick={() => setDialog({ type: "create" })}>New user</Button>
        </div>
      </PageHeader>
      {error && <Alert>{error}</Alert>}
      {!users && !error && <div className="space-y-2">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-20" />)}</div>}
      {users?.length === 0 && <Empty icon="users" title="No users" />}
      {users && users.length > 0 && (
        <WellList label="Users">
          {users.map((user) => (
            <li key={user.id} className="flex items-start gap-3 p-3.5">
              <Avatar user={user} className="size-10 text-body" />
              <div className="min-w-0 flex-1">
                <p className="truncate text-body font-medium">
                  {displayName(user)} {user.displayName && <span className="font-normal text-ink-3">@{user.username}</span>}
                </p>
                <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
                  {user.disabled && <Badge tone="bad">Disabled</Badge>}
                  {user.syncStatus === "OutOfSync" && <Badge tone="warn">Out of sync</Badge>}
                  {user.passwordPending && <Badge tone="warn">Password needed</Badge>}
                  {user.rights.admin && <Badge>Admin</Badge>}
                  {user.visularrId && <Badge>visularr</Badge>}
                  {user.modularrId != null && <Badge>modularr{user.rights.manageDownloads || user.rights.admin ? " admin" : ""}</Badge>}
                </div>
                {user.syncError && <p className="mt-1.5 text-meta break-words text-warn">{user.syncError}</p>}
                <p className="mt-1.5 text-caption text-ink-3">
                  <span title={when(user.lastSignIn)}>{user.lastSignIn ? `Signed in ${ago(user.lastSignIn)}` : "Never signed in"}</span>
                  {" · "}<span title={when(user.lastSynced)}>{user.lastSynced ? `synced ${ago(user.lastSynced)}` : "never synced"}</span>
                </p>
              </div>
              <Menu label={`Actions for ${user.username}`}>
                <MenuItem icon="sliders" onClick={() => setDialog({ type: "edit", user })}>Edit rights</MenuItem>
                <MenuItem icon="key" onClick={() => setDialog({ type: "password", user })}>Reset password</MenuItem>
                {user.passwordPending && <MenuItem icon="refresh" onClick={() => setDialog({ type: "resend", user })}>Send password again</MenuItem>}
                <MenuItem icon="list" onClick={() => setDialog({ type: "sessions", user })}>Sessions</MenuItem>
                <MenuItem icon="logout" onClick={() => setDialog({ type: "signout", user })}>Sign out everywhere</MenuItem>
                <MenuItem icon="refresh" onClick={() => void retry(user)}>Retry sync</MenuItem>
                {user.disabled
                  ? <MenuItem icon="check" onClick={() => void enable(user)}>Enable</MenuItem>
                  : <MenuItem icon="ban" onClick={() => setDialog({ type: "disable", user })}>Disable</MenuItem>}
                <MenuItem icon="trash" danger onClick={() => setDialog({ type: "delete", user })}>Delete</MenuItem>
              </Menu>
            </li>
          ))}
        </WellList>
      )}

      {unmanaged.length > 0 && (
        <div className="mt-6">
          <Section title="Unmanaged app users" description="Created directly in visularr or modularr. Adopt them to manage them here, or delete them.">
            <WellList label="Unmanaged users">
              {unmanaged.map((u) => (
                <li key={`${u.app}-${u.remoteId}`} className="flex items-center gap-3 p-3">
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-body font-medium">{u.name}</p>
                    <p className="mt-1 flex gap-1.5"><Badge>{u.app}</Badge>{u.admin && <Badge>Admin</Badge>}</p>
                  </div>
                  <Button size="sm" loading={adopting === `${u.app}-${u.remoteId}`} onClick={() => void adopt(u)}>Adopt</Button>
                  {(u.app === "visularr" || u.linkable) && (
                    <IconButton variant="ghost" icon="trash" label={`Delete ${u.name} from ${u.app}`} onClick={() => setDialog({ type: "remove", user: u })} />
                  )}
                </li>
              ))}
            </WellList>
          </Section>
        </div>
      )}

      {dialog?.type === "create" && (
        <UserDialog libraries={libraries} onClose={close}
          onSaved={(u) => { setUsers((l) => [...(l ?? []), u].sort((a, b) => a.username.localeCompare(b.username))); close(); done(u, syncNote(u, "Created")); }} />
      )}
      {dialog?.type === "edit" && <UserDialog user={dialog.user} libraries={libraries} onClose={close} onSaved={(u) => { replace(u); close(); done(u, syncNote(u, "Saved")); }} />}
      {(dialog?.type === "password" || dialog?.type === "resend") && (
        <PasswordDialog user={dialog.user} resend={dialog.type === "resend"} onClose={close}
          onSaved={(u) => { replace(u); close(); done(u, u.passwordPending ? "Saved in linkarr; an app is unreachable, send it again later." : "Password set in linkarr and both apps."); }} />
      )}
      {dialog?.type === "sessions" && <SessionsDialog user={dialog.user} onClose={close} />}
      {dialog?.type === "signout" && (
        <Confirm title={`Sign ${dialog.user.username} out everywhere?`} confirm="Sign out" onClose={close}
          description="Every linkarr session and every visularr device of this user ends. modularr sessions end with the next password change."
          onConfirm={async () => { await api.signOutUser(dialog.user.id); done(dialog.user, "Signed out of linkarr and visularr."); }} />
      )}
      {dialog?.type === "disable" && (
        <Confirm title={`Disable ${dialog.user.username}?`} confirm="Disable" onClose={close}
          description="The user is signed out and can no longer sign in to linkarr, visularr or modularr. Enable gives everything back."
          onConfirm={async () => { replace(await api.updateUser(dialog.user.id, { disabled: true })); done(dialog.user, "Disabled in linkarr and both apps."); }} />
      )}
      {dialog?.type === "remove" && (
        <Confirm title={`Delete ${dialog.user.name} from ${dialog.user.app}?`} confirm="Delete" onClose={close}
          description={`The account and its ${dialog.user.app} data are removed from ${dialog.user.app}. linkarr never managed it, so nothing is kept here.`}
          onConfirm={async () => {
            await api.deleteUnmanaged(dialog.user.app, dialog.user.remoteId);
            toast({ key: `remote-${dialog.user.remoteId}`, icon: "check", tone: "ok", title: dialog.user.name, message: `Deleted from ${dialog.user.app}.` });
            load();
          }} />
      )}
      {dialog?.type === "delete" && (
        <DeleteDialog user={dialog.user} onClose={close}
          onDeleted={() => { setUsers((l) => l?.filter((u) => u.id !== dialog.user.id) ?? null); close(); done(dialog.user, "Deleted from linkarr and both apps."); }} />
      )}
    </>
  );
}

// The user's linkarr sessions; ending one signs that browser out at its next request.
function SessionsDialog({ user, onClose }: { user: ManagedUser; onClose: () => void }): ReactNode {
  const toast = useToast();
  const [sessions, setSessions] = useState<SessionInfo[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [ending, setEnding] = useState<string | null>(null);

  useEffect(() => {
    api.userSessions(user.id).then(setSessions).catch((e: unknown) => setError(errorMessage(e)));
  }, [user.id]);

  async function end(session: SessionInfo): Promise<void> {
    setEnding(session.id);
    setError(null);
    try {
      await api.endUserSession(user.id, session.id);
      setSessions((list) => list?.filter((s) => s.id !== session.id) ?? null);
      toast({ key: `session-${session.id}`, icon: "check", tone: "ok", title: user.username, message: "That session is signed out of linkarr." });
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setEnding(null);
    }
  }

  return (
    <Modal title={`${user.username}’s sessions`} description="Browsers signed in to linkarr, with the address each signed in from and the last one seen." onClose={onClose}>
      <div className="space-y-3 px-5 pb-6">
        {error && <Alert>{error}</Alert>}
        {!sessions && !error && <Skeleton className="h-16" />}
        {sessions?.length === 0 && <p className="text-meta text-ink-3">Not signed in anywhere.</p>}
        {sessions && sessions.length > 0 && (
          <ul className="divide-y divide-hair-2">
            {sessions.map((s) => <SessionRow key={s.id} session={s} busy={ending === s.id} onEnd={() => void end(s)} />)}
          </ul>
        )}
      </div>
    </Modal>
  );
}

function RightsEditor({ rights, onChange, libraries, self }: { rights: Rights; onChange: (rights: Rights) => void; libraries: Library[]; self: boolean }): ReactNode {
  const set = (patch: Partial<Rights>): void => onChange({ ...rights, ...patch });
  return (
    <div className="space-y-1">
      <Switch label="Admin" description="Everything, in linkarr and both apps." checked={rights.admin} disabled={self} onChange={(admin) => set({ admin })} />
      <Switch label="visularr" description="Has a visularr account and can play." checked={rights.watch || rights.admin} disabled={rights.admin} onChange={(watch) => set({ watch })} />
      <Switch label="Request titles" description="Can search and request through linkarr." checked={rights.request || rights.admin} disabled={rights.admin} onChange={(request) => set({ request })} />
      <Switch label="modularr" description="Has a modularr account and can sign in to it." checked={rights.modularrAccess || rights.admin} disabled={rights.admin} onChange={(modularrAccess) => set({ modularrAccess })} />
      <Switch label="Manage downloads" description="modularr admin instead of member." checked={rights.manageDownloads || rights.admin}
        disabled={rights.admin || !rights.modularrAccess} onChange={(manageDownloads) => set({ manageDownloads })} />
      {!rights.admin && rights.watch && (
        <div className="well mt-2 rounded-card p-3.5">
          <Switch label="All libraries" checked={rights.allLibraries} onChange={(allLibraries) => set({ allLibraries })} />
          {!rights.allLibraries && (libraries.length === 0
            ? <p className="text-meta text-ink-3">Connect visularr in Settings to pick libraries.</p>
            : libraries.map((l) => (
              <Checkbox key={l.itemId} label={l.name} checked={rights.libraries.includes(l.itemId)}
                onChange={(on) => set({ libraries: on ? [...rights.libraries, l.itemId] : rights.libraries.filter((id) => id !== l.itemId) })} />
            )))}
        </div>
      )}
    </div>
  );
}

function UserDialog({ user, libraries, onClose, onSaved }: { user?: ManagedUser; libraries: Library[]; onClose: () => void; onSaved: (user: ManagedUser) => void }): ReactNode {
  const { user: me } = useSession();
  const [username, setUsername] = useState(user?.username ?? "");
  const [name, setName] = useState(user?.displayName ?? "");
  const [password, setPassword] = useState("");
  const [rights, setRights] = useState<Rights>(user?.rights ?? defaultRights);
  const { busy, error, run } = useAction();

  function submit(event: FormEvent): void {
    event.preventDefault();
    void run(async () => {
      const saved = user
        ? await api.updateUser(user.id, { username: username === user.username ? undefined : username, displayName: name, rights })
        : await api.createUser({ username, displayName: name || null, password, rights });
      onSaved(saved);
    });
  }

  return (
    <Modal title={user ? `Edit ${user.username}` : "New user"} onClose={onClose}>
      <form onSubmit={submit} className="space-y-4 px-5 pb-6">
        {error && <Alert>{error}</Alert>}
        <Field label="Username" hint={`3 to 32 letters, digits, '.', '_' or '-'. The same name in every app${user ? "; renaming signs the user out of linkarr" : ""}.`}>
          <Input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" required minLength={3} maxLength={32} />
        </Field>
        <Field label="Display name"><Input value={name} onChange={(e) => setName(e.target.value)} maxLength={64} /></Field>
        {!user && (
          <Field label="Password" hint="At least 8 characters. Sent to both apps, never stored in clear.">
            <Input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" required minLength={8} />
          </Field>
        )}
        <RightsEditor rights={rights} onChange={setRights} libraries={libraries} self={user?.id === me.id} />
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="ghost" onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={busy}>{user ? "Save" : "Create"}</Button>
        </div>
      </form>
    </Modal>
  );
}

// resend: the password the apps missed, checked against linkarr's; otherwise a new one for linkarr and both apps.
function PasswordDialog({ user, resend, onClose, onSaved }: { user: ManagedUser; resend: boolean; onClose: () => void; onSaved: (user: ManagedUser) => void }): ReactNode {
  const [password, setPassword] = useState("");
  const { busy, error, run } = useAction();

  function submit(event: FormEvent): void {
    event.preventDefault();
    void run(async () => onSaved(await (resend ? api.syncUser(user.id, password) : api.resetPassword(user.id, password))));
  }

  return (
    <Modal title={resend ? `Send ${user.username}’s password again` : `Reset ${user.username}’s password`}
      description={resend ? "Enter the user’s current linkarr password; linkarr does not keep it." : "linkarr, visularr and modularr get the new password; the user’s sessions end."}
      onClose={onClose}>
      <form onSubmit={submit} className="space-y-4 px-5 pb-6">
        {error && <Alert>{error}</Alert>}
        <Field label="Password">
          <Input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" required minLength={8} autoFocus />
        </Field>
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={busy}>{resend ? "Send" : "Reset"}</Button>
        </div>
      </form>
    </Modal>
  );
}

function DeleteDialog({ user, onClose, onDeleted }: { user: ManagedUser; onClose: () => void; onDeleted: () => void }): ReactNode {
  return (
    <Confirm title={`Delete ${user.username}?`} description="The user is removed from visularr and modularr, then from linkarr." confirm="Delete" onClose={onClose}
      onConfirm={async () => { await api.deleteUser(user.id); onDeleted(); }} />
  );
}
