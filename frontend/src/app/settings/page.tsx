"use client";

import { useEffect, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { Alert, Button, Field, Input, PageHeader, Section, Skeleton, Switch, useAction } from "@/components/ui";
import { api, errorMessage } from "@/lib/api";
import type { AppName, Settings, SettingsInput } from "@/lib/api";

type Draft = SettingsInput;

const toDraft = (s: Settings): Draft => ({
  modularrUrl: s.modularrUrl, modularrApiKey: null, modularrPublicUrl: s.modularrPublicUrl,
  visularrUrl: s.visularrUrl, visularrApiKey: null, visularrPublicUrl: s.visularrPublicUrl,
  linkarrPublicUrl: s.linkarrPublicUrl, requestsNeedApproval: s.requestsNeedApproval, autoFixNotIndexed: s.autoFixNotIndexed,
  requestsPerDay: s.requestsPerDay,
});

export default function SettingsPage(): ReactNode {
  const [settings, setSettings] = useState<Settings | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const save = useAction();
  const rotate = useAction();

  useEffect(() => {
    api.settings().then((s) => { setSettings(s); setDraft(toDraft(s)); }).catch((e: unknown) => setLoadError(errorMessage(e)));
  }, []);

  const header = <PageHeader title="Settings" description="How linkarr reaches visularr and modularr. API keys are encrypted at rest and never shown again." />;
  if (loadError) return <>{header}<Alert>{loadError}</Alert></>;
  if (!settings || !draft) return <>{header}<div className="grid max-w-3xl gap-4">{[0, 1].map((i) => <Skeleton key={i} className="h-72" />)}</div></>;

  const set = (patch: Partial<Draft>): void => setDraft({ ...draft, ...patch });
  const fromDeployment = (field: keyof SettingsInput): boolean => settings.deploymentFields.includes(field);

  function submit(event: FormEvent): void {
    event.preventDefault();
    if (!draft) return;
    void save.run(async () => {
      const saved = await api.saveSettings(draft);
      setSettings(saved);
      setDraft(toDraft(saved));
    });
  }

  return (
    <>
      {header}
      <form onSubmit={submit} className="grid max-w-3xl gap-4">
        {save.error && <Alert>{save.error}</Alert>}
        {save.done && <Alert tone="info">Saved. Users are being synced to both apps.</Alert>}
        <AppSection app="modularr" url={draft.modularrUrl} apiKey={draft.modularrApiKey} publicUrl={draft.modularrPublicUrl} keySet={settings.modularrKeySet}
          placeholder="http://modularr:8080"
          locked={{ url: fromDeployment("modularrUrl"), apiKey: fromDeployment("modularrApiKey"), publicUrl: fromDeployment("modularrPublicUrl") }}
          onChange={(url, apiKey, publicUrl) => set({ modularrUrl: url, modularrApiKey: apiKey, modularrPublicUrl: publicUrl })} />
        <AppSection app="visularr" url={draft.visularrUrl} apiKey={draft.visularrApiKey} publicUrl={draft.visularrPublicUrl} keySet={settings.visularrKeySet}
          placeholder="http://visularr:8096"
          locked={{ url: fromDeployment("visularrUrl"), apiKey: fromDeployment("visularrApiKey"), publicUrl: fromDeployment("visularrPublicUrl") }}
          onChange={(url, apiKey, publicUrl) => set({ visularrUrl: url, visularrApiKey: apiKey, visularrPublicUrl: publicUrl })} />
        <Section title="linkarr" description="Both apps link here for accounts and for each other's titles.">
          <div className="space-y-4">
            <Field label="Public URL" hint={fromDeployment("linkarrPublicUrl") ? deploymentHint
              : "How browsers reach linkarr. Empty: this page’s address when you save. modularr also posts its events here (set up automatically), so it must reach this address."}>
              <Input value={draft.linkarrPublicUrl ?? ""} disabled={fromDeployment("linkarrPublicUrl")} onChange={(e) => set({ linkarrPublicUrl: e.target.value || null })} placeholder="http://localhost:8070" />
            </Field>
            <Switch label="Requests need approval" description="Requests from non-admins wait for an admin before reaching modularr."
              checked={draft.requestsNeedApproval} onChange={(requestsNeedApproval) => set({ requestsNeedApproval })} />
            <Field label="Requests per day" hint="How many titles a non-admin may request in 24 hours. 0: no limit.">
              <Input type="number" min={0} max={1000} value={draft.requestsPerDay}
                onChange={(e) => set({ requestsPerDay: Math.max(0, Math.trunc(Number(e.target.value) || 0)) })} />
            </Field>
            <Switch label="Fix titles visularr has not indexed" description="After each catalog sync, ask visularr to scan the folder of every file it does not show yet."
              checked={draft.autoFixNotIndexed} onChange={(autoFixNotIndexed) => set({ autoFixNotIndexed })} />
            {rotate.error && <Alert>{rotate.error}</Alert>}
            {rotate.done && <Alert tone="info">New webhook token sent to modularr.</Alert>}
            <Button type="button" size="sm" icon="refresh" loading={rotate.busy}
              onClick={() => { if (confirm("Replace the token modularr uses to post its events here?")) void rotate.run(() => api.rotateWebhookToken()); }}>
              Rotate webhook token
            </Button>
          </div>
        </Section>
        <div><Button variant="primary" loading={save.busy}>Save settings</Button></div>
      </form>
    </>
  );
}

const deploymentHint = "Set by the deployment.";

function AppSection({ app, url, apiKey, publicUrl, keySet, placeholder, locked, onChange }: {
  app: AppName;
  url: string | null;
  apiKey: string | null;
  publicUrl: string | null;
  keySet: boolean;
  placeholder: string;
  // Fields set by the deployment: read-only here.
  locked: { url: boolean; apiKey: boolean; publicUrl: boolean };
  onChange: (url: string | null, apiKey: string | null, publicUrl: string | null) => void;
}): ReactNode {
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null);
  const [testing, setTesting] = useState(false);

  async function test(): Promise<void> {
    setTesting(true);
    setResult(null);
    try {
      setResult(await api.testApp(app, url, apiKey));
    } catch (e) {
      setResult({ ok: false, message: errorMessage(e) });
    } finally {
      setTesting(false);
    }
  }

  return (
    <Section title={app} actions={<Button type="button" size="sm" icon="bolt" loading={testing} onClick={test}>Test</Button>}>
      <div className="space-y-4">
        {result && <Alert tone={result.ok ? "info" : "bad"}>{result.message}</Alert>}
        <Field label="URL" hint={locked.url ? deploymentHint : "How linkarr reaches it (in Docker, by service name)."}>
          <Input value={url ?? ""} disabled={locked.url} onChange={(e) => onChange(e.target.value || null, apiKey, publicUrl)} placeholder={placeholder} />
        </Field>
        <Field label="API key" hint={locked.apiKey ? deploymentHint : keySet ? "A key is saved. Leave empty to keep it (a changed URL drops it: enter it again)." : "Not set."}>
          <Input type="password" value={apiKey ?? ""} disabled={locked.apiKey} onChange={(e) => onChange(url, e.target.value || null, publicUrl)} autoComplete="off"
            placeholder={keySet ? "••••••••" : ""} />
        </Field>
        {keySet && !locked.apiKey && <Button type="button" size="sm" variant="ghost" onClick={() => onChange(url, "", publicUrl)}>Clear the saved key</Button>}
        <Field label="Public URL" hint={locked.publicUrl ? deploymentHint : "How browsers reach it, for links from linkarr."}>
          <Input value={publicUrl ?? ""} disabled={locked.publicUrl} onChange={(e) => onChange(url, apiKey, e.target.value || null)} placeholder={placeholder.replace(/\/\/\w+:/, "//localhost:")} />
        </Field>
      </div>
    </Section>
  );
}
