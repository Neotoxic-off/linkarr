"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import type { ReactNode } from "react";
import { useSession } from "@/components/AuthGate";
import { ServicesTile, WatchingTile } from "@/components/Overview";
import { stateLook, Thumb } from "@/components/Titles";
import { Alert, ButtonLink, PageHeader, Progress, Tile, TileError, TileSkeleton } from "@/components/ui";
import { api, displayName, duration, errorMessage, rate, watchHref, when } from "@/lib/api";
import type { CatalogState, Dashboard, Stats } from "@/lib/api";

const states: readonly CatalogState[] = ["Available", "Downloading", "NotIndexed", "Untracked", "Missing", "Unmonitored"];

export default function DashboardPage(): ReactNode {
  const { user } = useSession();
  const [data, setData] = useState<Dashboard | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Admins also get the live overview (services, speed, who is watching); members never ask for it.
  const [stats, setStats] = useState<Stats | null>(null);
  const [statsError, setStatsError] = useState<string | null>(null);
  const admin = user.admin;

  const load = useCallback((): void => {
    api.dashboard().then((d) => { setData(d); setError(null); }).catch((e: unknown) => setError(errorMessage(e)));
    if (admin) api.stats().then((s) => { setStats(s); setStatsError(null); }).catch((e: unknown) => setStatsError(errorMessage(e)));
  }, [admin]);

  useEffect(() => {
    load();
    const timer = setInterval(load, 30_000);
    return () => clearInterval(timer);
  }, [load]);

  return (
    <>
      <PageHeader title="Dashboard" description={`Hello ${displayName(user)}.`}>
        <div className="flex flex-wrap gap-2">
          {data?.links.visularr && (user.admin || user.rights.watch) && <ButtonLink href={data.links.visularr} icon="play">Open visularr</ButtonLink>}
          {data?.links.modularr && (user.admin || user.rights.modularrAccess) && <ButtonLink href={data.links.modularr} icon="download">Open modularr</ButtonLink>}
        </div>
      </PageHeader>
      {user.passwordPending && (
        <div className="mb-4"><Alert tone="warn">An app did not get your last password. <Link href="/account/" className="underline">Enter it again</Link> to sign in there.</Alert></div>
      )}
      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        <Tile title="Catalog" link={user.admin ? { href: "/catalog/", label: "Open" } : undefined} className="xl:col-span-2">
          {error ? <TileError message={error} onRetry={load} /> : !data ? <TileSkeleton /> : (
            <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3">
              {states.map((state) => (
                <li key={state} className="well rounded-card p-3.5">
                  <p className="font-display text-[32px] leading-none tabular-nums">{data.counts[state] ?? 0}</p>
                  <p className="mt-1 text-meta text-ink-2" title={stateLook[state].hint}>{stateLook[state].label}</p>
                </li>
              ))}
            </ul>
          )}
        </Tile>
        {admin ? <ServicesTile stats={stats} error={statsError} onRetry={load} /> : <Tile title="Sync">
          {error ? <TileError message={error} onRetry={load} /> : !data ? <TileSkeleton /> : (
            <div className="space-y-3 text-meta">
              <div><p className="text-ink-3">Catalog</p><p>{when(data.lastCatalogSync)}</p></div>
              <div><p className="text-ink-3">Users</p><p>{when(data.lastUserSync)}</p></div>
              {data.catalogError && <Alert>{data.catalogError}</Alert>}
              {data.userError && <Alert>{data.userError}</Alert>}
            </div>
          )}
        </Tile>}
        <Tile title="Downloading" className="xl:col-span-2">
          {stats?.downloads && stats.downloads.active > 0 && (
            <p className="-mt-1 text-meta text-ink-2 tabular-nums">
              {rate(stats.downloads.downloadBps)} down · {rate(stats.downloads.uploadBps)} up{stats.downloads.etaSeconds != null && ` · ${duration(stats.downloads.etaSeconds)} left`}
            </p>
          )}
          {error ? <TileError message={error} onRetry={load} /> : !data ? <TileSkeleton /> : data.downloads.length === 0 ? <p className="text-meta text-ink-3">Nothing in modularr’s queue.</p> : (
            <ul className="space-y-3">
              {data.downloads.map((d) => (
                <li key={d.key} className="flex items-center gap-3">
                  <Thumb src={d.poster} className="h-12 w-8" />
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-body font-medium">
                      {watchHref(data.links.visularr, d) ? <a href={watchHref(data.links.visularr, d)!} target="_blank" rel="noopener noreferrer" className="hover:underline">{d.title}</a> : d.title}
                      {" "}<span className="text-ink-3">{d.year || ""}</span>
                    </p>
                    <Progress value={d.progress} tone="live" label={`${d.title} progress`} className="mt-1.5" />
                  </div>
                  <span className="text-meta text-ink-2 tabular-nums">{Math.round(d.progress * 100)}%</span>
                </li>
              ))}
            </ul>
          )}
        </Tile>
        {data?.users && (
          <Tile title="Users" link={{ href: "/users/", label: "Manage" }}>
            <div className="grid grid-cols-3 gap-3">
              <Count label="Users" value={data.users.total} />
              <Count label="Out of sync" value={data.users.outOfSync} warn />
              <Count label="Unmanaged" value={data.users.unmanaged} warn />
            </div>
            {data.pendingRequests > 0 && (
              <Link href="/requests/" className="-mx-2 inline-flex min-h-11 items-center self-start rounded-pill px-2 text-meta text-ink-2 underline hover:text-ink">
                {data.pendingRequests} request{data.pendingRequests > 1 ? "s" : ""} awaiting approval
              </Link>
            )}
          </Tile>
        )}
        {admin && <WatchingTile stats={stats} error={statsError} onRetry={load} className="md:col-span-2" />}
      </div>
    </>
  );
}

function Count({ label, value, warn = false }: { label: string; value: number; warn?: boolean }): ReactNode {
  return (
    <div className="well rounded-card p-3">
      <p className={`font-display text-[28px] leading-none tabular-nums ${warn && value > 0 ? "text-warn" : ""}`}>{value}</p>
      <p className="mt-1 text-caption text-ink-3">{label}</p>
    </div>
  );
}
