import { type Metadata } from 'next'
import localFont from 'next/font/local'
import clsx from 'clsx'

import { Providers } from '@/app/providers'
import { Layout } from '@/components/Layout'

import '@/styles/tailwind.css'

const lexend = localFont({
  src: '../fonts/lexend.woff2',
  display: 'swap',
  variable: '--font-lexend',
})

export const metadata: Metadata = {
  title: {
    template: '%s | Sounding',
    default: 'What is Sounding? | Sounding',
  },
  description:
    'Sounding shows what your Docker containers and their hosts are doing, live and for the last hour, in one dashboard.',
  metadataBase: new URL('https://famda.github.io'),
  icons: {
    icon: `${process.env.NEXT_PUBLIC_BASE_PATH ?? '/sounding'}/assets/logo.svg`,
  },
}

export default function RootLayout({
  children,
}: {
  children: React.ReactNode
}) {
  return (
    <html
      lang="en"
      className={clsx('h-full antialiased', lexend.variable)}
      suppressHydrationWarning
    >
      <body className="flex min-h-full bg-white dark:bg-slate-900">
        <Providers>
          <Layout>{children}</Layout>
        </Providers>
      </body>
    </html>
  )
}
