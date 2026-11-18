'use client'

import clsx from 'clsx'
import { Fragment, useEffect, useState } from 'react'
import { Highlight } from 'prism-react-renderer'

import { useInCallout } from '@/components/Callout'

const languageLabels: Record<string, string> = {
  sh: 'Terminal',
  bash: 'Terminal',
  shell: 'Terminal',
  powershell: 'PowerShell',
  json: 'JSON',
  yaml: 'YAML',
  yml: 'YAML',
  dockerfile: 'Dockerfile',
  text: 'Text',
  output: 'Output',
}

const shellLanguages = new Set(['sh', 'bash', 'shell', 'powershell'])

// Prism's bundled grammars don't cover these, so their whole-line "#" comments are marked here.
const hashCommentLanguages = new Set([...shellLanguages, 'dockerfile', 'dotenv'])

function CodeLines({ code, language, className }: { code: string; language: string; className: string }) {
  const markComments = hashCommentLanguages.has(language)
  return (
    <Highlight code={code} language={language} theme={{ plain: {}, styles: [] }}>
      {({ className: languageClass, style, tokens, getTokenProps }) => (
        <pre className={clsx(languageClass, 'code-scroll', className)} style={style} tabIndex={0}>
          <code>
            {tokens.map((line, lineIndex) => {
              const text = markComments ? line.map((token) => token.content).join('') : ''
              return (
                <Fragment key={lineIndex}>
                  {markComments && text.trimStart().startsWith('#') ? (
                    <span className="token comment">{text}</span>
                  ) : (
                    line
                      .filter((token) => !token.empty)
                      .map((token, tokenIndex) => <span key={tokenIndex} {...getTokenProps({ token })} />)
                  )}
                  {'\n'}
                </Fragment>
              )
            })}
          </code>
        </pre>
      )}
    </Highlight>
  )
}

function HeaderIcon({ terminal }: { terminal: boolean }) {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 20 20"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-4 w-4 flex-none"
    >
      {terminal ? (
        <path d="m5 7 3 3-3 3M10.5 13H15" />
      ) : (
        <path d="M11.5 2.5H6A1.5 1.5 0 0 0 4.5 4v12A1.5 1.5 0 0 0 6 17.5h8a1.5 1.5 0 0 0 1.5-1.5V6.5l-4-4Zm0 0v4h4" />
      )}
    </svg>
  )
}

export function Fence({
  children,
  language,
  title,
  tabs,
}: {
  children: string
  language?: string
  title?: string
  // Shown in the header in place of the title: ShellTabs' shell picker.
  tabs?: React.ReactNode
}) {
  const inCallout = useInCallout()
  const [copyState, setCopyState] = useState<'idle' | 'copying' | 'copied' | 'error'>('idle')

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

  const code = children.trimEnd()
  const highlightLanguage = language ?? 'text'
  const copied = copyState === 'copied'

  const copyButton = (
    <button
      type="button"
      onClick={copyCode}
      disabled={copyState === 'copying'}
      aria-label={`Copy ${title ?? 'code'}`}
      className={clsx(
        'inline-flex shrink-0 items-center gap-1.5 rounded-md px-2 py-1 text-xs font-medium transition focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-500 disabled:cursor-wait',
        copied
          ? 'text-emerald-600 dark:text-emerald-400'
          : 'text-slate-500 hover:bg-slate-200/70 hover:text-slate-900 dark:text-slate-400 dark:hover:bg-slate-700/60 dark:hover:text-white',
      )}
    >
      <svg
        aria-hidden="true"
        viewBox="0 0 20 20"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinecap="round"
        strokeLinejoin="round"
        className="h-4 w-4"
      >
        {copied ? (
          <path d="m4.5 10.5 3.5 3.5 7.5-8" />
        ) : (
          <>
            <rect x="7" y="7" width="10" height="10" rx="2" />
            <path d="M13 7V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h2" />
          </>
        )}
      </svg>
      <span className={inCallout ? 'sr-only' : undefined}>{copied ? 'Copied' : 'Copy'}</span>
    </button>
  )

  const status = (
    <span role="status" className="sr-only">
      {copied ? 'Code copied to clipboard.' : ''}
    </span>
  )

  const copyError = copyState === 'error' && (
    <p role="alert" className="m-0 px-4 pt-3 text-sm text-rose-600 dark:text-rose-300">
      Could not copy. Select the code and copy it manually.
    </p>
  )

  if (inCallout) {
    return (
      <div className="not-prose my-2.5 rounded-lg border border-slate-200 bg-white/80 py-1.5 pr-1.5 pl-3.5 dark:border-slate-700/70 dark:bg-slate-950/90">
        <div className="flex items-center justify-between gap-3">
          <CodeLines
            code={code}
            language={highlightLanguage}
            className="m-0 min-w-0 flex-1 overflow-x-auto font-mono text-xs leading-5"
          />
          {copyButton}
          {status}
        </div>
        {copyError}
      </div>
    )
  }

  return (
    <div className="not-prose my-6 overflow-hidden rounded-xl border border-slate-200 bg-slate-50 dark:border-slate-700/70 dark:bg-slate-950/90">
      <div className="flex items-center justify-between gap-4 border-b border-slate-200 bg-white/70 py-1.5 pr-2 pl-4 dark:border-slate-700/70 dark:bg-slate-800/90">
        {tabs ?? (
          <span className="flex min-w-0 items-center gap-2 text-xs font-medium text-slate-500 dark:text-slate-400">
            <HeaderIcon terminal={!title && shellLanguages.has(highlightLanguage)} />
            <span className={clsx('truncate', title && 'font-mono')}>
              {title ?? languageLabels[highlightLanguage] ?? highlightLanguage}
            </span>
          </span>
        )}
        {copyButton}
        {status}
      </div>
      {copyError}
      <CodeLines
        code={code}
        language={highlightLanguage}
        className="m-0 overflow-x-auto px-4 py-3.5 font-mono text-[13px] leading-6"
      />
    </div>
  )
}
