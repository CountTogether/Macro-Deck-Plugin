# CountTogether plugin for Macro Deck

Show, change and react to your [CountTogether](https://counttogether.app) counters in
[Macro Deck 3](https://macro-deck.app): increment and decrement counters with a button press, read and set
their values and dates through variables, run actions when a counter changes, see your To Date counters in
Macro Deck's calendar and place counter widgets on your deck that look like the CountTogether home screen
widgets.

This plugin needs Macro Deck 3. It is not a standalone app and does not work with Macro Deck 2.

## Features

### Actions

| Action | What it does |
| --- | --- |
| Increment counter | Increases a regular counter by its own step size, or by an amount you enter (1 to 1,000,000). |
| Decrement counter | Decreases a regular counter the same way. |

Pick the counter from the list of your regular counters. Date counters (From Date and To Date counters)
cannot be changed by these actions. When the change fails, the action reports why: not set up, not
connected, counter gone, no permission for a shared counter, too many requests or a timeout.

### Variables

Every counter becomes a numeric variable named `counttogether_<counter name>`, for example
`{{ vars.counttogether_push_ups }}` for a counter called "Push-ups". The name is built like in the Macro Deck 2
plugin: lowercased, `ä`, `ö`, `ü` and `ß` spelled out as `ae`, `oe`, `ue` and `ss`, other accents dropped and
every other character except letters and digits replaced by `_`. Two counters with the same name get `_2`,
`_3` and so on, in the order they were created.

- Regular counters report their count.
- From Date counters report the days since the start date.
- To Date counters report the days left, which becomes negative once the date has passed.

From Date and To Date counters also get a date variable, `counttogether_<counter name>_date`, holding the
start date or target date as `YYYY-MM-DD`, for example `{{ vars.counttogether_vacation_date }}`. If that name
is already taken by another counter's value variable, the date variable gets the `_2` suffix instead.

`counttogether_connected` is `true` while Macro Deck is connected to CountTogether, for example to grey out
buttons while offline.

Renaming a counter in the app renames its variables, so update templates that use the old name.

#### Writing variables

- The value variable of a regular counter is writable, for example from a Slider widget or a Set Variable
  action. The new value shows right away, but is only sent to CountTogether once it has stayed the same for
  half a second, so dragging a slider sends one change instead of one per step. The plugin changes the
  counter by the difference to its current value, so the counter's step size stays as it is and shared
  counters work without admin rights. A single write can change a counter by at most 1,000,000.
- The date variable is writable with a date in the `YYYY-MM-DD` format. This changes the counter in
  CountTogether and needs admin rights on it. The start date of a From Date counter cannot lie in the future
  and the target date of a To Date counter cannot lie in the past.
- The day values of date counters are read-only, because they follow from the date.

### Calendar

Every To Date counter appears as its own calendar in the **CountTogether** calendar account, with its target
date as an all-day event. Macro Deck's Calendar widget, its Event Starts Soon, Event Started and Event Ended
triggers and the calendar filters therefore work with your countdowns, per counter, next to Google and
Outlook calendars.

### Events

| Event | Fires when | Payload |
| --- | --- | --- |
| Counter value changed | A counter's value changes, from Macro Deck, the app, or another member of a shared counter. Date counters change once a day. | `counterId`, `counterName`, `value`, `previousValue`, `change` |
| Counter deleted | A counter is deleted or you lose access to it. | `counterId`, `counterName` |

Both events can be limited to one counter; leave the counter empty to react to every counter.

### Widgets

Two widget types are modelled on the CountTogether iOS home screen widgets:

- **CountTogether counter** shows the counter name and its value. Date counters show the number of days
  with "days left", "days ago" or "days overdue" (in red), and "Today" on the day itself.
- **CountTogether counter with buttons** adds a minus and a plus button below a regular counter. A failed
  change shows "Change failed" for a few seconds.

Choose the counter in the widget's settings. The value uses the CountTogether blue by default; set an accent
colour or a background colour in the widget's appearance settings, or with Macro Deck's widget appearance
actions. Both widgets work at every tile size and in the light and dark theme, and show a clear hint when no
counter is selected, the integration is not set up, the token was rejected, CountTogether cannot be reached
or the counter no longer exists. While CountTogether cannot be reached, variables report no value instead
of a stale one.

## Setup

1. Install the plugin from the Macro Deck Store.
2. Open the CountTogether app, go to **Settings**, **API Token** and tap **Show API token**.
3. In Macro Deck, set up the **CountTogether** integration and paste the token. The plugin checks it with
   CountTogether before it saves it, and stores it in Macro Deck's encrypted secret store.

If you rotate the token in the app later, Macro Deck shows an issue on the integration that takes you back to
the setup to enter the new one.

### Coming from Macro Deck 2

The Macro Deck 3 migration can take over the API token and your Increment and Decrement buttons from the
Macro Deck 2 CountTogether plugin. Install and enable this plugin before you run the migration, so Macro Deck
can ask it how to convert them. Macro Deck 2 plugin variables are not migrated; the new variables follow the
same naming scheme (`counttogether_<counter name>`), so templates usually keep working. Unlike the Macro
Deck 2 plugin, date counters now get variables too.

## Data and network

The plugin talks only to the CountTogether Public API at `developers.counttogether.app`: HTTPS requests to
read and change counters, and a WebSocket connection that receives counter changes as they happen. It sends
your API token and your machine's time zone (so date counters count days in your local time). It reads the
list of counters every 65 seconds to catch changes that are not pushed, such as a new day for date counters.
Nothing is sent anywhere else, and the token never appears in logs.

The CountTogether Public API allows four requests per second per account. The plugin spaces its own
requests to at most three per second; when more are waiting than it is willing to queue, the action or widget
fails with a "too many requests" message instead of sending requests the API would reject.

## Limitations

- Goals, colours, step sizes and the up-only or down-only mode of a counter are not available through the
  Public API, so widgets show no goal bar and always offer both buttons for regular counters.
- Values are shown as plain numbers without thousands separators.
- Telling a rejected token from an outage relies on the status code in the WebSocket error the .NET runtime
  reports; on a system that localizes that message, a rejected token is reported as "unreachable".

## Development

Requirements: the .NET 10 SDK and the Macro Deck plugin CLI in the version pinned in
`Directory.Packages.props` (`MacroDeckSdkVersion`).

```bash
dotnet tool install --global MacroDeck.Plugin.Cli --version 3.0.0-beta.16
```

Build and test:

```bash
dotnet build -c Release -warnaserror
```

```bash
dotnet test -c Release
```

Run the conformance suite and render the widget previews:

```bash
macrodeck-plugin test --project src/CountTogether.MacroDeckPlugin
```

```bash
macrodeck-plugin preview render --project src/CountTogether.MacroDeckPlugin --cells 1x1 --cells 2x1 --output previews
```

Debug against a running Macro Deck with the **Macro Deck - Real Host** launch profile, or pair it from a
terminal (Developer Mode must be enabled in Macro Deck):

```bash
macrodeck-plugin run --project src/CountTogether.MacroDeckPlugin
```

Build the package the way the release workflow does:

```bash
macrodeck-plugin build --source src/CountTogether.MacroDeckPlugin --output ./artifacts
```

Publishing a GitHub release runs `.github/workflows/release.yaml`, which builds every platform, runs the
conformance suite and uploads the package to the Macro Deck Creator Portal. See [AGENTS.md](AGENTS.md) for the
rules the code follows.

## License

MIT, see [LICENSE](LICENSE). The plugin ships third-party packages under their own licenses, listed in
[THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES).
