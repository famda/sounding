import { basePath } from '@/lib/navigation'

// A file the site publishes beside its pages, such as a sample's zip (scripts/prepare.mjs), as a download button.
export function Download({
  href,
  title,
  description,
}: {
  href: string
  title: string
  description?: string
}) {
  return (
    <div className="not-prose my-8 flex flex-wrap items-center gap-x-6 gap-y-3">
      <a
        href={`${basePath}/${href}`}
        download
        className="inline-flex items-center gap-2 rounded-full bg-sky-300 px-5 py-2.5 text-sm font-semibold text-slate-900 hover:bg-sky-200 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-300/50 active:bg-sky-500"
      >
        <svg aria-hidden="true" viewBox="0 0 20 20" className="h-4 w-4 fill-none stroke-current stroke-2">
          <path strokeLinecap="round" strokeLinejoin="round" d="M10 3v10m0 0-4-4m4 4 4-4M4 16h12" />
        </svg>
        {title}
      </a>
      {description ? <span className="text-sm text-slate-500 dark:text-slate-400">{description}</span> : null}
    </div>
  )
}
