# FFLogs parser bundled here

| | |
|---|---|
| File | `parser-ff.js` |
| parserVersion | 3075 (the constant the Archon host page reports for this build) |
| CDN name | `parser-ff.b61c058222500675.js` |
| Source | `https://assets.rpglogs.cn/js/log-parsers/parser-ff.b61c058222500675.js`, referenced by `https://www.fflogs.com/desktop-client/parser` (same build on the global and CN mirrors) |
| Fetched | 2026-09-13 |
| Form | de-obfuscated copy of the minified CDN file; identical behaviour, verified by replaying a real ACT network log with `tools/fflogs-local/replay.js` |

This is FFLogs' own client-side log parser: the `window.LogParser` class the Archon uploader
drives over `postMessage`. With `metersEnabled` it computes the live-meter rDPS family
(`amount / amountTaken / singleTargetAmountTaken / amountGiven` per actor), which is what
`parser-host.html` reads and hands to the plugin.

It is proprietary, obfuscated code owned by RPGLogs. It ships in this repository and in the
release zip by the maintainer's decision; see the README for the terms-of-service note.

## Updating

1. Sign in to fflogs.com, open `/desktop-client/parser`, and take the `parser-ff.<hash>.js` URL
   from its first `<script src>`. Download it over `parser-ff.js` (minified is fine; the
   de-obfuscation was only for reading).
2. Update the table above and `PARSER_VERSION` in `parser-host.html` (`const parserVersion`
   in the fetched page).
3. Replay a known log and compare against its FFLogs report:
   `node tools/fflogs-local/replay.js <Network_*.log>`
4. `node tools/Test-FflogsHost.js`
