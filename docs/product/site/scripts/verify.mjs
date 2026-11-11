import assert from 'node:assert/strict'
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs'
import path from 'node:path'
import yaml from 'js-yaml'

const output = path.resolve('dist')
const app = path.resolve('src/app')
const basePath = (process.env.NEXT_PUBLIC_BASE_PATH ?? '/sounding').replace(/\/$/, '')
const origin = process.env.DOCS_SITE_ORIGIN ?? 'https://famda.github.io'

function files(directory, extension) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const filename = path.join(directory, entry.name)
    return entry.isDirectory()
      ? files(filename, extension)
      : filename.endsWith(extension)
        ? [filename]
        : []
  })
}

function attributes(html) {
  const results = []
  for (const match of html.matchAll(/\b(id|href|src|srcset)\s*=\s*(["'])(.*?)\2/gi)) {
    results.push([match[1].toLowerCase(), match[3].replaceAll('&amp;', '&')])
  }
  return results
}

function decodeHtml(value) {
  return value
    .replaceAll('&amp;', '&')
    .replaceAll('&quot;', '"')
    .replaceAll('&lt;', '<')
    .replaceAll('&gt;', '>')
    .replace(/&#(?:x27|39);/gi, "'")
}

const documents = files(app, '.md')
const pages = new Map(
  files(output, '.html').map((filename) => {
    const attrs = attributes(readFileSync(filename, 'utf8'))
    const ids = new Set(
      attrs.filter(([name]) => name === 'id').map(([, value]) => value),
    )
    return [filename, { attrs, ids }]
  }),
)

assert.ok(documents.length > 0, 'No documentation routes were generated.')
for (const document of documents) {
  const relative = path.relative(app, document).split(path.sep).join('/')
  const route = relative.replace(/\/page\.md$/, '').replace(/^page\.md$/, '')
  const expected = path.join(output, route, 'index.html')
  assert.ok(existsSync(expected), `Missing generated page for ${document}.`)
  const frontmatter = readFileSync(document, 'utf8').match(/^---\r?\n([\s\S]*?)\r?\n---/)
  const { title } = yaml.load(frontmatter[1])
  const htmlTitle = decodeHtml(
    readFileSync(expected, 'utf8').match(/<title>(.*?)<\/title>/)?.[1] ?? '',
  )
  assert.equal(htmlTitle, `${title} | Sounding`, `Incorrect page title for ${document}.`)
}
assert.ok(existsSync(path.join(output, '404.html')), 'Missing exported 404 page.')
assert.ok(existsSync(path.join(output, '404', 'index.html')), 'Missing exported fallback page.')
assert.ok(existsSync(path.join(output, '_not-found', 'index.html')), 'Missing exported not-found route.')
assert.equal(pages.size, documents.length + 3, 'Unexpected number of exported HTML pages.')
assert.ok(existsSync(path.join(output, 'assets', 'logo.svg')), 'Missing copied documentation assets.')
assert.ok(existsSync(path.join(output, '_next')), 'Missing Next.js static resources.')

let references = 0
for (const [filename, page] of pages) {
  const relativePage = path.relative(output, filename).split(path.sep).join('/')
  const pagePath = relativePage.replace(/(?:^|\/)index\.html$/, '')
  const pageUrl = new URL(`${basePath}/${pagePath}`, origin)

  for (const [attribute, value] of page.attrs) {
    if (!['href', 'src', 'srcset'].includes(attribute)) continue
    const urls =
      attribute === 'srcset'
        ? value.split(',').map((entry) => entry.trim().split(/\s+/)[0])
        : [value]

    for (const reference of urls) {
      if (!reference || reference.startsWith('data:')) continue
      const url = new URL(reference, pageUrl)
      if (url.origin !== origin || !['http:', 'https:'].includes(url.protocol)) continue
      references++
      assert.ok(
        url.pathname === basePath || url.pathname.startsWith(`${basePath}/`),
        `Local link leaves the GitHub Pages base path: ${reference} in ${filename}`,
      )

      const outputPath = decodeURIComponent(url.pathname.slice(basePath.length)).replace(/^\/+/, '')
      let target = path.resolve(output, outputPath)
      assert.ok(
        target === output || target.startsWith(`${output}${path.sep}`),
        `Link leaves the output directory: ${reference}`,
      )
      assert.ok(existsSync(target), `Missing generated link or asset: ${reference} in ${filename}`)
      if (statSync(target).isDirectory()) target = path.join(target, 'index.html')
      if (!existsSync(target) && !path.extname(target)) {
        target = path.join(target, 'index.html')
      }
      assert.ok(existsSync(target), `Missing generated page: ${reference} in ${filename}`)

      if (url.hash && pages.has(target)) {
        assert.ok(
          pages.get(target).ids.has(decodeURIComponent(url.hash.slice(1))),
          `Missing generated anchor: ${reference} in ${filename}`,
        )
      }
    }
  }
}

console.log(`Verified ${documents.length} documentation pages and ${references} local links and assets.`)
