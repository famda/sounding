import { nodes as defaultNodes, Tag } from '@markdoc/markdoc'
import { slugifyWithCounter } from '@sindresorhus/slugify'
import yaml from 'js-yaml'

import { DocsLayout } from '@/components/DocsLayout'
import { ZoomableImage } from '@/components/ZoomableImage'
import { DocumentationTable } from '@/components/DocumentationTable'
import { Fence } from '@/components/Fence'
import { toLeadList } from '@/markdoc/lead-list.mjs'

let documentSlugifyMap = new Map()

const siteOrigin = process.env.DOCS_SITE_ORIGIN ?? 'https://famda.github.io'

function isExternal(href) {
  return typeof href === 'string' && /^https?:\/\//i.test(href) && !href.startsWith(`${siteOrigin}/`)
}

const nodes = {
  table: {
    ...defaultNodes.table,
    render: DocumentationTable,
  },
  image: {
    ...defaultNodes.image,
    render: ZoomableImage,
  },
  document: {
    ...defaultNodes.document,
    render: DocsLayout,
    transform(node, config) {
      documentSlugifyMap.set(config, slugifyWithCounter())

      return new Tag(
        this.render,
        {
          frontmatter: yaml.load(node.attributes.frontmatter),
          nodes: node.children,
        },
        node.transformChildren(config),
      )
    },
  },
  heading: {
    ...defaultNodes.heading,
    transform(node, config) {
      let slugify = documentSlugifyMap.get(config)
      let attributes = node.transformAttributes(config)
      let children = node.transformChildren(config)
      let text = children.filter((child) => typeof child === 'string').join(' ')
      let id = attributes.id ?? slugify(text)

      return new Tag(
        `h${node.attributes.level}`,
        { ...attributes, id },
        children,
      )
    },
  },
  th: {
    ...defaultNodes.th,
    attributes: {
      ...defaultNodes.th.attributes,
      scope: {
        type: String,
        default: 'col',
      },
    },
  },
  link: {
    ...defaultNodes.link,
    transform(node, config) {
      const attributes = node.transformAttributes(config)
      const children = node.transformChildren(config)
      if (!isExternal(attributes.href)) return new Tag('a', attributes, children)
      // Readers keep the documentation open while they follow a link elsewhere.
      return new Tag('a', { ...attributes, target: '_blank', rel: 'noopener noreferrer' }, [
        ...children,
        new Tag('span', { class: 'sr-only' }, [' (opens in a new tab)']),
      ])
    },
  },
  list: {
    ...defaultNodes.list,
    transform(node, config) {
      return toLeadList(
        new Tag(
          node.attributes.ordered ? 'ol' : 'ul',
          node.transformAttributes(config),
          node.transformChildren(config),
        ),
      )
    },
  },
  fence: {
    render: Fence,
    attributes: {
      language: {
        type: String,
      },
      title: {
        type: String,
      },
    },
  },
}

export default nodes
