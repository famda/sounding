export const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? '/sounding'

export function normalizePathname(pathname: string) {
  const withoutBase = pathname.startsWith(basePath)
    ? pathname.slice(basePath.length)
    : pathname
  return withoutBase.replace(/\/+$/, '') || '/'
}

export function routeHref(pathname: string) {
  const normalized = normalizePathname(pathname)
  return normalized === '/' ? '/' : `${normalized}/`
}

export interface NavLink {
  title: string
  href: string
  optional?: boolean
}

export const navigation: Array<{ title: string; links: Array<NavLink> }> = [
  {
    title: 'Get started',
    links: [
      { title: 'What is Sounding?', href: '/' },
      {
        title: 'Getting started',
        href: '/get-started/getting-started',
      },
      {
        title: 'Monitor more containers and hosts',
        href: '/get-started/monitor-more-containers',
      },
      {
        title: 'Try a standalone demo',
        href: '/get-started/full-demo',
        optional: true,
      },
    ],
  },
  {
    title: 'Dashboard',
    links: [
      {
        title: 'Find your way around',
        href: '/dashboard/find-your-way-around',
      },
      { title: 'Investigate CPU', href: '/dashboard/investigate-cpu' },
      {
        title: 'Investigate memory',
        href: '/dashboard/investigate-memory',
      },
      { title: 'Investigate disk', href: '/dashboard/investigate-disk' },
      {
        title: 'Investigate network',
        href: '/dashboard/investigate-network',
      },
      { title: 'Capture packets', href: '/dashboard/capture-packets' },
      { title: 'Monitor GPUs', href: '/dashboard/monitor-gpus' },
      {
        title: 'Processes, events, and output',
        href: '/dashboard/processes-events-and-output',
      },
    ],
  },
  {
    title: 'Concepts',
    links: [
      { title: 'How Sounding works', href: '/concepts/how-sounding-works' },
      {
        title: 'Hosts and containers',
        href: '/concepts/hosts-and-containers',
      },
      { title: 'Samples and history', href: '/concepts/samples-and-history' },
      { title: 'Limits and capacity', href: '/concepts/limits-and-capacity' },
    ],
  },
  {
    title: 'Operate',
    links: [
      { title: 'Set up the database', href: '/operate/set-up-the-database' },
      {
        title: 'Show host details',
        href: '/operate/show-host-network-disk-and-gpu',
      },
      {
        title: 'Allow packet inspection and process details',
        href: '/operate/allow-packet-inspection-and-process-details',
      },
      { title: 'Secure Sounding', href: '/operate/secure-sounding' },
      { title: 'Upgrade Sounding', href: '/operate/upgrade-sounding' },
      { title: 'Remove Sounding', href: '/operate/remove-sounding' },
    ],
  },
  {
    title: 'Reference',
    links: [
      { title: 'Configuration', href: '/reference/configuration' },
      { title: 'Events', href: '/reference/events' },
      { title: 'Metrics', href: '/reference/metrics' },
      { title: 'Requirements', href: '/reference/requirements' },
    ],
  },
  {
    title: 'Troubleshooting',
    links: [
      { title: 'Troubleshoot Sounding', href: '/troubleshooting/troubleshoot' },
      { title: 'Frequently asked questions', href: '/troubleshooting/faq' },
    ],
  },
]
