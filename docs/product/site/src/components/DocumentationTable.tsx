import clsx from 'clsx'

export function DocumentationTable({
  className,
  ...props
}: React.ComponentPropsWithoutRef<'table'>) {
  return (
    <div
      role="region"
      aria-label="Scrollable table"
      tabIndex={0}
      className="overflow-x-auto focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-500"
    >
      <table
        {...props}
        className={clsx(
          className,
          'min-w-full [&_td_code]:whitespace-nowrap [&_td:last-child]:min-w-64 [&_th]:whitespace-nowrap',
        )}
      />
    </div>
  )
}
