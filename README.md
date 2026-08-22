# TermuxHost

A mobile-first hosting control panel for Termux, built with ASP.NET Core 10 and Tailwind CSS.

## Goals

- Run and manage ASP.NET Core applications on Termux
- Execute Termux shell commands from a web UI
- Mobile-first responsive dashboard
- LAN access
- Auto-start with `termux-services` / `sv-enable`
- Git/GitHub based deployments
- Live logs and application lifecycle controls

## Quick install

```bash
curl -fsSL https://raw.githubusercontent.com/dhhieu113pro/termux-host/main/install.sh | bash
```

After installation, open:

```text
http://<PHONE-IP>:5050
```

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

## Planned

- App create/deploy/start/stop/restart
- Per-app environment variables and ports
- Git clone/pull/deploy
- Live application logs
- File manager
- Interactive PTY terminal
- Deployment history and rollback
- Authentication and HTTPS/reverse proxy
