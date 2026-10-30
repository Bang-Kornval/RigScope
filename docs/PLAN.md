# RigScope: plan (v2, shareable)

A free Windows app that anyone can download. It checks a gaming PC **for competitive FPS**: what to change, what to leave alone, and what's a myth. It goes deepest on **CS2**. A storage map is built in, so players can see what's filling the drive.

**Design rules**
1. **It detects everything and assumes nothing.** No hardcoded drives, users, vendors or paths. Every check first asks "does this apply to this PC?"
2. **It is read-only by design.** It never changes a setting, deletes a file, edits the registry or touches game processes. The only thing it writes is its own data in `%LOCALAPPDATA%\RigScope`. That makes it safe to share and to run alongside anti-cheat.
3. **It is anti-cheat safe.** It doesn't read game memory, inject into games, use kernel drivers or run in the background. It only reads files, the registry, WMI and vendor command-line tools. This works with VAC, FACEIT and Riot Vanguard.
4. **It is honest.** Each recommendation shows its expected impact, the strength of the evidence and its sources. Preferences are labelled as preferences, and myths are listed and explained.
5. **It is private.** No telemetry, no account, no network calls except an optional update check for the advice pack.

## Stack
- **C# / .NET 10 + WPF** with the Fluent theme, which follows Windows light/dark.
- **Published self-contained as a single `RigScope.exe`** (~70 MB), so friends don't need to install .NET. Runs without admin. Checks that need admin show "needs admin", and the user can choose to restart as admin.
- Development needs the **.NET 10 SDK**.
- Distribution: GitHub Releases with a checksum.
  - The exe is unsigned at first, so Windows SmartScreen will show a warning; the README explains this.
  - Code signing can come later through SignPath (free for open source) or Azure Trusted Signing.

## How "dynamic" works
```
Probes (read-only)  ──►  SystemSnapshot (one JSON doc)  ──►  Rules engine  ──►  Findings
                                                        ▲
                                    Advice pack (JSON, versioned, ships in the exe;
                                    optional online update)
```
- **Probes** collect facts:
  - CPU: vendor, model, hybrid P/E cores, AMD X3D/CCDs.
  - GPU(s): vendor and driver (via nvidia-smi on NVIDIA, AMD registry and driver version, Intel Arc).
  - Monitors with each one's refresh rate.
  - RAM speed vs rated speed (XMP/EXPO).
  - BIOS version and age, boot mode, Resizable BAR (where detectable), Memory Integrity, TPM and Secure Boot.
  - Windows build, Game Mode, HAGS, Game DVR.
  - Power plan.
  - Startup apps and running processes from a known-apps catalogue (RGB suites, recorders, VMs, overlays).
  - Network adapter (type, Wi-Fi vs Ethernet, link speed, EEE, known-bad adapter revisions).
  - Installed games and their config files.
- **The advice pack** is a data file, not code. Each entry has: id, title, the games it applies to, a condition on the snapshot, current-value formatting, a recommendation, impact, evidence level, sources with dates, how to change, how to undo, and a risk note. New advice and fixes ship without a new exe. Code is only needed for new probes.
- **Game detection:**
  - Steam: every library listed in `libraryfolders.vdf`, plus `appmanifest` files (with last played).
  - Riot: Valorant.
  - Battle.net: Overwatch 2.
  - EA: Apex.
  - Epic: Fortnite.
  - Ubisoft: Rainbow Six Siege.
- **Game profiles:**
  - **Deep (v1):** CS2. Reads `cs2_video.txt`, the `*.vcfg` convars, launch options and autoexec, all across every Steam account on the PC.
  - **Basic (v1):** Valorant, Apex Legends, Overwatch 2, Fortnite, Rainbow Six Siege, Marvel Rivals. Covers detection, shared PC checks and a few game-specific notes (Reflex/Anti-Lag availability, fullscreen mode, config location).
  - **Later:** deeper config parsing for Valorant and Apex.

## Screens
1. **Overview:** a hardware summary card, a "Your top 5 for competitive FPS" list, and drive usage across **all drives**.
2. **Competitive check:** pick a game (CS2 selected by default if installed). Findings are grouped into In-game · GPU driver · Windows · Background apps · Network · BIOS/firmware, with filters for *Worth doing*, *Already good*, *Preference* and *Myths*. Each finding opens a detail panel with steps and sources.
3. **Storage:** a drive picker for all fixed drives, a treemap you can click into, a ranked list, largest files, file types, changes since the last scan, and rule-based cleanup notes (games with last played across all launchers, shader caches for every GPU vendor, Recycle Bin, hiberfil, Temp, leftovers from uninstalled games). Actions are Open in Explorer and Copy path only.
4. **Measure:** how to test a change properly. Covers CapFrameX/PresentMon/FrameView, a benchmark map per game, 3 runs each, and that differences under 3% are noise. It also has a before/after notes log.
5. **Share report:** export the findings as Markdown or HTML to post on Discord or Reddit, or send to a friend. **Redacted by default:** usernames, paths and serial numbers are removed.

## Project layout
```
RigScope/
  src/RigScope.Core/        Probes/, Snapshot/, Rules/ (engine + condition evaluator),
                            Games/ (launcher detection, VDF/vcfg/ini parsers, profiles),
                            Storage/ (scanner, diff, cleanup rules), Report/ (export + redaction)
  src/RigScope.App/         WPF, MVVM, TreemapControl
  advice/                   advice-pack.json + schema; sources.md
  tests/RigScope.Core.Tests/
    snapshots/              fake PCs: intel-nvidia (this PC), amd-x3d-amd, intel-hybrid-nvidia-laptop,
                            wifi-only, no-games, multi-steam-library
```
Every rule is tested against those fake PCs, so "dynamic" is actually verified, not just this machine.

## Phases (each ends with something runnable)
0. **Setup:** install the SDK, create the git repo and solution, CI (GitHub Actions: build + tests), make sure an empty window runs.
1. **Probes + snapshot:** collect a snapshot JSON from this PC. Build the fake-PC fixtures. Add "Export snapshot" for bug reports.
2. **Rules engine + advice pack:** port the research into about 40 entries. Tests run each rule against each fake PC.
3. **Game detection + CS2 deep profile,** and basic profiles for the other 6 games.
4. **Storage:** multi-drive parallel scanner, treemap, history and diff, cleanup rules. The scanner is cross-checked against the PowerShell scan on this PC (within 1%).
5. **UI polish + share report,** with redaction tests.
6. **Release:** single-file publish, README (what it does and doesn't do, the SmartScreen note), a license, and a v0.1 GitHub release.

## Not in v1
- One-click "apply" with undo.
- Live FPS overlay.
- In-app benchmarking.
- AMD driver settings: AMD has no public read API comparable to NVIDIA's, so v1 explains them instead of reading them.
- Languages other than English.
