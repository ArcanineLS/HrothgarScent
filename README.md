<div align="center">

# 👃 HrothgarScent

**_Hrothgar smell you. Hrothgar know who watching._**

A FFXIV [Dalamud](https://github.com/goatcorp/Dalamud) plugin that lists the players around you — and puts an eye on every row that is **targeting you**. The radar and the stalker-detector are the same list.

[![Release](https://img.shields.io/github/v/release/ArcanineLS/HrothgarScent?style=flat-square&color=8a2be2)](https://github.com/ArcanineLS/HrothgarScent/releases/latest)
[![License](https://img.shields.io/badge/license-AGPL--3.0--or--later-blue?style=flat-square)](LICENSE.md)
[![Sibling of HrothgarMakeCoin](https://img.shields.io/badge/sibling%20of-HrothgarMakeCoin-ad8af5?style=flat-square)](https://github.com/ArcanineLS/HrothgarMakeCoin)

</div>

## ✨ Features

- **One list, two jobs.** Every nearby player, with a 👁 column showing who is targeting you right now.
- **Watchers first.** Sort watchers to the top and keep them there, on top of whatever column you sorted by.
- **Watcher history.** Who watched you, how many times, and when — kept after they walk away.
- **Alerts** in chat and/or sound when someone *new* targets you, with a cooldown so a crowd can't spam it.
- **Filters** — search by name, max distance, hide self/party/friends/dead/AFK, plus a per-player ignore list.
- **Highlighting** for friends, party, watchers and your own FC, all recolourable.
- **Server info bar** entry with the nearby count and watcher count. Click to open.
- **HUD mode** — no title bar, locked, adjustable opacity, optional click-through.
- **Hides itself in PvP**, always.

## 📦 Installation

1. In-game, open **Dalamud Settings** (`/xlsettings`) → **Experimental**.
2. Under **Custom Plugin Repositories**, add:

   ```text
   https://raw.githubusercontent.com/ArcanineLS/DalamudPluginRepo/master/pluginmaster.json
   ```

3. Click **+**, then **Save**.
4. Open the **Plugin Installer** (`/xlplugins`), search for **HrothgarScent**, and install.

Open it with `/hscent`.

## 👁 Watching you

Each row's eye column lights up while that player is targeting you. Turn on **Watchers first** and they float to the top of whatever sort you're using — it's a primary key, not a sort mode, so you keep your column sort.

**Hrothgar remember** logs who targeted you, how many times, and when. Entries stay after the player leaves; current watchers are never trimmed away.

Alerts fire **once per person**, not once per glance — someone re-targeting you inside the cooldown stays quiet. This is a deliberate improvement on the count-based trigger in the prior art, which could miss a new watcher entirely if another dropped in the same tick.

> [!NOTE]
> HrothgarScent can only see what your game client has loaded. It is not a radar for the whole zone, and it cannot see anyone the client hasn't told it about. This is an engine limit, not a bug.

The watcher history lives in memory only — it is never written to your config, and it is cleared on logout.

## 🛡️ PvP

The window, the info bar entry and the commands all **hide and refuse in PvP**, and the scanner stops collecting entirely. This is not configurable — it's a competitive-integrity requirement and a condition of Dalamud plugin acceptance.

## ⚙️ Configuration

The config window (`/hscent config`) has a left icon rail: **General**, **Filters**, **Colours**, **Alerts**, **Watchers**, **HUD**. Every setting saves the moment you change it — there's no Apply button.

### Commands

| Command | Description |
| --- | --- |
| `/hrothgarscent` | Toggles the Scent window. |
| `/hscent` | Short alias. |
| `/scent` | Alias. |
| `/hscent config` | Opens the settings window. |
| `/hscent hud` | Toggles HUD mode. Also how you escape click-through. |

### General

| Option | Default | Description |
| --- | --- | --- |
| Open on login | Off | Opens the Scent window automatically when you log in. |
| Hide in combat | Off | Hides the window while you're in combat. |
| Hide in duty | Off | Hides the window while you're bound by duty. |
| Hide in cutscenes | On | Hides the window during cutscenes. |
| Show search bar | On | Shows the name search box in the toolbar. |
| Show watcher history | On | Shows the **Hrothgar remember** section under the player list. |
| Use job abbreviations | On | `WAR` instead of `Warrior`. |
| Show server info bar entry | On | Adds the nearby/watcher count to the server info bar. |
| Rescan interval | 250 ms | How often Hrothgar sniffs. Lower is snappier and costs more CPU; below ~100 ms buys nothing. Floored at 50 ms. Double-click the slider to type an exact value. |

### Filters

| Option | Default | Description |
| --- | --- | --- |
| Hide self | On | Leaves you out of your own list. |
| Hide party members | Off | |
| Hide friends | Off | |
| Hide dead | Off | |
| Hide AFK | Off | Hides players flagged AFK. |
| Hide low level | On | Hides level 3 and below — mostly new characters in starting cities. |
| Max distance | 0 (unlimited) | In yalms. `0` shows everyone the client knows about. |
| Max players shown | 100 | `0` = unlimited. Truncation always keeps the **nearest**, whatever column you sorted by, so a watcher standing next to you can never be sorted off the list. |
| Ignore list | Empty | Right-click a player → **Ignore this player**. Ignored players are hidden and never alert. Keyed by name + home world. |

### Colours

| Option | Default | Description |
| --- | --- | --- |
| Default / Friend / Party / Same FC / Watcher | White / orange / cyan / gold / red | Name colours by relationship. Same-FC matching requires the same home world too, so a same-named FC elsewhere isn't painted as yours. |
| Highlight watcher rows | On | Tints the whole row of anyone targeting you. |
| Watcher row tint | 0.18 | Tint strength. |
| Use per-job colours | Off (role mode) | Colour job text by role bucket (Tank/Healer/Melee/Ranged/Other), or override any individual job. |

### Alerts

| Option | Default | Description |
| --- | --- | --- |
| Announce in chat | On | Prints a line when someone new targets you. |
| Play a sound | Off | Plays a game chat sound effect. |
| Sound | `<se.1>` | One of the game's 16 chat sound effects. Respects your in-game sound settings. **Test** plays it. |
| Cooldown | 10 s | Minimum gap between alerts. Filters apply *before* the cooldown, so someone you chose not to be alerted about can't burn it. |
| Alert for party members | Off | |
| Alert for friends | Off | |
| Alert for alliance members | Off | |
| Record history while window closed | On | Keeps logging watchers even when the Scent window isn't open. |

### Watchers

| Option | Default | Description |
| --- | --- | --- |
| Keep history | On | Off drops watchers from the log the moment they stop targeting you. |
| Entries to keep | 10 | Oldest are evicted first. Current watchers are never evicted. |
| Show timestamps | On | Today's sightings show a clock time; older ones show a date. |
| Clear history | — | Forgets everyone. |

### HUD

| Option | Default | Description |
| --- | --- | --- |
| HUD mode | Off | Strips the title bar, collapse arrow and scrollbar. |
| Lock position and size | On | The window can't be moved or resized. |
| Click-through | Off | The window ignores the mouse entirely. See the warning below. |
| Opacity | 0.65 | Background transparency in HUD mode. |

### Right-click a row

Target · Focus Target · Examine · Adventurer Plate · Link in chat · Copy name · Search on Lodestone · Ignore this player.

> [!NOTE]
> There's no "Send Tell" button, on purpose. Dalamud exposes no supported way for a plugin to send chat or set a tell target, and faking it with an unsupported hook is how plugins get people banned. **Link in chat** posts the game's own clickable player link instead — click it and you get the game's real menu, Tell included.

## 🖥️ HUD mode

Strips the title bar, locks the window, and dims it to your chosen opacity.

> [!WARNING]
> **Click-through** makes the Scent window ignore the mouse *entirely*, including any control that would turn it back off. Use `/hscent hud` or the checkbox in the config window to get it back.

## 🛠️ Building from source

Requires the **.NET 10 SDK** and a Dalamud dev environment.

```bash
git clone https://github.com/ArcanineLS/HrothgarScent.git
cd HrothgarScent
dotnet build HrothgarScent/HrothgarScent.csproj -c Release
```

Releases are built and published automatically by [`.github/workflows/create_release.yml`](.github/workflows/create_release.yml) when you push a `v*` tag (e.g. `git tag v1.0.0.0 && git push origin v1.0.0.0`). Bump `<Version>` in `HrothgarScent.csproj` to match the tag first.

## ❤️ Credits / Prior art

HrothgarScent is an original, from-scratch implementation written against the public Dalamud API. It contains no code from any other plugin. It does, however, owe its feature set to two projects that got there first, and whose authors deserve the credit for the ideas:

- **[Wholist](https://github.com/Blooym/Dalamud.Wholist)** by **[Blooym](https://github.com/Blooym)** — the nearby-players list, and the model of surfacing "who is around me" as a first-class panel. Licensed AGPL-3.0.
- **[PeepingTom](https://github.com/thakyZ/PeepingTom)**, maintained by **[thakyZ](https://github.com/thakyZ)**, originally created by **ascclemens (Anna Clemens)** — the targeting-history concept: showing who is currently targeting you, and who recently was. Licensed EUPL-1.2.

Neither project's authors are affiliated with HrothgarScent, and neither has endorsed it. No code, assets, or icons were taken from either; the licenses above are listed to point you at the original works, not because they impose any terms on this one. Any bugs here are ours alone.

Sibling to **[HrothgarMakeCoin](https://github.com/ArcanineLS/HrothgarMakeCoin)**.

## 📄 License

[AGPL-3.0-or-later](LICENSE.md).
