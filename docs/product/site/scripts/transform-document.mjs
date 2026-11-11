import path from 'node:path'
import Markdoc from '@markdoc/markdoc'
import yaml from 'js-yaml'

function routeFor(relativeDocument) {
  const route = relativeDocument
    .replace(/\.md$/, '')
    .replace(/(^|\/)index$/, '')
  return route ? `/${route}/` : '/'
}

function transformDestination(destination, relativeDocument, basePath) {
  if (
    !destination ||
    destination.startsWith('#') ||
    /^[a-z][a-z\d+.-]*:/i.test(destination) ||
    destination.startsWith('//')
  ) {
    return destination
  }

  const [, targetPath, suffix = ''] =
    destination.match(/^([^?#]*)([?#].*)?$/) ?? []
  if (!targetPath) return destination
  const decodedPath = decodeURIComponent(targetPath)
  const resolved = path.posix.normalize(
    path.posix.join(
      'content',
      path.posix.dirname(relativeDocument),
      decodedPath,
    ),
  )
  let urlPath

  if (resolved.startsWith('assets/')) {
    urlPath = `${basePath}/${resolved}`
  } else if (resolved.startsWith('content/') && resolved.endsWith('.md')) {
    urlPath = `${basePath}${routeFor(resolved.slice('content/'.length))}`
  } else if (decodedPath.startsWith('/')) {
    return destination
  } else {
    throw new Error(`Unsupported relative documentation link: ${destination}`)
  }

  return `${urlPath}${suffix}`
}

function transformLinks(body, relativeDocument, basePath) {
  body = body.replace(
    /(!?\[[^\]]*\]\()([^\s)]+)([^)]*\))/g,
    (_, prefix, destination, suffix) =>
      `${prefix}${transformDestination(destination, relativeDocument, basePath)}${suffix}`,
  )
  return body.replace(
    /^(\[[^\]]+\]:\s*)(\S+)(.*)$/gm,
    (_, prefix, destination, suffix) =>
      `${prefix}${transformDestination(destination, relativeDocument, basePath)}${suffix}`,
  )
}

function transformProse(body, relativeDocument, basePath) {
  const alertPattern = /^> \[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\][ \t]*\r?\n((?:>.*(?:\r?\n|$))*)/gim
  let result = ''
  let cursor = 0
  for (const match of body.matchAll(alertPattern)) {
    result += transformLinks(body.slice(cursor, match.index), relativeDocument, basePath)
    const normalizedType = match[1].toLowerCase()
    const type = ['warning', 'caution'].includes(normalizedType)
      ? 'warning'
      : 'note'
    const title =
      normalizedType.charAt(0).toUpperCase() + normalizedType.slice(1)
    const inner = transformBody(
      match[2].replace(/^>[ \t]?/gm, '').trimEnd(),
      relativeDocument,
      basePath,
    ).trimEnd()
    result += `{% callout title=${JSON.stringify(title)} type="${type}" %}\n\n${inner}\n\n{% /callout %}\n`
    cursor = match.index + match[0].length
  }
  return result + transformLinks(body.slice(cursor), relativeDocument, basePath)
}

function transformBody(body, relativeDocument, basePath) {
  const lineOffsets = [0]
  for (const match of body.matchAll(/\n/g)) lineOffsets.push(match.index + 1)

  let transformed = ''
  let cursor = 0
  let blockquoteDepth = 0
  for (const token of new Markdoc.Tokenizer().tokenize(body)) {
    if (token.type === 'blockquote_open') {
      blockquoteDepth++
      continue
    }
    if (token.type === 'blockquote_close') {
      blockquoteDepth--
      continue
    }
    if (blockquoteDepth > 0 || (token.type !== 'fence' && token.type !== 'code_block')) continue
    const start = lineOffsets[token.map[0]]
    const end = lineOffsets[token.map[1]] ?? body.length
    transformed += transformProse(body.slice(cursor, start), relativeDocument, basePath)
    transformed += body.slice(start, end).replace(
      /^([ \t]*(?:`{3,}|~{3,}))(\S+)[ \t]+title=(["'])(.*?)\3[ \t]*(\r?)(?=\n|$)/,
      (_, fence, language, quote, title, carriageReturn) =>
        `${fence}${language} {% title=${JSON.stringify(title)} %}${carriageReturn}`,
    )
    cursor = end
  }
  return transformed + transformProse(body.slice(cursor), relativeDocument, basePath)
}

export function transformDocument(markdown, { relativeDocument, basePath }) {
  const frontmatterMatch = markdown.match(/^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/)
  if (!frontmatterMatch) {
    throw new Error(`Missing frontmatter in ${relativeDocument}`)
  }

  const frontmatter = yaml.load(frontmatterMatch[1])
  if (
    !frontmatter ||
    typeof frontmatter.title !== 'string' ||
    typeof frontmatter.description !== 'string'
  ) {
    throw new Error(`Expected title and description in ${relativeDocument}`)
  }

  const rewrittenFrontmatter = [
    '---',
    `title: ${JSON.stringify(frontmatter.title)}`,
    `description: ${JSON.stringify(frontmatter.description)}`,
    '---',
  ].join('\n')

  const body = transformBody(
    markdown.slice(frontmatterMatch[0].length),
    relativeDocument,
    basePath,
  )

  return `${rewrittenFrontmatter}\n\n${body.trimStart()}`
}
