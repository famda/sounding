import { cp, mkdir, readdir, rm, writeFile } from 'node:fs/promises'
import path from 'node:path'

const output = 'dist'
const exported = 'out'

await mkdir(output, { recursive: true })
for (const entry of await readdir(output)) {
  await rm(path.join(output, entry), { recursive: true, force: true })
}
await cp(exported, output, { recursive: true })
await writeFile(path.join(output, '.nojekyll'), '')

console.log(`Published static export to ${output}.`)
