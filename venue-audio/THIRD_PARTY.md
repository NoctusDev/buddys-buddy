# Bundled dependencies

- NAudio Core / WASAPI / WinMM 2.2.1 — Mark Heath, MIT.
- Concentus 1.1.7 — Logan Stromberg / Xiph.Org and contributors, BSD-style Opus licenses.
- cloudflared 2026.10.7 Windows amd64 — Cloudflare, Apache-2.0.
  Official release SHA-256: `2493dbc168923bb3e59e5e7716f71deced92992eea9acd6e9d74f84ac7751343`.
- The self-contained relay includes the Microsoft .NET 10 runtime and ASP.NET Core.

The prepared build contains dependency license texts under `Tools/Licenses`.
Cloudflared is downloaded from Cloudflare's official GitHub release and checked against
the release digest. It is not installed as a Windows service and does not update itself.

Quick Tunnel use is subject to Cloudflare's terms and is intended for temporary testing.
