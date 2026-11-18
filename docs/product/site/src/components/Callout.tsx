'use client'

import clsx from 'clsx'
import { createContext, useContext } from 'react'

const CalloutContext = createContext(false)

export function useInCallout() {
  return useContext(CalloutContext)
}

const styles = {
  note: {
    container:
      'border-sky-200 border-l-sky-500 bg-sky-50/70 dark:border-sky-400/15 dark:border-l-sky-400 dark:bg-sky-400/5',
    accent: 'text-sky-600 dark:text-sky-400',
  },
  warning: {
    container:
      'border-amber-200 border-l-amber-500 bg-amber-50/70 dark:border-amber-400/15 dark:border-l-amber-400 dark:bg-amber-400/5',
    accent: 'text-amber-600 dark:text-amber-400',
  },
}

const icons = {
  note: (
    <>
      <circle cx="12" cy="12" r="9" />
      <path d="M12 11v5M12 8h.01" />
    </>
  ),
  warning: (
    <>
      <path d="M10.3 4.2 2.6 17.5A2 2 0 0 0 4.3 20.5h15.4a2 2 0 0 0 1.7-3L13.7 4.2a2 2 0 0 0-3.4 0Z" />
      <path d="M12 9.5v4M12 17h.01" />
    </>
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
  return (
    <CalloutContext.Provider value={true}>
      <div
        role="note"
        className={clsx('my-8 rounded-xl border border-l-4 px-5 py-4', styles[type].container)}
      >
        <div className={clsx('not-prose flex items-center gap-2', styles[type].accent)}>
          <svg
            aria-hidden="true"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            className="h-5 w-5 flex-none"
          >
            {icons[type]}
          </svg>
          <p className="m-0 font-display text-sm font-semibold tracking-wide">{title}</p>
        </div>
        <div className="prose prose-slate mt-2 max-w-none text-[0.9375rem] dark:prose-invert dark:text-slate-300 dark:prose-strong:text-white [&>:first-child]:mt-0 [&>:last-child]:mb-0">
          {children}
        </div>
      </div>
    </CalloutContext.Provider>
  )
}
