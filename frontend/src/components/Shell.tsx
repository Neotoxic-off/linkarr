"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useRef } from "react";
import type { ReactNode } from "react";
import { Ambient } from "@/components/Ambient";
import { useSession } from "@/components/AuthGate";
import { Icon } from "@/components/icons";
import type { IconName } from "@/components/icons";
import { ToastProvider } from "@/components/Toasts";
import { Avatar, Logo, Menu, MenuItem } from "@/components/ui";
import type { Me } from "@/lib/api";
import { displayName } from "@/lib/api";

interface NavItem {
  href: string;
  label: string;
  icon: IconName;
  shown: (user: Me) => boolean;
  // On the phone's tab bar (five at most, or the labels no longer fit at 360px); the others are in the user menu there.
  tab: boolean;
}

// Labels match each page's own title.
const navItems: readonly NavItem[] = [
  { href: "/", label: "Dashboard", icon: "home", shown: () => true, tab: true },
  { href: "/stats/", label: "Stats", icon: "chart", shown: (u) => u.admin, tab: true },
  { href: "/discover/", label: "Discover", icon: "compass", shown: (u) => u.admin || u.rights.request, tab: true },
  { href: "/requests/", label: "Requests", icon: "list", shown: (u) => u.admin || u.rights.request, tab: true },
  { href: "/catalog/", label: "Catalog", icon: "layers", shown: (u) => u.admin, tab: false },
  { href: "/users/", label: "Users", icon: "users", shown: (u) => u.admin, tab: true },
  { href: "/roles/", label: "Roles", icon: "key", shown: (u) => u.admin, tab: false },
  { href: "/groups/", label: "Groups", icon: "folder", shown: (u) => u.admin, tab: false },
  { href: "/settings/", label: "Settings", icon: "settings", shown: (u) => u.admin, tab: false },
];

const isCurrent = (pathname: string, href: string): boolean => (href === "/" ? pathname === "/" : pathname.startsWith(href));

export function Shell({ children }: { children: ReactNode }): ReactNode {
  const pathname = usePathname();
  const { user } = useSession();
  const first = useRef(true);
  const shown = navItems.filter((item) => item.shown(user));
  const tabs = shown.filter((item) => item.tab);

  // Moves focus to the new page's heading so screen readers start there; the first load keeps the browser's focus.
  useEffect(() => {
    if (first.current) { first.current = false; return; }
    const frame = requestAnimationFrame(() => document.querySelector<HTMLElement>("main h1")?.focus({ preventScroll: true }));
    return () => cancelAnimationFrame(frame);
  }, [pathname]);

  return (
    <ToastProvider>
      <Ambient src={null} />
      <div id="app" className="flex min-h-dvh">
        <a href="#content" className="sr-only z-50 rounded-pill bg-white px-4 py-2.5 font-semibold text-canvas focus:not-sr-only focus:fixed focus:top-3 focus:left-3">Skip to content</a>
        <aside className="sticky top-0 hidden h-dvh shrink-0 p-3 md:block">
          <div className="glass flex h-full w-[72px] flex-col rounded-panel px-3 py-4 xl:w-60">
            <Link href="/" aria-label="linkarr, Dashboard" className="mb-5 flex h-11 items-center rounded-pill px-1.5 max-xl:justify-center max-xl:px-0">
              <span className="xl:hidden"><Logo markOnly /></span>
              <span className="max-xl:hidden"><Logo /></span>
            </Link>
            <nav aria-label="Main" className="flex flex-1 flex-col gap-1 overflow-y-auto">
              {shown.map((item) => (
                <Link key={item.href} href={item.href} aria-current={isCurrent(pathname, item.href) ? "page" : undefined} title={item.label}
                  className="flex h-11 items-center gap-3 rounded-pill px-3.5 text-body font-medium text-ink-2 transition hover:bg-fill hover:text-ink aria-[current=page]:bg-fill-2 aria-[current=page]:text-ink max-xl:justify-center max-xl:px-0">
                  <Icon name={item.icon} size={20} className="shrink-0" />
                  <span className="flex-1 truncate max-xl:sr-only">{item.label}</span>
                </Link>
              ))}
            </nav>
          </div>
        </aside>
        <div className="flex min-w-0 flex-1 flex-col md:pr-3">
          <header className="glass sticky top-0 z-30 flex h-[calc(56px+env(safe-area-inset-top))] items-center gap-2 rounded-none border-x-0 border-t-0 px-3 pt-[env(safe-area-inset-top)] md:top-3 md:mt-3 md:h-16 md:rounded-panel md:border md:border-transparent md:bg-transparent md:px-3 md:pl-5 md:shadow-none md:backdrop-blur-none">
            <Link href="/" aria-label="linkarr, Dashboard" className="rounded-pill md:hidden"><Logo /></Link>
            <div className="ml-auto flex items-center gap-2"><AppSwitcher /><UserMenu /></div>
          </header>
          <main id="content" className="mx-auto w-full max-w-[1600px] min-w-0 flex-1 p-4 pb-28 md:pb-8 lg:p-5">{children}</main>
        </div>
        <nav aria-label="Main" style={{ gridTemplateColumns: `repeat(${tabs.length}, minmax(0, 1fr))` }}
          className="glass fixed inset-x-0 bottom-0 z-30 mx-3 mb-[max(12px,env(safe-area-inset-bottom))] grid h-16 rounded-panel px-1 md:hidden">
          {tabs.map((tab) => (
            <Link key={tab.href} href={tab.href} aria-current={isCurrent(pathname, tab.href) ? "page" : undefined}
              className="group flex flex-col items-center justify-center gap-0.5 rounded-tile text-caption font-semibold text-ink-3 transition aria-[current=page]:text-ink">
              <span className="grid h-8 w-12 place-items-center rounded-pill transition group-aria-[current=page]:bg-fill-2"><Icon name={tab.icon} size={20} /></span>
              <span className="max-w-full truncate">{tab.label}</span>
            </Link>
          ))}
        </nav>
      </div>
    </ToastProvider>
  );
}

const apps: readonly { name: string; icon: IconName; shown: (user: Me) => boolean }[] = [
  { name: "visularr", icon: "tv", shown: (u) => u.admin || u.rights.watch },
  { name: "modularr", icon: "download", shown: (u) => u.admin || u.rights.modularrAccess },
];

// The other apps, through linkarr's /go/app/ redirect (a server route, so a plain link), in a new tab.
function AppSwitcher(): ReactNode {
  const { user } = useSession();
  return (
    <nav aria-label="Apps" className="flex items-center gap-1">
      {apps.filter((a) => a.shown(user)).map((a) => (
        <a key={a.name} href={`/go/app/${a.name}`} target="_blank" rel="noopener noreferrer" title={`Open ${a.name} in a new tab`}
          className="flex h-11 items-center gap-2 rounded-pill px-3.5 text-body font-medium text-ink-2 transition hover:bg-fill hover:text-ink">
          <Icon name={a.icon} size={18} className="shrink-0" />
          <span className="max-sm:sr-only">{a.name}</span>
        </a>
      ))}
    </nav>
  );
}

function UserMenu(): ReactNode {
  const { user, logout } = useSession();
  const name = displayName(user);
  return (
    <Menu label={`Account menu for ${name}`}
      trigger={user.avatar ? <Avatar user={user} className="size-11" /> : name[0]}
      className={`grid size-11 cursor-pointer place-items-center overflow-hidden rounded-full font-display text-[22px] leading-none uppercase transition ${user.avatar ? "hover:opacity-85" : "bg-white text-canvas hover:bg-ink"}`}>
      <div className="mb-1 border-b border-hair-2 px-3 pt-2 pb-3">
        <p className="truncate text-body font-semibold">{name}</p>
        <p className="text-meta text-ink-3">{user.role}</p>
      </div>
      <MenuItem icon="user" href="/account/">Account</MenuItem>
      {navItems.filter((item) => !item.tab && item.shown(user)).map((item) => (
        <MenuItem key={item.href} icon={item.icon} href={item.href} className="md:hidden">{item.label}</MenuItem>
      ))}
      <MenuItem icon="logout" onClick={logout}>Sign out</MenuItem>
    </Menu>
  );
}
