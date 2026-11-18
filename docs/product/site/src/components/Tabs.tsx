'use client'

import clsx from 'clsx'
import { Children, isValidElement, useId, useRef, useState, type ReactElement } from 'react'

type TabElement = ReactElement<{ label: string; children: React.ReactNode }>

export function Tab({ children }: { label: string; children: React.ReactNode }) {
  return <>{children}</>
}

// Alternative instructions for the same step, one tab each. Every panel stays in the page, so search and printing
// still see all of them.
export function Tabs({ children }: { children: React.ReactNode }) {
  const tabs = Children.toArray(children).filter(isValidElement) as TabElement[]
  const [active, setActive] = useState(0)
  const id = useId()
  const buttons = useRef<(HTMLButtonElement | null)[]>([])

  function onKeyDown(event: React.KeyboardEvent) {
    const last = tabs.length - 1
    const next =
      event.key === 'ArrowRight' ? (active === last ? 0 : active + 1)
      : event.key === 'ArrowLeft' ? (active === 0 ? last : active - 1)
      : event.key === 'Home' ? 0
      : event.key === 'End' ? last
      : -1
    if (next < 0) return
    event.preventDefault()
    setActive(next)
    buttons.current[next]?.focus()
  }

  return (
    <div className="my-8">
      <div
        role="tablist"
        onKeyDown={onKeyDown}
        className="scrollbar-hidden flex gap-6 overflow-x-auto border-b border-slate-200 dark:border-slate-800"
      >
        {tabs.map((tab, i) => (
          <button
            key={tab.props.label}
            ref={(button) => {
              buttons.current[i] = button
            }}
            type="button"
            role="tab"
            id={`${id}-tab-${i}`}
            aria-selected={i === active}
            aria-controls={`${id}-panel-${i}`}
            tabIndex={i === active ? 0 : -1}
            onClick={() => setActive(i)}
            className={clsx(
              '-mb-px shrink-0 border-b-2 pb-2.5 text-sm font-medium whitespace-nowrap transition focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-sky-500',
              i === active
                ? 'border-sky-500 text-sky-700 dark:border-sky-400 dark:text-sky-400'
                : 'border-transparent text-slate-500 hover:border-slate-300 hover:text-slate-800 dark:text-slate-400 dark:hover:border-slate-600 dark:hover:text-slate-200',
            )}
          >
            {tab.props.label}
          </button>
        ))}
      </div>
      {tabs.map((tab, i) => (
        <div
          key={tab.props.label}
          role="tabpanel"
          id={`${id}-panel-${i}`}
          aria-labelledby={`${id}-tab-${i}`}
          hidden={i !== active}
          className="pt-1 [&>.not-prose:first-child]:mt-5 [&>:last-child]:mb-0"
        >
          {tab.props.children}
        </div>
      ))}
    </div>
  )
}
