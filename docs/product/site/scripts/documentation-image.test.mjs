import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import test from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import ts from 'typescript'

const require = createRequire(import.meta.url)
function loadComponent(relativePath) {
  const source = readFileSync(new URL(relativePath, import.meta.url), 'utf8')
  const { outputText } = ts.transpileModule(source, {
    compilerOptions: {
      module: ts.ModuleKind.CommonJS,
      jsx: ts.JsxEmit.ReactJSX,
      esModuleInterop: true,
    },
  })
  const exports = {}
  new Function('require', 'exports', outputText)(
    (specifier) => require(specifier === '@/lib/navigation' ? '../src/lib/navigation.ts' : specifier),
    exports,
  )
  return exports
}

const { DocumentationImage } = loadComponent('../src/components/DocumentationImage.tsx')
const { DocumentationTable } = loadComponent('../src/components/DocumentationTable.tsx')

test('preserves semantic table content inside a keyboard-accessible scroll region', () => {
  const html = renderToStaticMarkup(createElement(DocumentationTable, null,
    createElement('thead', null,
      createElement('tr', null,
        createElement('th', { scope: 'col' }, 'Setting'),
        createElement('th', { scope: 'col' }, 'Default'),
      ),
    ),
    createElement('tbody', null,
      createElement('tr', null,
        createElement('td', null, createElement('code', null, 'SOUNDING_RETENTION_MINUTES')),
        createElement('td', null, createElement('code', null, '1440')),
      ),
    ),
  ))
  assert.match(html, /^<div[^>]*role="region"/)
  assert.match(html, /aria-label="Scrollable table"/)
  assert.match(html, /tabindex="0"/)
  assert.match(html, /class="[^"]*\boverflow-x-auto\b/)
  assert.match(html, /<table\b/)
  assert.ok(html.includes('<th scope="col">Default</th>'))
  assert.ok(html.includes('<code>SOUNDING_RETENTION_MINUTES</code>'))
  assert.ok(html.includes('<code>1440</code>'))
})

test('renders matching light and dark screenshots and animations', () => {
  for (const filename of ['container-cpu.png', 'overview.gif']) {
    const src = `/sounding/assets/${filename}`
    const html = renderToStaticMarkup(createElement(DocumentationImage, {
      src, alt: 'Dashboard', width: 1440, height: 900, className: 'h-auto w-full',
    }))
    const images = [...html.matchAll(/<img\b[^>]*>/g)].map(([image]) => image)
    assert.equal(images.length, 2)
    assert.ok(images[0].includes(`src="${src.replace(/\.(png|gif)$/, '.light.$1')}"`))
    assert.match(images[0], /class="h-auto w-full dark:hidden"/)
    assert.ok(images[1].includes(`src="${src}"`))
    assert.match(images[1], /class="h-auto w-full hidden dark:block"/)
    for (const image of images) {
      assert.match(image, /alt="Dashboard"/)
      assert.match(image, /width="1440"/)
      assert.match(image, /height="900"/)
    }
  }
})

test('leaves diagrams and external images unchanged', () => {
  for (const src of ['/sounding/assets/how-it-works.svg', 'https://example.com/image.png']) {
    const html = renderToStaticMarkup(createElement(DocumentationImage, { src, alt: 'Diagram' }))
    assert.equal([...html.matchAll(/<img\b/g)].length, 1)
    assert.ok(html.includes(`src="${src}"`))
    assert.doesNotMatch(html, /\.light\.|dark:hidden|dark:block/)
  }
})
