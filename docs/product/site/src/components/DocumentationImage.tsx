/* eslint-disable @next/next/no-img-element -- Documentation assets are published unchanged. */
import clsx from 'clsx'

import { basePath } from '@/lib/navigation'

export function DocumentationImage({
  src,
  className,
  ...props
}: React.ComponentPropsWithoutRef<'img'>) {
  if (
    typeof src !== 'string' ||
    !src.startsWith(`${basePath}/assets/`) ||
    !/\.(png|gif)$/.test(src)
  ) {
    return <img {...props} src={src} className={className} />
  }

  return (
    <>
      <img
        {...props}
        src={src.replace(/(\.(?:png|gif))$/, '.light$1')}
        className={clsx(className, 'dark:hidden')}
      />
      <img
        {...props}
        src={src}
        className={clsx(className, 'hidden dark:block')}
      />
    </>
  )
}
