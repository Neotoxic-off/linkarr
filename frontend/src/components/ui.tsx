"use client";

import Link from "next/link";
import { Children, createContext, isValidElement, useContext, useEffect, useId, useRef, useState } from "react";
import type { ButtonHTMLAttributes, CSSProperties, InputHTMLAttributes, KeyboardEvent as ReactKeyboardEvent, ReactNode, RefObject } from "react";
import { createPortal } from "react-dom";
import { errorMessage } from "@/lib/api";
import { Icon } from "@/components/icons";
import type { IconName } from "@/components/icons";

const variants = {
  primary: "bg-white text-canvas hover:bg-ink",
  glass: "glass text-ink hover:bg-fill-2",
  ghost: "text-ink-2 hover:bg-fill hover:text-ink",
  danger: "bg-bad/15 text-bad ring-1 ring-bad/30 hover:bg-bad/25",
} as const;

const sizes = { sm: "h-9 px-4 text-meta pointer-coarse:h-11", md: "h-11 px-5 text-body" } as const;

export const buttonClass = (variant: keyof typeof variants = "glass", size: keyof typeof sizes = "md"): string =>
  `inline-flex shrink-0 cursor-pointer items-center justify-center gap-2 rounded-pill font-semibold whitespace-nowrap transition active:scale-[.98] disabled:pointer-events-none disabled:opacity-40 ${sizes[size]} ${variants[variant]}`;

// A link that looks like a Button (same heights, touch target included), for navigation rather than actions.
export function ButtonLink({ href, variant = "glass", size = "md", icon, className = "", children, onClick }: {
  href: string; variant?: keyof typeof variants; size?: keyof typeof sizes; icon?: IconName; className?: string; children: ReactNode; onClick?: () => void;
}): ReactNode {
  return (
    <Link href={href} onClick={onClick} className={`${buttonClass(variant, size)} ${className}`}>
      {icon && <Icon name={icon} size={size === "sm" ? 14 : 16} />}
      {children}
    </Link>
  );
}

// A spinner takes the icon's place while loading, so the label and the width stay put.
export function Button({ variant = "glass", size = "md", icon, loading = false, className = "", children, disabled, ...props }:
  ButtonHTMLAttributes<HTMLButtonElement> & { variant?: keyof typeof variants; size?: keyof typeof sizes; icon?: IconName; loading?: boolean }): ReactNode {
  return (
    <button disabled={disabled || loading}
      className={`${buttonClass(variant, size)} ${className}`}
      {...props}>
      {loading ? <Spinner /> : icon && <Icon name={icon} size={size === "sm" ? 14 : 16} />}
      {children}
    </button>
  );
}

// glass for toolbars and headers; ghost for row actions inside a list, where a filled circle per row would be noise.
export function IconButton({ icon, label, className = "", badge, variant = "glass", ...props }:
  ButtonHTMLAttributes<HTMLButtonElement> & { icon: IconName; label: string; badge?: number; variant?: "glass" | "ghost" }): ReactNode {
  return (
    <button aria-label={label} title={label}
      className={`relative grid size-11 shrink-0 cursor-pointer place-items-center rounded-full text-ink-2 transition hover:bg-fill-2 hover:text-ink active:scale-[.98] disabled:opacity-40 ${variant === "glass" ? "glass" : ""} ${className}`}
      {...props}>
      <Icon name={icon} size={variant === "glass" ? 20 : 16} />
      {!!badge && <Count value={badge} className="absolute -top-0.5 -right-0.5" />}
    </button>
  );
}

// bg picks the colour: neutral by default, live for the downloads count.
export function Count({ value, className = "", bg = "bg-ink" }: { value: number; className?: string; bg?: string }): ReactNode {
  return (
    <span aria-hidden="true" className={`grid h-[18px] min-w-[18px] place-items-center rounded-pill ${bg} px-1 text-[10px] leading-none font-bold text-canvas tabular-nums ${className}`}>
      {value > 99 ? "99+" : value}
    </span>
  );
}

const control = "well w-full min-w-0 rounded-ctl text-body text-ink outline-none transition placeholder:text-ink-3 hover:border-white/20 focus:border-white/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-white/70";

export function Input({ className = "", icon, ...props }: InputHTMLAttributes<HTMLInputElement> & { icon?: IconName }): ReactNode {
  if (!icon) return <input className={`h-11 px-3.5 ${control} ${className}`} {...props} />;
  return (
    <div className={`relative min-w-0 ${className}`}>
      <Icon name={icon} size={16} className="pointer-events-none absolute top-1/2 left-3.5 -translate-y-1/2 text-ink-3" />
      <input className={`h-11 pr-3.5 pl-10 ${control}`} {...props} />
    </div>
  );
}

type Choice = { value: string; label: ReactNode };
const textOf = (c: Choice): string => (typeof c.label === "string" ? c.label : c.value);

// A themed listbox: native <select> popups ignore the theme (Firefox draws them in its own font and colours).
function Dropdown({ choices, value, onPick, label, disabled, className, children }: {
  choices: readonly Choice[];
  value: string;
  onPick: (value: string) => void;
  label?: string;
  disabled?: boolean;
  className: string;
  children: ReactNode;
}): ReactNode {
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const [at, setAt] = useState<CSSProperties>({});
  const button = useRef<HTMLButtonElement>(null);
  const list = useRef<HTMLUListElement>(null);
  const id = useId();

  // Fixed and portalled, so a scrolling panel or modal cannot clip it; it flips up when the space below is short.
  const show = (): void => {
    const box = button.current?.getBoundingClientRect();
    if (!box) return;
    const below = window.innerHeight - box.bottom - 12;
    const up = below < 240 && box.top > below;
    setAt({ left: box.left, minWidth: box.width, maxHeight: Math.min(320, up ? box.top - 12 : below),
      ...(up ? { bottom: window.innerHeight - box.top + 6 } : { top: box.bottom + 6 }) });
    setActive(Math.max(0, choices.findIndex((c) => c.value === value)));
    setOpen(true);
  };
  const close = (): void => { setOpen(false); button.current?.focus(); };
  const pick = (i: number): void => {
    close();
    const choice = choices[i];
    if (choice && choice.value !== value) onPick(choice.value);
  };

  useEffect(() => {
    if (!open) return;
    list.current?.focus();
    const away = (e: Event): void => {
      const target = e.target instanceof Node ? e.target : null;
      if (!target || (!list.current?.contains(target) && !button.current?.contains(target))) setOpen(false);
    };
    document.addEventListener("mousedown", away);
    window.addEventListener("scroll", away, true);
    window.addEventListener("resize", away);
    return () => {
      document.removeEventListener("mousedown", away);
      window.removeEventListener("scroll", away, true);
      window.removeEventListener("resize", away);
    };
  }, [open]);

  useEffect(() => {
    if (open) list.current?.children[active]?.scrollIntoView({ block: "nearest" });
  }, [open, active]);

  function key(e: ReactKeyboardEvent): void {
    const last = choices.length - 1;
    const moves: Record<string, number> = { ArrowDown: Math.min(active + 1, last), ArrowUp: Math.max(active - 1, 0), Home: 0, End: last, PageDown: Math.min(active + 8, last), PageUp: Math.max(active - 8, 0) };
    const move = moves[e.key];
    if (move !== undefined) setActive(move);
    else if (e.key === "Enter" || e.key === " ") pick(active);
    else if (e.key === "Escape") { e.stopPropagation(); close(); }
    else if (e.key === "Tab") setOpen(false);
    else if (e.key.length === 1) {
      // Type-ahead: the next choice starting with that letter.
      const letter = e.key.toLowerCase();
      const order = [...choices.keys()].map((n) => (active + 1 + n) % choices.length);
      const next = order.find((n) => !!choices[n] && textOf(choices[n]).toLowerCase().startsWith(letter));
      if (next !== undefined) setActive(next);
    } else return;
    if (e.key !== "Tab") e.preventDefault();
  }

  return (
    <>
      <button ref={button} type="button" disabled={disabled} aria-haspopup="listbox" aria-expanded={open} aria-controls={open ? id : undefined} aria-label={label}
        onClick={() => (open ? setOpen(false) : show())}
        onKeyDown={(e) => { if (e.key === "ArrowDown" || e.key === "ArrowUp") { e.preventDefault(); show(); } }}
        className={className}>
        {children}
      </button>
      {open && createPortal(
        <ul ref={list} id={id} role="listbox" aria-label={label} tabIndex={-1} onKeyDown={key} style={at}
          aria-activedescendant={`${id}-${active}`} onMouseDown={(e) => e.stopPropagation()}
          className="glass-pop fixed z-[60] max-w-[min(360px,calc(100vw-24px))] animate-rise overflow-y-auto overscroll-contain rounded-tile p-1.5 text-body outline-none">
          {choices.map((c, i) => (
            <li key={c.value} id={`${id}-${i}`} role="option" aria-selected={c.value === value}
              onMouseMove={() => setActive(i)} onClick={() => pick(i)}
              className={`flex min-h-10 cursor-pointer items-center gap-2 rounded-chip px-3 py-2 ${i === active ? "bg-fill-2 text-ink" : "text-ink-2"}`}>
              <span className="min-w-0 flex-1 truncate">{c.label}</span>
              {c.value === value && <Icon name="check" size={14} className="shrink-0 text-ink" />}
            </li>
          ))}
        </ul>,
        document.body,
      )}
    </>
  );
}

// Same API as a native select (value, onChange reading e.target.value, <option> children), drawn with the theme.
export function Select({ className = "", children, value, onChange, disabled, "aria-label": label }: {
  className?: string;
  children: ReactNode;
  value?: string | number;
  onChange?: (e: { target: { value: string } }) => void;
  disabled?: boolean;
  "aria-label"?: string;
}): ReactNode {
  const choices: Choice[] = Children.toArray(children).filter(isValidElement<{ value?: string | number; children?: ReactNode }>)
    .map((o) => ({ value: String(o.props.value ?? ""), label: o.props.children }));
  const current = String(value ?? "");
  return (
    <div className={`relative ${className}`}>
      <Dropdown choices={choices} value={current} onPick={(v) => onChange?.({ target: { value: v } })} label={label} disabled={disabled}
        className={`flex h-11 cursor-pointer items-center pr-9 pl-3.5 text-left disabled:cursor-default disabled:opacity-60 ${control}`}>
        <span className="truncate">{choices.find((c) => c.value === current)?.label}</span>
      </Dropdown>
      <Icon name="chevronDown" size={16} className="pointer-events-none absolute top-1/2 right-3 -translate-y-1/2 text-ink-3" />
    </div>
  );
}

export function Field({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }): ReactNode {
  return (
    <label className="block space-y-2">
      <span className="text-body font-medium">{label}</span>
      {children}
      {hint && <span className="block text-meta leading-relaxed text-ink-3">{hint}</span>}
    </label>
  );
}

// The whole row is the switch, so the hit area is the label as well as the track.
// hideLabel keeps the name for assistive tech only, for a switch inside a row that already says what it is.
export function Switch({ checked, onChange, label, description, disabled, hideLabel = false }: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
  description?: string;
  disabled?: boolean;
  hideLabel?: boolean;
}): ReactNode {
  return (
    <button type="button" role="switch" aria-checked={checked} disabled={disabled} onClick={() => onChange(!checked)}
      className={`group flex min-h-11 cursor-pointer items-center gap-3 rounded-ctl text-left disabled:cursor-default disabled:opacity-40 ${hideLabel ? "min-w-11 shrink-0 justify-center" : "w-full"}`}>
      <span className={hideLabel ? "sr-only" : "min-w-0 flex-1"}>
        <span className="block text-body font-medium">{label}</span>
        {description && <span className="block text-meta text-ink-3">{description}</span>}
      </span>
      <span className={`relative h-6 w-10 shrink-0 rounded-pill transition ${checked ? "bg-ok" : "bg-fill-2"}`}>
        <span className={`absolute top-0.5 left-0.5 size-5 rounded-full bg-white shadow transition-transform ${checked ? "translate-x-4" : ""}`} />
      </span>
    </button>
  );
}

export function Checkbox({ checked, onChange, label, description }: { checked: boolean; onChange: (checked: boolean) => void; label: string; description?: ReactNode }): ReactNode {
  return (
    <label className="flex min-h-11 cursor-pointer items-start gap-3 py-1 text-body">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} className="mt-0.5 size-4 shrink-0 cursor-pointer accent-white" />
      <span className="min-w-0">
        <span className="block font-medium">{label}</span>
        {description && <span className="block text-meta break-words text-ink-3">{description}</span>}
      </span>
    </label>
  );
}

export function Chip({ selected = false, onClick, children, className = "" }: { selected?: boolean; onClick?: () => void; children: ReactNode; className?: string }): ReactNode {
  return (
    <button type="button" onClick={onClick} aria-pressed={selected}
      className={`well inline-flex min-h-11 shrink-0 cursor-pointer items-center gap-1.5 rounded-pill px-4 text-meta font-medium text-ink-2 transition hover:text-ink aria-pressed:bg-fill-2 aria-pressed:text-ink sm:min-h-9 [@media(pointer:coarse)]:min-h-11 ${className}`}>
      {selected && <Icon name="check" size={14} bold />}
      {children}
    </button>
  );
}

// An ordered pick of several values (first is most wanted): chips to remove, a select to add.
export function PickList({ label, hint, values, choices, onChange, empty = "None" }: {
  label: string;
  hint?: ReactNode;
  values: readonly string[];
  choices: readonly string[];
  onChange: (values: string[]) => void;
  empty?: string;
}): ReactNode {
  const id = useId();
  const left = choices.filter((c) => !values.includes(c));
  return (
    <div role="group" aria-labelledby={id} className="space-y-2">
      <span id={id} className="block text-body font-medium">{label}</span>
      <div className="flex flex-wrap items-center gap-2">
        {values.length === 0 && <span className="text-meta text-ink-3">{empty}</span>}
        {values.map((value) => (
          <span key={value} className="well inline-flex h-9 items-center gap-1 rounded-pill pr-1 pl-3.5 text-meta font-medium">
            {value}
            <button type="button" onClick={() => onChange(values.filter((v) => v !== value))} aria-label={`Remove ${value}`}
              className="grid size-7 cursor-pointer place-items-center rounded-full text-ink-3 hover:bg-fill-2 hover:text-ink">
              <Icon name="x" size={14} />
            </button>
          </span>
        ))}
        {left.length > 0 && (
          <Dropdown choices={left.map((c) => ({ value: c, label: c }))} value="" onPick={(c) => onChange([...values, c])} label={`Add to ${label}`}
            className="inline-flex h-9 cursor-pointer items-center gap-1 rounded-pill border border-dashed border-hair px-3.5 text-meta font-medium text-ink-2 hover:text-ink disabled:opacity-60">
            <Icon name="plus" size={14} />Add
          </Dropdown>
        )}
      </div>
      {hint && <span className="block text-meta leading-relaxed text-ink-3">{hint}</span>}
    </div>
  );
}

const alertTones = { bad: "bg-bad/10 text-bad ring-bad/30", warn: "bg-warn/10 text-warn ring-warn/30", info: "bg-fill text-ink-2 ring-hair" } as const;

export function Alert({ children, tone = "bad" }: { children: ReactNode; tone?: keyof typeof alertTones }): ReactNode {
  return (
    <div role={tone === "info" ? "note" : "alert"} className={`flex items-start gap-2.5 rounded-ctl px-4 py-3 text-meta ring-1 ${alertTones[tone]}`}>
      <Icon name={tone === "info" ? "info" : "alert"} size={16} className="mt-0.5 shrink-0" />
      <div className="min-w-0 break-words">{children}</div>
    </div>
  );
}

// Done, downloading, missing, failed, plus seek while a search runs: each colour means one state, never decoration.
export type Tone = "neutral" | "ok" | "live" | "seek" | "warn" | "bad";

const badgeTones: Record<Tone, string> = {
  neutral: "bg-fill text-ink-2 ring-hair",
  ok: "bg-ok/12 text-ok ring-ok/25",
  live: "bg-live/12 text-live ring-live/25",
  seek: "bg-seek/12 text-seek ring-seek/25",
  warn: "bg-warn/12 text-warn ring-warn/25",
  bad: "bg-bad/12 text-bad ring-bad/25",
};

export const toneText: Record<Tone, string> = {
  neutral: "text-ink-3", ok: "text-ok", live: "text-live", seek: "text-seek", warn: "text-warn", bad: "text-bad",
};

export const toneDot: Record<Tone, string> = {
  neutral: "bg-ink-3", ok: "bg-ok", live: "bg-live", seek: "bg-seek", warn: "bg-warn", bad: "bg-bad",
};

export function Badge({ tone = "neutral", children, className = "" }: { tone?: Tone; children: ReactNode; className?: string }): ReactNode {
  return <span className={`inline-flex h-6 items-center gap-1 rounded-pill px-2.5 text-meta font-semibold whitespace-nowrap ring-1 ring-inset ${badgeTones[tone]} ${className}`}>{children}</span>;
}

export type Status =
  | { state: "library" | "searching" | "missing" | "failed" | "unmonitored" }
  | { state: "downloading"; progress: number }
  | { state: "unreleased"; date: string | null };

const statusLook: Record<Status["state"], { tone: Tone; label: string }> = {
  library: { tone: "ok", label: "In library" },
  downloading: { tone: "live", label: "Downloading" },
  searching: { tone: "seek", label: "Searching" },
  missing: { tone: "warn", label: "Missing" },
  failed: { tone: "bad", label: "Failed" },
  unmonitored: { tone: "neutral", label: "Unmonitored" },
  unreleased: { tone: "neutral", label: "Not released" },
};

// Laid over artwork or on a tile; the text label always carries the state, the colour only repeats it.
export function StatusBadge({ status, className = "" }: { status: Status; className?: string }): ReactNode {
  const { tone, label } = statusLook[status.state];
  const text = status.state === "downloading" ? `${label} ${Math.round(status.progress * 100)}%`
    : status.state === "unreleased" && status.date ? new Date(status.date).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" })
    : label;
  return (
    <span className={`inline-flex h-6 items-center gap-1.5 rounded-pill bg-black/80 px-2.5 text-caption font-semibold whitespace-nowrap ring-1 ring-white/10 backdrop-blur ${tone === "neutral" ? "text-ink-2" : toneText[tone]} ${className}`}>
      {status.state === "library" ? <Icon name="check" size={14} bold />
        : tone !== "neutral" && <span className={`size-1.5 rounded-full ${toneDot[tone]} ${status.state === "searching" ? "animate-pulse-dot" : ""}`} />}
      {text}
    </span>
  );
}

export function Rating({ value, className = "" }: { value: number | null; className?: string }): ReactNode {
  if (value == null || value <= 0) return null;
  return (
    <span className={`inline-flex items-center gap-1 font-semibold text-ink-2 tabular-nums ${className}`}>
      <Icon name="star" size={14} fill="currentColor" strokeWidth={0} className="text-ink-3" />{value.toFixed(1)}
    </span>
  );
}

// Width moves smoothly, but assistive tech hears 10% steps only.
export function Progress({ value, eta, label = "Progress", tone = "ink", className = "" }: { value: number; eta?: string | null; label?: string; tone?: "ink" | "live"; className?: string }): ReactNode {
  const step = Math.floor(Math.min(Math.max(value, 0), 1) * 10) * 10;
  return (
    <div role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={step}
      aria-valuetext={`${step}%${eta ? `, ${eta} left` : ""}`} className={`h-1.5 overflow-hidden rounded-full bg-fill-2 ${className}`}>
      <div className={`h-full rounded-full transition-[width] duration-700 ${tone === "live" ? "bg-live" : "bg-ink"}`} style={{ width: `${value * 100}%` }} />
    </div>
  );
}

// The one inset surface: a card inside a section or page. WellList is the same surface holding rows split by hairlines.
export function Well({ className = "", children }: { className?: string; children: ReactNode }): ReactNode {
  return <div className={`well rounded-card p-3.5 ${className}`}>{children}</div>;
}

export function WellList({ className = "", children, label }: { className?: string; children: ReactNode; label?: string }): ReactNode {
  return <ul aria-label={label} className={`well divide-y divide-hair-2 overflow-hidden rounded-card ${className}`}>{children}</ul>;
}

export function Section({ title, description, actions, children }: { title: string; description?: ReactNode; actions?: ReactNode; children: ReactNode }): ReactNode {
  return (
    <section className="glass rounded-panel">
      <header className="flex flex-wrap items-start justify-between gap-3 border-b border-hair-2 px-5 py-4">
        <div className="min-w-0">
          <h2 className="text-title font-semibold">{title}</h2>
          {description && <p className="mt-0.5 max-w-prose text-meta text-ink-3">{description}</p>}
        </div>
        {actions}
      </header>
      <div className="p-5">{children}</div>
    </section>
  );
}

export function PageHeader({ title, description, children }: { title: string; description?: ReactNode; children?: ReactNode }): ReactNode {
  return (
    <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
      <div className="min-w-0">
        <h1 tabIndex={-1} className="font-display text-[40px] leading-none outline-none">{title}</h1>
        {description && <p className="mt-2 text-meta text-ink-3">{description}</p>}
      </div>
      {children}
    </div>
  );
}

export function Empty({ icon = "film", title, children }: { icon?: IconName; title: string; children?: ReactNode }): ReactNode {
  return (
    <div className="glass flex flex-col items-center rounded-tile px-6 py-12 text-center">
      <span className="mb-4 grid size-12 place-items-center rounded-full bg-fill-2 text-ink-2"><Icon name={icon} size={20} /></span>
      <p className="text-title font-semibold">{title}</p>
      {children && <div className="mt-1.5 max-w-sm text-meta text-ink-2">{children}</div>}
    </div>
  );
}

export function Spinner({ className = "" }: { className?: string }): ReactNode {
  return <span role="status" aria-label="Loading" className={`inline-block size-4 animate-spin rounded-full border-2 border-current border-t-transparent ${className}`} />;
}

export function Skeleton({ className = "" }: { className?: string }): ReactNode {
  return <div aria-hidden="true" className={`animate-shimmer rounded-chip bg-[linear-gradient(90deg,rgb(255_255_255/.05)_0%,rgb(255_255_255/.11)_50%,rgb(255_255_255/.05)_100%)] bg-[length:200%_100%] ${className}`} />;
}

export function Segmented<T extends string>({ value, options, onChange, label, className = "" }: {
  value: T;
  options: readonly { value: T; label: string; count?: number }[];
  onChange: (value: T) => void;
  label: string;
  className?: string;
}): ReactNode {
  const move = (event: ReactKeyboardEvent, index: number): void => {
    const step = event.key === "ArrowRight" || event.key === "ArrowDown" ? 1 : event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    const target = (index + step + options.length) % options.length;
    const next = options[target];
    if (next) onChange(next.value);
    (event.currentTarget.parentElement?.children[target] as HTMLElement | undefined)?.focus();
  };
  return (
    <div role="radiogroup" aria-label={label} className={`glass rail inline-flex h-11 max-w-full shrink-0 items-center gap-1 overflow-x-auto rounded-pill p-1 pointer-coarse:h-12 ${className}`}>
      {options.map((o, i) => (
        <button key={o.value} type="button" role="radio" aria-checked={o.value === value} tabIndex={o.value === value ? 0 : -1}
          onClick={() => onChange(o.value)} onKeyDown={(e) => move(e, i)}
          className="flex h-full shrink-0 cursor-pointer items-center gap-1.5 rounded-pill px-4 text-meta font-semibold text-ink-2 transition hover:text-ink aria-checked:bg-fill-3 aria-checked:text-ink">
          {o.label}
          {o.count !== undefined && <span className="text-caption text-ink-3 tabular-nums">{o.count}</span>}
        </button>
      ))}
    </div>
  );
}

// The card every bento cell is made of. Loading, empty and error keep its height so the grid never jumps.
export function Tile({ title, link, className = "", style, children }: {
  title: string;
  link?: { href: string; label: string };
  className?: string;
  style?: CSSProperties;
  children: ReactNode;
}): ReactNode {
  return (
    <section aria-label={title} style={style} className={`glass flex min-h-[180px] min-w-0 animate-rise flex-col gap-3 rounded-tile p-5 ${className}`}>
      <header className="flex items-center justify-between gap-3">
        <h2 className="caption truncate">{title}</h2>
        {link && <Link href={link.href} className="-my-2 -mr-2 inline-flex shrink-0 items-center gap-0.5 rounded-pill px-2 py-2 text-meta font-medium whitespace-nowrap text-ink-2 hover:text-ink">{link.label}<Icon name="chevronRight" size={14} /></Link>}
      </header>
      {children}
    </section>
  );
}

export function TileSkeleton(): ReactNode {
  return (
    <div className="flex flex-1 flex-col gap-2.5" aria-hidden="true">
      <Skeleton className="h-10 w-1/2" />
      <Skeleton className="h-4" />
      <Skeleton className="h-4 w-2/3" />
    </div>
  );
}

export function TileError({ message, onRetry }: { message: string; onRetry: () => void }): ReactNode {
  return (
    <div className="flex flex-1 flex-col items-start justify-center gap-3 text-meta text-ink-2">
      <p className="flex items-start gap-2"><span className="mt-1.5 size-2 shrink-0 rounded-full bg-bad" /><span><span className="sr-only">Error: </span>{message}</span></p>
      <Button size="sm" variant="ghost" icon="refresh" onClick={onRetry}>Retry</Button>
    </div>
  );
}

let openDialogs = 0;

// Focus trap, Escape, focus restore and an inert background: shared by every dialog, sheet and popover.
export function useDialog(panel: RefObject<HTMLElement | null>, onClose: () => void): void {
  // Parents pass inline closures; the latest one is read so the setup runs once per opening.
  const close = useRef(onClose);
  useEffect(() => { close.current = onClose; });

  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    (panel.current?.querySelector<HTMLElement>("[autofocus], input, select") ?? panel.current)?.focus();
    const key = (e: KeyboardEvent): void => {
      if (e.key === "Escape") { e.stopPropagation(); close.current(); }
      if (e.key !== "Tab" || !panel.current) return;
      const focusable = [...panel.current.querySelectorAll<HTMLElement>("button, input, select, textarea, summary, a[href], [tabindex]:not([tabindex='-1'])")].filter((el) => !el.hasAttribute("disabled"));
      const [first, last] = [focusable[0], focusable[focusable.length - 1]];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last?.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus(); }
    };
    const app = document.getElementById("app");
    openDialogs += 1;
    app?.setAttribute("inert", "");
    document.body.style.overflow = "hidden";
    window.addEventListener("keydown", key);
    return () => {
      window.removeEventListener("keydown", key);
      openDialogs -= 1;
      if (openDialogs === 0) {
        app?.removeAttribute("inert");
        document.body.style.overflow = "";
      }
      previous?.focus();
    };
  }, [panel]);
}

// Below 640px every dialog is a bottom sheet; "popover" stays a sheet up to 1024px, then hangs under the header.
const modalLayouts = {
  md: { overlay: "sm:items-center sm:p-6", panel: "sm:max-w-xl sm:rounded-panel" },
  lg: { overlay: "sm:items-center sm:p-6", panel: "sm:max-w-4xl sm:rounded-panel" },
  popover: { overlay: "lg:items-start lg:justify-end lg:bg-transparent lg:pt-[84px] lg:pr-5", panel: "sm:max-w-xl lg:max-h-[min(680px,82dvh)] lg:w-[380px] lg:rounded-tile" },
} as const;

export function Modal({ title, description, actions, banner, onClose, size = "md", children, footer }: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  banner?: string | null;
  onClose: () => void;
  size?: keyof typeof modalLayouts;
  children: ReactNode;
  footer?: ReactNode;
}): ReactNode {
  const id = useId();
  const panel = useRef<HTMLDivElement>(null);
  useDialog(panel, onClose);

  return createPortal(
    <div className={`fixed inset-0 z-50 flex items-end justify-center bg-overlay ${modalLayouts[size].overlay}`} onMouseDown={onClose}>
      <div ref={panel} role="dialog" aria-modal="true" aria-labelledby={id} tabIndex={-1} onMouseDown={(e) => e.stopPropagation()}
        className={`glass-pop relative flex max-h-[92dvh] w-full animate-rise flex-col overflow-hidden rounded-t-panel outline-none ${modalLayouts[size].panel}`}>
        <span aria-hidden="true" className={`mx-auto mt-2 h-1 w-10 shrink-0 rounded-full bg-fill-3 sm:hidden ${size === "popover" ? "lg:hidden" : ""}`} />
        {banner && (
          // eslint-disable-next-line @next/next/no-img-element
          <img src={banner} alt="" className="pointer-events-none absolute inset-x-0 top-0 h-48 w-full object-cover opacity-35 [mask-image:linear-gradient(to_bottom,black,transparent)]" />
        )}
        <header className="relative flex items-start justify-between gap-4 px-5 pt-4 pb-3">
          <div className="min-w-0 pt-2">
            <h2 id={id} className="text-title font-semibold">{title}</h2>
            {description && <div className="mt-0.5 text-meta text-ink-2">{description}</div>}
          </div>
          <div className="-mr-2 flex shrink-0 items-center gap-1">
            {actions}
            <button onClick={onClose} aria-label="Close" className="grid size-11 cursor-pointer place-items-center rounded-full text-ink-2 hover:bg-fill-2 hover:text-ink">
              <Icon name="x" size={20} />
            </button>
          </div>
        </header>
        <div className="relative min-h-0 flex-1 overflow-y-auto">{children}</div>
        {footer && <footer className="relative flex flex-wrap justify-end gap-2 border-t border-hair-2 px-5 pt-3.5 pb-[max(14px,env(safe-area-inset-bottom))]">{footer}</footer>}
      </div>
    </div>,
    document.body,
  );
}

// Asked before an action that cannot be undone from here: the button says what happens, a failure shows inside and keeps it open.
export function Confirm({ title, description, confirm, variant = "danger", onConfirm, onClose }: {
  title: string;
  description: ReactNode;
  confirm: string;
  variant?: "danger" | "primary";
  onConfirm: () => Promise<void>;
  onClose: () => void;
}): ReactNode {
  const { busy, error, run } = useAction();
  return (
    <Modal title={title} description={description} onClose={onClose}
      footer={<>
        <Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant={variant} loading={busy} onClick={() => void run(async () => { await onConfirm(); onClose(); })}>{confirm}</Button>
      </>}>
      {error && <div className="px-5 pb-4"><Alert>{error}</Alert></div>}
    </Modal>
  );
}

// A trend over time as a line with a faint fill; the label carries the numbers for assistive tech.
export function Sparkline({ values, label, className = "h-12 w-full" }: { values: readonly number[]; label: string; className?: string }): ReactNode {
  if (values.length < 2) return null;
  const low = Math.min(...values);
  const span = Math.max(...values) - low || 1;
  const points = values.map((v, i) => `${(i / (values.length - 1)) * 100},${29 - ((v - low) / span) * 27}`).join(" ");
  return (
    <svg viewBox="0 0 100 30" preserveAspectRatio="none" role="img" aria-label={label} className={`overflow-visible text-ink ${className}`}>
      <polygon points={`0,30 ${points} 100,30`} className="fill-white/[.06]" />
      <polyline points={points} fill="none" stroke="currentColor" strokeWidth={1.5} strokeLinejoin="round" vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

// How full something is (a disk): warn past 90%.
export function Meter({ value, label, className = "" }: { value: number; label: string; className?: string }): ReactNode {
  const used = Math.min(Math.max(value, 0), 1);
  return (
    <div role="meter" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(used * 100)} className={`h-1.5 overflow-hidden rounded-full bg-fill-2 ${className}`}>
      <div className={`h-full rounded-full ${used > 0.9 ? "bg-warn" : "bg-ink"}`} style={{ width: `${used * 100}%` }} />
    </div>
  );
}

// Runs a save or delete so every form reports errors and success the same way.
export interface Action {
  busy: boolean;
  error: string | null;
  done: boolean;
  run: (action: () => Promise<void>) => Promise<void>;
  setError: (error: string | null) => void;
}

export function useAction(): Action {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const run = async (action: () => Promise<void>): Promise<void> => {
    setBusy(true);
    setError(null);
    setDone(false);
    try {
      await action();
      setDone(true);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  };
  return { busy, error, done, run, setError };
}

// Shown only while the form has unsaved changes, pinned above the bottom edge (and the phone nav) so it stays in reach.
export function SaveBar({ busy, error, dirty, label = "Save changes", disabled }: { busy: boolean; error: string | null; dirty: boolean; label?: string; disabled?: boolean }): ReactNode {
  if (!dirty && !busy) return error ? <Alert>{error}</Alert> : null;
  return (
    <div className="sticky bottom-[calc(88px+env(safe-area-inset-bottom))] z-20 animate-fade space-y-2 md:bottom-4">
      {error && <Alert>{error}</Alert>}
      <div className="glass flex items-center justify-between gap-3 rounded-pill py-1.5 pr-1.5 pl-5">
        <span className="text-meta text-ink-2">Unsaved changes</span>
        <Button variant="primary" loading={busy} disabled={disabled}>{label}</Button>
      </div>
    </div>
  );
}

const CloseMenu = createContext<() => void>(() => undefined);

// A dropdown of actions: arrow keys move, Escape and outside clicks close it and focus goes back to the trigger.
export function Menu({ label, trigger, className = "", align = "right", children }: {
  label: string;
  trigger?: ReactNode;
  className?: string;
  align?: "left" | "right";
  children: ReactNode;
}): ReactNode {
  const [at, setAt] = useState<CSSProperties | null>(null);
  const button = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const id = useId();
  const open = at !== null;

  // Fixed and portalled like Dropdown, so a clipping list or panel cannot cut it; it flips up when the space below is short.
  const show = (): void => {
    const box = button.current?.getBoundingClientRect();
    if (!box) return;
    const below = window.innerHeight - box.bottom - 12;
    const up = below < 280 && box.top > below;
    setAt({
      ...(align === "right" ? { right: Math.max(12, window.innerWidth - box.right) } : { left: Math.max(12, box.left) }),
      ...(up ? { bottom: window.innerHeight - box.top + 8 } : { top: box.bottom + 8 }),
      maxHeight: Math.max(160, (up ? box.top - 12 : below) - 8),
    });
  };
  const close = (): void => setAt(null);

  useEffect(() => {
    if (!open) return;
    const items = (): HTMLElement[] => [...(menu.current?.querySelectorAll<HTMLElement>("[role=menuitem]:not(:disabled)") ?? [])];
    const inside = (e: Event): boolean => e.target instanceof Node && (!!menu.current?.contains(e.target) || !!button.current?.contains(e.target));
    const away = (e: Event): void => { if (!inside(e)) setAt(null); };
    const key = (e: KeyboardEvent): void => {
      if (e.key === "Escape") {
        e.stopPropagation();
        setAt(null);
        button.current?.focus();
        return;
      }
      // Focus goes back to the trigger first, so the browser's own Tab moves on from there rather than off the page's end.
      if (e.key === "Tab") { setAt(null); button.current?.focus(); return; }
      const list = items();
      const now = list.indexOf(document.activeElement as HTMLElement);
      const moves: Record<string, number> = { ArrowDown: now + 1, ArrowUp: now - 1, Home: 0, End: list.length - 1 };
      const move = moves[e.key];
      if (move === undefined || list.length === 0) return;
      e.preventDefault();
      list[(move + list.length) % list.length]?.focus();
    };
    document.addEventListener("mousedown", away);
    document.addEventListener("keydown", key);
    window.addEventListener("scroll", away, true);
    window.addEventListener("resize", close);
    items()[0]?.focus();
    return () => {
      document.removeEventListener("mousedown", away);
      document.removeEventListener("keydown", key);
      window.removeEventListener("scroll", away, true);
      window.removeEventListener("resize", close);
    };
  }, [open]);

  return (
    <>
      <button ref={button} type="button" onClick={() => (open ? close() : show())} aria-haspopup="menu" aria-expanded={open} aria-controls={open ? id : undefined} aria-label={label} title={label}
        className={className || "glass grid size-11 cursor-pointer place-items-center rounded-full text-ink-2 transition hover:bg-fill-2 hover:text-ink"}>
        {trigger ?? <Icon name="more" size={20} bold />}
      </button>
      {at && createPortal(
        // Nearly opaque: a menu often opens over another blurred panel, whose own blur cannot reach behind it.
        <div ref={menu} id={id} role="menu" aria-label={label} style={at}
          className="glass-pop fixed z-[60] w-64 max-w-[calc(100vw-24px)] animate-rise overflow-y-auto overscroll-contain rounded-tile p-2">
          <CloseMenu value={close}>{children}</CloseMenu>
        </div>,
        document.body,
      )}
    </>
  );
}

export function MenuItem({ icon, href, onClick, danger = false, disabled, className = "", children }: {
  icon: IconName;
  href?: string;
  onClick?: () => void;
  danger?: boolean;
  disabled?: boolean;
  className?: string;
  children: ReactNode;
}): ReactNode {
  const close = useContext(CloseMenu);
  const look = `flex min-h-11 w-full cursor-pointer items-center gap-3 rounded-ctl px-3 text-left text-body font-medium transition hover:bg-fill-2 disabled:opacity-40 ${danger ? "text-bad" : "text-ink-2 hover:text-ink"} ${className}`;
  const content = <><Icon name={icon} size={16} className="shrink-0" />{children}</>;
  return href
    ? <Link role="menuitem" href={href} onClick={close} className={look}>{content}</Link>
    : <button role="menuitem" type="button" disabled={disabled} onClick={() => { close(); onClick?.(); }} className={look}>{content}</button>;
}

// The user's picture, or the first letter of their name.
export function Avatar({ user, className = "size-8 text-meta" }: { user: { username: string; avatar: string | null }; className?: string }): ReactNode {
  return user.avatar
    // eslint-disable-next-line @next/next/no-img-element
    ? <img src={user.avatar} alt="" className={`shrink-0 rounded-full object-cover ${className}`} />
    : <span aria-hidden="true" className={`grid shrink-0 place-items-center rounded-full bg-fill-2 font-semibold text-ink uppercase ${className}`}>{user.username[0]}</span>;
}

export function Logo({ markOnly = false }: { markOnly?: boolean }): ReactNode {
  return (
    // The mark is the word's first letter: [l]inkarr.
    <span className="flex items-center gap-1">
      <span aria-hidden="true" className="grid size-8 shrink-0 place-items-center rounded-chip bg-white font-display text-[22px] leading-none text-canvas">l</span>
      <span className="sr-only">linkarr</span>
      {!markOnly && <span aria-hidden="true" className="font-display text-[24px] leading-none">inkarr</span>}
    </span>
  );
}
