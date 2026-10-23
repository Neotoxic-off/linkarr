import type { ReactNode } from "react";
import { Badge } from "@/components/ui";
import type { Tone } from "@/components/ui";
import type { CatalogState, RequestStatus } from "@/lib/api";

// Each colour means one state; the label always carries it.
export const stateLook: Record<CatalogState, { tone: Tone; label: string; hint: string }> = {
  Available: { tone: "ok", label: "Available", hint: "In modularr and playable in visularr." },
  Downloading: { tone: "live", label: "Downloading", hint: "In modularr's queue." },
  NotIndexed: { tone: "bad", label: "Not indexed", hint: "modularr has the file, visularr does not show it yet." },
  Untracked: { tone: "seek", label: "Untracked", hint: "In visularr, unknown to modularr." },
  Missing: { tone: "warn", label: "Missing", hint: "Monitored by modularr, no file yet." },
  Unmonitored: { tone: "neutral", label: "Unmonitored", hint: "In modularr, not monitored, no file." },
};

const requestLook: Record<RequestStatus, { tone: Tone; label: string }> = {
  pending: { tone: "neutral", label: "Awaiting approval" },
  denied: { tone: "bad", label: "Denied" },
  failed: { tone: "bad", label: "Failed" },
  requested: { tone: "seek", label: "Requested" },
  downloading: { tone: "live", label: "Downloading" },
  available: { tone: "ok", label: "Available" },
};

export function StateBadge({ state, progress }: { state: CatalogState; progress?: number | null }): ReactNode {
  const look = stateLook[state];
  return <Badge tone={look.tone}>{look.label}{state === "Downloading" && progress != null ? ` ${Math.round(progress * 100)}%` : ""}</Badge>;
}

export function RequestBadge({ status, progress }: { status: RequestStatus; progress?: number | null }): ReactNode {
  const look = requestLook[status];
  return <Badge tone={look.tone}>{look.label}{status === "downloading" && progress != null ? ` ${Math.round(progress * 100)}%` : ""}</Badge>;
}

// A small poster, or an empty frame of the same size so rows line up.
export function Thumb({ src, className = "h-16 w-11" }: { src: string | null; className?: string }): ReactNode {
  return src
    // eslint-disable-next-line @next/next/no-img-element
    ? <img src={src} alt="" loading="lazy" referrerPolicy="no-referrer" className={`shrink-0 rounded-thumb object-cover ${className}`} />
    : <span aria-hidden="true" className={`shrink-0 rounded-thumb bg-fill-2 ${className}`} />;
}
