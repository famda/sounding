import { spawn } from 'node:child_process';
import { mkdir, rm } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';

const baseURL = 'http://dashboard:8080';
const hosts = ['demo-db', 'demo-edge', 'demo-gpu'];

async function waitForDashboard() {
  const deadline = Date.now() + 60_000;
  let failure;
  do {
    try {
      const response = await fetch(`${baseURL}/healthz`, { signal: AbortSignal.timeout(5000) });
      if (response.ok) return;
      failure = new Error(`Dashboard returned HTTP ${response.status}`);
    } catch (error) {
      failure = error;
    }
    await delay(250);
  } while (Date.now() < deadline);
  throw new Error('Documentation dashboard did not become ready.', { cause: failure });
}

async function waitForFleet() {
  const response = await fetch(`${baseURL}/api/fleet/stream?hosts=${hosts.join(',')}`, {
    signal: AbortSignal.timeout(30_000),
  });
  if (!response.ok) throw new Error(`Fleet stream returned HTTP ${response.status}`);
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  try {
    while (true) {
      const event = buffer.match(/(?:^|\n)data:\s*([^\n]+)\r?\n\r?\n/);
      if (event) {
        buffer = buffer.slice(event.index + event[0].length);
        const update = JSON.parse(event[1]);
        if (update.hosts.length === hosts.length) return;
        continue;
      }
      const { value, done } = await reader.read();
      if (done) throw new Error('Fleet stream ended before reporting the documentation scenario.');
      buffer += decoder.decode(value, { stream: true });
    }
  } finally {
    await reader.cancel();
  }
}

export default async function setup() {
  await rm('/captures', { recursive: true, force: true });
  await mkdir('/captures');
  const seed = spawn('/seed/Sounding.Demo', { stdio: ['ignore', 'pipe', 'inherit'] });
  const exited = new Promise(resolve => seed.once('exit', resolve));
  const ready = new Promise((resolve, reject) => {
    let output = '';
    seed.stdout.on('data', chunk => {
      process.stdout.write(chunk);
      output += chunk;
      if (output.includes('Synthetic fleet ready:')) resolve();
    });
    seed.once('error', reject);
    seed.once('exit', code => reject(new Error(`Documentation seeding stopped (exit ${code}).`)));
  });
  async function stop() {
    if (seed.pid && seed.exitCode === null && seed.signalCode === null) {
      seed.kill('SIGTERM');
      await Promise.race([
        exited,
        delay(5000, undefined, { ref: false }).then(() => { seed.kill('SIGKILL'); return exited; }),
      ]);
    }
  }
  try {
    await Promise.race([
      ready,
      delay(60_000, undefined, { ref: false }).then(() => { throw new Error('Documentation seeding timed out.'); }),
    ]);
    await waitForDashboard();
    await waitForFleet();
  } catch (error) {
    await stop();
    throw error;
  }
  return stop;
}
