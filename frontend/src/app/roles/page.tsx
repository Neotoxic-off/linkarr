"use client";

import { useCallback, useEffect, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { useToast } from "@/components/Toasts";
import { Alert, Badge, Button, Confirm, Empty, Field, Input, Menu, MenuItem, Modal, PageHeader, Select, Skeleton, Switch, useAction, WellList } from "@/components/ui";
import { api, displayName, errorMessage } from "@/lib/api";
import type { ManagedUser, Rights, Role } from "@/lib/api";

// Jellyfin rating scores (its US table): a limit lets through everything rated at or under it.
const ratings: readonly { score: number; label: string }[] = [
  { score: 0, label: "G, TV-Y" },
  { score: 7, label: "TV-Y7" },
  { score: 10, label: "PG, TV-PG" },
  { score: 13, label: "PG-13" },
  { score: 14, label: "TV-14" },
  { score: 17, label: "R, TV-MA" },
];

const bitrates: readonly number[] = [1, 2, 4, 8, 10, 20, 40];

const newRights: Rights = {
  admin: false, watch: true, allLibraries: true, libraries: [], request: true, manageDownloads: false, modularrAccess: false, requestsPerDay: null,
  maxParentalRating: null, blockUnrated: false, canDownload: true, remoteAccess: true, maxActiveSessions: 0, transcoding: true, bitrateLimit: 0,
};

// The few words a list row needs: what stands out from a plain member.
const summary = (r: Rights): string => {
  if (r.admin) return "Everything, in linkarr and both apps";
  const parts: string[] = [];
  if (r.watch) {
    parts.push(r.allLibraries ? "visularr" : "visularr, groups' libraries");
    const rating = ratings.find((x) => x.score === r.maxParentalRating);
    if (rating) parts.push(`up to ${rating.label.split(",")[0]}`);
    if (!r.canDownload) parts.push("no downloads");
    if (!r.remoteAccess) parts.push("home only");
  }
  if (r.modularrAccess) parts.push(r.manageDownloads ? "modularr admin" : "modularr");
  if (r.request) parts.push(r.requestsPerDay === null ? "requests" : `${r.requestsPerDay} requests a day`);
  return parts.length ? parts.join(" · ") : "No access";
};

type Dialog = { type: "edit"; role: Role | null } | { type: "delete"; role: Role };

export default function RolesPage(): ReactNode {
  const toast = useToast();
  const [roles, setRoles] = useState<Role[] | null>(null);
  const [users, setUsers] = useState<ManagedUser[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [dialog, setDialog] = useState<Dialog | null>(null);

  const load = useCallback((): void => {
    api.roles().then((list) => { setRoles(list); setError(null); }).catch((e: unknown) => setError(errorMessage(e)));
    api.users().then(setUsers).catch(() => undefined);
  }, []);

  useEffect(() => load(), [load]);

  const close = (): void => setDialog(null);
  const saved = (role: Role, created: boolean): void => {
    setRoles((list) => (created ? [...(list ?? []), role] : list?.map((r) => (r.id === role.id ? role : r)) ?? [role]));
    close();
    toast({ key: `role-${role.id}`, icon: "check", tone: "ok", title: role.name,
      message: created ? "Role created." : role.members.length ? `Saved; its ${role.members.length} ${role.members.length === 1 ? "user is" : "users are"} synced to the apps in a few seconds.` : "Saved." });
  };

  return (
    <>
      <PageHeader title="Roles" description="What a user may do in visularr, modularr and linkarr. Every user has exactly one role.">
        <Button variant="primary" icon="plus" onClick={() => setDialog({ type: "edit", role: null })}>New role</Button>
      </PageHeader>
      {error && <Alert>{error}</Alert>}
      {!roles && !error && <div className="space-y-2">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-16" />)}</div>}
      {roles?.length === 0 && <Empty icon="key" title="No roles" />}
      {roles && roles.length > 0 && (
        <WellList label="Roles">
          {roles.map((role) => (
            <li key={role.id} className="flex items-start gap-3 p-3.5">
              <div className="min-w-0 flex-1">
                <p className="flex flex-wrap items-center gap-2 text-body font-medium">
                  <span className="truncate">{role.name}</span>
                  {role.builtIn && <Badge>Built-in</Badge>}
                  <span className="text-meta font-normal text-ink-3">{role.members.length} {role.members.length === 1 ? "user" : "users"}</span>
                </p>
                <p className="mt-1 text-meta text-ink-3">{summary(role.rights)}</p>
              </div>
              <Menu label={`Actions for ${role.name}`}>
                <MenuItem icon="sliders" onClick={() => setDialog({ type: "edit", role })}>{role.builtIn === "admin" ? "View" : "Edit"}</MenuItem>
                <MenuItem icon="trash" danger disabled={role.builtIn !== null || role.members.length > 0} onClick={() => setDialog({ type: "delete", role })}>
                  {role.builtIn ? "Built-in, kept" : role.members.length ? "In use, kept" : "Delete"}
                </MenuItem>
              </Menu>
            </li>
          ))}
        </WellList>
      )}

      {dialog?.type === "edit" && <RoleDialog role={dialog.role} users={users} onClose={close} onSaved={saved} />}
      {dialog?.type === "delete" && (
        <Confirm title={`Delete ${dialog.role.name}?`} description="Nobody has this role; it is removed, and groups that gave it to new users give the default instead." confirm="Delete" onClose={close}
          onConfirm={async () => {
            await api.deleteRole(dialog.role.id);
            setRoles((list) => list?.filter((r) => r.id !== dialog.role.id) ?? null);
            toast({ key: `role-${dialog.role.id}`, icon: "check", tone: "ok", title: dialog.role.name, message: "Role deleted." });
          }} />
      )}
    </>
  );
}

function AppGroup({ title, children }: { title: string; children: ReactNode }): ReactNode {
  return (
    <fieldset className="well rounded-card px-3.5 pt-2 pb-3">
      <legend className="caption px-1">{title}</legend>
      <div className="space-y-1">{children}</div>
    </fieldset>
  );
}

// Grouped by the app each permission lands in; the built-in Admin role is shown read-only (it always has everything).
function RoleDialog({ role, users, onClose, onSaved }: { role: Role | null; users: ManagedUser[]; onClose: () => void; onSaved: (role: Role, created: boolean) => void }): ReactNode {
  const [name, setName] = useState(role?.name ?? "");
  const [rights, setRights] = useState<Rights>(role?.rights ?? newRights);
  const { busy, error, run } = useAction();
  const locked = role?.builtIn === "admin";
  const set = (patch: Partial<Rights>): void => setRights((r) => ({ ...r, ...patch }));
  const off = locked || rights.admin;
  const members = users.filter((u) => role?.members.includes(u.id));

  function submit(event: FormEvent): void {
    event.preventDefault();
    void run(async () => onSaved(role ? await api.updateRole(role.id, name, rights) : await api.createRole(name, rights), !role));
  }

  return (
    <Modal title={role ? (locked ? role.name : `Edit ${role.name}`) : "New role"}
      description={locked ? "The Admin role always has everything; it cannot be changed." : role?.members.length ? "Saving re-syncs every user of this role to both apps." : undefined}
      onClose={onClose}>
      <form onSubmit={submit} className="space-y-4 px-5 pb-6">
        {error && <Alert>{error}</Alert>}
        <Field label="Name"><Input value={name} onChange={(e) => setName(e.target.value)} required maxLength={48} disabled={locked} /></Field>

        <AppGroup title="linkarr">
          <Switch label="Admin" description="Everything, in linkarr and both apps." checked={rights.admin} disabled={locked} onChange={(admin) => set({ admin })} />
          <Switch label="Request titles" description="Can search and request through linkarr." checked={rights.request || rights.admin} disabled={off} onChange={(request) => set({ request })} />
          {rights.request && !rights.admin && (
            <Field label="Requests a day" hint="Empty: the limit in Settings. 0: no limit.">
              <Input type="number" min={0} max={1000} value={rights.requestsPerDay ?? ""} disabled={locked}
                onChange={(e) => set({ requestsPerDay: e.target.value === "" ? null : Math.max(0, Math.round(Number(e.target.value))) })} />
            </Field>
          )}
        </AppGroup>

        <AppGroup title="visularr">
          <Switch label="visularr account" description="Has an account and can play." checked={rights.watch || rights.admin} disabled={off} onChange={(watch) => set({ watch })} />
          <Switch label="All libraries" description="Off: only the libraries of the user's groups." checked={rights.allLibraries || rights.admin} disabled={off} onChange={(allLibraries) => set({ allLibraries })} />
          <Field label="Parental rating" hint="Titles rated above it are hidden.">
            <Select aria-label="Parental rating" value={rights.maxParentalRating ?? ""} disabled={locked}
              onChange={(e) => set({ maxParentalRating: e.target.value === "" ? null : Number(e.target.value) })}>
              <option value="">No limit</option>
              {ratings.map((r) => <option key={r.score} value={r.score}>Up to {r.label}</option>)}
            </Select>
          </Field>
          <Switch label="Hide unrated titles" description="Only with a parental rating limit." checked={rights.blockUnrated && rights.maxParentalRating !== null}
            disabled={locked || rights.maxParentalRating === null} onChange={(blockUnrated) => set({ blockUnrated })} />
          <Switch label="Download and sync" description="Can save media to a device." checked={rights.canDownload} disabled={locked} onChange={(canDownload) => set({ canDownload })} />
          <Switch label="Remote access" description="Can play from outside the home network." checked={rights.remoteAccess} disabled={locked} onChange={(remoteAccess) => set({ remoteAccess })} />
          <Switch label="Transcoding" description="The server may convert video and audio the device cannot play." checked={rights.transcoding} disabled={locked} onChange={(transcoding) => set({ transcoding })} />
          <div className="grid gap-3 pt-2 sm:grid-cols-2">
            <Field label="Streams at once" hint="0: no limit.">
              <Input type="number" min={0} max={100} value={rights.maxActiveSessions} disabled={locked}
                onChange={(e) => set({ maxActiveSessions: Math.max(0, Math.round(Number(e.target.value))) })} />
            </Field>
            <Field label="Remote bitrate">
              <Select aria-label="Remote bitrate" value={rights.bitrateLimit} disabled={locked} onChange={(e) => set({ bitrateLimit: Number(e.target.value) })}>
                <option value={0}>No limit</option>
                {bitrates.map((mbps) => <option key={mbps} value={mbps * 1_000_000}>{mbps} Mbps</option>)}
              </Select>
            </Field>
          </div>
        </AppGroup>

        <AppGroup title="modularr">
          <Switch label="modularr account" description="Can sign in to modularr." checked={rights.modularrAccess || rights.admin} disabled={off} onChange={(modularrAccess) => set({ modularrAccess })} />
          <Switch label="Manage downloads" description="modularr admin instead of member." checked={rights.manageDownloads || rights.admin}
            disabled={off || !rights.modularrAccess} onChange={(manageDownloads) => set({ manageDownloads })} />
        </AppGroup>

        {role && (
          <div>
            <p className="text-body font-medium">Users</p>
            <p className="mt-1 text-meta text-ink-3">{members.length ? members.map(displayName).join(", ") : "Nobody has this role."}</p>
          </div>
        )}
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="ghost" onClick={onClose}>{locked ? "Close" : "Cancel"}</Button>
          {!locked && <Button variant="primary" loading={busy}>{role ? "Save" : "Create"}</Button>}
        </div>
      </form>
    </Modal>
  );
}
