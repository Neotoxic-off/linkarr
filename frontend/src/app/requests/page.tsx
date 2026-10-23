"use client";

import { useCallback, useEffect, useState } from "react";
import type { ReactNode } from "react";
import { useSession } from "@/components/AuthGate";
import { RequestBadge, Thumb } from "@/components/Titles";
import { useToast } from "@/components/Toasts";
import { Alert, ButtonLink, Confirm, Empty, IconButton, PageHeader, Skeleton, WellList } from "@/components/ui";
import { ago, api, errorMessage, when } from "@/lib/api";
import type { TitleRequest } from "@/lib/api";

export default function RequestsPage(): ReactNode {
  const { user } = useSession();
  const toast = useToast();
  const [requests, setRequests] = useState<TitleRequest[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [working, setWorking] = useState<number | null>(null);
  const [forgetting, setForgetting] = useState<TitleRequest | null>(null);

  const load = useCallback((): void => {
    api.requests().then((list) => { setRequests(list); setError(null); }).catch((e: unknown) => setError(errorMessage(e)));
  }, []);

  useEffect(() => {
    load();
    const timer = setInterval(load, 30_000);
    return () => clearInterval(timer);
  }, [load]);

  async function act(request: TitleRequest, action: () => Promise<unknown>, done: string): Promise<void> {
    setWorking(request.id);
    try {
      await action();
      toast({ key: `request-${request.id}`, icon: "check", tone: "ok", title: request.title, message: done });
      load();
    } catch (e) {
      toast({ key: `request-${request.id}`, icon: "alert", tone: "bad", title: request.title, message: errorMessage(e) });
    } finally {
      setWorking(null);
    }
  }

  return (
    <>
      <PageHeader title="Requests" description={user.admin ? "Every request, newest first." : "Your requests, newest first."}>
        {(user.admin || user.rights.request) && <ButtonLink href="/discover/" icon="plus">Request a title</ButtonLink>}
      </PageHeader>
      {error && <Alert>{error}</Alert>}
      {!requests && !error && <div className="space-y-2">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-20" />)}</div>}
      {requests?.length === 0 && <Empty icon="list" title="No requests yet">Find a title in Discover and request it.</Empty>}
      {requests && requests.length > 0 && (
        <WellList label="Requests">
          {requests.map((r) => (
            <li key={r.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 p-3 sm:flex-nowrap">
              <Thumb src={r.poster} />
              <div className="min-w-0 flex-1">
                <p className="truncate text-body font-medium">{r.title} <span className="text-ink-3">{r.year || ""}</span></p>
                <p className="mt-1 flex flex-wrap items-center gap-2 text-meta text-ink-3">
                  <RequestBadge status={r.status} progress={r.progress} />
                  <span title={when(r.created)}>{[r.kind === "Movie" ? "Movie" : "Series", user.admin ? r.username : null, ago(r.created)].filter(Boolean).join(" · ")}</span>
                </p>
                {r.error && <p className="mt-1 text-meta break-words text-bad">{r.error}</p>}
              </div>
              {user.admin && <div className="-mr-1.5 flex shrink-0 items-center max-sm:ml-auto">
              {user.admin && (r.status === "pending" || r.status === "failed") && (
                <IconButton variant="ghost" icon={r.status === "failed" ? "refresh" : "check"} label={r.status === "failed" ? `Retry ${r.title}` : `Approve ${r.title}`}
                  disabled={working === r.id} onClick={() => act(r, () => api.approveRequest(r.id), "Sent to modularr.")} />
              )}
              {user.admin && r.status === "pending" && (
                <IconButton variant="ghost" icon="ban" label={`Deny ${r.title}`} disabled={working === r.id} onClick={() => act(r, () => api.denyRequest(r.id), "Denied.")} />
              )}
              {user.admin && (
                <IconButton variant="ghost" icon="trash" label={`Forget ${r.title}`} disabled={working === r.id} onClick={() => setForgetting(r)} />
              )}
              </div>}
            </li>
          ))}
        </WellList>
      )}
      {forgetting && (
        <Confirm title={`Forget the request for ${forgetting.title}?`} confirm="Forget" onClose={() => setForgetting(null)}
          description={`It leaves this list${user.admin && forgetting.username !== user.username ? ` and ${forgetting.username}’s` : ""}. The title stays in modularr if it was added; it can be requested again.`}
          onConfirm={async () => {
            await api.deleteRequest(forgetting.id);
            toast({ key: `request-${forgetting.id}`, icon: "check", tone: "ok", title: forgetting.title, message: "Removed from the list." });
            load();
          }} />
      )}
    </>
  );
}
