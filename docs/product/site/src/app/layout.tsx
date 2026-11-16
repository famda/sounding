import { type Metadata } from 'next'
import localFont from 'next/font/local'
import clsx from 'clsx'

import { Providers } from '@/app/providers'
import { Layout } from '@/components/Layout'
import { pageMetadata } from '@/lib/metadata'
import { basePath } from '@/lib/navigation'

import '@/styles/tailwind.css'

const lexend = localFont({
  src: '../fonts/lexend.woff2',
  display: 'swap',
  variable: '--font-lexend',
})

export const metadata: Metadata = {
  ...pageMetadata(
    'What is Sounding?',
    'Sounding shows what your Docker containers and their hosts are doing, live and for the last 24 hours, in one dashboard.',
    '/',
  ),
  title: {
    template: '%s | Sounding',
    default: 'What is Sounding? | Sounding',
  },
  metadataBase: new URL(process.env.DOCS_SITE_ORIGIN ?? 'https://famda.github.io'),
  icons: {
    icon: `${basePath}/assets/logo.svg`,
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
