import { execFile } from 'node:child_process';
import { mkdtemp, rmdir, unlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import { expect, test as base } from '@playwright/test';

const execFileAsync = promisify(execFile);

export const test = base.extend({
  page: async ({ page, colorScheme }, use) => {
    await page.addInitScript(theme => {
      localStorage.setItem('sounding:range', '300');
      localStorage.setItem('sounding:theme', theme);
    }, colorScheme);
    await use(page);
  },
});

export function imagePath(filename) {
  const themedFilename = test.info().project.use.colorScheme === 'light'
    ? filename.replace(/(\.(?:png|gif))$/, '.light$1') : filename;
  return path.join('/captures', themedFilename);
}

export async function capturePage(page, filename, { animations = 'disabled' } = {}) {
  await page.mouse.move(1435, 895);
  await page.screenshot({
    path: imagePath(filename), fullPage: false, animations,
  });
}

async function encodeAnimation(filename, input, { frames, fps }) {
  await execFileAsync('ffmpeg', [
    '-hide_banner', '-loglevel', 'error', '-y', ...input,
    '-filter_complex', `[0:v]fps=${fps},split[a][b];[a]palettegen[p];[b][p]paletteuse=dither=none:diff_mode=rectangle`,
    '-frames:v', String(frames), '-loop', '0', imagePath(filename),
  ]);
}

export async function captureLiveAnimation(page, filename, { seconds, fps }) {
  const viewport = page.viewportSize();
  const session = await page.context().newCDPSession(page);
  const captured = [];
  const acknowledgements = [];
  session.on('Page.screencastFrame', frame => {
    captured.push(frame);
    acknowledgements.push(session.send('Page.screencastFrameAck', { sessionId: frame.sessionId }));
  });
  try {
    await page.mouse.move(1435, 895);
    await session.send('Page.startScreencast', { format: 'png', maxWidth: viewport.width, maxHeight: viewport.height });
    await page.waitForTimeout(seconds * 1000);
    await session.send('Page.stopScreencast');
    await Promise.all(acknowledgements);
  } finally {
    await session.detach();
  }
  const end = captured[0].metadata.timestamp + seconds;
  const recorded = captured.filter(frame => frame.metadata.timestamp < end);
  const directory = await mkdtemp(path.join(os.tmpdir(), 'sounding-live-animation-'));
  const files = [];
  try {
    const manifest = [];
    for (const [index, frame] of recorded.entries()) {
      const file = path.join(directory, `frame-${index}.png`);
      await writeFile(file, Buffer.from(frame.data, 'base64'));
      files.push(file);
      const next = recorded[index + 1]?.metadata.timestamp ?? end;
      manifest.push(`file '${file}'`, `duration ${next - frame.metadata.timestamp}`);
    }
    manifest.push(`file '${files.at(-1)}'`);
    const input = path.join(directory, 'frames.txt');
    await writeFile(input, manifest.join('\n'));
    files.push(input);
    await encodeAnimation(filename, ['-f', 'concat', '-safe', '0', '-i', input], {
      frames: seconds * fps, fps,
    });
  } finally {
    for (const file of files) await unlink(file);
    await rmdir(directory);
  }
}

export async function captureAnimation(page, filename, { frames, fps, beforeFrame }) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'sounding-animation-'));
  const files = [];
  try {
    await page.mouse.move(1435, 895);
    for (let frame = 0; frame < frames; frame++) {
      await beforeFrame(frame, frames);
      const file = path.join(directory, `frame-${String(frame).padStart(3, '0')}.png`);
      await page.screenshot({ path: file, fullPage: false, animations: 'allow' });
      files.push(file);
    }

    await encodeAnimation(filename, [
      '-framerate', String(fps), '-i', path.join(directory, 'frame-%03d.png'),
    ], { frames, fps });
  } finally {
    for (const file of files) await unlink(file);
    await rmdir(directory);
  }
}

export async function openDashboard(page, url = '/') {
  const { searchParams } = new URL(url, 'http://dashboard:8080');
  const host = searchParams.get('host');
  const container = searchParams.get('container');
  const historyPath = container ? `/api/containers/${encodeURIComponent(container)}/history`
    : host ? `/api/hosts/${encodeURIComponent(host)}/history` : '/api/fleet/history';
  const historyReady = page.waitForResponse(response =>
    new URL(response.url()).pathname === historyPath && response.status() === 200);
  await page.goto(url);
  await expect(page.locator('html')).toHaveAttribute('data-theme', test.info().project.use.colorScheme);
  await (await historyReady).finished();
  await page.getByRole('banner').getByText('Live', { exact: true }).waitFor();
  await page.locator('.recharts-surface').first().waitFor();
  await page.getByText(/^Loading(?:\u2026|\.{3})$/).first().waitFor({ state: 'detached' });
  await page.evaluate(() => document.fonts.ready);
  await page.mouse.move(1435, 895);
}
