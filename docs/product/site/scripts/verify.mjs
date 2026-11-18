import assert from 'node:assert/strict'
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs'
import path from 'node:path'

import sharp from 'sharp'
import yaml from 'js-yaml'

import { redirects } from './prepare.mjs'

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

function metadata(html) {
  const values = new Map()
  for (const [tag] of html.matchAll(/<meta\b[^>]*>/gi)) {
    const name = tag.match(/\b(?:name|property)=(["'])(.*?)\1/i)?.[2]
    const content = tag.match(/\bcontent=(["'])(.*?)\1/i)?.[2]
    if (name && content !== undefined) values.set(name, decodeHtml(content))
  }
  return values
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
  const { title, description } = yaml.load(frontmatter[1])
  const html = readFileSync(expected, 'utf8')
  const htmlTitle = decodeHtml(
    html.match(/<title>(.*?)<\/title>/)?.[1] ?? '',
  )
  assert.equal(htmlTitle, `${title} | Sounding`, `Incorrect page title for ${document}.`)
  const tags = metadata(html)
  const imageUrl = new URL(`${basePath}/assets/social-preview.png`, origin).href
  for (const [name, value] of [
    ['og:title', htmlTitle],
    ['og:description', description],
    ['og:site_name', 'Sounding'],
    ['og:type', 'website'],
    ['og:url', new URL(`${basePath}/${route}${route ? '/' : ''}`, origin).href],
    ['og:image', imageUrl],
    ['og:image:type', 'image/png'],
    ['og:image:width', '1200'],
    ['og:image:height', '630'],
    ['twitter:card', 'summary_large_image'],
    ['twitter:title', htmlTitle],
    ['twitter:description', description],
    ['twitter:image', imageUrl],
  ]) {
    assert.equal(tags.get(name), value, `Incorrect or missing ${name} for ${document}.`)
  }
  assert.ok(tags.get('og:image:alt'), `Missing Open Graph image alt text for ${document}.`)
  assert.equal(tags.get('twitter:image:alt'), tags.get('og:image:alt'),
    `Inconsistent social image alt text for ${document}.`)
}
const socialImage = path.join(output, 'assets', 'social-preview.png')
const { format, width, height } = await sharp(socialImage).metadata()
assert.deepEqual({ format, width, height }, { format: 'png', width: 1200, height: 630 },
  'The social preview must be a 1200x630 PNG.')
assert.ok(statSync(socialImage).size < 5 * 1024 * 1024, 'The social preview must be under 5 MiB.')
assert.ok(existsSync(path.join(output, '404.html')), 'Missing exported 404 page.')
assert.ok(existsSync(path.join(output, '404', 'index.html')), 'Missing exported fallback page.')
assert.ok(existsSync(path.join(output, '_not-found', 'index.html')), 'Missing exported not-found route.')
assert.equal(pages.size, documents.length + 3 + Object.keys(redirects).length, 'Unexpected number of exported HTML pages.')
for (const [from, destination] of Object.entries(redirects)) {
  const page = path.join(output, from, 'index.html')
  assert.ok(existsSync(page), `Missing redirect page for ${from}.`)
  assert.ok(readFileSync(page, 'utf8').includes(`url=${basePath}${destination}`), `Redirect from ${from} does not lead to ${destination}.`)
}
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

console.log(`Verified ${documents.length} documentation pages, social previews and ${references} local links and assets.`)
