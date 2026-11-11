import { useEffect, useMemo, useState } from 'react';
import { T } from '../theme';
import { type Col, Table } from './Table';

// A capture's packets, read in the browser: the pcap file holds raw IP packets cut at 128 bytes (net-meter's
// headers-only capture), so every packet is decoded down to its transport header and the first bytes after.
interface Packet {
  n: number;
  time: number;
  length: number;
  source: string;
  destination: string;
  protocol: string;
  info: string;
  fields: [string, string][];
  bytes: Uint8Array;
}

const ICMP4: Record<number, string> = { 0: 'Echo reply', 3: 'Destination unreachable', 5: 'Redirect', 8: 'Echo request', 11: 'Time exceeded' };
const ICMP6: Record<number, string> = { 1: 'Destination unreachable', 2: 'Packet too big', 3: 'Time exceeded', 128: 'Echo request', 129: 'Echo reply', 135: 'Neighbor solicitation', 136: 'Neighbor advertisement' };
const UNREACHABLE: Record<number, string> = { 0: 'network', 1: 'host', 3: 'port', 4: 'fragmentation needed', 13: 'administratively prohibited' };
const METHODS = ['GET ', 'POST ', 'PUT ', 'HEAD ', 'DELETE ', 'PATCH ', 'OPTIONS ', 'HTTP/'];

const ipv4 = (b: Uint8Array, at: number) => `${b[at]}.${b[at + 1]}.${b[at + 2]}.${b[at + 3]}`;
const ipv6 = (b: Uint8Array, at: number) =>
  Array.from({ length: 8 }, (_, i) => ((b[at + 2 * i] << 8) | b[at + 2 * i + 1]).toString(16)).join(':').replace(/(^|:)0(:0)+(:|$)/, '::');
const u16 = (b: Uint8Array, at: number) => (b[at] << 8) | b[at + 1];
const u32 = (b: Uint8Array, at: number) => ((b[at] << 24) >>> 0) + (b[at + 1] << 16) + (b[at + 2] << 8) + b[at + 3];
const text = (b: Uint8Array, from: number, to: number) => String.fromCharCode(...b.subarray(from, to)).replace(/[^\x20-\x7e]/g, '.');

function dnsName(b: Uint8Array, at: number) {
  const labels: string[] = [];
  while (at < b.length && b[at] !== 0 && labels.length < 20) {
    if ((b[at] & 0xc0) === 0xc0) break;
    labels.push(text(b, at + 1, at + 1 + b[at]));
    at += 1 + b[at];
  }
  return labels.join('.');
}

// The protocol a TCP payload shows by its own bytes; none when they don't tell.
function application(b: Uint8Array, at: number): [string, string] | null {
  if (at >= b.length) return null;
  const head = text(b, at, Math.min(b.length, at + 48));
  if (b[at] === 0x16 && at + 5 < b.length) {
    const kind = { 1: 'Client Hello', 2: 'Server Hello', 11: 'Certificate' }[b[at + 5]];
    return ['TLS', kind ? `TLS ${kind}` : 'TLS handshake'];
  }
  if (b[at] === 0x17) return ['TLS', 'TLS application data'];
  if (METHODS.some(m => head.startsWith(m))) return ['HTTP', head.split('..')[0]];
  return null;
}

function decode(n: number, time: number, length: number, b: Uint8Array): Packet {
  const fields: [string, string][] = [];
  let source = '?', destination = '?', protocol = 'IP', info = '', transport = -1, next = -1, payloadLength = 0;
  const version = b[0] >> 4;
  if (version === 4 && b.length >= 20) {
    const header = (b[0] & 15) * 4;
    source = ipv4(b, 12);
    destination = ipv4(b, 16);
    next = b[9];
    transport = header;
    payloadLength = u16(b, 2) - header;
    fields.push(['IPv4', `${source} → ${destination}`], ['Time to live', String(b[8])], ['Total length', String(u16(b, 2))], ['Identification', `0x${u16(b, 4).toString(16)}`],
      ['Don’t fragment', (b[6] & 0x40) ? 'yes' : 'no']);
  } else if (version === 6 && b.length >= 40) {
    source = ipv6(b, 8);
    destination = ipv6(b, 24);
    next = b[6];
    transport = 40;
    payloadLength = u16(b, 4);
    fields.push(['IPv6', `${source} → ${destination}`], ['Hop limit', String(b[7])], ['Payload length', String(payloadLength)]);
  }

  if (next === 6 && transport + 20 <= b.length) {
    const [sp, dp] = [u16(b, transport), u16(b, transport + 2)];
    const header = (b[transport + 12] >> 4) * 4;
    const flags = b[transport + 13];
    const names = [[0x02, 'SYN'], [0x10, 'ACK'], [0x01, 'FIN'], [0x04, 'RST'], [0x08, 'PSH']].filter(([bit]) => flags & (bit as number)).map(([, name]) => name);
    const data = Math.max(0, payloadLength - header);
    protocol = 'TCP';
    info = `${sp} → ${dp} [${names.join(', ')}] Seq=${u32(b, transport + 4)}${flags & 0x10 ? ` Ack=${u32(b, transport + 8)}` : ''} Win=${u16(b, transport + 14)} Len=${data}`;
    fields.push(['TCP ports', `${sp} → ${dp}`], ['Flags', names.join(', ') || 'none'], ['Sequence', String(u32(b, transport + 4))], ['Acknowledgment', String(u32(b, transport + 8))],
      ['Window', u16(b, transport + 14) === 0 ? '0 (zero window: the receiver is full)' : String(u16(b, transport + 14))], ['Data', `${data} bytes`]);
    const app = data > 0 ? application(b, transport + header) : null;
    if (app) {
      protocol = app[0];
      info = `${info}  ${app[1]}`;
      fields.push(['Starts with', app[1]]);
    }
  } else if (next === 17 && transport + 8 <= b.length) {
    const [sp, dp] = [u16(b, transport), u16(b, transport + 2)];
    protocol = 'UDP';
    info = `${sp} → ${dp} Len=${u16(b, transport + 4) - 8}`;
    fields.push(['UDP ports', `${sp} → ${dp}`], ['Length', String(u16(b, transport + 4))]);
    if ((sp === 53 || dp === 53) && transport + 20 <= b.length) {
      const at = transport + 8;
      const reply = (b[at + 2] & 0x80) !== 0;
      const name = dnsName(b, at + 12);
      protocol = 'DNS';
      info = `${reply ? 'Reply' : 'Query'} 0x${u16(b, at).toString(16)} ${name}${reply ? ` (${['NOERROR', 'FORMERR', 'SERVFAIL', 'NXDOMAIN'][b[at + 3] & 15] ?? 'error'}, ${u16(b, at + 6)} answers)` : ''}`;
      fields.push(['DNS', reply ? 'reply' : 'query'], ['Name', name || '(cut off)']);
    }
  } else if ((next === 1 || next === 58) && transport + 4 <= b.length) {
    const [type, code] = [b[transport], b[transport + 1]];
    const name = (next === 1 ? ICMP4 : ICMP6)[type] ?? `type ${type}`;
    protocol = next === 1 ? 'ICMP' : 'ICMPv6';
    info = `${name}${(next === 1 && type === 3) || (next === 58 && type === 1) ? ` (${UNREACHABLE[code] ?? `code ${code}`})` : ''}`;
    fields.push([protocol, info]);
  }

  return { n, time, length, source, destination, protocol, info, fields, bytes: b };
}

function parse(file: ArrayBuffer): Packet[] {
  const view = new DataView(file);
  if (file.byteLength < 24 || view.getUint32(0, true) !== 0xa1b2c3d4) return [];
  const packets: Packet[] = [];
  let first: number | null = null;
  for (let at = 24; at + 16 <= file.byteLength;) {
    const seconds = view.getUint32(at, true), micros = view.getUint32(at + 4, true);
    const captured = view.getUint32(at + 8, true), length = view.getUint32(at + 12, true);
    const time = seconds + micros / 1e6;
    first ??= time;
    packets.push(decode(packets.length + 1, time - first, length, new Uint8Array(file, at + 16, Math.min(captured, file.byteLength - at - 16))));
    at += 16 + captured;
  }
  return packets;
}

function HexDump({ bytes }: { bytes: Uint8Array }) {
  const rows = [];
  for (let at = 0; at < bytes.length; at += 16) {
    const line = bytes.subarray(at, at + 16);
    rows.push(
      <div key={at} className="flex gap-4">
        <span className="text-muted">{at.toString(16).padStart(4, '0')}</span>
        <span className="w-[23rem]">{Array.from(line, x => x.toString(16).padStart(2, '0')).join(' ')}</span>
        <span className="text-text-2">{text(line, 0, line.length)}</span>
      </div>,
    );
  }
  return <div className="font-mono text-[11px] leading-5 text-text">{rows}</div>;
}

const COLS: Col<Packet>[] = [
  { label: 'No.', sort: p => p.n, right: true, width: '4rem', cell: p => p.n },
  { label: 'Time', sort: p => p.time, right: true, width: '6rem', cell: p => p.time.toFixed(6) },
  { label: 'Source', sort: p => p.source, width: '9rem', cell: p => <span className="font-mono">{p.source}</span> },
  { label: 'Destination', sort: p => p.destination, width: '9rem', cell: p => <span className="font-mono">{p.destination}</span> },
  { label: 'Protocol', sort: p => p.protocol, width: '6.5rem', cell: p => p.protocol },
  { label: 'Length', sort: p => p.length, right: true, width: '5rem', cell: p => p.length },
  {
    label: 'Info', sort: p => p.info,
    cell: p => <span className="font-mono" style={{ color: /RST|zero window|unreachable|too big/i.test(p.info) || /Win=0 /.test(p.info) ? T.warn : undefined }}>{p.info}</span>,
    title: p => p.info,
  },
];

export function PacketViewer({ id }: { id: number }) {
  const [file, setFile] = useState<ArrayBuffer | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    setFile(null);
    setSelected(null);
    fetch(`api/captures/${id}`, { signal: controller.signal })
      .then(response => (response.ok ? response.arrayBuffer() : Promise.reject(new Error(`HTTP ${response.status}`))))
      .then(setFile)
      .catch(e => { if (!controller.signal.aborted) setError(String(e.message ?? e)); });
    return () => controller.abort();
  }, [id]);
  const packets = useMemo(() => (file ? parse(file) : []), [file]);
  const packet = packets.find(p => p.n === selected);

  if (error) return <div className="py-3 text-center" style={{ color: T.warn }}>The capture couldn’t be read: {error}</div>;
  if (!file) return <div className="py-3 text-center text-muted">Loading the capture…</div>;
  return (
    <div className="grid gap-2">
      <div className="max-h-[360px] overflow-auto in-[.panel-maximized]:max-h-[55vh]">
        <Table id="packets" rows={packets} cols={COLS} rowKey={p => String(p.n)} onRow={p => setSelected(p.n)} empty="No packets in this capture." />
      </div>
      {packet && (
        <div className="grid gap-x-8 gap-y-2 rounded border border-line bg-bg p-2.5 md:grid-cols-[minmax(0,22rem)_1fr]">
          <div className="grid grid-cols-[auto_1fr] content-start gap-x-4 gap-y-0.5">
            {packet.fields.map(([label, value]) => (
              <div key={label} className="contents">
                <span className="text-muted">{label}</span>
                <span className="font-mono text-text">{value}</span>
              </div>
            ))}
          </div>
          <div>
            <div className="mb-1 text-muted">Packet {packet.n}: the first {packet.bytes.length} of {packet.length} bytes</div>
            <HexDump bytes={packet.bytes} />
          </div>
        </div>
      )}
    </div>
  );
}
