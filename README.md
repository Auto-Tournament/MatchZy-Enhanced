<div align="center">

  <img src="assets/icon.svg" alt="Matchzy Enhanced" width="140" height="140">

# Matchzy Enhanced

⚡ **Enhanced CS2 match management plugin tailored for tournament automation**

  <p>Enhanced fork of MatchZy tailored for the automatic tournament platform. Adds more events and enables external tools to setup, control, and track matches in real-time.</p>

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![C#](https://img.shields.io/badge/C%23-239120?logo=c-sharp&logoColor=white)](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)

**🔗 [MatchZy Auto Tournament](https://github.com/sivert-io/matchzy-auto-tournament)** • **[CS2 Server Manager](https://github.com/sivert-io/cs2-server-manager)**

</div>

---

## 🚀 Quick Start

**Use [CS2 Server Manager](https://github.com/sivert-io/cs2-server-manager)** for automated setup with MatchZy Enhanced pre-configured:

👉 **[Get Started with CS2 Server Manager](https://github.com/sivert-io/cs2-server-manager)**

### Manual Installation

1. Download the [latest release](https://github.com/sivert-io/MatchZy-Enhanced/releases)
2. Extract to `game/csgo/` directory
3. Restart your server

📖 **[Documentation](https://docs.sivert.io/docs/me)**

---

## ✨ What's Enhanced

Built for **[MatchZy Auto Tournament](https://github.com/sivert-io/matchzy-auto-tournament)** with extended APIs and events for tournament automation:

### Tournament Features
- 📡 **Extended event system** for real-time match tracking
- 🔧 **Match report API** with structured JSON state
- 🔄 **Thread-safe operations** for reliable automation
- 🤖 **Simulation mode** for testing and demos
- 🔁 **Event retry system** with automatic queue and recovery
- 📊 **Server tracking** with health monitoring and status events
- 💾 **Pull API** for direct match stats retrieval

### Queued match loads

`matchzy_loadmatch_url` (and `matchzy match load`) sent while the current series is in postgame does not load right away. The match is queued and loads after the series resets. The reply ends with `queued_match=<id>`, where `<id>` is the config file name without its extension (for `/api/matches/r2m1.json` that is `r2m1`), and the `matchzy_tournament_next_match` convar holds the same id. Sending another URL while one is queued replaces it.

The queued match is loaded only by the automatic reset after a series ends. It is dropped when:

- `css_restart` or `css_endmatch` resets the server. The reply includes `cleared_queued_match=<id>`.
- `matchzy_clear_queued_match` is run. This is server console / RCON only. The reply is `cleared_queued_match=<id>`, or `cleared_queued_match=none` when nothing was queued.

### Multi-server setups sharing one database

Several servers can point at the same MySQL database. That is the point of a shared stats
database, and it now works for persistent config too.

Everything MatchZy persists — the `matchzy_server_config` table and the event retry queue — is
stored against an identity for the server that wrote it, so one server can no longer overwrite
another's values. Before this, whichever server wrote last won, and on restart every server on the
box loaded that one server's `matchzy_server_id`, bootstrap URL and remote log settings.

**The settings that are now per server**, i.e. each server keeps its own value:

- `matchzy_server_id`
- `matchzy_bootstrap_url`, `matchzy_bootstrap_token`
- `matchzy_remote_log_url`, `matchzy_remote_log_header_key`, `matchzy_remote_log_header_value`
- `matchzy_webhook_url`, `matchzy_heartbeat_url`
- `matchzy_report_endpoint`, `matchzy_report_token`, `matchzy_match_token`
- `matchzy_demo_upload_url`
- `matchzy_admins_url`, `matchzy_admins_refresh_seconds`
- `matchzy_chat_prefix`, `matchzy_admin_chat_prefix`
- all `matchzy_warmup_*` settings

The chat prefixes and the warmup settings are usually the same on every server, but they are
scoped the same way as the rest: one shared row for them was only ever an accident of the old
storage, and "last writer wins" is not a useful way to share a value. Set them per server, or
leave the existing shared value in place (see backwards compatibility below).

Genuinely global data — match, map and player stats in `matchzy_stats_*` — is untouched and stays
shared, which is why you point several servers at one database in the first place.

**How a server identifies itself.** The identity is derived from the bind address and the game
port, e.g. `cs2:27015`, `cs2:27025`, `cs2:27035` for three servers on a box named `cs2`. The bind
address is used when it names a real interface; CS2 servers are nearly always started with
`-ip 0.0.0.0`, which identifies nothing, so the machine name is used instead. Nothing has to be
configured for this to work, including before a controller like MAT has ever talked to the server.

Two things change a server's identity: changing its game port, and renaming the box. Neither
loses data — the server simply finds no row of its own and falls back to the shared pre-upgrade
row, and a controller re-pushes its values on the next configure. To pin a name that survives
both, set an explicit scope:

```
# in the server's start arguments (reliable: config.cfg may not have executed yet)
+matchzy_config_scope tournament-eu-3
```

`matchzy_config_scope` can also go in `config.cfg`, but the start-argument form is the one to
prefer. It is never persisted to the database — a value that decides which rows you read cannot
itself be read from those rows.

**Backwards compatibility.** Rows written before this change are kept and treated as shared
fallbacks. A server reads its own row when it has one and the shared row otherwise, and only ever
writes its own row. So one server per database keeps working with no operator action, and a
multi-server setup keeps its current behaviour until each server writes its own values. The
schema migration runs automatically on startup and is a no-op once applied.

If you worked around this by moving servers to per-server SQLite files, you can move them back to
the shared MySQL database.

### Player Features
- 🚀 **Auto-ready system** — Instant match starts (optional)
- ⏸️ **Enhanced pauses** — Team limits, timeouts, dual unpause
- ⏱️ **Side selection timer** — Auto-decide after knife round
- 🏳️ **`.gg` command** — Team vote to forfeit early
- 🚫 **FFW system** — Handle full team disconnects
- ⚡ **Smart demo delays** — 10s restart when demos disabled
- 📺 **Center notifications** — Important events shown center-screen with countdown timers

---

## 📖 Documentation (docs.sivert.io)

- 📋 **[Configuration Guide](https://docs.sivert.io/docs/me/user/configuration)** — All ConVars and examples
- 🎮 **[Commands Reference](https://docs.sivert.io/docs/me/user/commands)** — Player and admin commands
- 🔗 **[Integration Guide](https://docs.sivert.io/docs/me/advanced/integration)** — API endpoints and events
- 📝 **[Changelog](https://docs.sivert.io/docs/me/advanced/changelog)** — Release history

---

## 🔗 Related Projects

- **[MatchZy Auto Tournament](https://github.com/sivert-io/matchzy-auto-tournament)** — Automated tournament platform
- **[CS2 Server Manager](https://github.com/sivert-io/cs2-server-manager)** — Multi-server deployment tool

## 🙏 Credits

**Original MatchZy:** [shobhit-pathak/MatchZy](https://github.com/shobhit-pathak/MatchZy) by WD-  
**Enhanced Fork:** Maintained by [sivert-io](https://github.com/sivert-io) for [MatchZy Auto Tournament](https://github.com/sivert-io/matchzy-auto-tournament)

Built with [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/) • Inspired by [Get5](https://github.com/splewis/get5)

---

<div align="center">

<strong>Made with ❤️ for the CS2 community</strong>

</div>
