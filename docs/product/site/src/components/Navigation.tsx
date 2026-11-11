import { useEffect, useRef } from 'react'
import Link from 'next/link'
import { usePathname } from 'next/navigation'
import clsx from 'clsx'

import { navigation, normalizePathname, routeHref } from '@/lib/navigation'

export function Navigation({
  className,
  onLinkClick,
}: {
  className?: string
  onLinkClick?: React.MouseEventHandler<HTMLAnchorElement>
}) {
  let pathname = normalizePathname(usePathname())
  let activeLinkRef = useRef<HTMLAnchorElement>(null)

  // scrollIntoView would also scroll the window, hiding the top of the page (the hero on the home page).
  useEffect(() => {
    let link = activeLinkRef.current
    let container = link?.closest<HTMLElement>('.overflow-y-auto')
    if (!link || !container) return
    let linkBox = link.getBoundingClientRect()
    let containerBox = container.getBoundingClientRect()
    container.scrollTop +=
      linkBox.top - containerBox.top - (container.clientHeight - linkBox.height) / 2
  }, [pathname])

  return (
    <nav className={clsx('text-sm', className)}>
      <ul role="list" className="space-y-7">
        {navigation.map((section) => (
          <li key={section.title}>
            <h2 className="font-display text-xs font-semibold tracking-wider text-slate-500 uppercase dark:text-slate-400">
              {section.title}
            </h2>
            <ul
              role="list"
              className="mt-3 space-y-2 border-l-2 border-slate-200 dark:border-slate-800"
            >
              {section.links.map((link) => (
                <li key={link.href} className="relative">
                  <Link
                    href={routeHref(link.href)}
                    prefetch={false}
                    onClick={onLinkClick}
                    aria-current={
                      normalizePathname(link.href) === pathname
                        ? 'page'
                        : undefined
                    }
                    ref={
                      normalizePathname(link.href) === pathname
                        ? activeLinkRef
                        : undefined
                    }
                    className={clsx(
                      'block py-1 pl-3.5 leading-5 transition-colors before:pointer-events-none before:absolute before:top-1/2 before:-left-1 before:h-1.5 before:w-1.5 before:-translate-y-1/2 before:rounded-full',
                      normalizePathname(link.href) === pathname
                        ? 'font-semibold text-sky-600 before:bg-sky-500 dark:text-sky-300'
                        : 'text-slate-600 before:hidden hover:text-sky-600 dark:text-slate-400 dark:hover:text-white',
                    )}
                  >
                    {link.title}
                  </Link>
                </li>
              ))}
            </ul>
          </li>
        ))}
      </ul>
    </nav>
  )
}
