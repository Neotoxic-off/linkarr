"use client";

import { useCallback, useEffect, useState } from "react";
import type { ReactNode } from "react";
import { StateBadge, stateLook, Thumb } from "@/components/Titles";
import { useToast } from "@/components/Toasts";
import { Alert, Button, buttonClass, Empty, Input, PageHeader, Segmented, Select, Skeleton, WellList } from "@/components/ui";
import { api, errorMessage, watchHref } from "@/lib/api";
import type { CatalogItem, CatalogState, Links, TitleKind } from "@/lib/api";

type StateFilter = CatalogState | "All";

const stateOptions: readonly { value: StateFilter; label: string }[] = [
  { value: "All", label: "All" },
  ...(["Available", "Downloading", "NotIndexed", "Untracked", "Missing", "Unmonitored"] as const).map((s) => ({ value: s, label: stateLook[s].label })),
];

export default function CatalogPage(): ReactNode {
  const toast = useToast();
  const [state, setState] = useState<StateFilter>("All");
  const [kind, setKind] = useState<TitleKind | "">("");
  const [search, setSearch] = useState("");
  const [items, setItems] = useState<CatalogItem[] | null>(null);
  const [links, setLinks] = useState<Links>({ modularr: null, visularr: null });
  const [error, setError] = useState<string | null>(null);
  const [syncing, setSyncing] = useState(false);
  const [working, setWorking] = useState<string | null>(null);

  const load = useCallback((): void => {
    api.catalog({ state: state === "All" ? undefined : state, kind: kind || undefined, search: search.trim() || undefined })
      .then((list) => { setItems(list); setError(null); })
      .catch((e: unknown) => setError(errorMessage(e)));
  }, [state, kind, search]);

  useEffect(() => {
    const timer = setTimeout(load, 200);
    return () => clearTimeout(timer);
  }, [load]);

  useEffect(() => { api.links().then(setLinks).catch(() => undefined); }, []);

  async function sync(): Promise<void> {
    setSyncing(true);
    try {
      const result = await api.syncCatalog();
      if (result.catalogSyncError) toast({ key: "catalog", icon: "alert", tone: "bad", title: "Catalog sync failed", message: result.catalogSyncError });
      else toast({ key: "catalog", icon: "check", tone: "ok", title: "Catalog synced" });
      load();
    } catch (e) {
      toast({ key: "catalog", icon: "alert", tone: "bad", title: "Catalog sync failed", message: errorMessage(e) });
    } finally {
      setSyncing(false);
    }
  }

  async function act(item: CatalogItem, action: "refresh" | "monitor"): Promise<void> {
    setWorking(item.key);
    try {
      await (action === "refresh" ? api.refreshTitle(item.key) : api.monitorTitle(item.key));
      toast({
        key: item.key, icon: "check", tone: "ok", title: item.title,
        message: action === "refresh" ? "visularr is scanning its folder." : "modularr now monitors it.",
      });
      load();
    } catch (e) {
      toast({ key: item.key, icon: "alert", tone: "bad", title: item.title, message: errorMessage(e) });
    } finally {
      setWorking(null);
    }
  }

  return (
    <>
      <PageHeader title="Catalog" description="modularr’s library and queue merged with what visularr shows.">
        <Button icon="refresh" loading={syncing} onClick={sync}>Sync now</Button>
      </PageHeader>
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <Segmented label="State" value={state} options={stateOptions} onChange={setState} />
        <Select aria-label="Kind" value={kind} onChange={(e) => setKind(e.target.value === "Movie" || e.target.value === "Series" ? e.target.value : "")} className="w-36">
          <option value="">All kinds</option>
          <option value="Movie">Movies</option>
          <option value="Series">Series</option>
        </Select>
        <Input icon="search" placeholder="Search titles" aria-label="Search titles" value={search} onChange={(e) => setSearch(e.target.value)} className="w-full sm:w-64" />
      </div>
      {error && <Alert>{error}</Alert>}
      {!items && !error && <div className="space-y-2">{[0, 1, 2, 3].map((i) => <Skeleton key={i} className="h-20" />)}</div>}
      {items?.length === 0 && (state !== "All" || kind || search.trim()
        ? <Empty icon="searchX" title="No title matches">Try another state, kind or search.</Empty>
        : <Empty icon="layers" title="Nothing here yet">Connect both apps in Settings, then sync.</Empty>)}
      {items && items.length > 0 && (
        <WellList label="Titles">
          {items.map((item) => {
            const watch = item.state === "Available" ? watchHref(links.visularr, item) : null;
            return (
              <li key={item.key} className="flex flex-wrap items-center gap-x-3 gap-y-2 p-3 sm:flex-nowrap">
                <Thumb src={item.poster} />
                <div className="min-w-0 flex-1 basis-40">
                  <p className="truncate text-body font-medium">{item.title} <span className="text-ink-3">{item.year || ""}</span></p>
                  <p className="mt-1 flex flex-wrap items-center gap-2 text-meta text-ink-3">
                    <StateBadge state={item.state} progress={item.progress} />
                    {item.kind === "Movie" ? "Movie" : "Series"}
                  </p>
                </div>
                {item.state === "NotIndexed" && (
                  <Button size="sm" icon="refresh" loading={working === item.key} onClick={() => act(item, "refresh")}>Refresh visularr</Button>
                )}
                {item.state === "Untracked" && (item.tmdbId || item.tvdbId) && (
                  <Button size="sm" icon="plus" loading={working === item.key} onClick={() => act(item, "monitor")}>Monitor in modularr</Button>
                )}
                {watch && <a href={watch} target="_blank" rel="noopener noreferrer" className={buttonClass("ghost", "sm")} aria-label={`Watch ${item.title} in visularr`}>Watch</a>}
              </li>
            );
          })}
        </WellList>
      )}
    </>
  );
}
