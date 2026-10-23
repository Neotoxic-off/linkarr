"use client";

import { useCallback, useEffect, useState } from "react";
import type { ReactNode } from "react";
import { useSession } from "@/components/AuthGate";
import { CatalogTile, DownloadsTile, ImportedTile, NetworkTile, RequestedTile, RequestersTile, ServicesTile, SessionRow, StorageTile, WatchingTile } from "@/components/Overview";
import { useToast } from "@/components/Toasts";
import { Alert, Avatar, Badge, Button, Empty, PageHeader, Section, Skeleton, WellList } from "@/components/ui";
import { ago, api, displayName, errorMessage, when } from "@/lib/api";
import type { Stats } from "@/lib/api";

// Admin only (the API refuses members): IPs and devices of every user are on this page.
export default function StatsPage(): ReactNode {
  const { user } = useSession();
  const toast = useToast();
  const [stats, setStats] = useState<Stats | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [ending, setEnding] = useState<string | null>(null);

  const load = useCallback((): Promise<void> =>
    api.stats().then((s) => { setStats(s); setError(null); }).catch((e: unknown) => setError(errorMessage(e))), []);

  useEffect(() => {
    if (!user.admin) return;
    void load();
    const timer = setInterval(() => void load(), 30_000);
    return () => clearInterval(timer);
  }, [load, user.admin]);

  if (!user.admin) return <Empty icon="chart" title="Admins only">Stats show every user’s devices and addresses.</Empty>;

  async function endSession(userId: number, name: string, id: string): Promise<void> {
    setEnding(id);
    try {
      await api.endUserSession(userId, id);
      setStats((s) => s && { ...s, users: s.users.map((u) => (u.id === userId ? { ...u, sessions: u.sessions.filter((x) => x.id !== id) } : u)) });
      toast({ key: `session-${id}`, icon: "check", tone: "ok", title: name, message: "That session is signed out of linkarr." });
    } catch (e) {
      toast({ key: `session-${id}`, icon: "alert", tone: "bad", title: name, message: errorMessage(e) });
    } finally {
      setEnding(null);
    }
  }

  const refresh = (): void => {
    setRefreshing(true);
    void load().finally(() => setRefreshing(false));
  };
  const tile = { stats, error, onRetry: refresh };
  return (
    <>
      <PageHeader title="Stats" description="Live from both apps, refreshed every 30 seconds.">
        <Button icon="refresh" loading={refreshing} onClick={refresh}>Refresh</Button>
      </PageHeader>
      {error && stats && <div className="mb-4"><Alert>Could not refresh: {error}</Alert></div>}
      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        <ServicesTile {...tile} />
        <DownloadsTile {...tile} />
        <NetworkTile {...tile} />
        <CatalogTile {...tile} className="md:col-span-2" />
        <StorageTile {...tile} />
        <WatchingTile {...tile} className="md:col-span-2" />
        <RequestersTile {...tile} />
        <ImportedTile {...tile} />
        <RequestedTile {...tile} className="xl:col-span-2" />
      </div>
      <div className="mt-6">
        <Section title="Sign-ins" description="linkarr sessions per user: the device, the address it signed in from and the last one seen.">
          {!stats ? <div className="space-y-2">{[0, 1].map((i) => <Skeleton key={i} className="h-16" />)}</div> : (
            <WellList label="Users">
              {stats.users.map((u) => (
                <li key={u.id} className="p-3.5">
                  <div className="flex items-center gap-3">
                    <Avatar user={u} className="size-9 text-meta" />
                    <div className="min-w-0 flex-1">
                      <p className="flex flex-wrap items-center gap-x-2 gap-y-1 text-body font-medium">
                        <span className="truncate">{displayName(u)}</span>
                        {u.admin && <Badge>Admin</Badge>}
                        {u.disabled && <Badge tone="bad">Disabled</Badge>}
                      </p>
                      <p className="text-caption text-ink-3" title={when(u.lastSignIn)}>
                        {u.lastSignIn ? `Signed in ${ago(u.lastSignIn)}` : "Never signed in"} · {u.sessions.length} {u.sessions.length === 1 ? "session" : "sessions"}
                      </p>
                    </div>
                  </div>
                  {u.sessions.length > 0 && (
                    <ul className="mt-1 divide-y divide-hair-2 pl-12">
                      {u.sessions.map((s) => (
                        <SessionRow key={s.id} session={s} busy={ending === s.id} onEnd={() => void endSession(u.id, u.username, s.id)} />
                      ))}
                    </ul>
                  )}
                </li>
              ))}
            </WellList>
          )}
        </Section>
      </div>
    </>
  );
}
