'use client'

import { Fragment, useEffect, useState } from 'react'
import { Highlight } from 'prism-react-renderer'

import { useInCallout } from '@/components/Callout'

export function Fence({
  children,
  language,
  title,
}: {
  children: string
  language?: string
  title?: string
}) {
  const inCallout = useInCallout()
  const [copyState, setCopyState] = useState<
    'idle' | 'copying' | 'copied' | 'error'
  >('idle')

  useEffect(() => {
    if (copyState !== 'copied') return
    const timeout = setTimeout(() => setCopyState('idle'), 2000)
    return () => clearTimeout(timeout)
  }, [copyState])

  async function copyCode() {
    setCopyState('copying')
    try {
      await navigator.clipboard.writeText(children)
      setCopyState('copied')
    } catch (error) {
      console.error('Could not copy the code block to the clipboard.', error)
      setCopyState('error')
    }
  }

  const copyButton = (
    <button
      type="button"
      onClick={copyCode}
      disabled={copyState === 'copying'}
      aria-label={`Copy ${title ?? 'code'}`}
      className="inline-flex shrink-0 items-center gap-1.5 rounded-md bg-slate-700/70 px-2.5 py-1.5 text-xs font-medium text-slate-200 transition hover:bg-slate-600/80 hover:text-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-400 disabled:cursor-wait"
    >
      <svg
        aria-hidden="true"
        viewBox="0 0 20 20"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        className="h-4 w-4"
      >
        <rect x="7" y="7" width="10" height="10" rx="2" />
        <path d="M13 7V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h2" />
      </svg>
      {copyState === 'copied' ? 'Copied' : 'Copy'}
    </button>
  )

  if (inCallout) {
    return (
      <div className="not-prose my-2.5 rounded-lg border border-slate-700/80 bg-slate-950 px-3.5 py-2 text-slate-100 dark:border-slate-700/70 dark:bg-slate-950/90">
        <div className="flex items-center justify-between gap-3">
          <Highlight
            code={children.trimEnd()}
            language={language ?? 'text'}
            theme={{ plain: {}, styles: [] }}
          >
            {({ className, style, tokens, getTokenProps }) => (
              <pre
                className={`${className} m-0 min-w-0 flex-1 overflow-x-auto font-mono text-xs leading-5`}
                style={style}
                tabIndex={0}
              >
                <code>
                  {tokens.map((line, lineIndex) => (
                    <Fragment key={lineIndex}>
                      {line
                        .filter((token) => !token.empty)
                        .map((token, tokenIndex) => (
                          <span key={tokenIndex} {...getTokenProps({ token })} />
                        ))}
                      {'\n'}
                    </Fragment>
                  ))}
                </code>
              </pre>
            )}
          </Highlight>
          {copyButton}
          <span role="status" className="sr-only">
            {copyState === 'copied' ? 'Code copied to clipboard.' : ''}
          </span>
        </div>
        {copyState === 'error' && (
          <p role="alert" className="m-0 pt-2 text-xs text-rose-300">
            Could not copy. Select the code and copy it manually.
          </p>
        )}
      </div>
    )
  }

  return (
    <div className="not-prose my-6 overflow-hidden rounded-xl border border-slate-700/80 bg-slate-950 text-slate-100 shadow-lg dark:border-slate-700/70 dark:bg-slate-950/90 dark:shadow-none">
      <div className="flex items-center justify-between gap-4 border-b border-slate-700/80 bg-slate-800/95 px-4 py-2 dark:border-slate-700/70 dark:bg-slate-800/90">
        <span className="min-w-0 truncate font-mono text-xs text-slate-300">
          {title ?? language ?? 'Code'}
        </span>
        {copyButton}
        <span role="status" className="sr-only">
          {copyState === 'copied' ? 'Code copied to clipboard.' : ''}
        </span>
      </div>
      {copyState === 'error' && (
        <p role="alert" className="m-0 bg-slate-950 px-4 pt-3 text-sm text-rose-300 dark:bg-slate-950/90">
          Could not copy. Select the code and copy it manually.
        </p>
      )}
      <Highlight
        code={children.trimEnd()}
        language={language ?? 'text'}
        theme={{ plain: {}, styles: [] }}
      >
        {({ className, style, tokens, getTokenProps }) => (
          <pre
            className={`${className} m-0 overflow-x-auto bg-slate-950 p-4 font-mono text-[13px] leading-6 dark:bg-slate-950/90`}
            style={style}
            tabIndex={0}
          >
            <code>
              {tokens.map((line, lineIndex) => (
                <Fragment key={lineIndex}>
                  {line
                    .filter((token) => !token.empty)
                    .map((token, tokenIndex) => (
                      <span key={tokenIndex} {...getTokenProps({ token })} />
                    ))}
                  {'\n'}
                </Fragment>
              ))}
            </code>
          </pre>
        )}
      </Highlight>
    </div>
  )
}
