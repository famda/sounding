import Link from 'next/link'

import { Button } from '@/components/Button'
import { DocumentationImage } from '@/components/DocumentationImage'
import { basePath } from '@/lib/navigation'

export function Hero() {
  return (
    <section className="home-hero relative isolate overflow-hidden">
      <div aria-hidden="true" className="home-hero-grid absolute inset-0" />
      <div
        aria-hidden="true"
        className="absolute -top-48 right-[-8rem] -z-10 h-[34rem] w-[34rem] rounded-full bg-sky-400/15 blur-3xl"
      />
      <div className="relative mx-auto grid max-w-8xl items-center gap-12 px-5 py-14 sm:px-8 sm:py-18 lg:grid-cols-[0.85fr_1.15fr] lg:gap-10 lg:px-12 lg:py-20 xl:gap-16 xl:px-16">
        <div className="max-w-xl">
          <p className="inline-flex items-center gap-2 rounded-full border border-sky-300/20 bg-sky-300/10 px-3 py-1.5 text-xs font-semibold tracking-[0.14em] text-sky-200 uppercase">
            <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />
            Container observability
          </p>
          <h1 className="mt-6 font-display text-4xl leading-[1.08] font-semibold tracking-tight text-white sm:text-5xl xl:text-6xl">
            Every container.
            <span className="block bg-linear-to-r from-sky-200 to-cyan-400 bg-clip-text text-transparent">
              One clear view.
            </span>
          </h1>
          <p className="mt-6 max-w-lg text-lg leading-8 text-slate-300">
            Understand resource pressure, network activity, and host health
            across your fleet—from one live dashboard.
          </p>
          <div className="mt-8 flex flex-wrap items-center gap-3">
            <Button href="/get-started/getting-started/">
              Add to your application
            </Button>
            <Link
              href="/concepts/how-sounding-works/"
              className="inline-flex items-center rounded-full px-4 py-2 text-sm font-semibold text-slate-200 transition hover:bg-white/10 hover:text-white"
            >
              How it works
              <span aria-hidden="true" className="ml-2">
                &rarr;
              </span>
            </Link>
          </div>
          <div className="mt-9 flex flex-wrap gap-x-6 gap-y-2 border-t border-white/10 pt-5 text-sm text-slate-400">
            <span>Live host and container metrics</span>
            <span>Your data stays in your database</span>
          </div>
        </div>

        <div className="relative mx-auto w-full max-w-3xl lg:mx-0">
          <div
            aria-hidden="true"
            className="absolute -inset-5 rounded-[2rem] bg-sky-400/10 blur-2xl"
          />
          <div className="relative overflow-hidden rounded-2xl border border-white/15 bg-slate-900 shadow-2xl shadow-black/40 ring-1 ring-white/10">
            <div
              aria-hidden="true"
              className="flex items-center gap-2 border-b border-white/10 bg-slate-900/90 px-4 py-3"
            >
              <span className="h-2.5 w-2.5 rounded-full bg-red-400" />
              <span className="h-2.5 w-2.5 rounded-full bg-amber-400" />
              <span className="h-2.5 w-2.5 rounded-full bg-emerald-400" />
            </div>
            <DocumentationImage
              src={`${basePath}/assets/overview.gif`}
              alt="Sounding dashboard showing live CPU, memory, GPU, disk, and network metrics across multiple hosts."
              width={1440}
              height={900}
              fetchPriority="high"
              className="h-auto w-full"
            />
          </div>
          <p className="mt-3 text-center text-xs text-slate-400">
            A live look at hosts, containers, and their resources
          </p>
        </div>
      </div>
    </section>
  )
}
