export type ContainerStatus = 'online' | 'offline' | 'stopped';

export interface FleetUpdate {
  timestamp: string;
  ageMilliseconds: number;
  hosts: HostSummary[];
  details: Record<string, HostSample>;
}

export interface HostSummary {
  hostId: string;
  displayName: string;
  online: boolean;
  lastSeen: string;
  leaderContainerId: string | null;
  sampledAt: string | null;
  kernel: string | null;
  uptimeSeconds: number | null;
  cpuPercent: number | null;
  logicalCores: number | null;
  memoryUsedBytes: number | null;
  memoryTotalBytes: number | null;
  memoryPercent: number | null;
  gpus: GpuSummary[];
  diskReadBytesPerSecond: number | null;
  diskWriteBytesPerSecond: number | null;
  netReceiveBytesPerSecond: number | null;
  netTransmitBytesPerSecond: number | null;
  containerTotals: ContainerTotals | null;
  volumes: VolumeSummary[];
  containers: ContainerState[];
}

export interface VolumeSummary {
  name: string;
  path: string;
  usedBytes: number;
  totalBytes: number;
  usagePercent: number;
}

export interface GpuSummary {
  index: number;
  name: string;
  utilizationPercent: number | null;
  memoryUsagePercent: number | null;
  memoryUsedBytes: number | null;
  memoryTotalBytes: number | null;
  temperatureCelsius: number | null;
}

export interface ContainerTotals {
  count: number;
  cpuPercentOfHost: number;
  cpuCores: number;
  memoryBytes: number;
}

export interface ContainerState {
  containerId: string;
  status: ContainerStatus;
  isLeader: boolean;
  startedAt: string;
  lastSeen: string;
  sampledAt: string | null;
  usage: ContainerUsage | null;
  name: string | null;
  shortName?: string;
  group?: string | null;
  restarts: number;
  exitCode: number | null;
  exitSignal: number | null;
  stopSeconds: number | null;
}

export interface ContainerUsage {
  cpuPercentOfHost: number;
  cpuCores: number;
  cpuLimitCores: number | null;
  memoryBytes: number;
  memoryLimitBytes: number | null;
  ioReadBytesPerSecond: number;
  ioWriteBytesPerSecond: number;
  netReceiveBytesPerSecond: number;
  netTransmitBytesPerSecond: number;
  gpuPercent: number | null;
  gpuMemoryBytes: number | null;
  diskUsedBytes: number | null;
  detail: ContainerDetail | null;
}

export interface Pressure { cpuSome: number; cpuFull: number | null; memorySome: number; memoryFull: number; ioSome: number; ioFull: number }

export interface ContainerDetail {
  pressure: Pressure | null;
  throttling: { throttledPercent: number; throttledMillisecondsPerSecond: number; throttledPeriodsTotal: number } | null;
  cpuset: string | null;
  tasks: number | null;
  tasksLimit: number | null;
  memory: {
    anon: number; file: number; kernel: number; shmem: number; sock: number; peakBytes: number | null;
    majorFaultsPerSecond: number | null; refaultsPerSecond: number | null; swapBytes: number | null;
    currentBytes: number | null; inactiveFileBytes: number | null; dirtyBytes: number | null; writebackBytes: number | null;
    reclaimScannedBytesPerSecond: number | null; reclaimedBytesPerSecond: number | null;
  } | null;
  memoryEvents: { high: number; max: number; oom: number; oomKill: number } | null;
  ioDevices: { device: string; readBytesPerSecond: number; writeBytesPerSecond: number; readOpsPerSecond: number; writeOpsPerSecond: number }[];
  gpus: {
    pciBusId: string; percent: number; memoryBytes: number; hostToDeviceBytesPerSecond: number; deviceToHostBytesPerSecond: number;
    launchesPerSecond: number | null; averageKernelMicroseconds: number | null;
  }[];
  gpuMemoryPeakBytes: number | null;
  interfaces: NetworkInterface[] | null;
  traffic: {
    internetOutBytesPerSecond: number; internetInBytesPerSecond: number; internalOutBytesPerSecond: number; internalInBytesPerSecond: number;
    networkOutBytesPerSecond: number | null; networkInBytesPerSecond: number | null;
  } | null;
  tcp: TcpStats | null;
  largestPaths: { path: string; bytes: number; files: number }[] | null;
  fileCount: number | null;
  tasksStartedPerSecond: number | null;
  gpuKernels: GpuKernel[] | null;
  gpuAllocationFailures: number | null;
  mounts: MountUsage[] | null;
  diskGrowthBytesPerHour: number | null;
  diskFreeBytes: number | null;
  openFiles: number | null;
  openFilesUncounted: number | null;
  udp: UdpStats | null;
  conntrack: Conntrack | null;
  dns: { lookupsPerSecond: number; failuresPerSecond: number; averageReplyMs: number | null } | null;
  deletedFiles: { pid: number; process: string; path: string; bytes: number }[] | null;
  growingPaths: { path: string; bytes: number; files: number }[] | null;
  largestFiles: { path: string; bytes: number }[] | null;
  logs: { linesPerSecond: number; errorsPerSecond: number; warningsPerSecond: number; bytesPerSecond: number } | null;
  recentLogs: LogLine[] | null;
}

export interface LogLine { time: string; stream: 'stdout' | 'stderr'; level: 'error' | 'warning' | 'info'; text: string }

export interface HostEvent { time: string; containerId: string | null; kind: string; level: 'error' | 'warning' | 'info'; message: string }

export interface CaptureInfo { id: number; requestedAt: string; seconds: number; status: 'requested' | 'running' | 'done' | 'failed'; finishedAt: string | null; packets: number | null; bytes: number | null }

export interface UdpStats { receiveErrorsPerSecond: number; bufferErrorsPerSecond: number; noPortPerSecond: number }
export interface Conntrack { entries: number; max: number | null }
export interface GpuKernel { name: string; busyPercent: number; launchesPerSecond: number; averageMicroseconds: number }
export interface MountUsage {
  mountPoint: string; source: string; fileSystem: string; readOnly: boolean;
  totalBytes: number | null; freeBytes: number | null; inodesTotal: number | null; inodesFree: number | null; usedBytes: number | null;
}
export interface TemperatureSensor { source: string; label: string; celsius: number; highCelsius: number | null; criticalCelsius: number | null }

export interface TcpStats {
  retransmitsPerSecond: number;
  resetsSentPerSecond: number;
  failedConnectsPerSecond: number;
  listenDropsPerSecond: number;
  activeOpensPerSecond: number;
  passiveOpensPerSecond: number;
  established: number;
}

export interface ProcessUsage {
  pid: number;
  name: string;
  commandLine: string | null;
  user: string | null;
  cpuCores: number;
  memoryBytes: number;
  threads: number;
  gpuPercent: number | null;
  gpuMemoryBytes: number | null;
  gpuMemoryPeakBytes: number | null;
  state: string | null;
  waitMillisecondsPerSecond: number | null;
  voluntarySwitchesPerSecond: number | null;
  involuntarySwitchesPerSecond: number | null;
  pssBytes: number | null;
  oomScore: number | null;
  readBytesPerSecond: number | null;
  writeBytesPerSecond: number | null;
  openFiles: number | null;
  openFilesLimit: number | null;
  startedAt: string | null;
  parentPid: number | null;
  threadName: string | null;
  anonBytes: number | null;
  fileBytes: number | null;
  shmemBytes: number | null;
  swapBytes: number | null;
  sounding: boolean | null;
  ports: number[] | null;
  connections: number | null;
  hotThreads: { tid: number; name: string; cpuCores: number }[] | null;
  capabilities: string[] | null;
  unconfined: boolean | null;
}

export interface ContainerProcesses { containerId: string; processes: ProcessUsage[] }

export interface ProcessWindow {
  pid: number;
  name: string;
  commandLine: string | null;
  user: string | null;
  startedAt: string | null;
  firstSeen: string;
  lastSeen: string;
  running: boolean;
  averageCores: number | null;
  peakCores: number;
  peakMemoryBytes: number;
  memoryBytes: number;
  averageGpuPercent: number | null;
  peakGpuMemoryBytes: number | null;
  readBytes: number | null;
  writeBytes: number | null;
  threads: number;
  parentPid: number | null;
  memoryGrowthBytesPerHour: number | null;
}

export interface ContainerProcessWindow { containerId: string; processes: ProcessWindow[] }

export interface NetworkActivity {
  source: 'packets' | 'sockets';
  captureError: string | null;
  connections: Connection[];
  connectionsTotal: number;
  listening: {
    protocol: string; address: string; port: number; new: boolean; pid: number | null; process: string | null; user: string | null; acceptQueue: number | null;
    clients: number | null; internetClients: number | null; hostClients: number | null; hostPorts: number[] | null;
  }[];
  lookups: { name: string; result: string; answers: string[]; firstSeenSecondsAgo: number; lastSeenSecondsAgo: number; latencyMs: number | null; aliases: string[] | null }[];
  socketStates: Record<string, number> | null;
  listenBacklogLimit: number | null;
  addresses: string[] | null;
  requests: RequestStats[] | null;
  ephemeral: { usedPercent: number; inUse: number; range: number; destination: string } | null;
  networks: string[] | null;
  untracked?: { outBytesPerSecond: number; inBytesPerSecond: number; outBytes: number; inBytes: number } | null;
  peers?: Peer[] | null;
}

// Who a container talked to over the retention period, per peer, service port and direction.
export interface Peer {
  protocol: string;
  address: string;
  name: string | null;
  port: number;
  inbound: boolean;
  sure: boolean;
  zone: Connection['zone'];
  internet: boolean;
  bytes: number;
  bytesPerSecond: number;
  firstSeenSecondsAgo: number;
  lastSeenSecondsAgo: number;
}

export interface RequestStats {
  protocol: string;
  role: 'client' | 'server';
  peer: string;
  peerName: string | null;
  port: number;
  operation: string;
  requestsPerSecond: number;
  errorsPerSecond: number;
  p50Ms: number | null;
  p95Ms: number | null;
  requestBytes: number | null;
  responseBytes: number | null;
  requestsPerConnection: number | null;
  process: string | null;
}

export interface Connection {
  protocol: string;
  remoteAddress: string;
  remotePort: number;
  remoteName: string | null;
  localPort: number;
  internet: boolean;
  newDestination: boolean;
  bytesOut: number | null;
  bytesIn: number | null;
  outBytesPerSecond: number | null;
  inBytesPerSecond: number | null;
  firstSeenSecondsAgo: number;
  lastSeenSecondsAgo: number;
  state: string | null;
  pid: number | null;
  process: string | null;
  user: string | null;
  handshakeMs: number | null;
  retransmitsOut: number | null;
  retransmitsIn: number | null;
  tlsName: string | null;
  httpRequest: string | null;
  httpStatus: number | null;
  httpResponseMs: number | null;
  tlsVersion: string | null;
  alpn: string | null;
  alpnOffered: string | null;
  sendQueueBytes: number | null;
  receiveQueueBytes: number | null;
  zone: 'network' | 'host' | 'private' | 'internet' | null;
  rttMs: number | null;
  deliveryBytesPerSecond: number | null;
  limitedBy: 'receiver' | 'send buffer' | 'application' | 'network' | null;
  problems: string[] | null;
}

export interface ContainerNetwork { containerId: string; network: NetworkActivity; hostId: string | null }

export interface SeriesHistory { bucketSeconds: number; t: number[]; columns: Record<string, Column> }

export interface HostSample {
  timestamp: string;
  reportedBy: string;
  snapshot: HostSnapshot;
}

export interface HostSnapshot {
  system: { hostname: string; kernel: string | null; uptimeSeconds: number | null; loadAverage: number[] | null };
  cpu: {
    model: string | null; logicalCores: number; usagePercent: number; coreUsagePercent: number[];
    split: { user: number; nice: number; system: number; ioWait: number; irq: number; softIrq: number; steal: number } | null;
    activity: { running: number; blocked: number; contextSwitchesPerSecond: number | null; processesStartedPerSecond: number | null } | null;
  } | null;
  memory: {
    totalBytes: number; usedBytes: number; availableBytes: number; cachedBytes: number; swapTotalBytes: number; swapUsedBytes: number; usagePercent: number;
    majorFaultsPerSecond: number | null; swapInBytesPerSecond: number | null; swapOutBytesPerSecond: number | null;
    dirtyBytes: number | null; writebackBytes: number | null; committedBytes: number | null; commitLimitBytes: number | null;
    slabReclaimableBytes: number | null; slabUnreclaimableBytes: number | null;
    reclaimScannedBytesPerSecond: number | null; reclaimedBytesPerSecond: number | null;
  } | null;
  gpu: { available: boolean; error: string | null; devices: GpuDevice[] };
  disk: { volumes: DiskVolume[]; devices: DiskDevice[]; readBytesPerSecond: number; writeBytesPerSecond: number };
  network: {
    available: boolean; error: string | null; interfaces: NetworkInterface[]; receiveBytesPerSecond: number; transmitBytesPerSecond: number;
    tcp: TcpStats | null; udp: UdpStats | null; conntrack: Conntrack | null;
  };
  containers: ContainerTotals;
  pressure: Pressure | null;
  temperatures: TemperatureSensor[] | null;
  files: { open: number; max: number } | null;
}

export interface GpuDevice {
  index: number;
  name: string;
  driverVersion: string | null;
  utilizationPercent: number | null;
  memoryUtilizationPercent: number | null;
  memoryTotalBytes: number | null;
  memoryUsedBytes: number | null;
  memoryUsagePercent: number | null;
  temperatureCelsius: number | null;
  powerDrawWatts: number | null;
  powerLimitWatts: number | null;
  fanPercent: number | null;
  clockSmMhz: number | null;
  clockSmMaxMhz: number | null;
  pciBusId: string | null;
  performanceState: string | null;
  pcieGeneration: number | null;
  pcieWidth: number | null;
  clockEventReasons: string[] | null;
  encoderPercent: number | null;
  decoderPercent: number | null;
  clockMemoryMhz: number | null;
  clockMemoryMaxMhz: number | null;
  temperatureHeadroomCelsius: number | null;
  eccCorrected: number | null;
  eccUncorrected: number | null;
}

export interface DiskVolume {
  name: string;
  path: string;
  format: string | null;
  totalBytes: number;
  usedBytes: number;
  freeBytes: number;
  usagePercent: number;
  error: string | null;
  inodesTotal: number | null;
  inodesFree: number | null;
}

export interface DiskDevice {
  name: string; readBytesPerSecond: number; writeBytesPerSecond: number; busyPercent: number; readOpsPerSecond: number; writeOpsPerSecond: number;
  readLatencyMs: number | null; writeLatencyMs: number | null; queueDepth: number | null;
}

export interface NetworkInterface {
  name: string;
  receiveBytesPerSecond: number;
  transmitBytesPerSecond: number;
  receivePacketsPerSecond: number;
  transmitPacketsPerSecond: number;
  receiveBytesTotal: number;
  transmitBytesTotal: number;
  errorsTotal: number;
  dropsTotal: number;
}

type Column = (number | null)[];

export interface FleetHistory {
  bucketSeconds: number;
  t: number[];
  hosts: { hostId: string; cpuCores: Column; memoryUsed: Column; gpuUtilization: Column; vramUsed: Column; diskRead: Column; diskWrite: Column; netReceive: Column; netTransmit: Column; containersCpuCores: Column }[];
}

export interface HostHistory {
  bucketSeconds: number;
  t: number[];
  cpu: Column;
  memoryUsed: Column;
  diskRead: Column;
  diskWrite: Column;
  netReceive: Column;
  netTransmit: Column;
  gpus: { index: number; utilization: Column; memoryUsage: Column }[];
  containers: { containerId: string; cpuPercentOfHost: Column; memoryBytes: Column }[];
}

export interface ContainerHistory {
  bucketSeconds: number;
  t: number[];
  cpuCores: number[];
  memoryBytes: number[];
  ioRead: number[];
  ioWrite: number[];
  netReceive: number[];
  netTransmit: number[];
  gpuPercent: Column;
  gpuMemoryBytes: Column;
}
