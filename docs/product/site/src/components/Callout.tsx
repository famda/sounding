'use client'

import clsx from 'clsx'
import { createContext, useContext } from 'react'

import { Icon } from '@/components/Icon'

const CalloutContext = createContext(false)

export function useInCallout() {
  return useContext(CalloutContext)
}

const styles = {
  note: {
    container:
      'bg-sky-50 ring-1 ring-sky-200/80 dark:bg-slate-800/60 dark:ring-slate-300/10',
    title: 'text-sky-900 dark:text-sky-400',
    body: 'text-sky-800 prose-a:text-sky-900 prose-code:text-sky-900 dark:text-slate-300 dark:prose-a:text-sky-300 dark:prose-code:text-slate-100 dark:prose-strong:text-slate-100',
  },
  warning: {
    container:
      'bg-amber-50 ring-1 ring-amber-200/80 dark:bg-slate-800/60 dark:ring-slate-300/10',
    title: 'text-amber-900 dark:text-amber-500',
    body: 'text-amber-800 prose-a:text-amber-900 prose-code:text-amber-900 dark:text-slate-300 dark:prose-a:text-amber-300 dark:prose-code:text-slate-100 dark:prose-strong:text-slate-100',
  },
}

const icons = {
  note: (props: { className?: string }) => <Icon icon="lightbulb" {...props} />,
  warning: (props: { className?: string }) => (
    <Icon icon="warning" color="amber" {...props} />
  ),
}

export function Callout({
  title,
  children,
  type = 'note',
}: {
  title: string
  children: React.ReactNode
  type?: keyof typeof styles
}) {
  let IconComponent = icons[type]

  return (
    <CalloutContext.Provider value={true}>
      <div className={clsx('my-8 rounded-2xl p-5 sm:p-6', styles[type].container)}>
        <div className="flex items-center gap-3">
          <IconComponent className="h-6 w-6 flex-none" />
          <p
            className={clsx('m-0 not-prose font-display text-lg font-medium', styles[type].title)}
          >
            {title}
          </p>
        </div>
        <div
          className={clsx(
            'prose mt-3 max-w-none dark:prose-invert [&>:first-child]:mt-0 [&>:last-child]:mb-0',
            styles[type].body,
          )}
        >
          {children}
        </div>
      </div>
    </CalloutContext.Provider>
  )
}
