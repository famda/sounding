import assert from 'node:assert/strict'
import test from 'node:test'
import Markdoc from '@markdoc/markdoc'

import { transformDocument } from './transform-document.mjs'

const options = {
  relativeDocument: 'get-started/getting-started.md',
  basePath: '/sounding',
}

test('rewrites documentation, image, anchor, and external links', () => {
  const result = transformDocument(
    [
      '---',
      'title: Quickstart',
      'description: Get started.',
      '---',
      '',
      '[Next](../operate/secure-sounding.md#secrets)',
      '![Diagram](../../assets/example.svg)',
      '[Web](https://example.com)',
    ].join('\n'),
    options,
  )

  assert.match(result, /\]\(\/sounding\/operate\/secure-sounding\/#secrets\)/)
  assert.match(result, /!\[Diagram\]\(\/sounding\/assets\/example\.svg\)/)
  assert.match(result, /\]\(https:\/\/example\.com\)/)
  assert.match(result, /title: "Quickstart"/)
})

test('links to the renamed getting-started page and its anchors', () => {
  for (const [relativeDocument, destination] of [
    ['index.md', 'get-started/getting-started.md'],
    ['get-started/monitor-more-containers.md', 'getting-started.md'],
    ['troubleshooting/troubleshoot.md', '../get-started/getting-started.md'],
  ]) {
    const result = transformDocument(
      '---\ntitle: Start\ndescription: Start here.\n---\n\n' +
      `[Get started](${destination}#1-add-the-agent-to-your-image)`,
      { ...options, relativeDocument },
    )
    assert.match(result, /\]\(\/sounding\/get-started\/getting-started\/#1-add-the-agent-to-your-image\)/)
  }
})

test('converts GitHub alert blockquotes to Markdoc callouts', () => {
  const result = transformDocument(
    [
      '---',
      'title: Database',
      'description: Database notes.',
      '---',
      '',
      '> [!WARNING]',
      '> Keep this private.',
    ].join('\n'),
    { ...options, relativeDocument: 'operate/database.md' },
  )

  assert.match(result, /{% callout title="Warning" type="warning" %}/)
  assert.match(result, /Keep this private\.\n\n{% \/callout %}/)
})

test('supports fenced code blocks inside GitHub alert blockquotes', () => {
  const result = transformDocument(
    [
      '---',
      'title: Inspect',
      'description: Inspect an image.',
      '---',
      '',
      '> [!TIP]',
      '> Pull and inspect the image:',
      '>',
      '> ```sh',
      '> docker pull app:tag && docker inspect app:tag',
      '> ```',
    ].join('\n'),
    options,
  )

  const callout = Markdoc.parse(result).children.find((node) => node.type === 'tag' && node.tag === 'callout')
  assert.ok(callout)
  const fence = callout.children.find((node) => node.type === 'fence')
  assert.ok(fence)
  assert.equal(fence.attributes.language, 'sh')
  assert.equal(fence.attributes.content, 'docker pull app:tag && docker inspect app:tag\n')
})

test('rejects documents without required metadata', () => {
  assert.throws(
    () =>
      transformDocument('# Missing frontmatter', {
        ...options,
        relativeDocument: 'missing.md',
      }),
    /Missing frontmatter/,
  )
})

test('preserves code verbatim and exposes fence filenames to Markdoc', () => {
  const code = '[Example](missing.md)\n> [!WARNING]\n  Keep this indentation.\n'
  for (const fence of ['```', '~~~']) {
    const result = transformDocument(
      [
        '---',
        'title: Example',
        'description: Copy this example.',
        '---',
        '',
        `${fence}text title="example.txt"`,
        code + fence,
        '',
        '[Next](../operate/secure-sounding.md)',
      ].join('\n'),
      options,
    )
    const node = Markdoc.parse(result).children.find((node) => node.type === 'fence')
    assert.equal(node.attributes.title, 'example.txt')
    assert.equal(node.attributes.language, 'text')
    assert.equal(node.attributes.content, code)
    assert.match(result, /\]\(\/sounding\/operate\/secure-sounding\/\)/)
  }
})
