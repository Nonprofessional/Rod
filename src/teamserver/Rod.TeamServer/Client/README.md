# Rod operator UI

The React + TypeScript + Vite single-page app operators use to run an
engagement (architecture.md Sec 4.2). It talks to the teamserver's HTTP API
same-origin: the host serves the built bundle from `wwwroot`, and Vite's dev
server proxies API calls to the teamserver in development.

## Develop

1. Start the teamserver (`dotnet run` from `src/teamserver/Rod.TeamServer`);
   the default dev listener is `http://localhost:5080`.
2. Run `npm ci` once, then `npm run dev` in this directory.
3. Open the Vite URL (default `http://localhost:5173`). API calls proxy to
   `http://localhost:5080`; override with `ROD_API_TARGET` when the
   teamserver runs elsewhere.

## Build

`npm run build` type-checks (`tsc -b`) and emits the bundle into
`../wwwroot`, which the teamserver host serves at `/`. `wwwroot` is
gitignored; on a fresh clone, building `Rod.TeamServer` builds the UI first
when Node is available (the `EnsureOperatorUiBundle` MSBuild target).

## Lint

`npm run lint` runs oxlint.
