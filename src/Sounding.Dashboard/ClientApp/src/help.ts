import type { Scope } from './data';
import { FREE_SWATCH, HEAT_RAMP, OTHER_GREY, PALETTE, T } from './theme';

export type Mark =
  | { kind: 'line' | 'box'; color: string }
  | { kind: 'dashed' }
  | { kind: 'palette' }
  | { kind: 'ramp' }
  | { kind: 'badge'; warn?: boolean };

export interface HelpItem { label: string; text: string; mark?: Mark }

// What a "?" shows: a short explanation, then a legend for the chart's lines (matched by their exact label), its
// dashed line, and any other marks. Details belong in the documentation, not here.
export interface Help {
  meaning: string;
  series?: Record<string, string>;
  anySeries?: string;
  reference?: string;
  items?: HelpItem[];
}

const box = (color: string): Mark => ({ kind: 'box', color });
const badge = (warn = false): Mark => ({ kind: 'badge', warn });

type Level = 'fleet' | 'host' | 'container';
const level = (scope: Scope): Level => (!scope.hostId ? 'fleet' : !scope.containerId ? 'host' : 'container');

const PRESSURE = { 'Some waiting': 'some work waiting', 'All waiting': 'all work waiting' };

export function overviewHelp(id: string, scope: Scope, wsl: boolean): Help {
  const l = level(scope);
  switch (id) {
    case 'cpu':
      return {
        fleet: {
          meaning: 'Processor use across all hosts, and how much of it is the containers.',
          series: { 'Host total': 'everything', 'In use': 'everything', Containers: 'the containers' },
          reference: 'total capacity',
        },
        host: {
          meaning: 'How busy this host’s processors are, and how much of it is the containers. A big gap means something outside the containers is busy.',
          series: { 'Host total': 'everything', 'In use': 'everything', Containers: 'the containers' },
          reference: 'all cores',
        },
        container: {
          meaning: 'Processor use of this container against its limit. Its limit can slow it down even below the dashed line: see CPU throttling.',
          series: { 'In use': 'cores in use' },
          reference: 'CPU limit',
        },
      }[l];
    case 'memory':
      return {
        fleet: {
          meaning: 'Memory in use across all hosts. Cache that can be freed isn’t counted.',
          series: { 'In use': 'memory taken' },
          reference: 'total memory',
        },
        host: {
          meaning: `Memory in use on this host, and how much of it is the containers. Cache that can be freed isn’t counted.${wsl ? ' On Docker Desktop this is Docker’s VM, not the whole PC.' : ''}`,
          series: { 'Host total': 'memory taken', 'In use': 'memory taken', Containers: 'the containers' },
          reference: wsl ? 'the VM’s memory' : 'total memory',
          items: [
            { label: 'Other', text: 'not containers', mark: box(T.usage) },
            { label: 'Containers', text: 'the containers', mark: box(T.second) },
            { label: 'Available', text: 'free', mark: box(FREE_SWATCH) },
          ],
        },
        container: {
          meaning: 'Memory this container uses against its limit. At the limit it slows down, then programs in it get killed.',
          series: { 'In use': 'memory taken' },
          reference: 'memory limit',
          items: [
            { label: 'This container', text: '', mark: box(T.usage) },
            { label: 'Rest of the host', text: '', mark: box(OTHER_GREY) },
          ],
        },
      }[l];
    case 'gpu':
      return {
        fleet: {
          meaning: 'How busy the GPUs are and how full their memory is.',
          series: { Compute: 'time busy', VRAM: 'memory in use' },
        },
        host: {
          meaning: 'How busy this host’s GPUs are and how full their memory is.',
          series: { Compute: 'time busy', VRAM: 'memory in use' },
        },
        container: {
          meaning: 'How much of the GPU this container’s programs use.',
          series: { Compute: 'time its work ran', 'VRAM %': 'its share of GPU memory' },
        },
      }[l];
    case 'disk':
      return {
        fleet: {
          meaning: 'How much the hosts read and write. Below, how full each disk is.',
          series: { Read: 'read', Write: 'written' },
        },
        host: {
          meaning: 'How much this host reads and writes, and how full the disk with Docker’s data is.',
          series: { Read: 'read', Write: 'written' },
          items: [
            { label: 'Other', text: 'not containers', mark: box(T.usage) },
            { label: 'Containers', text: 'their files', mark: box(T.second) },
            { label: 'Free', text: '', mark: box(FREE_SWATCH) },
          ],
        },
        container: {
          meaning: 'How much this container reads and writes, and the space its files take.',
          series: { Read: 'read', Write: 'written' },
          items: [
            { label: 'This container', text: '', mark: box(T.usage) },
            { label: 'Rest of the disk', text: '', mark: box(OTHER_GREY) },
          ],
        },
      }[l];
    case 'network':
      return {
        fleet: {
          meaning: 'Traffic in and out of all hosts. Below, each host’s share.',
          series: { In: 'received', Out: 'sent' },
        },
        host: {
          meaning: 'Traffic in and out of this host. Traffic between its containers isn’t included.',
          series: { In: 'received', Out: 'sent' },
        },
        container: {
          meaning: 'Traffic in and out of this container, including to other containers on the same host.',
          series: { In: 'received', Out: 'sent' },
        },
      }[l];
    default:
      return l === 'fleet'
        ? {
            meaning: 'Which hosts use the most, and what is left. Click a host to open it.',
            items: [
              { label: 'Each host', text: 'its own colour', mark: { kind: 'palette' } },
              { label: 'Free', text: '', mark: box(FREE_SWATCH) },
            ],
          }
        : {
            meaning: `Which containers use the most of this host, and what is left. Limits compares the containers’ limits with what the host has. ${l === 'container' ? 'Click this container again to go back to the host.' : 'Click a container to open it.'}`,
            items: [
              { label: 'Each container', text: 'its own colour', mark: { kind: 'palette' } },
              { label: 'Other', text: 'not containers', mark: box(OTHER_GREY) },
              { label: 'Free', text: '', mark: box(FREE_SWATCH) },
            ],
          };
  }
}

const PROCESS_BADGES: HelpItem[] = [
  { label: 'Sounding', text: 'part of the monitoring', mark: badge() },
  { label: 'root', text: 'runs as administrator', mark: badge() },
  { label: '+privileges', text: 'extra privileges', mark: badge(true) },
  { label: 'unrestricted', text: 'no security filter', mark: badge(true) },
];

export const HELP = {
  cpuPressure: {
    meaning: 'How often work had to wait for a processor. A steady value means there isn’t enough CPU.',
    series: PRESSURE,
  },
  cpuThrottling: {
    meaning: 'How much CPU limits held work back. Any value means it would run faster with a higher limit.',
    series: { Throttled: 'time held back', Containers: 'all containers' },
  },
  cores: {
    meaning: 'How busy each core is right now. Dimmed cores can’t be used by this container.',
    items: [
      { label: 'Colour', text: 'idle to busy', mark: { kind: 'ramp' } },
      { label: 'Dimmed', text: 'not allowed', mark: box(OTHER_GREY) },
    ],
  },
  cpuLimits: {
    meaning: 'The CPU limits and how close the work is to them. Amber values are worth a look.',
  },
  processes: {
    meaning: 'The processes using the most. Last … covers the selected time, including programs that ended; Now is this moment. Hover a chart to see that moment.',
    items: PROCESS_BADGES,
  },
  memoryPressure: {
    meaning: 'How often work stalled waiting for memory. A steady value means memory is too tight.',
    series: PRESSURE,
  },
  memoryCompositionContainer: {
    meaning: 'What this container’s memory is made of. Program data growing steadily can mean a leak.',
    series: { 'Program data': 'its own data', Cache: 'can be freed', Kernel: 'system memory for it' },
    reference: 'memory limit',
  },
  memoryCompositionHost: {
    meaning: 'Memory in use versus cache. Cache is freed when needed, so a high cache is fine.',
    series: { 'In use': 'taken', Cache: 'can be freed' },
    reference: 'total memory',
  },
  memoryBreakdown: {
    meaning: 'Memory figures right now, and how often a limit was hit. Any out-of-memory kill means a limit was too low.',
  },
  power: {
    meaning: 'Power the GPU draws, for the whole card. At its limit the GPU slows down.',
    anySeries: 'power draw',
    reference: 'power limit',
  },
  transfers: {
    meaning: 'Data the containers’ programs copy to and from the GPU. Lots of copying with little GPU use means the GPU is waiting for data.',
    series: { 'To GPU': 'to the GPU', 'To host': 'back from it' },
  },
  card: {
    meaning: 'Each GPU right now. “Clock limited by” says why it isn’t at full speed; GPU idle is normal.',
  },
  gpuProcesses: {
    meaning: 'The programs using the GPU and how much. Last … covers the selected time; Now is this moment.',
  },
  diskHostVs: {
    meaning: 'The host’s disk traffic next to the containers’. A large gap means something outside the containers is using the disk.',
    series: { 'Host total': 'reads + writes', Containers: 'reads + writes', 'This container': 'reads + writes' },
  },
  ioPressure: {
    meaning: 'How often work waited for the disk. A steady value means the disk is a bottleneck.',
    series: PRESSURE,
  },
  operations: {
    meaning: 'Read and write requests per second. Many small requests can overload a disk even with little data.',
    series: { Read: 'reads', Write: 'writes' },
  },
  storage: {
    meaning: 'Disk space in use: for a container, the size of its files; for the host, the disk with Docker’s data.',
  },
  devices: {
    meaning: 'Traffic per disk. Busy near 100% means the disk is at its limit.',
  },
  networkHostVs: {
    meaning: 'The host’s traffic next to the containers’. Containers can show more, because traffic between them stays inside the host.',
    series: { 'Host total': 'in + out', Containers: 'in + out', 'This container': 'in + out' },
  },
  internet: {
    meaning: 'Traffic split by where it goes: the internet, or inside your network.',
    series: { 'To internet': 'sent', 'From internet': 'received', 'Not internet': 'both ways', 'Same network': 'other containers on its networks' },
  },
  packets: {
    meaning: 'Packets in and out per second. Many packets with little data means many small messages.',
    series: { In: 'received', Out: 'sent' },
  },
  connections: {
    meaning: 'Who the containers talk to, the program behind each connection and the data sent. Look for destinations you don’t expect.',
    items: [
      { label: 'New destination', text: 'first seen in the last 15 min', mark: badge(true) },
      { label: 'Same network', text: 'another container', mark: badge() },
      { label: 'Host', text: 'this host', mark: badge() },
      { label: 'Private', text: 'your network', mark: badge() },
      { label: 'Internet', text: 'a public address', mark: badge() },
      { label: 'Problems', text: 'failing or unhealthy', mark: badge(true) },
    ],
  },
  lookups: {
    meaning: 'The names the containers looked up. Unknown names or many failures are worth checking.',
    items: [{ label: 'NXDOMAIN', text: 'name doesn’t exist', mark: badge(true) }],
  },
  listening: {
    meaning: 'Ports the containers accept connections on. A port you don’t expect is worth checking.',
    items: [
      { label: 'New', text: 'opened in the last 15 min', mark: badge(true) },
      { label: 'host :8088', text: 'reachable on the host’s port 8088', mark: badge() },
      { label: 'internet', text: 'used from the internet', mark: badge(true) },
      { label: 'network', text: 'used from your network', mark: badge() },
    ],
  },
  cpuSplit: {
    meaning: 'Where processor time went besides the programs’ own work. High I/O wait means waiting on the disk.',
    series: { System: 'system work', 'I/O wait': 'waiting on disk', Interrupts: 'hardware events', 'Stolen by VM': 'taken by the VM host' },
  },
  pageFaults: {
    meaning: 'How often memory had to be read back from disk. A steady rate means memory is too small for the work.',
    series: { 'From disk': 'read from disk', 'Re-read': 'read again soon after' },
  },
  swap: {
    meaning: 'Memory moved to disk and back. Steady activity means the machine is short of memory.',
    series: { 'Swapped in': 'back from disk', 'Swapped out': 'moved to disk' },
  },
  launches: {
    meaning: 'Pieces of GPU work the containers’ programs start per second.',
    series: { 'This container': 'its work', Containers: 'all containers' },
  },
  largestPaths: {
    meaning: 'The folders taking the most space (volumes not included).',
  },
  diskProcesses: {
    meaning: 'The processes reading and writing the most.',
  },
  tcp: {
    meaning: 'Signs of network trouble: data sent again, connections cut off, and attempts that failed.',
    series: { Resent: 'sent again', 'Connections reset': 'cut off', 'Failed connections': 'never opened' },
  },
  socketStates: {
    meaning: 'Network interfaces with their traffic, and the open connections by state. A growing CLOSE_WAIT count usually means a program isn’t closing connections.',
  },
  runQueue: {
    meaning: 'Threads that want a processor. Above the dashed line, some of them wait.',
    series: { Running: 'running or ready', 'Waiting on I/O': 'waiting on disk' },
    reference: 'number of cores',
  },
  processesStarted: {
    meaning: 'New processes started per second. A steady high rate means something keeps starting short-lived programs.',
    series: { 'Host total': 'whole machine', Containers: 'all containers', 'This container': 'this container' },
  },
  temperatures: {
    meaning: 'The machine’s temperature sensors. Virtual machines usually have none; GPU temperature is on the GPU page.',
    anySeries: 'a sensor (the hottest are shown)',
    reference: 'critical temperature',
  },
  reclaim: {
    meaning: 'The system freeing memory. Steady activity means memory is tight.',
    series: { Checked: 'looked at', Freed: 'freed' },
  },
  dirty: {
    meaning: 'Data written by programs but not yet saved to disk. If it keeps growing, the disk can’t keep up.',
    series: { Waiting: 'not saved yet', 'Being written': 'being saved' },
  },
  committed: {
    meaning: 'Memory programs have reserved, against the system’s limit.',
    series: { Committed: 'reserved' },
    reference: 'commit limit',
  },
  gpuKernels: {
    meaning: 'The GPU work that took the most time, by name.',
  },
  storageGrowth: {
    meaning: 'The space the container’s files take, over time. A steady climb is usually logs or a cache.',
    series: { 'This container': 'its files', Containers: 'all containers' },
  },
  mounts: {
    meaning: 'Volumes and folders mounted into the containers, and the free space where they’re stored.',
  },
  udp: {
    meaning: 'UDP messages lost, or sent to a port nobody listens on.',
    series: { 'Receive errors': 'lost', 'Buffer full': 'lost, program too slow', 'No listener': 'nobody listening' },
  },
  dns: {
    meaning: 'Name lookups per second, and those that failed.',
    series: { Lookups: 'answers', Failures: 'failed' },
  },
  dnsReplyTime: {
    meaning: 'How long name lookups take. Slow lookups slow down every new connection.',
    series: { 'Average reply': 'average', 'Slowest container': 'the slowest one' },
  },
  conntrack: {
    meaning: 'Connections the system tracks for its firewall and port forwarding. At the limit, new connections are dropped.',
    series: { Entries: 'tracked now' },
    reference: 'limit',
  },
  largestFiles: {
    meaning: 'The biggest files (volumes not included).',
  },
  growingPaths: {
    meaning: 'Folders that grew in the last 30 seconds: where the container is writing now.',
  },
  deletedFiles: {
    meaning: 'Deleted files a program still holds open. Their space isn’t freed until it closes them.',
  },
  requests: {
    meaning: 'Requests made or answered in the last minute: how many, how many failed, and how fast. Encrypted traffic can’t be read.',
    items: [
      { label: 'Median', text: 'half were faster' },
      { label: '95th', text: '95 in 100 were faster' },
      { label: 'Per connection', text: '1 means a new connection per request' },
    ],
  },
  capture: {
    meaning: 'Records this container’s traffic for a few seconds, into a file Wireshark opens. It can contain sensitive data.',
  },
  events: {
    meaning: 'Things that happened: starts and stops, out-of-memory kills, limits reached, new ports and destinations.',
  },
  logs: {
    meaning: 'What the containers print: lines per second, and lines that look like errors or warnings. Latest output shows the last few lines.',
    series: { Lines: 'all lines', Errors: 'look like errors', Warnings: 'look like warnings' },
  },
  map: {
    meaning: 'Who talks to whom: the host, its networks and containers, with a line for each pair that exchanged traffic.',
    items: [
      { label: 'Dots', text: 'move from the side that connects', mark: box(T.usage) },
    ],
  },
} satisfies Record<string, Help>;

export const HELP_RAMP = HEAT_RAMP;
export const HELP_PALETTE = PALETTE.slice(0, 4);
