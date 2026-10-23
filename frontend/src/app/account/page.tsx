"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { useSession } from "@/components/AuthGate";
import { SessionRow } from "@/components/Overview";
import { useToast } from "@/components/Toasts";
import { Alert, Avatar, Button, Confirm, Field, Input, PageHeader, Section, Skeleton, useAction } from "@/components/ui";
import { api, displayName, errorMessage, UNAUTHORIZED } from "@/lib/api";
import type { SessionInfo } from "@/lib/api";

export default function AccountPage(): ReactNode {
  const { user, setUser } = useSession();
  const toast = useToast();
  const [name, setName] = useState(user.displayName ?? "");
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const profile = useAction();
  const password = useAction();
  const picture = useAction();
  const [sessions, setSessions] = useState<SessionInfo[] | null>(null);
  const [sessionsError, setSessionsError] = useState<string | null>(null);
  const [ending, setEnding] = useState<string | null>(null);
  const [confirmAll, setConfirmAll] = useState(false);
  const file = useRef<HTMLInputElement>(null);

  const loadSessions = useCallback((): void => {
    api.sessions().then((list) => { setSessions(list); setSessionsError(null); }).catch((e: unknown) => setSessionsError(errorMessage(e)));
  }, []);

  useEffect(loadSessions, [loadSessions]);

  async function endSession(session: SessionInfo): Promise<void> {
    setEnding(session.id);
    setSessionsError(null);
    try {
      await api.endSession(session.id);
      setSessions((list) => list?.filter((s) => s.id !== session.id) ?? null);
      toast({ key: `session-${session.id}`, icon: "check", tone: "ok", title: "Session signed out" });
    } catch (e) {
      setSessionsError(errorMessage(e));
    } finally {
      setEnding(null);
    }
  }

  function saveProfile(event: FormEvent): void {
    event.preventDefault();
    void profile.run(async () => {
      setUser(await api.updateProfile(name.trim() || null));
      toast({ key: "profile", icon: "check", tone: "ok", title: "Profile saved" });
    });
  }

  function savePassword(event: FormEvent): void {
    event.preventDefault();
    void password.run(async () => {
      const me = await api.changePassword(current, next);
      setUser(me);
      setCurrent("");
      setNext("");
      loadSessions();
      toast(me.passwordPending
        ? { key: "password", icon: "alert", tone: "warn", title: "Password changed in linkarr", message: "An app is unreachable: enter it again later to send it there." }
        : { key: "password", icon: "check", tone: "ok", title: "Password changed everywhere" });
    });
  }

  function upload(image: File | undefined): void {
    if (!image) return;
    void picture.run(async () => {
      setUser(await api.setAvatar(image));
      toast({ key: "picture", icon: "check", tone: "ok", title: "Picture changed" });
    });
  }

  return (
    <>
      <PageHeader title="Account" description={`Signed in as ${user.username}.`} />
      <div className="grid max-w-3xl gap-4">
        <Section title="Profile">
          <form onSubmit={saveProfile} className="space-y-4">
            {profile.error && <Alert>{profile.error}</Alert>}
            {picture.error && <Alert>{picture.error}</Alert>}
            <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
              <Avatar user={{ username: displayName(user), avatar: user.avatar }} className="size-16 text-title" />
              <input ref={file} type="file" accept="image/png,image/jpeg,image/gif,image/webp" className="sr-only" aria-label="Picture"
                onChange={(e) => { upload(e.target.files?.[0]); e.target.value = ""; }} />
              <Button type="button" size="sm" loading={picture.busy} onClick={() => file.current?.click()}>Change picture</Button>
              {user.avatar && <Button type="button" size="sm" variant="ghost" onClick={() => void picture.run(async () => setUser(await api.removeAvatar()))}>Remove</Button>}
            </div>
            <Field label="Display name"><Input value={name} onChange={(e) => setName(e.target.value)} maxLength={64} /></Field>
            <Button variant="primary" loading={profile.busy}>Save profile</Button>
          </form>
        </Section>
        <Section title="Password" description="One password for linkarr, visularr and modularr. Your other sessions end.">
          <form onSubmit={savePassword} className="space-y-4">
            {user.passwordPending && (
              <Alert tone="warn">An app did not get your last password. Enter your current password in both fields to send it again.</Alert>
            )}
            {password.error && <Alert>{password.error}</Alert>}
            <Field label="Current password">
              <Input type="password" value={current} onChange={(e) => setCurrent(e.target.value)} autoComplete="current-password" required />
            </Field>
            <Field label="New password" hint="At least 8 characters.">
              <Input type="password" value={next} onChange={(e) => setNext(e.target.value)} autoComplete="new-password" minLength={8} required />
            </Field>
            <Button variant="primary" loading={password.busy}>Change password</Button>
          </form>
        </Section>
        <Section title="Sessions" description="Where you are signed in to linkarr: the device, the address it signed in from and the last one seen. Only you and admins see this.">
          <div className="space-y-4">
            {sessionsError && <Alert>{sessionsError}</Alert>}
            {!sessions && !sessionsError && <Skeleton className="h-16" />}
            {sessions && sessions.length > 0 && (
              <ul className="well divide-y divide-hair-2 rounded-card px-3.5">
                {sessions.map((s) => <SessionRow key={s.id} session={s} busy={ending === s.id} onEnd={() => void endSession(s)} />)}
              </ul>
            )}
            <Button icon="logout" onClick={() => setConfirmAll(true)}>Sign out everywhere</Button>
          </div>
        </Section>
        {confirmAll && (
          <Confirm title="Sign out everywhere?" confirm="Sign out" onClose={() => setConfirmAll(false)}
            description="Every linkarr session and every visularr device ends, this browser included. modularr sessions end with your next password change."
            onConfirm={async () => { await api.signOutEverywhere(); window.dispatchEvent(new Event(UNAUTHORIZED)); }} />
        )}
      </div>
    </>
  );
}
