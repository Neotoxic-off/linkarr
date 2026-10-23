"use client";

import type { ReactNode } from "react";
import { RequestBadge, stateLook, Thumb } from "@/components/Titles";
import { Alert, Avatar, Badge, IconButton, Meter, Progress, Sparkline, Tile, TileError, TileSkeleton, toneDot } from "@/components/ui";
import type { Tone } from "@/components/ui";
import { ago, bytes, device, duration, rate, when } from "@/lib/api";
import type { CatalogState, SessionInfo, ServiceHealth, Stats } from "@/lib/api";

// The Stats page's tiles; the Dashboard reuses a few. Each takes the whole answer (null while loading) and its error.
interface TileProps {
  stats: Stats | null;
  error: string | null;
  onRetry: () => void;
  className?: string;
}

const states: readonly CatalogState[] = ["Available", "Downloading", "NotIndexed", "Untracked", "Missing", "Unmonitored"];
const count = new Intl.NumberFormat();

// Loading and error keep the tile's height; children render only once the answer is in.
function Body({ stats, error, onRetry, children }: TileProps & { children: (stats: Stats) => ReactNode }): ReactNode {
  if (error && !stats) return <TileError message={error} onRetry={onRetry} />;
  if (!stats) return <TileSkeleton />;
  return children(stats);
}

// The colour repeats a state the text already says; label only when it does not.
function Dot({ tone, label }: { tone: Tone; label?: string }): ReactNode {
  return label
    ? <span className={`block size-2 shrink-0 rounded-full ${toneDot[tone]}`} title={label}><span className="sr-only">{label}</span></span>
    : <span aria-hidden="true" className={`block size-2 shrink-0 rounded-full ${toneDot[tone]}`} />;
}

const serviceTone = (s: ServiceHealth): Tone => (!s.configured ? "neutral" : s.reachable ? "ok" : "bad");

export function ServicesTile(props: TileProps): ReactNode {
  return (
    <Tile title="Services" link={{ href: "/settings/", label: "Settings" }} className={props.className}>
      <Body {...props}>
        {(stats) => (
          <div className="space-y-3">
            <ul className="space-y-2.5">
              {stats.services.map((s) => (
                <li key={s.app} className="flex items-start gap-2.5">
                  <span className="mt-[7px]"><Dot tone={serviceTone(s)} label={!s.configured ? "Not connected" : s.reachable ? "Reachable" : "Unreachable"} /></span>
                  <div className="min-w-0 flex-1">
                    <p className="flex flex-wrap items-baseline gap-x-2 text-body font-medium">
                      {s.app}
                      {s.version && <span className="text-meta font-normal text-ink-3">v{s.version}</span>}
                      {s.latencyMs != null && <span className="ml-auto text-meta font-normal text-ink-2 tabular-nums">{s.latencyMs} ms</span>}
                    </p>
                    {!s.configured && <p className="text-meta text-ink-3">Not connected.</p>}
                    {s.error && <p className="text-meta break-words text-bad">{s.error}</p>}
                  </div>
                </li>
              ))}
            </ul>
            <dl className="grid grid-cols-2 gap-3 border-t border-hair-2 pt-3 text-meta">
              <div><dt className="text-ink-3">Catalog sync</dt><dd title={when(stats.sync.lastCatalogSync)}>{ago(stats.sync.lastCatalogSync)}</dd></div>
              <div><dt className="text-ink-3">User sync</dt><dd title={when(stats.sync.lastUserSync)}>{ago(stats.sync.lastUserSync)}</dd></div>
            </dl>
            {stats.sync.catalogError && <Alert>Catalog: {stats.sync.catalogError}</Alert>}
            {stats.sync.userError && <Alert>Users: {stats.sync.userError}</Alert>}
          </div>
        )}
      </Body>
    </Tile>
  );
}

function Figure({ value, label, unit }: { value: string; label: string; unit?: string }): ReactNode {
  return (
    <div className="min-w-0">
      <p className="truncate font-display text-[28px] leading-none tabular-nums">{value}{unit && <span className="ml-1 font-sans text-meta text-ink-3">{unit}</span>}</p>
      <p className="mt-1 text-caption text-ink-3">{label}</p>
    </div>
  );
}

export function DownloadsTile(props: TileProps): ReactNode {
  return (
    <Tile title="Downloads" className={props.className}>
      <Body {...props}>
        {(stats) => !stats.downloads ? <p className="text-meta text-ink-3">No figures: modularr is not connected or did not answer.</p> : (
          <div className="grid grid-cols-2 gap-4">
            <Figure value={rate(stats.downloads.downloadBps)} label="Down" />
            <Figure value={rate(stats.downloads.uploadBps)} label="Up" />
            <Figure value={count.format(stats.downloads.active)} label={stats.downloads.active === 1 ? "Active download" : "Active downloads"} />
            <Figure value={stats.downloads.etaSeconds != null ? duration(stats.downloads.etaSeconds) : "–"}
              label={stats.downloads.remainingBytes > 0 ? `Left · ${bytes(stats.downloads.remainingBytes)}` : "Left"} />
          </div>
        )}
      </Body>
    </Tile>
  );
}

// Behind a VPN this is the VPN's exit: the provider name tells the admin at a glance whether it is on.
export function NetworkTile(props: TileProps): ReactNode {
  return (
    <Tile title="Download IP" className={props.className}>
      <Body {...props}>
        {(stats) => !stats.publicIp ? <p className="text-meta text-ink-3">Not known yet: modularr checks its public address every 5 minutes.</p> : (
          <div className="space-y-1.5">
            <p className="font-mono text-[22px] leading-tight break-all">{stats.publicIp.ip}</p>
            <p className="text-meta text-ink-2">{[stats.publicIp.isp, stats.publicIp.country].filter(Boolean).join(" · ") || "Unknown provider"}</p>
            <p className="text-caption text-ink-3" title={when(stats.publicIp.checkedAt)}>Checked {ago(stats.publicIp.checkedAt)}. A VPN shows its provider here, not your ISP.</p>
          </div>
        )}
      </Body>
    </Tile>
  );
}

export function StorageTile(props: TileProps): ReactNode {
  return (
    <Tile title="Storage" className={props.className}>
      <Body {...props}>
        {(stats) => stats.storage.length === 0 ? <p className="text-meta text-ink-3">No figures: modularr is not connected or did not answer.</p> : (
          <ul className="space-y-3.5">
            {stats.storage.map((s) => (
              <li key={s.path}>
                <p className="flex items-baseline justify-between gap-3 text-meta">
                  <span className="min-w-0 truncate font-mono" title={s.path}>{s.path}</span>
                  <span className="shrink-0 text-ink-2 tabular-nums">{bytes(s.freeBytes)} free</span>
                </p>
                <Meter value={s.totalBytes > 0 ? 1 - s.freeBytes / s.totalBytes : 0} label={`${s.path} used`} className="mt-1.5" />
                <p className="mt-1 text-caption text-ink-3 tabular-nums">of {bytes(s.totalBytes)}</p>
              </li>
            ))}
          </ul>
        )}
      </Body>
    </Tile>
  );
}

export function CatalogTile(props: TileProps): ReactNode {
  return (
    <Tile title="Catalog" link={{ href: "/catalog/", label: "Open" }} className={props.className}>
      <Body {...props}>
        {(stats) => {
          const total = states.reduce((sum, s) => sum + (stats.catalog.counts[s] ?? 0), 0);
          const trend = stats.catalog.trend;
          const first = trend[0];
          const last = trend[trend.length - 1];
          return (
            <div className="space-y-4">
              <div className="flex flex-wrap gap-x-8 gap-y-3">
                <Figure value={count.format(total)} label="Titles" />
                <Figure value={count.format(stats.catalog.kinds.Movie ?? 0)} label="Movies" />
                <Figure value={count.format(stats.catalog.kinds.Series ?? 0)} label="Series" />
              </div>
              {total > 0 && (
                <div>
                  <div className="flex h-2.5 overflow-hidden rounded-full bg-fill-2" aria-hidden="true">
                    {states.map((s) => (stats.catalog.counts[s] ?? 0) > 0 && (
                      <span key={s} className={toneDot[stateLook[s].tone]} style={{ width: `${(stats.catalog.counts[s] / total) * 100}%` }} />
                    ))}
                  </div>
                  <ul className="mt-3 grid grid-cols-2 gap-x-4 gap-y-1.5 text-meta sm:grid-cols-3">
                    {states.map((s) => (
                      <li key={s} className="flex items-center gap-2" title={stateLook[s].hint}>
                        <Dot tone={stateLook[s].tone} />
                        <span className="min-w-0 flex-1 truncate text-ink-2">{stateLook[s].label}</span>
                        <span className="tabular-nums">{count.format(stats.catalog.counts[s] ?? 0)}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              {first && last && trend.length > 1 && (
                <div className="border-t border-hair-2 pt-3">
                  <p className="flex items-baseline justify-between gap-3 text-meta">
                    <span className="text-ink-3">Available, last {trend.length} days</span>
                    <span className="tabular-nums">{last.available - first.available >= 0 ? "+" : ""}{count.format(last.available - first.available)}</span>
                  </p>
                  <Sparkline values={trend.map((d) => d.available)} className="mt-2 h-12 w-full"
                    label={`Available titles from ${count.format(first.available)} on ${first.date} to ${count.format(last.available)} on ${last.date}`} />
                </div>
              )}
            </div>
          );
        }}
      </Body>
    </Tile>
  );
}

export function WatchingTile(props: TileProps): ReactNode {
  return (
    <Tile title="Watching now" className={props.className}>
      <Body {...props}>
        {(stats) => stats.watching.length === 0
          ? <p className="text-meta text-ink-3">{stats.services.find((s) => s.app === "visularr")?.reachable ? "Nobody is on visularr right now." : "visularr is not connected or did not answer."}</p>
          : (
            <ul className="space-y-3">
              {stats.watching.map((w, i) => (
                <li key={`${w.user}-${w.device}-${i}`} className="flex items-start gap-3">
                  <Avatar user={{ username: w.user, avatar: null }} className="size-9 text-meta" />
                  <div className="min-w-0 flex-1">
                    <p className="flex flex-wrap items-center gap-x-2 text-body">
                      <span className="font-medium">{w.user}</span>
                      {w.nowPlaying ? <span className="min-w-0 truncate text-ink-2">{w.nowPlaying.title}</span> : <span className="text-ink-3">Idle</span>}
                      {w.nowPlaying?.paused && <Badge>Paused</Badge>}
                    </p>
                    {w.nowPlaying?.progress != null && <Progress value={w.nowPlaying.progress} tone="live" label={`${w.user} playback`} className="mt-1.5" />}
                    <p className="mt-1 text-caption break-words text-ink-3">
                      {[w.device, w.client, w.ip].filter(Boolean).join(" · ")}{w.lastActivity && <span title={when(w.lastActivity)}> · {ago(w.lastActivity)}</span>}
                    </p>
                  </div>
                </li>
              ))}
            </ul>
          )}
      </Body>
    </Tile>
  );
}

export function ImportedTile(props: TileProps): ReactNode {
  return (
    <Tile title="Recently available" className={props.className}>
      <Body {...props}>
        {(stats) => stats.catalog.recentlyImported.length === 0 ? <p className="text-meta text-ink-3">Titles show here as they become playable.</p> : (
          <ul className="space-y-2.5">
            {stats.catalog.recentlyImported.map((t) => (
              <li key={t.key} className="flex items-center gap-3">
                <Thumb src={t.poster} className="h-12 w-8" />
                <p className="min-w-0 flex-1 truncate text-body">{t.title} <span className="text-ink-3">{t.year || ""}</span></p>
                <span className="shrink-0 text-meta text-ink-3" title={when(t.at)}>{ago(t.at)}</span>
              </li>
            ))}
          </ul>
        )}
      </Body>
    </Tile>
  );
}

export function RequestedTile(props: TileProps): ReactNode {
  return (
    <Tile title="Recently requested" link={{ href: "/requests/", label: "All" }} className={props.className}>
      <Body {...props}>
        {(stats) => stats.catalog.recentlyRequested.length === 0 ? <p className="text-meta text-ink-3">No requests yet.</p> : (
          <ul className="space-y-2.5">
            {stats.catalog.recentlyRequested.map((r) => (
              <li key={r.id} className="flex items-center gap-3">
                <Thumb src={r.poster} className="h-12 w-8" />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-body">{r.title} <span className="text-ink-3">{r.year || ""}</span></p>
                  <p className="text-caption text-ink-3"><span title={when(r.created)}>{r.username} · {ago(r.created)}</span></p>
                </div>
                <RequestBadge status={r.status} />
              </li>
            ))}
          </ul>
        )}
      </Body>
    </Tile>
  );
}

export function RequestersTile(props: TileProps): ReactNode {
  return (
    <Tile title="Top requesters" className={props.className}>
      <Body {...props}>
        {(stats) => {
          const top = stats.catalog.topRequesters;
          const most = top[0]?.count ?? 1;
          return top.length === 0 ? <p className="text-meta text-ink-3">No requests yet.</p> : (
            <ul className="space-y-2.5">
              {top.map((r) => (
                <li key={r.username}>
                  <p className="flex items-baseline justify-between gap-3 text-body"><span className="truncate">{r.username}</span><span className="text-meta text-ink-2 tabular-nums">{count.format(r.count)}</span></p>
                  <div aria-hidden="true" className="mt-1 h-1.5 rounded-full bg-ink/70" style={{ width: `${(r.count / most) * 100}%` }} />
                </li>
              ))}
            </ul>
          );
        }}
      </Body>
    </Tile>
  );
}

// One signed-in browser: device, where it signed in and was last seen, and how to end it.
export function SessionRow({ session, onEnd, busy }: { session: SessionInfo; onEnd?: () => void; busy?: boolean }): ReactNode {
  const moved = session.lastIp && session.ip && session.lastIp !== session.ip;
  return (
    <li className="flex items-center gap-3 py-2.5">
      <div className="min-w-0 flex-1">
        <p className="flex flex-wrap items-center gap-2 text-body font-medium">
          <span title={session.userAgent ?? undefined}>{device(session.userAgent)}</span>
          {session.current && <Badge tone="ok">This browser</Badge>}
        </p>
        <p className="mt-0.5 text-caption break-words text-ink-3">
          <span className="font-mono">{session.ip ?? "unknown address"}</span>
          {moved && <> → <span className="font-mono" title="Last seen from">{session.lastIp}</span></>}
          {" · "}<span title={`Signed in ${when(session.created)}`}>active {ago(session.lastSeen)}</span>
        </p>
      </div>
      {onEnd && !session.current && <IconButton variant="ghost" icon="logout" label={`Sign out ${device(session.userAgent)}`} onClick={onEnd} disabled={busy} />}
    </li>
  );
}
