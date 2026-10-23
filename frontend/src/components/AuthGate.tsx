"use client";

import { createContext, useCallback, useContext, useEffect, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { api, errorMessage, UNAUTHORIZED } from "@/lib/api";
import type { AuthState, Me } from "@/lib/api";
import { Ambient } from "@/components/Ambient";
import { Alert, Button, Field, Input, Logo, Spinner } from "@/components/ui";

interface Session {
  user: Me;
  // After the user changes their own name, picture or password.
  setUser: (user: Me) => void;
  logout: () => Promise<void>;
}

const SessionContext = createContext<Session | null>(null);

export function useSession(): Session {
  const session = useContext(SessionContext);
  if (!session) throw new Error("useSession outside AuthGate");
  return session;
}

// The whole app is behind this gate: the API refuses everything but auth until a session cookie exists.
export function AuthGate({ children }: { children: ReactNode }): ReactNode {
  const [state, setState] = useState<AuthState | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback((): void => {
    api.authState().then(setState).catch((e: unknown) => setError(errorMessage(e)));
  }, []);

  useEffect(() => {
    refresh();
    window.addEventListener(UNAUTHORIZED, refresh);
    return () => window.removeEventListener(UNAUTHORIZED, refresh);
  }, [refresh]);

  const logout = useCallback(async (): Promise<void> => {
    await api.logout();
    setState((s) => s && { ...s, user: null });
  }, []);

  if (error) {
    return (
      <Centered>
        <div className="flex max-w-sm flex-col items-start gap-3">
          <Alert>Can’t reach the linkarr server: {error}</Alert>
          <Button icon="refresh" onClick={() => { setError(null); refresh(); }}>Try again</Button>
        </div>
      </Centered>
    );
  }
  if (!state) return <Centered><Spinner className="text-ink-2" /></Centered>;
  if (!state.user) return <Login setup={state.setupRequired} setupToken={state.setupTokenRequired === true} onSignedIn={(user) => setState({ setupRequired: false, user })} />;

  const setUser = (user: Me): void => setState((s) => s && { ...s, user });
  return <SessionContext value={{ user: state.user, setUser, logout }}>{children}</SessionContext>;
}

function Login({ setup, setupToken, onSignedIn }: { setup: boolean; setupToken: boolean; onSignedIn: (user: Me) => void }): ReactNode {
  const [username, setUsername] = useState("");
  const [token, setToken] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(event: FormEvent): Promise<void> {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      onSignedIn(await (setup ? api.setup(username, password, setupToken ? token : undefined) : api.login(username, password)));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="grid min-h-dvh place-items-center sm:p-4">
      <Ambient src={null} />
      <form onSubmit={submit} className="glass flex min-h-dvh w-full animate-rise flex-col justify-center gap-5 rounded-none border-0 p-6 pt-[max(24px,env(safe-area-inset-top))] sm:min-h-0 sm:max-w-sm sm:rounded-panel sm:border sm:p-8">
        <div className="space-y-6">
          <Logo />
          <div>
            <h1 className="font-display text-[40px] leading-none">{setup ? "One account, every app." : "Welcome back."}</h1>
            {/* Same sign-in card as modularr and visularr: one account, three apps. */}
            <p className="mt-2 text-meta text-ink-2">
              {setup ? "Create the admin account. It signs in to linkarr, visularr and modularr." : "Sign in with your linkarr account."}
            </p>
          </div>
        </div>
        {error && <Alert>{error}</Alert>}
        <Field label="Username">
          <Input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" autoFocus required />
        </Field>
        <Field label="Password" hint={setup ? "At least 8 characters." : undefined}>
          <Input type="password" value={password} onChange={(e) => setPassword(e.target.value)}
            autoComplete={setup ? "new-password" : "current-password"} minLength={setup ? 8 : undefined} required />
        </Field>
        {setup && setupToken && (
          <Field label="Setup token" hint="The LINKARR_SETUP_TOKEN value of this deployment.">
            <Input type="password" value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" required />
          </Field>
        )}
        <Button variant="primary" className="w-full" loading={busy}>{setup ? "Create account" : "Sign in"}</Button>
      </form>
    </main>
  );
}

function Centered({ children }: { children: ReactNode }): ReactNode {
  return <div className="flex min-h-dvh items-center justify-center p-4"><Ambient src={null} />{children}</div>;
}
