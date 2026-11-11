# Documentation Site

A Next.js 16 static export of the [product documentation](../README.md), published to GitHub Pages at
<https://famda.github.io/sounding/>. Node.js and npm run only inside Docker.

## Generate

From the repository root:

```powershell
.\platform.ps1 docs generate
```

This captures fresh screenshots into [assets](../assets/) (see [screenshots](../screenshots/)), then builds the site.
The build copies [content](../content/) and [assets](../assets/) into the app, runs the Markdown transformation tests,
exports the static site, and verifies every generated page, local link, anchor and asset. The output is written to
`docs/product/site/dist` (not committed).

To rebuild the site from the screenshots already in `assets`, without capturing them again:

```powershell
docker compose -p sounding-docs-site -f docs/product/site/compose.yaml run --build --rm --no-deps build
```

## Preview

```powershell
.\platform.ps1 docs start
```

Serves `dist` at <http://localhost:4321/sounding/> (loopback only). `-Port` changes the port, and `-NoBuild` reuses the
preview image.

## Publish

The [Pages workflow](../../../.github/workflows/pages.yml) runs `./platform.ps1 docs generate` on every push to `main`
(or when started by hand) and deploys `dist` to GitHub Pages. The repository's Pages source must be set to
**GitHub Actions** (Settings → Pages).

The site is served from the `/sounding` base path. `NEXT_PUBLIC_BASE_PATH` (default `/sounding`) sets it for the build
and the link checks, and `DOCS_SITE_ORIGIN` (default `https://famda.github.io`) sets the origin the link checks treat
as local.
