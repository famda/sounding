import { createReadStream, statSync } from 'node:fs'
import { createServer } from 'node:http'
import path from 'node:path'

const root = path.resolve('dist')
const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? '/sounding'
const portArgument = process.argv.indexOf('--port')
const port = Number(portArgument >= 0 ? process.argv[portArgument + 1] : 4321)

const contentTypes = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.gif', 'image/gif'],
  ['.html', 'text/html; charset=utf-8'],
  ['.ico', 'image/x-icon'],
  ['.jpeg', 'image/jpeg'],
  ['.jpg', 'image/jpeg'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.png', 'image/png'],
  ['.svg', 'image/svg+xml'],
  ['.txt', 'text/plain; charset=utf-8'],
  ['.webp', 'image/webp'],
  ['.woff2', 'font/woff2'],
])

createServer((request, response) => {
  const requestUrl = new URL(request.url ?? '/', 'http://localhost')
  if (requestUrl.pathname === '/') {
    response.writeHead(302, { Location: `${basePath}/` }).end()
    return
  }

  if (!requestUrl.pathname.startsWith(`${basePath}/`)) {
    response.writeHead(404).end('Not found')
    return
  }

  let pathname
  try {
    pathname = decodeURIComponent(requestUrl.pathname.slice(basePath.length))
  } catch {
    response.writeHead(400).end('Invalid URL')
    return
  }

  const relativePath = pathname.replace(/^\/+/, '')
  let filename = path.resolve(root, relativePath)
  if (filename !== root && !filename.startsWith(`${root}${path.sep}`)) {
    response.writeHead(403).end('Forbidden')
    return
  }
  if (pathname.endsWith('/')) filename = path.join(filename, 'index.html')

  try {
    const info = statSync(filename)
    if (!info.isFile()) {
      response.writeHead(404).end('Not found')
      return
    }

    response.writeHead(200, {
      'Content-Length': info.size,
      'Content-Type': contentTypes.get(path.extname(filename)) ?? 'application/octet-stream',
    })
    createReadStream(filename).pipe(response)
  } catch (error) {
    if (error?.code === 'ENOENT' || error?.code === 'ENOTDIR') {
      response.writeHead(404).end('Not found')
      return
    }
    throw error
  }
}).listen(port, '0.0.0.0', () => {
  console.log(`Documentation preview: http://localhost:${port}${basePath}/`)
})
