import assert from 'node:assert/strict';
import { copyFile, readdir, rename } from 'node:fs/promises';
import path from 'node:path';

const images = (await readdir('/captures')).filter(name => /\.(png|gif)$/.test(name)).sort();
assert.ok(images.length > 0, 'No screenshots were produced.');
const darkImages = images.filter(name => !/\.light\.(png|gif)$/.test(name));
assert.equal(images.length, darkImages.length * 2, 'Expected one light variant for every dark screenshot.');
for (const name of darkImages) {
  const lightName = name.replace(/(\.(?:png|gif))$/, '.light$1');
  assert.ok(images.includes(lightName), `Missing light screenshot: ${lightName}`);
}
for (const name of images) {
  const output = path.join('/output', name);
  await copyFile(path.join('/captures', name), `${output}.tmp`);
  await rename(`${output}.tmp`, output);
  console.log(`Saved ${name}`);
}
