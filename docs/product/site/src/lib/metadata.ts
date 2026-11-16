import { type Metadata } from 'next'

import { basePath } from '@/lib/navigation'

const socialImage = {
  url: `${basePath}/assets/social-preview.png`,
  width: 1200,
  height: 630,
  type: 'image/png',
  alt: 'Sounding: Every container. One clear view. Live container and host metrics in one dashboard.',
}

export function pageMetadata(
  title: string,
  description: string,
  route: string,
): Metadata {
  const socialTitle = `${title} | Sounding`

  return {
    title,
    description,
    openGraph: {
      title: socialTitle,
      description,
      url: `${basePath}${route}`,
      siteName: 'Sounding',
      type: 'website',
      images: [socialImage],
    },
    twitter: {
      card: 'summary_large_image',
      title: socialTitle,
      description,
      images: [{ url: socialImage.url, alt: socialImage.alt }],
    },
  }
}
