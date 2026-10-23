"use client";

import Link from "next/link";
import { createContext, useCallback, useContext, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { createPortal } from "react-dom";
import { Icon } from "@/components/icons";
import type { IconName } from "@/components/icons";
import { IconButton, toneText } from "@/components/ui";
import type { Tone } from "@/components/ui";

export interface ToastAction {
  label: string;
  href?: string;
  onClick?: () => void;
}

export interface Toast {
  // A toast with the same key replaces the previous one, so a title's "searching" note becomes its result.
  key: string;
  icon: IconName;
  tone: Tone;
  title: string;
  message?: string | null;
  href?: string | null;
  actions?: ToastAction[];
}

const LIFETIME_MS = 6000;
const ERROR_LIFETIME_MS = 10000;
const MAX_TOASTS = 4;

const ToastContext = createContext<(toast: Toast) => void>(() => undefined);

export const useToast = (): ((toast: Toast) => void) => useContext(ToastContext);

export function ToastProvider({ children }: { children: ReactNode }): ReactNode {
  const [toasts, setToasts] = useState<(Toast & { id: number })[]>([]);
  const nextId = useRef(0);
  const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>());
  // Hovered or focused toasts stay until the pointer and focus leave (WCAG 2.2.1).
  const paused = useRef(false);

  const dismiss = useCallback((key: string) => {
    clearTimeout(timers.current.get(key));
    timers.current.delete(key);
    setToasts((t) => t.filter((x) => x.key !== key));
  }, []);

  const schedule = useCallback((toast: Toast) => {
    clearTimeout(timers.current.get(toast.key));
    if (!paused.current) timers.current.set(toast.key, setTimeout(() => dismiss(toast.key), toast.tone === "bad" ? ERROR_LIFETIME_MS : LIFETIME_MS));
  }, [dismiss]);

  const show = useCallback((toast: Toast) => {
    nextId.current += 1;
    const entry = { ...toast, id: nextId.current };
    setToasts((t) => [...t.filter((x) => x.key !== toast.key), entry].slice(-MAX_TOASTS));
    schedule(toast);
  }, [schedule]);

  const pause = (): void => {
    paused.current = true;
    timers.current.forEach(clearTimeout);
    timers.current.clear();
  };

  const resume = (list: HTMLElement): void => {
    if (list.matches(":hover") || list.contains(document.activeElement)) return;
    paused.current = false;
    toasts.forEach(schedule);
  };

  // A toast removed under the pointer never fires mouseleave.
  useEffect(() => {
    if (toasts.length === 0) paused.current = false;
  }, [toasts.length]);

  useEffect(() => {
    const pending = timers.current;
    return () => pending.forEach(clearTimeout);
  }, []);

  const action = "inline-flex min-h-11 cursor-pointer items-center rounded-pill px-3.5 text-meta font-semibold text-ink hover:bg-fill-2";

  // Portalled out of #app, so toasts stay usable while a dialog makes the page inert.
  return (
    <ToastContext value={show}>
      {children}
      {createPortal(
        <div aria-label="Notifications"
          onMouseEnter={pause} onFocus={pause}
          onMouseLeave={(e) => resume(e.currentTarget)}
          onBlur={(e) => { if (!e.currentTarget.contains(e.relatedTarget)) resume(e.currentTarget); }}
          className="pointer-events-none fixed inset-x-3 top-[calc(64px+env(safe-area-inset-top))] z-[60] flex flex-col items-center gap-2 md:inset-x-auto md:top-[76px] md:right-5 md:w-[380px] md:items-end">
          {toasts.map((t) => (
            <div key={t.id} role={t.tone === "bad" ? "alert" : "status"}
              className="glass-pop pointer-events-auto flex w-full max-w-[420px] animate-rise items-start gap-3 rounded-tile p-3 pr-1.5">
              <span className={`grid size-9 shrink-0 place-items-center rounded-full bg-fill-2 ${toneText[t.tone]}`}><Icon name={t.icon} size={16} /></span>
              <div className="min-w-0 flex-1 py-0.5">
                {t.href
                  ? <Link href={t.href} onClick={() => dismiss(t.key)} className="block truncate text-body font-semibold hover:underline">{t.title}</Link>
                  : <p className="truncate text-body font-semibold">{t.title}</p>}
                {t.message && <p className="mt-0.5 line-clamp-2 text-meta leading-relaxed text-ink-2">{t.message}</p>}
                {t.actions && (
                  <div className="-ml-3.5 mt-1 flex flex-wrap">
                    {t.actions.map((a) => a.href
                      ? <Link key={a.label} href={a.href} onClick={() => dismiss(t.key)} className={action}>{a.label}</Link>
                      : <button key={a.label} onClick={() => { dismiss(t.key); a.onClick?.(); }} className={action}>{a.label}</button>)}
                  </div>
                )}
              </div>
              <IconButton variant="ghost" icon="x" label="Dismiss" onClick={() => dismiss(t.key)} />
            </div>
          ))}
        </div>,
        document.body,
      )}
    </ToastContext>
  );
}
