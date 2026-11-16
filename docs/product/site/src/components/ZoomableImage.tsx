'use client'

import { Dialog, DialogPanel } from '@headlessui/react'
import { useState } from 'react'

import { DocumentationImage } from '@/components/DocumentationImage'

// Screenshots show a whole screen at reading width: selecting one opens it as large as the window allows, over the
// page. Selecting it again, or Escape, closes it.
export function ZoomableImage(props: React.ComponentPropsWithoutRef<'img'>) {
  const [open, setOpen] = useState(false)

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="block w-full cursor-zoom-in"
        aria-label={`Enlarge image: ${props.alt ?? ''}`}
      >
        <DocumentationImage {...props} />
      </button>
      <Dialog open={open} onClose={() => setOpen(false)} className="relative z-50">
        <div className="fixed inset-0 bg-slate-900/80 backdrop-blur-sm" aria-hidden="true" />
        {/* As wide as the window allows; a tall screenshot scrolls rather than shrinking to the window's height. */}
        <div className="fixed inset-0 overflow-y-auto">
          <div className="flex min-h-full items-center justify-center p-4 sm:p-8">
            <DialogPanel
              as="button"
              type="button"
              onClick={() => setOpen(false)}
              className="cursor-zoom-out"
              aria-label="Close image"
            >
              <DocumentationImage
                {...props}
                className="m-0! h-auto w-[calc(100vw-2rem)] max-w-[1440px] sm:w-[calc(100vw-4rem)]"
              />
            </DialogPanel>
          </div>
        </div>
      </Dialog>
    </>
  )
}
