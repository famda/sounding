import { type Node } from '@markdoc/markdoc'

import { DocsHeader } from '@/components/DocsHeader'
import { PrevNextLinks } from '@/components/PrevNextLinks'
import { Prose } from '@/components/Prose'
import { TableOfContents } from '@/components/TableOfContents'
import { collectSections } from '@/lib/sections'

export function DocsLayout({
  children,
  frontmatter: { title },
  nodes,
}: {
  children: React.ReactNode
  frontmatter: { title?: string }
  nodes: Array<Node>
}) {
  let tableOfContents = collectSections(nodes)

  return (
    <>
      <div className="min-w-0 flex-auto px-5 py-12 lg:max-w-none lg:pr-4 lg:pl-6 xl:px-10">
        <article>
          <DocsHeader title={title} />
          <Prose>{children}</Prose>
        </article>
        <PrevNextLinks />
        <p className="mt-12 text-xs text-slate-400 dark:text-slate-500">
          Product names and logos are trademarks of their respective owners. Sounding is not affiliated with or
          endorsed by them.
        </p>
      </div>
      <TableOfContents tableOfContents={tableOfContents} />
    </>
  )
}
