"use client";

import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { Thumb } from "@/components/Titles";
import { useToast } from "@/components/Toasts";
import { Alert, Badge, Button, Empty, Input, PageHeader, Segmented, Skeleton } from "@/components/ui";
import { api, errorMessage } from "@/lib/api";
import type { DiscoverItem, TitleKind } from "@/lib/api";

const kinds: readonly { value: TitleKind; label: string }[] = [{ value: "Movie", label: "Movies" }, { value: "Series", label: "Series" }];

// useSearchParams needs a Suspense boundary in a static export.
export default function DiscoverPage(): ReactNode {
  return <Suspense><Discover /></Suspense>;
}

function Discover(): ReactNode {
  const toast = useToast();
  // visularr's "Request in linkarr" lands here with ?q=: the search runs on arrival.
  const q = useSearchParams().get("q")?.trim() ?? "";
  const [kind, setKind] = useState<TitleKind>("Movie");
  const [term, setTerm] = useState(q);
  const [results, setResults] = useState<DiscoverItem[] | null>(null);
  const [busy, setBusy] = useState(q !== "");
  const [error, setError] = useState<string | null>(null);
  const [requesting, setRequesting] = useState<number | null>(null);

  useEffect(() => {
    if (!q) return;
    api.discover("Movie", q).then(setResults).catch((e: unknown) => setError(errorMessage(e))).finally(() => setBusy(false));
  }, [q]);

  async function search(event?: FormEvent, nextKind: TitleKind = kind): Promise<void> {
    event?.preventDefault();
    if (!term.trim()) return;
    setBusy(true);
    setError(null);
    try {
      setResults(await api.discover(nextKind, term.trim()));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function request(item: DiscoverItem): Promise<void> {
    const id = item.kind === "Movie" ? item.tmdbId : item.tvdbId;
    if (!id) return;
    setRequesting(id);
    try {
      const result = await api.requestTitle(item.kind, id);
      setResults((list) => list?.map((r) => (r === item ? { ...r, requested: true } : r)) ?? null);
      toast({
        key: `request-${id}`, icon: "check", tone: "ok", title: item.title, href: "/requests/",
        message: result.status === "pending" ? "Requested, waiting for an admin’s approval." : "Requested: modularr is looking for it.",
      });
    } catch (e) {
      toast({ key: `request-${id}`, icon: "alert", tone: "bad", title: item.title, message: errorMessage(e) });
    } finally {
      setRequesting(null);
    }
  }

  return (
    <>
      <PageHeader title="Discover" description="Find a movie or a series and request it." />
      <form onSubmit={search} className="mb-5 flex flex-wrap items-center gap-2">
        <Segmented label="Kind" value={kind} options={kinds} onChange={(k) => { setKind(k); setResults(null); void search(undefined, k); }} />
        <Input icon="search" placeholder={kind === "Movie" ? "Search movies" : "Search series"} aria-label="Search" value={term}
          onChange={(e) => setTerm(e.target.value)} className="min-w-0 flex-1 sm:max-w-md" />
        <Button variant="primary" loading={busy}>Search</Button>
      </form>
      {error && <Alert>{error}</Alert>}
      {busy && !results && <div className="space-y-2">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-28" />)}</div>}
      {!results && !busy && !error && <Empty icon="search" title="Search for a title">Type a name, then request what is missing; modularr finds and downloads it.</Empty>}
      {results?.length === 0 && <Empty icon="searchX" title="No match">Try another title or the other kind.</Empty>}
      {results && results.length > 0 && (
        <ul className="grid gap-3 md:grid-cols-2">
          {results.map((item) => {
            const id = item.kind === "Movie" ? item.tmdbId : item.tvdbId;
            return (
              <li key={`${item.kind}-${id}`} className="glass flex gap-4 rounded-tile p-3">
                <Thumb src={item.poster} className="h-36 w-24" />
                <div className="flex min-w-0 flex-1 flex-col">
                  <p className="text-title font-semibold">{item.title} <span className="font-normal text-ink-3">{item.year || ""}</span></p>
                  {item.overview && <p className="mt-1 line-clamp-3 text-meta text-ink-2">{item.overview}</p>}
                  <div className="mt-auto flex flex-wrap items-center gap-2 pt-3">
                    {item.inLibrary ? <Badge tone="ok">In the library</Badge>
                      : item.requested ? <Badge tone="seek">Requested</Badge>
                      : <Button size="sm" icon="plus" loading={requesting === id} onClick={() => request(item)}>Request</Button>}
                  </div>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </>
  );
}
