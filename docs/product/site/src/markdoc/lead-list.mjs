import Markdoc from '@markdoc/markdoc'

const { Tag } = Markdoc

const labelEnd = /[.?:!]$/

function textOf(node) {
  if (typeof node === 'string') return node
  return Tag.isTag(node) ? node.children.map(textOf).join('') : ''
}

function contentOf(item) {
  const [first] = item.children
  return Tag.isTag(first) && first.name === 'p' ? first.children : item.children
}

function isLeadItem(item) {
  const [first] = contentOf(item)
  return Tag.isTag(first) && first.name === 'strong' && labelEnd.test(textOf(first).trim())
}

function trimStart(nodes) {
  let start = 0
  while (start < nodes.length && typeof nodes[start] === 'string' && !nodes[start].trim()) start++
  const rest = nodes.slice(start)
  // On its own line below the label, the description reads as a sentence: "Label: the text" becomes "The text".
  // Code and links keep their case.
  if (typeof rest[0] === 'string') {
    const text = rest[0].trimStart()
    rest[0] = text.charAt(0).toUpperCase() + text.slice(1)
  }
  return rest
}

function toLeadItem(item) {
  const [first, ...others] = item.children
  let label
  let body
  if (Tag.isTag(first) && first.name === 'p') {
    const [strong, ...rest] = first.children
    const text = trimStart(rest)
    label = strong
    body = text.length ? [new Tag('p', first.attributes, text), ...others] : others
  } else {
    label = first
    body = trimStart(others)
  }
  const children = body.length ? [label, new Tag('div', { class: 'lead-body' }, body)] : [label]
  return new Tag('li', item.attributes, children)
}

// A bulleted list whose every item opens with a bold label ("**Label:** text") reads as labelled entries:
// the label sits on its own line above its description. Lists where a bold phrase starts a sentence stay as they are.
export function toLeadList(list) {
  if (list.name !== 'ul' || !list.children.length || !list.children.every(isLeadItem)) return list
  return new Tag('ul', { ...list.attributes, class: 'lead-list' }, list.children.map(toLeadItem))
}
