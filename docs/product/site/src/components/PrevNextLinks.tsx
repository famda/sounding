'use client'

import Link from 'next/link'
import { usePathname } from 'next/navigation'
import clsx from 'clsx'

import {
  type NavLink,
  navigation,
  normalizePathname,
  routeHref,
} from '@/lib/navigation'

function ArrowIcon(props: React.ComponentPropsWithoutRef<'svg'>) {
  return (
    <svg viewBox="0 0 16 16" aria-hidden="true" {...props}>
      <path d="m9.182 13.423-1.17-1.16 3.505-3.505H3V7.065h8.517l-3.506-3.5L9.181 2.4l5.512 5.511-5.511 5.512Z" />
    </svg>
  )
}

function PageLink({
  title,
  href,
  dir = 'next',
  ...props
}: Omit<React.ComponentPropsWithoutRef<'div'>, 'dir' | 'title'> & {
  title: string
  href: string
  dir?: 'previous' | 'next'
}) {
  return (
    <div {...props}>
      <dt className="font-display text-sm font-medium text-slate-900 dark:text-white">
        {dir === 'next' ? 'Next' : 'Previous'}
      </dt>
      <dd className="mt-1">
        <Link
          href={routeHref(href)}
          className={clsx(
            'flex items-center gap-x-1 text-base font-semibold text-slate-500 hover:text-slate-600 dark:text-slate-400 dark:hover:text-slate-300',
            dir === 'previous' && 'flex-row-reverse',
          )}
        >
          {title}
          <ArrowIcon
            className={clsx(
              'h-4 w-4 flex-none fill-current',
              dir === 'previous' && '-scale-x-100',
            )}
          />
        </Link>
      </dd>
    </div>
  )
}

export function PrevNextLinks() {
  let pathname = normalizePathname(usePathname())
  let allLinks = navigation.flatMap((section) => section.links)
  let current = allLinks.find(
    (link) => normalizePathname(link.href) === pathname,
  )
  let mainLinks = allLinks.filter((link) => !link.optional)
  let previousPage: NavLink | null = null
  let nextPage: NavLink | null = null

  if (current?.optional) {
    previousPage = mainLinks[0] ?? null
    nextPage = mainLinks[1] ?? null
  } else {
    let linkIndex = mainLinks.findIndex(
      (link) => normalizePathname(link.href) === pathname,
    )
    previousPage = linkIndex > 0 ? mainLinks[linkIndex - 1] : null
    nextPage =
      linkIndex > -1 && linkIndex < mainLinks.length - 1
        ? mainLinks[linkIndex + 1]
        : null
  }

  if (!nextPage && !previousPage) {
    return null
  }

  return (
    <dl className="mt-12 flex border-t border-slate-200 pt-6 dark:border-slate-800">
      {previousPage && (
        <PageLink
          dir="previous"
          title={previousPage.title}
          href={previousPage.href}
        />
      )}
      {nextPage && (
        <PageLink
          className="ml-auto text-right"
          title={nextPage.title}
          href={nextPage.href}
        />
      )}
    </dl>
  )
}
