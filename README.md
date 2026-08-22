# TermuxHost

A mobile-first hosting control panel for Termux, built with ASP.NET Core 10 and Tailwind CSS.

## Goals

- Run and manage ASP.NET Core applications on Termux
- Execute Termux shell commands from a web UI
- Mobile-first responsive dashboard
- LAN access
- Auto-start with `termux-services` / `sv-enable`
- Release-package based installation and updates
- Live logs and application lifecycle controls

## Quick install

TermuxHost is installed from the latest GitHub Release package. The phone does not clone or build the TermuxHost repository.

```bash
curl -fsSL https://raw.githubusercontent.com/dhhieu113pro/termux-host/main/install.sh | bash
```

The bootstrap installs the requested Termux tools, including .NET 10 SDK, Git, GitHub CLI, and `termux-services`, then downloads `termux-host-aarch64.zip` from the latest GitHub Release.

After installation, open:

```text
http://<PHONE-IP>:5050
```

Useful service commands:

```bash
sv status termux-host
sv restart termux-host
sv down termux-host
sv up termux-host
```

## Release layout

Installed versions are kept separately:

```text
~/termux-host/
├── releases/
│   ├── v0.1.0/
│   └── v0.2.0/
├── current -> releases/v0.2.0
└── logs/
```

The runit service always starts the version referenced by `~/termux-host/current`, which allows future updates and rollback without overwriting the running installation in place.

## Creating a release

Push a version tag:

```bash
git tag v0.1.0
git push origin v0.1.0
```

GitHub Actions will:

1. Publish the ASP.NET Core application.
2. Create `termux-host-aarch64.zip`.
3. Run a Termux aarch64 container with QEMU.
4. Run the real `install.sh` against that ZIP.
5. Start TermuxHost and verify `http://127.0.0.1:5050` with `curl`.
6. Attach the tested ZIP to the GitHub Release.

## Development

```bash
dotnet restore
dotnet run --urls=http://0.0.0.0:5050
```

## V1

- Dashboard
- System information
- Shell command execution
- Responsive Tailwind UI
- Termux installer
- `runit` service registration
- ARM64 release ZIP packaging and smoke test

## Planned

- In-app update check against GitHub Releases
- One-click update and rollback
- App create/deploy/start/stop/restart
- Per-app environment variables and ports
- Git clone/pull/deploy for hosted applications
- Live application logs
- File manager
- Interactive PTY terminal
- Deployment history
- Authentication and HTTPS/reverse proxy
