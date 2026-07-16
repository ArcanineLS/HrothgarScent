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
- **Escalation** — a glance and a thirty-second stare are not the same event, and Hrothgar says which one it is.
- **Marks** — remember the people you care about. A note, a colour, focus and ignore, all on one record. Mark them from the list, or from the game's own right-click menu anywhere it shows a name.
- **Search** that means something — `note:griefer`, `world:sarg`, `job:whm`, `!bob`. No wait, no mode dropdown.
- **Filters** — max distance, hide self/party/friends/dead/AFK.
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

Each row's eye column lights up while that player is targeting you. Turn on **Watchers first** and they float to the top of whatever sort you're using — you keep your column sort.

**Hrothgar remember** logs who targeted you, how many times, and when. Entries stay after the player leaves.

Alerts fire **once per person**, not once per glance.

> [!NOTE]
> HrothgarScent can only see what your game client has loaded. It is not a radar for the whole zone. This is an engine limit, not a bug.

The watcher history lives in memory only. It is never written to disk, and it is cleared on logout.

### 👁 A glance is not a stare

Someone cycling targets holds you for a second. Someone *fixed on you* holds you for a minute. Hrothgar tells them apart:

| | |
| --- | --- |
| They target you | *Hrothgar smell Bob Smith@Sargatanas watching you.* |
| Still on you 15s later | *Hrothgar smell Bob Smith@Sargatanas watching you 15 seconds.* |
| Still on you 45s later | *Hrothgar smell Bob Smith@Sargatanas still watching you. 45 seconds now.* |

Said once each, never repeated. Look away and back and it starts over. Both thresholds are sliders in **Alerts**, and `0` turns either off.

Hover the count in **Hrothgar remember** for how long someone has watched you in total this session.

### 🔔 One cooldown, no starving

Everything shares the one cooldown you set. When two things happen at once, the most urgent wins:

1. Someone has been staring at you
2. Someone new is targeting you
3. Someone you marked walked into range

The rest wait their turn rather than being dropped, and anything still waiting is re-checked first — if they stopped watching or walked off, Hrothgar stays quiet. Set the cooldown to `0` to hear everything.

## 📝 Marks — what Hrothgar writes down

> **Hrothgar writes down exactly the players you pointed at. Everyone else is forgotten when you log out.**

Right-click a player → **Remember this player** for a note, a colour, and the focus/ignore ticks. Or right-click their name **anywhere the game shows it** — friend list, Party Finder, chat log, FC roster — and pick **Hrothgar remember**. That reaches people the Scent window can't see at all.

Untick everything and clear the note, and the record is deleted. **Nothing is ever added just by walking past someone.**

Marks live in `marks.json`, beside your config. Manage them in **Filters → Marks**.

> [!NOTE]
> A note is something **you** wrote, so it's yours to keep. The watcher log is a record of what **other people** did, gathered without their say — so it dies with the session. There is no option to make it durable.

Marks are matched on **name + home world**. They do **not** survive a rename or a world transfer — detecting those needs Lodestone scraping, which Hrothgar doesn't do.

So instead of guessing, Hrothgar **dims** any mark it hasn't seen in a while and gives you a **Renamed?** button. Tell it who they are now and the note, colour and ticks all move across. Nothing is ever deleted by time — the dim is a question, not an expiry — and if you never see that player again, the mark stays exactly where it is.

### The one thing Hrothgar writes down that you didn't type

**Last seen** — when and where Hrothgar last spotted a marked player near you. It's the one stored thing you didn't type yourself, so:

- Only for players you marked.
- One line, overwritten. Never a history.
- Deleted with the record when you unmark them.
- Has its own switch in **Filters → Marks**.

Hover a name in the Marks table to see it.

### What Hrothgar refuses to smell

The game exposes two identifiers that would survive renames. Hrothgar reads **neither**.

**Account IDs** — Dalamud bans collecting these outright, for any purpose.

**Content IDs** — per-character, and *not* banned. Hrothgar still doesn't store one. A stable, cross-name handle on a stranger is what turns a notes file into a dossier. Name+world does everything a notes file needs, so we'd rather lose renames.

## 🛡️ PvP

The window, the info bar entry, the commands and the game-menu **Hrothgar remember** entry all **hide and refuse in PvP**, and the scanner stops collecting entirely. This is not configurable — it's a competitive-integrity requirement and a condition of Dalamud plugin acceptance.

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
| Show search bar | On | Shows the search box in the toolbar. See [Searching](#-searching). |
| Add 'Hrothgar remember' to the game's right-click menu | On | Adds an entry wherever the game shows a player's name — friend list, Party Finder, chat log, FC roster. The only way to mark someone the Scent window can't see. Nothing is written down until you click it. Never appears in PvP. |
| Show watcher history | On | Shows the **Hrothgar remember** section under the player list. |
| Use job abbreviations | On | `WAR` instead of `Warrior`. |
| Show job icons | On | The game's own job icon beside each job name. |
| Show server info bar entry | On | Adds the nearby/watcher count to the server info bar. The count turns red when someone is actually fixated on you, rather than just glancing, and a marker appears when someone you've marked is nearby. Hover it for the details. |
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
| Marks | Empty | Everyone you've focused, ignored, coloured or written a note about. Ignored players are hidden and never alert; ignore beats focus if a player carries both. Keyed by name + home world. See [Marks](#-marks--what-hrothgar-writes-down). |
| Note when and where you last saw them | On | One overwritten line per marked player — never a history, never for anyone unmarked. The only thing stored that you didn't type; see [above](#the-one-thing-hrothgar-writes-down-that-you-didnt-type). |
| Dim a mark unseen for | 30 days | Dims marks Hrothgar hasn't matched in this long, so an orphaned one can be found and fixed with **Renamed?**. Nothing is deleted by time. `0` never dims. |

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
| Say again if they keep watching | On | Adds a line when a watcher is *still* watching. See [A glance is not a stare](#-a-glance-is-not-a-stare). |
| Say 'watching you' | 15 s | How long they must hold you before Hrothgar mentions it again. `0` turns the rung off. Must be above the cooldown or it can never fire. |
| Say 'still watching' | 45 s | The last thing Hrothgar says about one stare. `0` turns the rung off. |
| Alert for party members | Off | |
| Alert for friends | Off | |
| Alert for alliance members | Off | |
| Record history while window closed | On | Keeps logging watchers even when the Scent window isn't open. |

### Watchers

| Option | Default | Description |
| --- | --- | --- |
| Keep watchers after they look away | On | Off drops watchers from the log the moment they stop targeting you. Either way the log is dropped on logout. |
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

Target · Focus Target · Examine · Adventurer Plate · Link in chat · Copy name · Search on Lodestone · Focus / Unfocus · Ignore this player · **Remember this player…**

> [!NOTE]
> There's no "Send Tell" button, on purpose. Dalamud exposes no supported way for a plugin to send chat or set a tell target, and faking it with an unsupported hook is how plugins get people banned. **Link in chat** posts the game's own clickable player link instead — click it and you get the game's real menu, Tell included.

## 🔍 Searching

The box filters on the frame you type. There's no debounce and no mode dropdown — just type.

| | |
| --- | --- |
| `bob smith` | name contains it — spaces and all, so a full name is one search, not two |
| `world:sarg` | home world |
| `fc:free company` | FC tag (values can have spaces too) |
| `job:whm` | job — matches the short *or* long name, whichever you have showing |
| `race:lala` | race |
| `note:griefer` | what **you** wrote about them |
| `note:*` / `note:!` | anyone you have / haven't written a note about |
| `sarg*` · `*sarg` | starts with · ends with |
| `=Bob Smith` | exactly |
| `!bob` · `!world:sarg` | not |

Terms stack: `world:sarg job:whm !bob` is all three at once. It's all one hover away from the `(?)` beside the box, which turns amber if you type a field that doesn't exist.

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
