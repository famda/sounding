'use client'

import clsx from 'clsx'
import { Children, cloneElement, isValidElement, useEffect, useState, type ReactElement } from 'react'

const storageKey = 'sounding-shell'
const changeEvent = 'sounding-shell-change'

const labels: Record<string, string> = { sh: 'bash', bash: 'bash', powershell: 'PowerShell' }

type FenceElement = ReactElement<{ language?: string; tabs?: React.ReactNode }>

// The same step for each shell: one code block whose header holds a button per shell. The reader's choice applies to
// every such block and is remembered; on Windows, PowerShell is shown first.
export function ShellTabs({ children }: { children: React.ReactNode }) {
  const blocks = Children.toArray(children).filter(isValidElement) as FenceElement[]
  const shells = blocks.map((block) => labels[block.props.language ?? ''] ?? block.props.language ?? 'shell')
  const [active, setActive] = useState(shells[0])

  useEffect(() => {
    let saved: string | null = null
    try {
      saved = localStorage.getItem(storageKey)
    } catch {}
    if (saved && shells.includes(saved)) setActive(saved)
    else if (navigator.userAgent.includes('Windows') && shells.includes('PowerShell')) setActive('PowerShell')

    const follow = (event: Event) => {
      const shell = (event as CustomEvent<string>).detail
      if (shells.includes(shell)) setActive(shell)
    }
    window.addEventListener(changeEvent, follow)
    return () => window.removeEventListener(changeEvent, follow)
    // The shells of a block don't change after it renders.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  function choose(shell: string) {
    try {
      localStorage.setItem(storageKey, shell)
    } catch {}
    window.dispatchEvent(new CustomEvent(changeEvent, { detail: shell }))
  }

  const index = Math.max(0, shells.indexOf(active))
  // Toggle buttons rather than tabs: the block shown is the panel itself, so there is no separate panel to point to.
  const picker = (
    <div
      role="group"
      aria-label="Shell"
      className="-ml-2 flex gap-0.5 rounded-lg bg-slate-200/60 p-0.5 dark:bg-slate-950/50"
    >
      {shells.map((shell, i) => (
        <button
          key={shell}
          type="button"
          aria-pressed={i === index}
          onClick={() => choose(shell)}
          className={clsx(
            'rounded-md px-2.5 py-1 text-xs font-medium transition focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-sky-500',
            i === index
              ? 'bg-white text-slate-900 shadow-sm ring-1 ring-slate-900/5 dark:bg-slate-700 dark:text-white dark:ring-white/10'
              : 'text-slate-500 hover:text-slate-900 dark:text-slate-400 dark:hover:text-white',
          )}
        >
          {shell}
        </button>
      ))}
    </div>
  )

  // One key for whichever block is shown: React updates it in place, so the button just pressed keeps the focus.
  return cloneElement(blocks[index], { key: 'shown', tabs: picker })
}
