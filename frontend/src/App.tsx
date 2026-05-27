import {
  Activity,
  Boxes,
  ChevronRight,
  Cpu,
  Database,
  Download,
  FileCode2,
  FilePlus2,
  Folder,
  FolderPlus,
  Gauge,
  HardDrive,
  KeyRound,
  LayoutDashboard,
  LogOut,
  Network,
  Play,
  PlugZap,
  Plus,
  Pencil,
  RefreshCw,
  Save,
  Send,
  Server as ServerIcon,
  Settings,
  Shield,
  Square,
  Terminal,
  Trash2,
  Upload,
  Users,
  Wrench,
  X
} from "lucide-react";
import { clsx } from "clsx";
import type { ButtonHTMLAttributes, FormEvent, ReactNode } from "react";
import { useEffect, useRef, useState } from "react";
import { ApiClient, ApiError } from "./api";
import type {
  ApiKeySummary,
  Dashboard,
  DockerStatus,
  FileEntry,
  GameTemplate,
  PortDto,
  Server,
  UserSummary
} from "./types";

const tokenKey = "gamehostpanel.token";

type View =
  | { page: "dashboard" }
  | { page: "servers" }
  | { page: "create" }
  | { page: "admin" }
  | { page: "detail"; id: string };

export default function App() {
  const [token, setToken] = useState(() => localStorage.getItem(tokenKey));
  const [api] = useState(() => new ApiClient(token));
  const [user, setUser] = useState<UserSummary | null>(null);
  const [view, setView] = useState<View>({ page: "dashboard" });
  const [bootstrapping, setBootstrapping] = useState(true);

  useEffect(() => {
    api.setToken(token);
    if (token) {
      localStorage.setItem(tokenKey, token);
      api
        .me()
        .then(setUser)
        .catch(() => {
          localStorage.removeItem(tokenKey);
          setToken(null);
          setUser(null);
        })
        .finally(() => setBootstrapping(false));
    } else {
      setUser(null);
      setBootstrapping(false);
    }
  }, [api, token]);

  if (bootstrapping) {
    return <Splash />;
  }

  if (!token || !user) {
    return <LoginScreen api={api} onLogin={(nextToken, nextUser) => {
      setToken(nextToken);
      setUser(nextUser);
    }} />;
  }

  return (
    <div className="min-h-screen bg-[#f6f8fb]">
      <aside className="fixed inset-y-0 left-0 hidden w-64 border-r border-slate-200 bg-white lg:block">
        <div className="flex h-16 items-center gap-3 border-b border-slate-200 px-5">
          <div className="grid h-9 w-9 place-items-center rounded-md bg-slate-950 text-white">
            <ServerIcon size={18} />
          </div>
          <div>
            <div className="text-sm font-semibold text-slate-950">GameHostPanel</div>
            <div className="text-xs text-slate-500">Docker Game Hosting</div>
          </div>
        </div>
        <nav className="space-y-1 p-3">
          <NavButton icon={<LayoutDashboard size={18} />} label="Dashboard" active={view.page === "dashboard"} onClick={() => setView({ page: "dashboard" })} />
          <NavButton icon={<ServerIcon size={18} />} label="Server" active={view.page === "servers" || view.page === "detail"} onClick={() => setView({ page: "servers" })} />
          <NavButton icon={<Plus size={18} />} label="Erstellen" active={view.page === "create"} onClick={() => setView({ page: "create" })} />
          {user.role === "Admin" && (
            <NavButton icon={<Shield size={18} />} label="Admin" active={view.page === "admin"} onClick={() => setView({ page: "admin" })} />
          )}
        </nav>
        <div className="absolute bottom-0 left-0 right-0 border-t border-slate-200 p-4">
          <div className="mb-3 rounded-md bg-slate-50 p-3">
            <div className="text-sm font-medium text-slate-900">{user.username}</div>
            <div className="text-xs text-slate-500">{user.role}</div>
          </div>
          <button
            className="flex w-full items-center justify-center gap-2 rounded-md border border-slate-200 bg-white px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50"
            onClick={() => {
              localStorage.removeItem(tokenKey);
              setToken(null);
              setUser(null);
            }}
          >
            <LogOut size={16} /> Logout
          </button>
        </div>
      </aside>

      <main className="lg:pl-64">
        <TopBar view={view} setView={setView} user={user} onLogout={() => {
          localStorage.removeItem(tokenKey);
          setToken(null);
          setUser(null);
        }} />
        <div className="mx-auto max-w-7xl px-4 py-5 sm:px-6 lg:px-8">
          {view.page === "dashboard" && <DashboardView api={api} setView={setView} />}
          {view.page === "servers" && <ServersView api={api} setView={setView} />}
          {view.page === "create" && <CreateServerView api={api} setView={setView} />}
          {view.page === "admin" && <AdminView api={api} />}
          {view.page === "detail" && <ServerDetailView api={api} id={view.id} setView={setView} />}
        </div>
      </main>
    </div>
  );
}

function Splash() {
  return (
    <div className="grid min-h-screen place-items-center bg-slate-950 text-white">
      <div className="flex items-center gap-3">
        <RefreshCw className="animate-spin" size={20} />
        <span className="text-sm font-medium">Panel startet</span>
      </div>
    </div>
  );
}

function LoginScreen({ api, onLogin }: { api: ApiClient; onLogin: (token: string, user: UserSummary) => void }) {
  const [bootstrapRequired, setBootstrapRequired] = useState(false);
  const [username, setUsername] = useState("admin");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.getBootstrapRequired().then((x) => setBootstrapRequired(x.required)).catch(() => setBootstrapRequired(false));
  }, [api]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      const response = bootstrapRequired
        ? await api.bootstrap(username, password)
        : await api.login(username, password);
      onLogin(response.token, response.user);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Login fehlgeschlagen");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid min-h-screen grid-cols-1 bg-[#f6f8fb] lg:grid-cols-[1.1fr_0.9fr]">
      <section className="hidden bg-slate-950 p-10 text-white lg:flex lg:flex-col lg:justify-between">
        <div className="flex items-center gap-3">
          <div className="grid h-10 w-10 place-items-center rounded-md bg-emerald-400 text-slate-950">
            <ServerIcon size={20} />
          </div>
          <div className="font-semibold">GameHostPanel</div>
        </div>
        <div className="max-w-xl">
          <h1 className="text-5xl font-semibold leading-tight tracking-normal">Self-hosted Game-Server unter deiner Kontrolle.</h1>
          <div className="mt-8 grid grid-cols-3 gap-3 text-sm">
            <MiniStat icon={<Boxes size={18} />} label="Docker" value="Engine API" />
            <MiniStat icon={<Database size={18} />} label="DB" value="SQLite/Postgres" />
            <MiniStat icon={<Gauge size={18} />} label="RAM" value="leicht" />
          </div>
        </div>
        <div className="text-xs text-slate-400">Windows und Linux · lokale Installation · WebSocket-Konsole</div>
      </section>
      <section className="flex items-center justify-center px-5 py-10">
        <form onSubmit={submit} className="w-full max-w-md rounded-lg border border-slate-200 bg-white p-7 shadow-panel">
          <div className="mb-7">
            <div className="mb-3 grid h-11 w-11 place-items-center rounded-md bg-slate-950 text-white lg:hidden">
              <ServerIcon size={20} />
            </div>
            <h2 className="text-2xl font-semibold text-slate-950">{bootstrapRequired ? "Admin anlegen" : "Einloggen"}</h2>
            <p className="mt-1 text-sm text-slate-500">{bootstrapRequired ? "Erster Start: erstelle den lokalen Admin-Account." : "Melde dich am lokalen Panel an."}</p>
          </div>
          <label className="mb-4 block">
            <span className="mb-1 block text-sm font-medium text-slate-700">Benutzername</span>
            <input className="h-11 w-full rounded-md border border-slate-300 px-3 text-sm outline-none focus:border-emerald-500 focus:ring-4 focus:ring-emerald-100" value={username} onChange={(event) => setUsername(event.target.value)} />
          </label>
          <label className="mb-5 block">
            <span className="mb-1 block text-sm font-medium text-slate-700">Passwort</span>
            <input className="h-11 w-full rounded-md border border-slate-300 px-3 text-sm outline-none focus:border-emerald-500 focus:ring-4 focus:ring-emerald-100" type="password" value={password} onChange={(event) => setPassword(event.target.value)} />
          </label>
          {error && <div className="mb-4 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</div>}
          <button disabled={busy} className="flex h-11 w-full items-center justify-center gap-2 rounded-md bg-emerald-500 px-4 text-sm font-semibold text-slate-950 hover:bg-emerald-400">
            <KeyRound size={16} /> {busy ? "Bitte warten" : bootstrapRequired ? "Admin erstellen" : "Login"}
          </button>
        </form>
      </section>
    </div>
  );
}

function TopBar({ view, setView, user, onLogout }: { view: View; setView: (view: View) => void; user: UserSummary; onLogout: () => void }) {
  return (
    <header className="sticky top-0 z-20 border-b border-slate-200 bg-white/90 backdrop-blur">
      <div className="flex h-16 items-center justify-between px-4 sm:px-6 lg:px-8">
        <div className="flex min-w-0 items-center gap-3">
          <div className="grid h-9 w-9 place-items-center rounded-md bg-slate-950 text-white lg:hidden">
            <ServerIcon size={18} />
          </div>
          <div>
            <div className="text-sm font-semibold text-slate-950">{titleForView(view)}</div>
            <div className="text-xs text-slate-500">localhost panel</div>
          </div>
        </div>
        <div className="flex items-center gap-2 lg:hidden">
          <button className="rounded-md border border-slate-200 p-2 text-slate-700" onClick={() => setView({ page: "dashboard" })}><LayoutDashboard size={17} /></button>
          <button className="rounded-md border border-slate-200 p-2 text-slate-700" onClick={() => setView({ page: "servers" })}><ServerIcon size={17} /></button>
          <button className="rounded-md border border-slate-200 p-2 text-slate-700" onClick={() => setView({ page: "create" })}><Plus size={17} /></button>
          {user.role === "Admin" && <button className="rounded-md border border-slate-200 p-2 text-slate-700" onClick={() => setView({ page: "admin" })}><Shield size={17} /></button>}
          <button className="rounded-md border border-slate-200 p-2 text-slate-700" onClick={onLogout}><LogOut size={17} /></button>
        </div>
      </div>
    </header>
  );
}

function DashboardView({ api, setView }: { api: ApiClient; setView: (view: View) => void }) {
  const [dashboard, setDashboard] = useState<Dashboard | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    let active = true;
    api.dashboard()
      .then((data) => active && setDashboard(data))
      .catch((err) => active && setError(messageOf(err)));
    const timer = window.setInterval(() => {
      api.dashboard().then((data) => active && setDashboard(data)).catch(() => undefined);
    }, 8000);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [api]);

  if (error) {
    return <ErrorBox message={error} />;
  }

  return (
    <div className="space-y-5">
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-4">
        <MetricCard icon={<ServerIcon size={19} />} label="Server" value={dashboard?.totalServers ?? 0} tone="slate" />
        <MetricCard icon={<Activity size={19} />} label="Laufend" value={dashboard?.runningServers ?? 0} tone="emerald" />
        <MetricCard icon={<Square size={19} />} label="Gestoppt" value={dashboard?.stoppedServers ?? 0} tone="amber" />
        <MetricCard icon={<PlugZap size={19} />} label="Docker" value={dashboard?.docker.available ? "online" : "offline"} tone={dashboard?.docker.available ? "cyan" : "red"} />
      </div>

      <div className="grid grid-cols-1 gap-5 xl:grid-cols-[1.3fr_0.7fr]">
        <Panel title="Laufende Instanzen" icon={<Activity size={18} />} action={<Button onClick={() => setView({ page: "servers" })} variant="soft"><ChevronRight size={16} /> Server</Button>}>
          <div className="divide-y divide-slate-100">
            {(dashboard?.running ?? []).length === 0 && <EmptyState icon={<ServerIcon size={22} />} text="Keine laufenden Server" />}
            {(dashboard?.running ?? []).map((server) => (
              <button key={server.id} className="flex w-full items-center justify-between gap-3 py-3 text-left hover:bg-slate-50" onClick={() => setView({ page: "detail", id: server.id })}>
                <div className="min-w-0">
                  <div className="truncate text-sm font-semibold text-slate-900">{server.name}</div>
                  <div className="truncate text-xs text-slate-500">{server.image}</div>
                </div>
                <StatusBadge status={server.status} />
              </button>
            ))}
          </div>
        </Panel>
        <DockerCard docker={dashboard?.docker} />
      </div>
    </div>
  );
}

function ServersView({ api, setView }: { api: ApiClient; setView: (view: View) => void }) {
  const [servers, setServers] = useState<Server[]>([]);
  const [busyId, setBusyId] = useState("");
  const [error, setError] = useState("");

  const load = () => api.servers().then(setServers).catch((err) => setError(messageOf(err)));
  useEffect(() => {
    load();
  }, []);

  async function run(id: string, action: "start" | "stop" | "restart" | "kill") {
    setBusyId(id);
    try {
      await api.command(id, action);
      await load();
    } catch (err) {
      setError(messageOf(err));
    } finally {
      setBusyId("");
    }
  }

  return (
    <div className="space-y-5">
      <PageHeader title="Server" action={<Button onClick={() => setView({ page: "create" })}><Plus size={16} /> Server erstellen</Button>} />
      {error && <ErrorBox message={error} />}
      <div className="overflow-hidden rounded-lg border border-slate-200 bg-white shadow-panel">
        <div className="hidden grid-cols-[1.2fr_0.8fr_0.7fr_0.9fr_1fr] gap-4 border-b border-slate-200 bg-slate-50 px-4 py-3 text-xs font-semibold uppercase text-slate-500 md:grid">
          <div>Name</div>
          <div>Spiel</div>
          <div>Status</div>
          <div>Ports</div>
          <div>Aktionen</div>
        </div>
        <div className="divide-y divide-slate-100">
          {servers.length === 0 && <EmptyState icon={<ServerIcon size={22} />} text="Noch keine Server" />}
          {servers.map((server) => (
            <div key={server.id} className="grid grid-cols-1 gap-3 px-4 py-4 md:grid-cols-[1.2fr_0.8fr_0.7fr_0.9fr_1fr] md:items-center">
              <button className="min-w-0 text-left" onClick={() => setView({ page: "detail", id: server.id })}>
                <div className="truncate text-sm font-semibold text-slate-950">{server.name}</div>
                <div className="truncate text-xs text-slate-500">{server.image}</div>
              </button>
              <div className="text-sm text-slate-600">{server.game}</div>
              <StatusBadge status={server.status} />
              <div className="flex flex-wrap gap-1 text-xs text-slate-600">
                {server.ports.map((port) => <span key={`${port.host}-${port.protocol}`} className="rounded bg-slate-100 px-2 py-1">{port.host}:{port.container}/{port.protocol}</span>)}
              </div>
              <ServerActionBar disabled={busyId === server.id} onStart={() => run(server.id, "start")} onStop={() => run(server.id, "stop")} onRestart={() => run(server.id, "restart")} onOpen={() => setView({ page: "detail", id: server.id })} />
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

function CreateServerView({ api, setView }: { api: ApiClient; setView: (view: View) => void }) {
  const [templates, setTemplates] = useState<GameTemplate[]>([]);
  const [selectedId, setSelectedId] = useState("");
  const selected = templates.find((template) => template.id === selectedId);
  const [name, setName] = useState("");
  const [memory, setMemory] = useState(2048);
  const [cpu, setCpu] = useState(0);
  const [autoStart, setAutoStart] = useState(true);
  const [pullImage, setPullImage] = useState(true);
  const [startNow, setStartNow] = useState(false);
  const [envRows, setEnvRows] = useState<KeyValueRow[]>([]);
  const [ports, setPorts] = useState<PortDto[]>([]);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.templates().then((data) => {
      setTemplates(data);
      if (data[0]) {
        applyTemplate(data[0]);
      }
    }).catch((err) => setError(messageOf(err)));
  }, [api]);

  function applyTemplate(template: GameTemplate) {
    setSelectedId(template.id);
    setName(template.name);
    setMemory(template.recommendedMemoryMb);
    setEnvRows(envToRows(template.env));
    setPorts(template.ports);
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!selected) {
      return;
    }
    setBusy(true);
    setError("");
    try {
      const server = await api.createServer({
        templateId: selected.id,
        name,
        env: rowsToEnv(envRows),
        ports,
        memoryMb: memory,
        cpuLimit: cpu,
        autoStart,
        pullImage,
        startNow
      });
      setView({ page: "detail", id: server.id });
    } catch (err) {
      setError(messageOf(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="space-y-5">
      <PageHeader title="Server erstellen" />
      {error && <ErrorBox message={error} />}
      <div className="grid grid-cols-1 gap-5 xl:grid-cols-[0.95fr_1.05fr]">
        <Panel title="Template" icon={<Boxes size={18} />}>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            {templates.map((template) => (
              <button
                type="button"
                key={template.id}
                className={clsx("rounded-md border p-3 text-left transition", selectedId === template.id ? "border-emerald-500 bg-emerald-50" : "border-slate-200 bg-white hover:border-slate-300")}
                onClick={() => applyTemplate(template)}
              >
                <div className="text-sm font-semibold text-slate-950">{template.name}</div>
                <div className="mt-1 text-xs text-slate-500">{template.category}</div>
                <div className="mt-3 truncate rounded bg-slate-100 px-2 py-1 font-mono text-xs text-slate-600">{template.image}</div>
              </button>
            ))}
          </div>
        </Panel>

        <div className="space-y-5">
          <Panel title="Konfiguration" icon={<Settings size={18} />}>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
              <Field label="Name"><input className={inputClass} value={name} onChange={(event) => setName(event.target.value)} /></Field>
              <Field label="RAM Limit (MB)"><input className={inputClass} type="number" min={256} value={memory} onChange={(event) => setMemory(Number(event.target.value))} /></Field>
              <Field label="CPU Limit (0 = unbegrenzt)"><input className={inputClass} type="number" min={0} step={0.25} value={cpu} onChange={(event) => setCpu(Number(event.target.value))} /></Field>
              <div className="grid grid-cols-1 gap-2 pt-6 sm:grid-cols-3">
                <Check label="Autostart" checked={autoStart} onChange={setAutoStart} />
                <Check label="Image pullen" checked={pullImage} onChange={setPullImage} />
                <Check label="Direkt starten" checked={startNow} onChange={setStartNow} />
              </div>
            </div>
          </Panel>

          <Panel title="Ports" icon={<Network size={18} />}>
            <PortEditor ports={ports} setPorts={setPorts} />
          </Panel>

          <Panel title="Env Vars" icon={<FileCode2 size={18} />}>
            <KeyValueEditor rows={envRows} setRows={setEnvRows} />
          </Panel>

          <div className="flex justify-end">
            <Button disabled={busy}><Plus size={16} /> {busy ? "Erstelle" : "Server erstellen"}</Button>
          </div>
        </div>
      </div>
    </form>
  );
}

function ServerDetailView({ api, id, setView }: { api: ApiClient; id: string; setView: (view: View) => void }) {
  const [server, setServer] = useState<Server | null>(null);
  const [tab, setTab] = useState<"console" | "files" | "settings">("console");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  const load = () => api.server(id).then(setServer).catch((err) => setError(messageOf(err)));

  useEffect(() => {
    load();
    const timer = window.setInterval(load, 10000);
    return () => window.clearInterval(timer);
  }, [id]);

  async function run(action: "start" | "stop" | "restart" | "kill") {
    setBusy(true);
    setError("");
    try {
      setServer(await api.command(id, action));
    } catch (err) {
      setError(messageOf(err));
    } finally {
      setBusy(false);
    }
  }

  async function deleteServer() {
    if (!server) {
      return;
    }

    const confirmed = window.confirm(`Server '${server.name}' wirklich löschen?`);
    if (!confirmed) {
      return;
    }

    const deleteFiles = window.confirm("Sollen auch die Server-Dateien gelöscht werden?\nOK = Ja, Dateien löschen\nAbbrechen = Nein, nur aus Panel entfernen");

    setBusy(true);
    setError("");
    try {
      await api.deleteServer(server.id, deleteFiles);
      setView({ page: "servers" });
    } catch (err) {
      setError(messageOf(err));
    } finally {
      setBusy(false);
    }
  }

  if (!server) {
    return error ? <ErrorBox message={error} /> : <SplashPanel />;
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
        <div>
          <button className="mb-2 text-sm font-medium text-slate-500 hover:text-slate-900" onClick={() => setView({ page: "servers" })}>Server /</button>
          <div className="flex flex-wrap items-center gap-3">
            <h1 className="text-2xl font-semibold text-slate-950">{server.name}</h1>
            <StatusBadge status={server.status} />
            {server.autoStart && <span className="rounded bg-cyan-50 px-2 py-1 text-xs font-medium text-cyan-700">Autostart</span>}
          </div>
          <div className="mt-1 text-sm text-slate-500">{server.image}</div>
        </div>
        <div className="flex items-center gap-2">
          <ServerActionBar disabled={busy} onStart={() => run("start")} onStop={() => run("stop")} onRestart={() => run("restart")} onOpen={() => setTab("console")} />
          <Button variant="soft" className="text-red-700" disabled={busy} onClick={deleteServer}>
            <Trash2 size={16} />
            Server löschen
          </Button>
        </div>
      </div>

      {error && <ErrorBox message={error} />}

      <div className="grid grid-cols-1 gap-4 md:grid-cols-4">
        <MetricCard icon={<Cpu size={18} />} label="CPU" value={`${server.stats?.cpuPercent ?? 0}%`} tone="cyan" />
        <MetricCard icon={<HardDrive size={18} />} label="RAM" value={formatBytes(server.stats?.memoryBytes ?? 0)} sub={server.memoryMb ? `${server.memoryMb} MB Limit` : "kein Limit"} tone="emerald" />
        <MetricCard icon={<Network size={18} />} label="Netzwerk RX" value={formatBytes(server.stats?.networkRxBytes ?? 0)} tone="amber" />
        <MetricCard icon={<Network size={18} />} label="Netzwerk TX" value={formatBytes(server.stats?.networkTxBytes ?? 0)} tone="slate" />
      </div>

      <div className="rounded-lg border border-slate-200 bg-white shadow-panel">
        <div className="flex flex-wrap gap-1 border-b border-slate-200 p-2">
          <TabButton active={tab === "console"} icon={<Terminal size={16} />} label="Konsole" onClick={() => setTab("console")} />
          <TabButton active={tab === "files"} icon={<Folder size={16} />} label="Dateien" onClick={() => setTab("files")} />
          <TabButton active={tab === "settings"} icon={<Settings size={16} />} label="Einstellungen" onClick={() => setTab("settings")} />
        </div>
        <div className="p-4">
          {tab === "console" && <ConsolePanel api={api} server={server} />}
          {tab === "files" && <FilesPanel api={api} server={server} />}
          {tab === "settings" && <SettingsPanel api={api} server={server} onSaved={setServer} />}
        </div>
      </div>
    </div>
  );
}

function ConsolePanel({ api, server }: { api: ApiClient; server: Server }) {
  const [lines, setLines] = useState<string[]>([]);
  const [connected, setConnected] = useState(false);
  const [command, setCommand] = useState("");
  const [sending, setSending] = useState(false);
  const bottomRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    setLines([]);
    const socket = new WebSocket(api.websocketUrl(`/api/servers/${server.id}/logs/stream`));
    socket.onopen = () => setConnected(true);
    socket.onclose = () => setConnected(false);
    socket.onerror = () => setConnected(false);
    socket.onmessage = (event) => {
      setLines((current) => [...current.slice(-800), String(event.data)]);
    };
    return () => socket.close();
  }, [api, server.id]);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ block: "end" });
  }, [lines]);

  async function sendCommand(event: FormEvent) {
    event.preventDefault();
    const next = command.trim();
    if (!next || sending) {
      return;
    }

    setSending(true);
    try {
      setLines((prev) => [...prev, `\n> ${next}\n`]);
      const result = await api.consoleCommand(server.id, next);
      if (result.output) {
        setLines((prev) => [...prev, result.output.endsWith("\n") ? result.output : `${result.output}\n`]);
      }
      setCommand("");
    } catch (err) {
      setLines((prev) => [...prev, `Fehler: ${messageOf(err)}\n`]);
    } finally {
      setSending(false);
    }
  }

  return (
    <div>
      <div className="mb-3 flex items-center justify-between">
        <div className="flex items-center gap-2 text-sm font-medium text-slate-700">
          <span className={clsx("h-2.5 w-2.5 rounded-full", connected ? "bg-emerald-500" : "bg-slate-300")} />
          WebSocket {connected ? "verbunden" : "getrennt"}
        </div>
        <Button variant="soft" onClick={() => setLines([])}><X size={16} /> Leeren</Button>
      </div>
      <pre className="console-scroll h-[520px] overflow-auto rounded-md bg-[#101826] p-4 font-mono text-xs leading-5 text-slate-100">
        {lines.length === 0 ? "Warte auf Logs...\n" : lines.join("")}
        <div ref={bottomRef} />
      </pre>
      <form className="mt-3 flex gap-2" onSubmit={sendCommand}>
        <input
          className={clsx(inputClass, "font-mono text-sm")}
          value={command}
          onChange={(event) => setCommand(event.target.value)}
          placeholder="Befehl eingeben, z.B. ls -la /data"
        />
        <Button type="submit" disabled={sending || !command.trim()}>
          <Send size={15} />
          Senden
        </Button>
      </form>
    </div>
  );
}

function FilesPanel({ api, server }: { api: ApiClient; server: Server }) {
  const [path, setPath] = useState("");
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [selected, setSelected] = useState("");
  const [content, setContent] = useState("");
  const [error, setError] = useState("");
  const [dirty, setDirty] = useState(false);

  const load = (nextPath = path) => {
    api.listFiles(server.id, nextPath).then((data) => {
      setEntries(data);
      setPath(nextPath);
    }).catch((err) => setError(messageOf(err)));
  };

  useEffect(() => {
    load("");
  }, [server.id]);

  async function openFile(entry: FileEntry) {
    if (entry.isDirectory) {
      setSelected("");
      setContent("");
      load(entry.path);
      return;
    }

    setSelected(entry.path);
    setError("");
    try {
      const file = await api.readFile(server.id, entry.path);
      setContent(file.content);
      setDirty(false);
    } catch (err) {
      setError(messageOf(err));
    }
  }

  async function save() {
    if (!selected) {
      return;
    }
    try {
      await api.writeFile(server.id, selected, content);
      setDirty(false);
    } catch (err) {
      setError(messageOf(err));
    }
  }

  async function deleteEntry(entry: FileEntry) {
    if (!window.confirm(`Wirklich löschen: ${entry.path}?`)) {
      return;
    }

    try {
      await api.deletePath(server.id, entry.path);
      if (selected === entry.path) {
        setSelected("");
        setContent("");
        setDirty(false);
      }
      await load(path);
    } catch (err) {
      setError(messageOf(err));
    }
  }

  async function renameEntry(entry: FileEntry) {
    const nextName = window.prompt("Neuer Name", entry.name);
    if (!nextName || nextName.trim() === "" || nextName === entry.name) {
      return;
    }

    const base = entry.path.split("/").slice(0, -1).join("/");
    const targetPath = base ? `${base}/${nextName.trim()}` : nextName.trim();
    try {
      await api.movePath(server.id, entry.path, targetPath);
      if (selected === entry.path) {
        setSelected(targetPath);
      }
      await load(path);
    } catch (err) {
      setError(messageOf(err));
    }
  }

  async function moveEntry(entry: FileEntry) {
    const targetPath = window.prompt("Zielpfad (inkl. neuem Namen)", entry.path);
    if (!targetPath || targetPath.trim() === "" || targetPath === entry.path) {
      return;
    }

    try {
      await api.movePath(server.id, entry.path, targetPath.trim());
      if (selected === entry.path) {
        setSelected(targetPath.trim());
      }
      await load(path);
    } catch (err) {
      setError(messageOf(err));
    }
  }

  async function createDirectory() {
    const name = window.prompt("Ordnername");
    if (!name || !name.trim()) {
      return;
    }

    const targetPath = path ? `${path}/${name.trim()}` : name.trim();
    try {
      await api.makeDirectory(server.id, targetPath);
      await load(path);
    } catch (err) {
      setError(messageOf(err));
    }
  }

  async function createFile() {
    const name = window.prompt("Dateiname (z.B. server.properties)");
    if (!name || !name.trim()) {
      return;
    }

    const targetPath = path ? `${path}/${name.trim()}` : name.trim();
    try {
      await api.createFile(server.id, targetPath);
      await load(path);
    } catch (err) {
      setError(messageOf(err));
    }
  }

  async function downloadEntry(entry: FileEntry) {
    if (entry.isDirectory) {
      return;
    }

    try {
      const { blob, fileName } = await api.downloadFile(server.id, entry.path);
      const href = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = href;
      link.download = fileName;
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      URL.revokeObjectURL(href);
    } catch (err) {
      setError(messageOf(err));
    }
  }

  const parent = path.split("/").filter(Boolean).slice(0, -1).join("/");

  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[0.8fr_1.2fr]">
      <div className="rounded-md border border-slate-200">
        <div className="flex items-center justify-between border-b border-slate-200 px-3 py-2">
          <div className="truncate text-sm font-semibold text-slate-900">/{path}</div>
          <div className="flex gap-1">
            {path && <IconButton title="Zurück" onClick={() => load(parent)}><ChevronRight className="rotate-180" size={16} /></IconButton>}
            <IconButton title="Neuer Ordner" onClick={createDirectory}><FolderPlus size={16} /></IconButton>
            <IconButton title="Neue Datei" onClick={createFile}><FilePlus2 size={16} /></IconButton>
            <IconButton title="Aktualisieren" onClick={() => load()}><RefreshCw size={16} /></IconButton>
          </div>
        </div>
        <div className="max-h-[520px] overflow-auto p-2">
          {entries.map((entry) => (
            <div key={entry.path} className={clsx("flex items-center gap-2 rounded-md px-2 py-2 text-left text-sm hover:bg-slate-50", selected === entry.path && "bg-emerald-50 text-emerald-800")}>
              <button className="flex min-w-0 flex-1 items-center gap-2 text-left" onClick={() => openFile(entry)}>
                {entry.isDirectory ? <Folder size={16} className="text-amber-500" /> : <FileCode2 size={16} className="text-slate-500" />}
                <span className="min-w-0 flex-1 truncate">{entry.name}</span>
                {!entry.isDirectory && <span className="text-xs text-slate-400">{formatBytes(entry.size)}</span>}
              </button>
              <div className="flex gap-1">
                {!entry.isDirectory && <IconButton title="Download" onClick={() => downloadEntry(entry)}><Download size={14} /></IconButton>}
                <IconButton title="Umbenennen" onClick={() => renameEntry(entry)}><Pencil size={14} /></IconButton>
                <IconButton title="Verschieben" onClick={() => moveEntry(entry)}><ChevronRight size={14} /></IconButton>
                <IconButton title="Löschen" onClick={() => deleteEntry(entry)} className="text-red-700"><Trash2 size={14} /></IconButton>
              </div>
            </div>
          ))}
        </div>
        <div className="border-t border-slate-200 p-3">
          <label className="flex cursor-pointer items-center justify-center gap-2 rounded-md border border-dashed border-slate-300 px-3 py-2 text-sm font-medium text-slate-600 hover:border-emerald-400 hover:text-emerald-700">
            <Upload size={16} /> Upload
            <input
              type="file"
              className="hidden"
              onChange={async (event) => {
                const file = event.target.files?.[0];
                if (!file) return;
                try {
                  await api.uploadFile(server.id, path, file);
                  await load(path);
                } catch (err) {
                  setError(messageOf(err));
                }
                event.target.value = "";
              }}
            />
          </label>
        </div>
      </div>
      <div className="rounded-md border border-slate-200">
        <div className="flex items-center justify-between border-b border-slate-200 px-3 py-2">
          <div className="truncate text-sm font-semibold text-slate-900">{selected || "Keine Datei ausgewählt"}</div>
          <Button variant="soft" disabled={!selected || !dirty} onClick={save}><Save size={16} /> Speichern</Button>
        </div>
        {error && <div className="m-3"><ErrorBox message={error} /></div>}
        <textarea
          className="h-[558px] w-full resize-none rounded-b-md border-0 bg-slate-950 p-4 font-mono text-xs leading-5 text-slate-100 outline-none"
          value={content}
          onChange={(event) => {
            setContent(event.target.value);
            setDirty(true);
          }}
          placeholder="Textdatei öffnen"
        />
      </div>
    </div>
  );
}

function SettingsPanel({ api, server, onSaved }: { api: ApiClient; server: Server; onSaved: (server: Server) => void }) {
  const [name, setName] = useState(server.name);
  const [description, setDescription] = useState(server.description);
  const [image, setImage] = useState(server.image);
  const [startCommand, setStartCommand] = useState(server.startCommand ?? "");
  const [memory, setMemory] = useState(server.memoryMb);
  const [cpu, setCpu] = useState(server.cpuLimit);
  const [autoStart, setAutoStart] = useState(server.autoStart);
  const [recreate, setRecreate] = useState(false);
  const [ports, setPorts] = useState(server.ports);
  const [envRows, setEnvRows] = useState<KeyValueRow[]>(envToRows(server.env));
  const [error, setError] = useState("");
  const [saved, setSaved] = useState(false);

  async function save() {
    setError("");
    setSaved(false);
    try {
      const updated = await api.updateServer(server.id, {
        name,
        description,
        image,
        startCommand,
        memoryMb: memory,
        cpuLimit: cpu,
        autoStart,
        ports,
        env: rowsToEnv(envRows),
        recreateContainer: recreate
      });
      onSaved(updated);
      setSaved(true);
      setRecreate(false);
    } catch (err) {
      setError(messageOf(err));
    }
  }

  return (
    <div className="grid grid-cols-1 gap-5 xl:grid-cols-[1fr_1fr]">
      <div className="space-y-5">
        <Panel title="Basis" icon={<Settings size={18} />}>
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            <Field label="Name"><input className={inputClass} value={name} onChange={(event) => setName(event.target.value)} /></Field>
            <Field label="Image"><input className={inputClass} value={image} onChange={(event) => setImage(event.target.value)} /></Field>
            <Field label="RAM MB"><input className={inputClass} type="number" value={memory} onChange={(event) => setMemory(Number(event.target.value))} /></Field>
            <Field label="CPU Limit"><input className={inputClass} type="number" min={0} step={0.25} value={cpu} onChange={(event) => setCpu(Number(event.target.value))} /></Field>
          </div>
          <Field label="Beschreibung"><textarea className={clsx(inputClass, "mt-4 h-20 py-2")} value={description} onChange={(event) => setDescription(event.target.value)} /></Field>
          <div className="mt-4 grid gap-2 sm:grid-cols-2">
            <Check label="Autostart beim Hochfahren" checked={autoStart} onChange={setAutoStart} />
            <Check label="Container nach Speichern neu erstellen" checked={recreate} onChange={setRecreate} />
          </div>
        </Panel>

        <Panel title="Start Command Configurator" icon={<Wrench size={18} />}>
          <StartCommandConfigurator value={startCommand} onChange={setStartCommand} memoryMb={memory} />
        </Panel>
      </div>

      <div className="space-y-5">
        <Panel title="Ports" icon={<Network size={18} />}>
          <PortEditor ports={ports} setPorts={setPorts} />
        </Panel>
        <Panel title="Env Vars" icon={<FileCode2 size={18} />}>
          <KeyValueEditor rows={envRows} setRows={setEnvRows} />
        </Panel>
        {error && <ErrorBox message={error} />}
        {saved && <div className="rounded-md border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-800">Gespeichert</div>}
        <div className="flex justify-end">
          <Button onClick={save}><Save size={16} /> Speichern</Button>
        </div>
      </div>
    </div>
  );
}

function StartCommandConfigurator({ value, onChange, memoryMb }: { value: string; onChange: (value: string) => void; memoryMb: number }) {
  const [jar, setJar] = useState("server.jar");
  const [minRam, setMinRam] = useState(Math.max(512, Math.floor(memoryMb / 2)));
  const [maxRam, setMaxRam] = useState(memoryMb || 2048);
  const [flags, setFlags] = useState("nogui");

  function apply() {
    const command = `java -Xms${minRam}M -Xmx${maxRam}M -jar ${jar} ${flags}`.trim();
    onChange(command);
  }

  return (
    <div className="space-y-3">
      <textarea className="h-24 w-full rounded-md border border-slate-300 px-3 py-2 font-mono text-xs outline-none focus:border-emerald-500 focus:ring-4 focus:ring-emerald-100" value={value} onChange={(event) => onChange(event.target.value)} placeholder="Optionaler Docker CMD / Startbefehl" />
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-4">
        <Field label="Jar"><input className={inputClass} value={jar} onChange={(event) => setJar(event.target.value)} /></Field>
        <Field label="Xms MB"><input className={inputClass} type="number" value={minRam} onChange={(event) => setMinRam(Number(event.target.value))} /></Field>
        <Field label="Xmx MB"><input className={inputClass} type="number" value={maxRam} onChange={(event) => setMaxRam(Number(event.target.value))} /></Field>
        <Field label="Args"><input className={inputClass} value={flags} onChange={(event) => setFlags(event.target.value)} /></Field>
      </div>
      <Button type="button" variant="soft" onClick={apply}><Wrench size={16} /> Java Command einsetzen</Button>
    </div>
  );
}

function AdminView({ api }: { api: ApiClient }) {
  const [docker, setDocker] = useState<DockerStatus | null>(null);
  const [images, setImages] = useState<unknown[]>([]);
  const [volumes, setVolumes] = useState<unknown[]>([]);
  const [networks, setNetworks] = useState<unknown[]>([]);
  const [users, setUsers] = useState<UserSummary[]>([]);
  const [keys, setKeys] = useState<ApiKeySummary[]>([]);
  const [newKey, setNewKey] = useState("");
  const [pullImage, setPullImage] = useState("");
  const [error, setError] = useState("");

  const load = () => {
    Promise.all([
      api.dockerStatus().then(setDocker),
      api.dockerImages().then(setImages),
      api.dockerVolumes().then(setVolumes),
      api.dockerNetworks().then(setNetworks),
      api.users().then(setUsers),
      api.apiKeys().then(setKeys)
    ]).catch((err) => setError(messageOf(err)));
  };

  useEffect(load, []);

  return (
    <div className="space-y-5">
      <PageHeader title="Admin" action={<Button variant="soft" onClick={load}><RefreshCw size={16} /> Aktualisieren</Button>} />
      {error && <ErrorBox message={error} />}
      <div className="grid grid-cols-1 gap-5 xl:grid-cols-2">
        <DockerCard docker={docker} />
        <Panel title="Image Pull" icon={<Boxes size={18} />}>
          <div className="flex gap-2">
            <input className={inputClass} placeholder="itzg/minecraft-server:java21" value={pullImage} onChange={(event) => setPullImage(event.target.value)} />
            <Button onClick={async () => {
              await api.pullImage(pullImage);
              setPullImage("");
              load();
            }}>Pull</Button>
          </div>
        </Panel>
        <Panel title="Benutzer" icon={<Users size={18} />}>
          <div className="divide-y divide-slate-100">
            {users.map((user) => <div key={user.id} className="flex items-center justify-between py-2 text-sm"><span>{user.username}</span><span className="rounded bg-slate-100 px-2 py-1 text-xs">{user.role}</span></div>)}
          </div>
        </Panel>
        <Panel title="API Keys" icon={<KeyRound size={18} />}>
          <div className="mb-3 flex gap-2">
            <input className={inputClass} placeholder="Key Name" value={newKey} onChange={(event) => setNewKey(event.target.value)} />
            <Button onClick={async () => {
              const created = await api.createApiKey(newKey || "API Key");
              setNewKey(created.key);
              await api.apiKeys().then(setKeys);
            }}>Erstellen</Button>
          </div>
          <div className="divide-y divide-slate-100">
            {keys.map((key) => <div key={key.id} className="flex items-center justify-between py-2 text-sm"><span>{key.name}</span><span className="font-mono text-xs text-slate-500">{key.prefix}</span></div>)}
          </div>
        </Panel>
        <JsonPanel title={`Images (${images.length})`} data={images} />
        <JsonPanel title={`Volumes (${volumes.length})`} data={volumes} />
        <JsonPanel title={`Networks (${networks.length})`} data={networks} />
      </div>
    </div>
  );
}

function DockerCard({ docker }: { docker?: DockerStatus | null }) {
  return (
    <Panel title="Docker" icon={<PlugZap size={18} />}>
      <div className="grid grid-cols-2 gap-3">
        <Info label="Status" value={docker?.available ? "online" : "offline"} />
        <Info label="Version" value={docker?.version ?? "-"} />
        <Info label="OS" value={docker?.osType ?? "-"} />
        <Info label="Arch" value={docker?.architecture ?? "-"} />
        <Info label="CPU" value={docker?.cpuCount ? String(docker.cpuCount) : "-"} />
        <Info label="RAM" value={docker?.memoryBytes ? formatBytes(docker.memoryBytes) : "-"} />
      </div>
      {docker?.error && <div className="mt-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{docker.error}</div>}
    </Panel>
  );
}

function PortEditor({ ports, setPorts }: { ports: PortDto[]; setPorts: (ports: PortDto[]) => void }) {
  return (
    <div className="space-y-2">
      {ports.map((port, index) => (
        <div key={index} className="grid grid-cols-[1fr_1fr_0.8fr_auto] gap-2">
          <input className={inputClass} type="number" value={port.host} onChange={(event) => replaceAt(ports, setPorts, index, { ...port, host: Number(event.target.value) })} />
          <input className={inputClass} type="number" value={port.container} onChange={(event) => replaceAt(ports, setPorts, index, { ...port, container: Number(event.target.value) })} />
          <select className={inputClass} value={port.protocol} onChange={(event) => replaceAt(ports, setPorts, index, { ...port, protocol: event.target.value as "tcp" | "udp" })}>
            <option value="tcp">tcp</option>
            <option value="udp">udp</option>
          </select>
          <IconButton title="Port löschen" onClick={() => setPorts(ports.filter((_, itemIndex) => itemIndex !== index))}><Trash2 size={16} /></IconButton>
        </div>
      ))}
      <Button type="button" variant="soft" onClick={() => setPorts([...ports, { host: 25565, container: 25565, protocol: "tcp" }])}><Plus size={16} /> Port</Button>
    </div>
  );
}

type KeyValueRow = { key: string; value: string };

function KeyValueEditor({ rows, setRows }: { rows: KeyValueRow[]; setRows: (rows: KeyValueRow[]) => void }) {
  return (
    <div className="space-y-2">
      {rows.map((row, index) => (
        <div key={index} className="grid grid-cols-[0.8fr_1fr_auto] gap-2">
          <input className={inputClass} value={row.key} onChange={(event) => replaceAt(rows, setRows, index, { ...row, key: event.target.value })} />
          <input className={inputClass} value={row.value} onChange={(event) => replaceAt(rows, setRows, index, { ...row, value: event.target.value })} />
          <IconButton title="Variable löschen" onClick={() => setRows(rows.filter((_, itemIndex) => itemIndex !== index))}><Trash2 size={16} /></IconButton>
        </div>
      ))}
      <Button type="button" variant="soft" onClick={() => setRows([...rows, { key: "", value: "" }])}><Plus size={16} /> Variable</Button>
    </div>
  );
}

function ServerActionBar({ disabled, onStart, onStop, onRestart, onOpen }: { disabled?: boolean; onStart: () => void; onStop: () => void; onRestart: () => void; onOpen: () => void }) {
  return (
    <div className="flex flex-wrap gap-2">
      <IconButton title="Start" disabled={disabled} onClick={onStart}><Play size={16} /></IconButton>
      <IconButton title="Stop" disabled={disabled} onClick={onStop}><Square size={16} /></IconButton>
      <IconButton title="Restart" disabled={disabled} onClick={onRestart}><RefreshCw size={16} /></IconButton>
      <IconButton title="Konsole" disabled={disabled} onClick={onOpen}><Terminal size={16} /></IconButton>
    </div>
  );
}

function Panel({ title, icon, action, children }: { title: string; icon: ReactNode; action?: ReactNode; children: ReactNode }) {
  return (
    <section className="rounded-lg border border-slate-200 bg-white shadow-panel">
      <div className="flex items-center justify-between gap-3 border-b border-slate-200 px-4 py-3">
        <div className="flex items-center gap-2 text-sm font-semibold text-slate-950">{icon}{title}</div>
        {action}
      </div>
      <div className="p-4">{children}</div>
    </section>
  );
}

function PageHeader({ title, action }: { title: string; action?: ReactNode }) {
  return (
    <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
      <h1 className="text-2xl font-semibold text-slate-950">{title}</h1>
      {action}
    </div>
  );
}

function MetricCard({ icon, label, value, sub, tone }: { icon: ReactNode; label: string; value: ReactNode; sub?: string; tone: "slate" | "emerald" | "amber" | "cyan" | "red" }) {
  const toneClass = {
    slate: "bg-slate-100 text-slate-700",
    emerald: "bg-emerald-100 text-emerald-700",
    amber: "bg-amber-100 text-amber-700",
    cyan: "bg-cyan-100 text-cyan-700",
    red: "bg-red-100 text-red-700"
  }[tone];
  return (
    <div className="rounded-lg border border-slate-200 bg-white p-4 shadow-panel">
      <div className="flex items-center justify-between">
        <div className={clsx("grid h-9 w-9 place-items-center rounded-md", toneClass)}>{icon}</div>
      </div>
      <div className="mt-4 text-2xl font-semibold text-slate-950">{value}</div>
      <div className="text-sm text-slate-500">{label}</div>
      {sub && <div className="mt-1 text-xs text-slate-400">{sub}</div>}
    </div>
  );
}

function Button({ children, variant = "solid", className, ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: "solid" | "soft" }) {
  return (
    <button
      className={clsx(
        "inline-flex h-9 items-center justify-center gap-2 rounded-md px-3 text-sm font-semibold transition",
        variant === "solid" ? "bg-emerald-500 text-slate-950 hover:bg-emerald-400" : "border border-slate-200 bg-white text-slate-700 hover:bg-slate-50",
        className
      )}
      {...props}
    >
      {children}
    </button>
  );
}

function IconButton({ title, children, className, ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { title: string }) {
  return (
    <button
      title={title}
      aria-label={title}
      className={clsx("grid h-9 w-9 place-items-center rounded-md border border-slate-200 bg-white text-slate-700 hover:bg-slate-50", className)}
      {...props}
    >
      {children}
    </button>
  );
}

function NavButton({ icon, label, active, onClick }: { icon: ReactNode; label: string; active: boolean; onClick: () => void }) {
  return (
    <button className={clsx("flex w-full items-center gap-3 rounded-md px-3 py-2 text-sm font-medium", active ? "bg-slate-950 text-white" : "text-slate-600 hover:bg-slate-50 hover:text-slate-950")} onClick={onClick}>
      {icon} {label}
    </button>
  );
}

function TabButton({ active, icon, label, onClick }: { active: boolean; icon: ReactNode; label: string; onClick: () => void }) {
  return (
    <button className={clsx("flex items-center gap-2 rounded-md px-3 py-2 text-sm font-semibold", active ? "bg-slate-950 text-white" : "text-slate-600 hover:bg-slate-50")} onClick={onClick}>
      {icon} {label}
    </button>
  );
}

function StatusBadge({ status }: { status: string }) {
  const color = status === "running" ? "bg-emerald-100 text-emerald-700" : status === "stopped" ? "bg-amber-100 text-amber-700" : status === "crashed" ? "bg-red-100 text-red-700" : "bg-slate-100 text-slate-700";
  return <span className={clsx("inline-flex w-fit rounded px-2 py-1 text-xs font-semibold", color)}>{status}</span>;
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-semibold uppercase text-slate-500">{label}</span>
      {children}
    </label>
  );
}

function Check({ label, checked, onChange }: { label: string; checked: boolean; onChange: (checked: boolean) => void }) {
  return (
    <label className="flex h-10 items-center gap-2 rounded-md border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700">
      <input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} className="h-4 w-4 accent-emerald-500" />
      {label}
    </label>
  );
}

function Info({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="rounded-md bg-slate-50 p-3">
      <div className="text-xs font-medium uppercase text-slate-500">{label}</div>
      <div className="mt-1 truncate text-sm font-semibold text-slate-900">{value}</div>
    </div>
  );
}

function EmptyState({ icon, text }: { icon: ReactNode; text: string }) {
  return (
    <div className="grid place-items-center py-12 text-center text-slate-500">
      <div className="mb-2 grid h-10 w-10 place-items-center rounded-md bg-slate-100 text-slate-500">{icon}</div>
      <div className="text-sm">{text}</div>
    </div>
  );
}

function ErrorBox({ message }: { message: string }) {
  return <div className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{message}</div>;
}

function SplashPanel() {
  return <div className="rounded-lg border border-slate-200 bg-white p-8 text-sm text-slate-500 shadow-panel">Lade...</div>;
}

function MiniStat({ icon, label, value }: { icon: ReactNode; label: string; value: string }) {
  return (
    <div className="rounded-md border border-white/10 bg-white/5 p-3">
      <div className="mb-3 text-emerald-300">{icon}</div>
      <div className="text-xs text-slate-400">{label}</div>
      <div className="text-sm font-semibold">{value}</div>
    </div>
  );
}

function JsonPanel({ title, data }: { title: string; data: unknown }) {
  return (
    <Panel title={title} icon={<FileCode2 size={18} />}>
      <pre className="max-h-80 overflow-auto rounded-md bg-slate-950 p-3 font-mono text-xs leading-5 text-slate-100">{JSON.stringify(data, null, 2)}</pre>
    </Panel>
  );
}

function titleForView(view: View) {
  if (view.page === "dashboard") return "Dashboard";
  if (view.page === "servers") return "Server";
  if (view.page === "create") return "Server erstellen";
  if (view.page === "admin") return "Admin";
  return "Server Details";
}

function envToRows(env: Record<string, string>) {
  return Object.entries(env).map(([key, value]) => ({ key, value }));
}

function rowsToEnv(rows: KeyValueRow[]) {
  return rows.reduce<Record<string, string>>((acc, row) => {
    if (row.key.trim()) {
      acc[row.key.trim()] = row.value;
    }
    return acc;
  }, {});
}

function replaceAt<T>(items: T[], setItems: (items: T[]) => void, index: number, value: T) {
  setItems(items.map((item, itemIndex) => (itemIndex === index ? value : item)));
}

function formatBytes(value: number) {
  if (!value) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  return `${(value / 1024 ** index).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function messageOf(error: unknown) {
  if (error instanceof ApiError) return error.message;
  if (error instanceof Error) return error.message;
  return "Unbekannter Fehler";
}

const inputClass = "h-10 w-full rounded-md border border-slate-300 bg-white px-3 text-sm text-slate-900 outline-none focus:border-emerald-500 focus:ring-4 focus:ring-emerald-100";
