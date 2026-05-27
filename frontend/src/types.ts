export type Role = "Admin" | "User";

export type UserSummary = {
  id: number;
  username: string;
  role: Role;
  createdAt?: string;
};

export type LoginResponse = {
  token: string;
  expiresAt: string;
  user: UserSummary;
};

export type DockerStatus = {
  available: boolean;
  version?: string;
  osType?: string;
  architecture?: string;
  cpuCount: number;
  memoryBytes: number;
  error?: string;
};

export type PortDto = {
  container: number;
  host: number;
  protocol: "tcp" | "udp";
};

export type VolumeDto = {
  hostPath: string;
  containerPath: string;
  readOnly: boolean;
};

export type TemplateVolume = {
  hostPath?: string;
  containerPath: string;
  readOnly: boolean;
};

export type GameTemplate = {
  id: string;
  name: string;
  category: string;
  image: string;
  description: string;
  startCommand?: string;
  env: Record<string, string>;
  ports: PortDto[];
  volumes: TemplateVolume[];
  recommendedMemoryMb: number;
};

export type ResourceSnapshot = {
  cpuPercent: number;
  memoryBytes: number;
  memoryLimitBytes: number;
  networkRxBytes: number;
  networkTxBytes: number;
};

export type Server = {
  id: string;
  name: string;
  description: string;
  templateId: string;
  game: string;
  image: string;
  startCommand: string;
  status: string;
  dockerContainerId?: string;
  memoryMb: number;
  cpuLimit: number;
  autoStart: boolean;
  createdAt: string;
  updatedAt: string;
  ports: PortDto[];
  env: Record<string, string>;
  volumes: VolumeDto[];
  stats?: ResourceSnapshot;
};

export type Dashboard = {
  totalServers: number;
  runningServers: number;
  stoppedServers: number;
  running: Server[];
  docker: DockerStatus;
};

export type FileEntry = {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number;
  modifiedAt?: string;
};

export type FileContent = {
  path: string;
  content: string;
  modifiedAt: string;
};

export type ApiKeySummary = {
  id: number;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt?: string;
  expiresAt?: string;
};
