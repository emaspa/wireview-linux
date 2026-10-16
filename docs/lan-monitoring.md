# LAN monitoring

Beyond the single local device, the app can monitor and control **WireViews on other machines** over the LAN: one desktop reading several servers, or two PCs watching each other. Back to the [README](../README.md).

## Reading remote devices

In **Settings → Remote hosts**, add a comma-separated list of hosts (`192.168.1.50`, or `host:port` for a custom port; default `9876`). Each remote device appears in the device picker as `lan @ host` alongside local ones, with full live monitoring. A remote host is typically the [wireviewd](https://github.com/emaspa/wireview-hwmon) daemon (Linux/Unraid) or another copy of this app with publishing enabled (Windows).

[WireView Mini](wireview-mini.md) watches the same remote hosts: it raises their fault notifications, lists them in its tray's **Remote hosts** submenu and in the control panel, but a remote host's fault never powers this PC off.

## Publishing this host

**Publish this host on the LAN**, in WireView Mini's control panel and tray menu, opens a listener (default port `9876`, configurable) so other instances can read this machine's device. **Off by default.**

On Linux the `wireviewd` daemon owns publishing when it runs.

The toggle decides whether [WireView Mini](wireview-mini.md) listens on the LAN or on this PC only. With wireviewd running, WireView Mini leaves publishing to the daemon and keeps its own listener on `127.0.0.1`, off the daemon's port. Its port handling is described in [LAN publishing and ports](wireview-mini.md#lan-publishing-and-ports), and its control panel can edit wireviewd's own LAN settings (see [wireviewd settings](wireview-mini.md#wireviewd-settings)).

## Remote control and configuration (authenticated)

Set the same **Network secret** on the publisher and the reader to allow *writing* to a remote device: screen changes, NVM store/reset, clear-faults, and the full configuration editor all work against a remote device. Requests are signed with HMAC-SHA256 (the secret never crosses the wire; replays rejected). Reading a remote needs no secret; only writes do, and failures are surfaced precisely (e.g. *"set the network secret"*, *"rejected by the remote host"*, *"the remote host is unreachable"*).

On a host that publishes through wireviewd, the secret is the daemon's **Secret** in `/etc/wireview/config`. WireView Mini's control panel can copy it to this PC's network secret (**Use the same secret for remote hosts**), so this PC signs its writes to the other hosts with the same one.

> Publishing is independent of reading: you can read remote hosts without exposing your own. There is no TLS; this targets a trusted LAN.

## Ports

| Port | Who listens | Where |
|------|-------------|-------|
| `9876` (configurable) | wireviewd on Linux, or WireView Mini | The LAN when publishing is on |
| `9876` on `127.0.0.1` | WireView Mini with publishing off | This PC only |
| Publish port +1 to +10, else any free port | WireView Mini when the publish port is taken (for example by wireviewd) | This PC only |
