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
exports the static site, and verifies every generated page, social preview, local link, anchor and asset. The output is written to
`docs/product/site/dist` (not committed).

To rebuild the site from the screenshots already in `assets`, without capturing them again:

```powershell
docker compose -p sounding-docs-site -f docs/product/site/compose.yaml run --build --rm --no-deps build
```

### Screenshot scenarios

The [capture runner](../screenshots/package.json) runs the existing three-host fleet first, then the
[standalone demo spec](../screenshots/specs/standalone-demo.spec.mjs) with its
[own configuration](../screenshots/standalone.config.mjs). Both use the shared dark and light projects.
The second pass seeds `demo-host` with `web` and `client`, preserving the first pass's captures. Its synthetic
HTTP traffic represents one `GET /` every two seconds, matching the tutorial. The spec checks the visible
connection and request before taking `standalone-demo-request.png` and `standalone-demo-request.light.png`.
It also maximizes the host network map, checks that the `client` and `web` cards are visible and packets are
moving, and records six seconds at 25 fps as `standalone-demo-map.gif` and `standalone-demo-map.light.gif`.
All captures are published only after both passes succeed; the site selects the matching theme automatically.

To run just this spec, from the repository root:

```powershell
docker compose -p sounding-docs -f docs/product/screenshots/compose.yaml run --build --rm --entrypoint sh capture -c "npx playwright test --config standalone.config.mjs && node publish.mjs"
```

This starts the isolated documentation dependencies. Afterwards, remove them with
`docker compose -p sounding-docs -f docs/product/screenshots/compose.yaml down --remove-orphans`.

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
and the link checks, and `DOCS_SITE_ORIGIN` (default `https://famda.github.io`) sets the public origin for metadata
and the origin the link checks treat as local.

## Social previews

Every page exports Open Graph and X card metadata with its own title, description and public URL. All pages share
[social-preview.png](../assets/social-preview.png), a 1200 x 630 PNG of the homepage hero with Sounding branding,
the headline and the dashboard. It was captured once with `playwright-cli` against the running documentation
preview; it is a maintained asset, not part of automated screenshot generation. Browser-only layout adjustments
removed navigation, buttons and small text without changing the website.

To replace it, take another one-off 1200 x 630 capture and save it at the same path. The site build checks the
exported metadata, image URL, PNG dimensions, alt text and a file size below 5 MiB. Deploy through the Pages
workflow before testing public shares; platforms may cache earlier previews and need a fresh scrape.
