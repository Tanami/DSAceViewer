# DSAceViewer

A standalone Windows desktop viewer for Discovery Freelancer's `DSAce.log`.
Built with C# / .NET 10 and WPF, with no third-party runtime libraries.

## Run

Download **DSAceViewer-win-x64.exe** from the
[latest release](https://github.com/Tanami/DSAceViewer/releases/latest) and run it.
The executable includes the .NET runtime and runs on 64-bit Windows without an
installer. Bundled native runtime components extract to .NET's local cache on
first launch. The source log is opened read-only with write/delete sharing so
Freelancer can continue logging while the viewer runs.

Made by MERZBAU aka ZDF-Atazir.

The viewer first looks for `DSAce.log` in common Discovery/Freelancer folders in
`%APPDATA%` and `%LOCALAPPDATA%` (including versioned Discovery launcher folders).
It then checks Windows' actual Documents folder under `My Games/Freelancer`,
including OneDrive/redirected Documents. If no log exists, it waits for the
AppData default or you can choose a file.

Use **Settings → Choose file → Save**, press **Ctrl+O**, or drop a file
onto the viewer to remember a custom location for subsequent launches. Settings
also offers **Use auto-detected default** and an encoding override. Preferences
are saved to `%APPDATA%/ZDF/DSAceLogViewer/settings.json`. Passing a path on the
command line opens that file for that launch only.

## Controls

- **Following:** starts at the newest entry. Scrolling up pauses automatic
  scrolling while reading continues. Scroll back to the bottom or press
  **Ctrl+End** to follow again. A small bottom-right **last read** timestamp
  reports the most recent successful read. Its tooltip includes source,
  read status, matching counts and any new entries received while reading history.
- **Categories:** all categories start selected. Unselect a category to hide it.
  **All events** toggles between selecting everything and selecting nothing.
  Categories are Chat, OOC, Login, Traffic,
  Combat, System and Other. Category counts describe the retained log before search.
- **Search:** case-insensitive fuzzy matching accepts omitted letters and small
  typos, including adjacent transpositions. Search terms can appear in any order.
  Every term must match. Results retain log order.
- **Invert results:** excludes search matches within the chosen categories.
  An empty search shows all entries in those categories, even with invert on.
- **Export:** exports a snapshot of all currently matching retained entries,
  rather than only the rows on screen. Text `.log` export keeps original decoded
  lines and timestamps; CSV includes line number, timestamp, category and message.
  UTF-8 is used for exports. Exporting over the watched source path is blocked.
- **Copy:** select one or more rows and press Ctrl+C for original log lines.
- **Font:** the log uses the game's Arial Unicode MS chat font, with Arial as
  fallback when that font is not installed. The small top slider adjusts text
  size from 10 to 24 and remembers it across launches.
- **Optional columns:** Settings contains **Show line numbers** and **Show entry
  type**, both off by default. These affect the display; CSV retains its columns.
- **Word wrap:** enabled by default. Messages fit the window and rows expand to
  show the full text. The checkbox in Settings saves your preference.
- **Shortcuts:** Ctrl+O opens a log, Ctrl+F focuses search, Ctrl+E exports the view,
  and Ctrl+End resumes following.

## Log handling and limits

The viewer checks for appended data every 200 ms and handles missing files,
temporary sharing errors, truncation and rotation. Windows file identity and a
small trailing-byte anchor detect replacements, including same-size rewrites.
Incomplete final lines wait for their newline; split multi-byte characters are
buffered. DSAce's joined `Shutdown[timestamp] Initialising` records are separated.
Timestamps are displayed as logged, without guessing a timezone.

The newest **100,000 entries** are retained in memory. The last-read tooltip
reports when older entries have been dropped; exports include the retained
filtered view. Unusually long malformed lines are capped at 256K characters and
labelled as truncated. The grid virtualizes rendered rows. File reading and
filtering run in the background. The viewer cancels superseded searches.

Classification follows observed DSAce OOC, traffic, death, startup and welcome
formats. Login also recognizes explicit login/logout/connect/disconnect notices;
it cannot infer player events absent from the source log. Ordinary player chat
mentioning those words remains Chat. Unrecognized records remain visible in
Other. DSAce logs without channel markers cannot reliably distinguish local,
system and private player chat, so those are kept together under Chat.

Auto encoding honors UTF-8/UTF-16 BOMs, then tests the initial read for UTF-8 and
falls back to Windows-1252. If a file starts with plain ASCII and later contains
legacy accented characters, choose Windows-1252 in Settings and save to reload.

## Build and test

Requires the .NET 10 SDK on Windows:

```powershell
./build.ps1
dotnet run --project tests/DSAceLogViewer.Tests -c Release
```

The build script supports `-Runtime win-arm64` and `-Runtime win-x86` as well.
Each local build places `DSAceLogViewer.exe` in `dist`.

The regression runner uses temporary synthetic logs to verify classification,
fuzzy matching, inversion, exports, saved settings, live appends, split text/BOMs,
file sharing, replacement, truncation, rotation and cancellation. Its WPF test
exercises the actual filter controls and follow state in-process without opening
a desktop window or changing user preferences. Optional arguments:

```powershell
dotnet run --project tests/DSAceLogViewer.Tests -c Release -- --render qa --log 'C:/path/to/DSAce.log'
```

`--render` writes synthetic-data desktop, compact and settings layout PNGs.
`--log` performs an additional read-only format check without copying user chat
into the repository. Build outputs, QA renders and real logs are not tracked.

## Releases

The GitHub Actions workflow tests pushes and pull requests on Windows. Publishing
a version tag such as `v1.0.10` also builds a self-contained Windows x64 executable
and attaches it to a GitHub Release with a SHA-256 checksum file.

Set the version in `src/DSAceLogViewer/DSAceLogViewer.csproj`, commit the change,
then push the matching tag. The workflow checks that the tag and project version
match before it publishes the build.
