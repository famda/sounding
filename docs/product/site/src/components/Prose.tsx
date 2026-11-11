import clsx from 'clsx'

export function Prose<T extends React.ElementType = 'div'>({
  as,
  className,
  ...props
}: React.ComponentPropsWithoutRef<T> & {
  as?: T
}) {
  let Component = as ?? 'div'

  return (
    <Component
      className={clsx(
        className,
        'prose max-w-none prose-slate dark:text-slate-400 dark:prose-invert',
        // headings
        'prose-headings:scroll-mt-28 prose-headings:font-display prose-headings:font-normal lg:prose-headings:scroll-mt-34',
        // lead
        'prose-lead:text-slate-500 dark:prose-lead:text-slate-400',
        // links
        'prose-a:font-semibold prose-a:text-sky-700 prose-a:decoration-sky-400/60 prose-a:decoration-2 prose-a:underline-offset-4 prose-a:transition-colors prose-a:hover:text-sky-500 dark:prose-a:text-sky-300 dark:prose-a:decoration-sky-700 dark:prose-a:hover:text-sky-200',
        'prose-img:border prose-img:border-slate-200 dark:prose-img:border-slate-700 [&_img[src$=svg_i]]:border-0',
        // pre
        'prose-pre:rounded-xl prose-pre:bg-slate-900 prose-pre:shadow-lg dark:prose-pre:bg-slate-800/60 dark:prose-pre:shadow-none dark:prose-pre:ring-1 dark:prose-pre:ring-slate-300/10',
        // hr
        'dark:prose-hr:border-slate-800',
      )}
      {...props}
    />
  )
}
