import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { test } from 'node:test'
import ts from 'typescript'

test('the self-contained config enables strict Next.js TypeScript checking', () => {
  const filename = path.resolve('tsconfig.json')
  const { config, error } = ts.readConfigFile(filename, ts.sys.readFile)
  assert.equal(error, undefined, `Could not read ${filename}`)

  const parsed = ts.parseJsonConfigFileContent(config, ts.sys, path.dirname(filename))
  assert.deepEqual(parsed.errors, [])
  assert.equal(parsed.options.strict, true)
  assert.equal(parsed.options.noEmit, true)
  assert.equal(parsed.options.moduleResolution, ts.ModuleResolutionKind.Bundler)
  assert.ok(readFileSync(filename, 'utf8').includes('"name": "next"'))
})
