import type {
  ApiKeySummary,
  Dashboard,
  DockerStatus,
  FileContent,
  FileEntry,
  GameTemplate,
  LoginResponse,
  PortDto,
  Server,
  UserSummary
} from "./types";

const API_BASE = import.meta.env.VITE_API_BASE ?? "";

export class ApiError extends Error {
  constructor(
    message: string,
    public status: number
  ) {
    super(message);
  }
}

export class ApiClient {
  constructor(private token: string | null) {}

  setToken(token: string | null) {
    this.token = token;
  }

  websocketUrl(path: string) {
    const base = API_BASE || window.location.origin;
    const url = new URL(path, base);
    url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
    if (this.token) {
      url.searchParams.set("access_token", this.token);
    }
    return url.toString();
  }

  async getBootstrapRequired() {
    return this.request<{ required: boolean }>("/api/auth/bootstrap-required", { auth: false });
  }

  async bootstrap(username: string, password: string) {
    return this.request<LoginResponse>("/api/auth/bootstrap", {
      method: "POST",
      auth: false,
      body: { username, password }
    });
  }

  async login(username: string, password: string) {
    return this.request<LoginResponse>("/api/auth/login", {
      method: "POST",
      auth: false,
      body: { username, password }
    });
  }

  me() {
    return this.request<UserSummary>("/api/auth/me");
  }

  dashboard() {
    return this.request<Dashboard>("/api/dashboard");
  }

  templates() {
    return this.request<GameTemplate[]>("/api/templates");
  }

  servers() {
    return this.request<Server[]>("/api/servers");
  }

  server(id: string) {
    return this.request<Server>(`/api/servers/${id}`);
  }

  createServer(payload: unknown) {
    return this.request<Server>("/api/servers", { method: "POST", body: payload });
  }

  updateServer(id: string, payload: Partial<Server> & { ports?: PortDto[]; recreateContainer?: boolean }) {
    return this.request<Server>(`/api/servers/${id}`, { method: "PUT", body: payload });
  }

  command(id: string, action: "start" | "stop" | "restart" | "kill") {
    return this.request<Server>(`/api/servers/${id}/${action}`, { method: "POST" });
  }

  deleteServer(id: string, deleteFiles: boolean) {
    return this.request<void>(`/api/servers/${id}?deleteFiles=${deleteFiles ? "true" : "false"}`, { method: "DELETE" });
  }

  stats(id: string) {
    return this.request(`/api/servers/${id}/stats`);
  }

  listFiles(id: string, path = "") {
    return this.request<FileEntry[]>(`/api/servers/${id}/files?path=${encodeURIComponent(path)}`);
  }

  readFile(id: string, path: string) {
    return this.request<FileContent>(`/api/servers/${id}/files/content?path=${encodeURIComponent(path)}`);
  }

  writeFile(id: string, path: string, content: string) {
    return this.request<void>(`/api/servers/${id}/files/content?path=${encodeURIComponent(path)}`, {
      method: "PUT",
      body: { content }
    });
  }

  uploadFile(id: string, path: string, file: File) {
    const body = new FormData();
    body.append("file", file);
    return this.request(`/api/servers/${id}/files/upload?path=${encodeURIComponent(path)}`, {
      method: "POST",
      form: body
    });
  }

  makeDirectory(id: string, path: string) {
    return this.request<void>(`/api/servers/${id}/files/mkdir`, { method: "POST", body: { path } });
  }

  movePath(id: string, sourcePath: string, targetPath: string) {
    return this.request<void>(`/api/servers/${id}/files/move`, { method: "POST", body: { sourcePath, targetPath } });
  }

  deletePath(id: string, path: string) {
    return this.request<void>(`/api/servers/${id}/files?path=${encodeURIComponent(path)}`, { method: "DELETE" });
  }

  dockerStatus() {
    return this.request<DockerStatus>("/api/docker/status");
  }

  dockerImages() {
    return this.request<unknown[]>("/api/docker/images");
  }

  dockerVolumes() {
    return this.request<unknown[]>("/api/docker/volumes");
  }

  dockerNetworks() {
    return this.request<unknown[]>("/api/docker/networks");
  }

  pullImage(image: string) {
    return this.request<void>("/api/docker/images/pull", { method: "POST", body: { image } });
  }

  users() {
    return this.request<UserSummary[]>("/api/users");
  }

  createUser(username: string, password: string, role: string) {
    return this.request<UserSummary>("/api/users", { method: "POST", body: { username, password, role } });
  }

  deleteUser(id: number) {
    return this.request<void>(`/api/users/${id}`, { method: "DELETE" });
  }

  apiKeys() {
    return this.request<ApiKeySummary[]>("/api/api-keys");
  }

  createApiKey(name: string) {
    return this.request<{ key: string; summary: ApiKeySummary }>("/api/api-keys", { method: "POST", body: { name } });
  }

  deleteApiKey(id: number) {
    return this.request<void>(`/api/api-keys/${id}`, { method: "DELETE" });
  }

  private async request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    const headers = new Headers(options.headers);
    if (options.auth !== false && this.token) {
      headers.set("Authorization", `Bearer ${this.token}`);
    }

    let body: BodyInit | undefined;
    if (options.form) {
      body = options.form;
    } else if (options.body !== undefined) {
      headers.set("Content-Type", "application/json");
      body = JSON.stringify(options.body);
    }

    const response = await fetch(`${API_BASE}${path}`, {
      method: options.method ?? "GET",
      headers,
      body
    });

    if (!response.ok) {
      let message = response.statusText;
      try {
        const data = await response.json();
        message = data.message ?? message;
      } catch {
        // Keep status text.
      }
      throw new ApiError(message, response.status);
    }

    if (response.status === 204) {
      return undefined as T;
    }

    return (await response.json()) as T;
  }
}

type RequestOptions = {
  method?: string;
  headers?: HeadersInit;
  body?: unknown;
  form?: FormData;
  auth?: boolean;
};
