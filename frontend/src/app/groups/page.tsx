"use client";

import { useCallback, useEffect, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { useToast } from "@/components/Toasts";
import { Alert, Avatar, Badge, Button, Checkbox, Confirm, Empty, Field, Input, Menu, MenuItem, Modal, PageHeader, Select, Skeleton, Switch, useAction, WellList } from "@/components/ui";
import { api, displayName, errorMessage } from "@/lib/api";
import type { Group, GroupInput, Library, ManagedUser, Role } from "@/lib/api";

type Dialog = { type: "edit"; group: Group | null } | { type: "delete"; group: Group };

export default function GroupsPage(): ReactNode {
  const toast = useToast();
  const [groups, setGroups] = useState<Group[] | null>(null);
  const [users, setUsers] = useState<ManagedUser[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [libraries, setLibraries] = useState<Library[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [dialog, setDialog] = useState<Dialog | null>(null);

  const load = useCallback((): void => {
    api.groups().then((list) => { setGroups(list); setError(null); }).catch((e: unknown) => setError(errorMessage(e)));
    api.users().then(setUsers).catch(() => undefined);
    api.roles().then(setRoles).catch(() => undefined);
    api.libraries().then(setLibraries).catch(() => undefined);
  }, []);

  useEffect(() => load(), [load]);

  const close = (): void => setDialog(null);
  const libraryName = (id: string): string => libraries.find((l) => l.itemId === id)?.name ?? "Unknown library";
  const saved = (group: Group, created: boolean): void => {
    setGroups((list) => (created ? [...(list ?? []), group] : list?.map((g) => (g.id === group.id ? group : g)) ?? [group])
      .sort((a, b) => a.name.localeCompare(b.name)));
    close();
    toast({ key: `group-${group.id}`, icon: "check", tone: "ok", title: group.name,
      message: group.members.length ? "Saved; its users are synced to visularr in a few seconds." : "Saved." });
  };

  return (
    <>
      <PageHeader title="Groups" description="Sets of users (family, friends, kids) and the visularr libraries they see, on top of their role's.">
        <Button variant="primary" icon="plus" onClick={() => setDialog({ type: "edit", group: null })}>New group</Button>
      </PageHeader>
      {error && <Alert>{error}</Alert>}
      {!groups && !error && <div className="space-y-2">{[0, 1].map((i) => <Skeleton key={i} className="h-16" />)}</div>}
      {groups?.length === 0 && <Empty icon="folder" title="No groups">Create one to share libraries with several users at once.</Empty>}
      {groups && groups.length > 0 && (
        <WellList label="Groups">
          {groups.map((group) => {
            const members = users.filter((u) => group.members.includes(u.id));
            const role = roles.find((r) => r.id === group.defaultRoleId);
            return (
              <li key={group.id} className="flex items-start gap-3 p-3.5">
                <div className="min-w-0 flex-1">
                  <p className="flex flex-wrap items-center gap-2 text-body font-medium">
                    <span className="truncate">{group.name}</span>
                    {role && <Badge>New users: {role.name}</Badge>}
                  </p>
                  <p className="mt-1 text-meta break-words text-ink-3">
                    {group.allLibraries ? "Every library" : group.libraries.length ? group.libraries.map(libraryName).join(", ") : "No library"}
                  </p>
                  <div className="mt-2 flex flex-wrap items-center gap-1.5">
                    {members.length === 0 && <span className="text-meta text-ink-3">No members</span>}
                    {members.map((u) => (
                      <span key={u.id} className="well inline-flex h-7 items-center gap-1.5 rounded-pill pr-2.5 pl-0.5 text-caption font-medium">
                        <Avatar user={u} className="size-6 text-caption" />{displayName(u)}
                      </span>
                    ))}
                  </div>
                </div>
                <Menu label={`Actions for ${group.name}`}>
                  <MenuItem icon="sliders" onClick={() => setDialog({ type: "edit", group })}>Edit</MenuItem>
                  <MenuItem icon="trash" danger onClick={() => setDialog({ type: "delete", group })}>Delete</MenuItem>
                </Menu>
              </li>
            );
          })}
        </WellList>
      )}

      {dialog?.type === "edit" && <GroupDialog group={dialog.group} users={users} roles={roles} libraries={libraries} onClose={close} onSaved={saved} />}
      {dialog?.type === "delete" && (
        <Confirm title={`Delete ${dialog.group.name}?`} confirm="Delete" onClose={close}
          description="Its users keep their role and their other groups; the libraries only this group gave them are taken away."
          onConfirm={async () => {
            await api.deleteGroup(dialog.group.id);
            setGroups((list) => list?.filter((g) => g.id !== dialog.group.id) ?? null);
            toast({ key: `group-${dialog.group.id}`, icon: "check", tone: "ok", title: dialog.group.name, message: "Group deleted." });
          }} />
      )}
    </>
  );
}

function GroupDialog({ group, users, roles, libraries, onClose, onSaved }: {
  group: Group | null;
  users: ManagedUser[];
  roles: Role[];
  libraries: Library[];
  onClose: () => void;
  onSaved: (group: Group, created: boolean) => void;
}): ReactNode {
  const [input, setInput] = useState<GroupInput>(group
    ? { name: group.name, allLibraries: group.allLibraries, libraries: group.libraries, defaultRoleId: group.defaultRoleId, members: group.members }
    : { name: "", allLibraries: false, libraries: [], defaultRoleId: null, members: [] });
  const { busy, error, run } = useAction();
  const set = (patch: Partial<GroupInput>): void => setInput((i) => ({ ...i, ...patch }));
  const toggle = <T,>(list: T[], value: T, on: boolean): T[] => (on ? [...list, value] : list.filter((v) => v !== value));

  function submit(event: FormEvent): void {
    event.preventDefault();
    void run(async () => onSaved(group ? await api.updateGroup(group.id, input) : await api.createGroup(input), !group));
  }

  return (
    <Modal title={group ? `Edit ${group.name}` : "New group"} description="A group gives libraries only: what its users may do stays their role's." onClose={onClose}>
      <form onSubmit={submit} className="space-y-4 px-5 pb-6">
        {error && <Alert>{error}</Alert>}
        <Field label="Name"><Input value={input.name} onChange={(e) => set({ name: e.target.value })} required maxLength={48} placeholder="Family" /></Field>

        <div className="well rounded-card p-3.5">
          <Switch label="All libraries" checked={input.allLibraries} onChange={(allLibraries) => set({ allLibraries })} />
          {!input.allLibraries && (libraries.length === 0
            ? <p className="text-meta text-ink-3">Connect visularr in Settings to pick libraries.</p>
            : libraries.map((l) => (
              <Checkbox key={l.itemId} label={l.name} checked={input.libraries.includes(l.itemId)} onChange={(on) => set({ libraries: toggle(input.libraries, l.itemId, on) })} />
            )))}
        </div>

        <Field label="Role of new users" hint="Given to a user created in this group without a role. Joining never changes an existing user's role.">
          <Select aria-label="Role of new users" value={input.defaultRoleId ?? ""} onChange={(e) => set({ defaultRoleId: e.target.value === "" ? null : Number(e.target.value) })}>
            <option value="">None</option>
            {roles.filter((r) => !r.rights.admin).map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </Select>
        </Field>

        <div role="group" aria-label="Members" className="space-y-1">
          <span className="block text-body font-medium">Members</span>
          {users.length === 0
            ? <p className="text-meta text-ink-3">No users yet.</p>
            : users.map((u) => (
              <Checkbox key={u.id} label={displayName(u)} description={`@${u.username} · ${u.role.name}`}
                checked={input.members.includes(u.id)} onChange={(on) => set({ members: toggle(input.members, u.id, on) })} />
            ))}
        </div>

        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="ghost" onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={busy}>{group ? "Save" : "Create"}</Button>
        </div>
      </form>
    </Modal>
  );
}
