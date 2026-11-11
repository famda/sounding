import clsx from 'clsx'

const logoUrl = `${process.env.NEXT_PUBLIC_BASE_PATH ?? '/sounding'}/assets/logo.svg`

export function Logomark(props: React.ComponentPropsWithoutRef<'img'>) {
  return <img src={logoUrl} alt="" aria-hidden="true" {...props} />
}

export function Logo({
  className,
  ...props
}: React.ComponentPropsWithoutRef<'div'>) {
  return (
    <div
      {...props}
      className={clsx(
        'shrink-0 items-center gap-2 whitespace-nowrap',
        className,
      )}
    >
      <img src={logoUrl} alt="" aria-hidden="true" className="h-8 w-8" />
      <span className="font-display text-lg leading-none font-semibold tracking-tight text-slate-800 dark:text-white">
        Sounding
      </span>
    </div>
  )
}
