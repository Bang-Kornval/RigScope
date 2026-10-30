# RigScope

Read-only Windows app that checks a gaming PC for competitive FPS (CS2 deepest), explains which tweaks are myths, maps drive storage and shows which peripheral suites can stop running. C# / .NET 10, WPF (Fluent theme), xUnit. Shared publicly via GitHub releases.

## Non-negotiables
- **Read-only.** Probes only read files, registry, WMI, PnP and vendor CLIs (`nvidia-smi`). Never write system settings, never open/write device handles, no admin, no background service, nothing anti-cheat could flag. The only write is scan history in `%LOCALAPPDATA%\RigScope\scans` (and files the user saves via dialogs).
- **Dynamic.** Never hardcode anything to one PC (drives, users, vendors, paths, device ids). Other people run this.
- **Honest advice.** Every rule states impact and evidence level; cite sources that actually load; "unknown/check" beats a guess. No registry hacks or tweak scripts in steps.

## Layout
- `src/RigScope.Core/Probes/` — read-only collectors → `Snapshot/SystemSnapshot.cs`. A probe that fails records `ProbeErrors`, never throws. Probes that depend on others run late (see `ProbeRunner.CaptureAsync`).
- `advice/advice-pack.json` — every recommendation as data (condition on the snapshot → outcomes). Path/condition/template syntax is documented at the top of `src/RigScope.Core/Rules/SnapshotQuery.cs`. Embedded into Core.
- `advice/peripherals.json` — vendor/device catalogue for the Peripherals page (embedded).
- `src/RigScope.Core/Storage/` — scanner, history, diff, cleanup notes. `Report/` — Markdown report with redaction.
- `src/RigScope.App/` — WPF MVVM (CommunityToolkit.Mvvm). Pages registered in `ViewModels/MainViewModel.cs`; views in `Views/`; shared styles/converters in `App.xaml`.
- `src/RigScope.Cli/` — `rigscope snapshot | check [game] | scan [path]`, handy for checking changes without the UI.
- `tests/RigScope.Core.Tests/` — `FakePcs.cs` holds made-up PCs; `Every_rule_evaluates_cleanly_on_every_pc_and_game` runs every rule on every fake PC and game.

## Commands
```
dotnet build -c Release                 # TreatWarningsAsErrors is on
dotnet test --no-build -c Release
dotnet run --project src/RigScope.Cli -- check cs2
dotnet run --project src/RigScope.App
dotnet publish src/RigScope.App -c Release -r win-x64 -o publish   # single 62 MB exe
```
CI (`.github/workflows/ci.yml`) runs build + test on windows-latest. Pushing a `v*` tag runs `release.yml` (exe + SHA-256).

## When changing things
- New advice: add a rule to `advice-pack.json`, add a scenario assertion in `RuleTests.cs`, run tests. Only the last outcome of a rule may lack `when`.
- New probe data: extend `SystemSnapshot`, fill it in a probe, give the fake PCs values (including "missing").
- UI changes: launch the app and look at it; the Fluent theme follows Windows light/dark.
- Match surrounding code style and comment density. Copy is plain and direct, written for gamers, no hype.

## Git
- Commit author email: `44898413+Kornvalles@users.noreply.github.com` (keep the personal email out of history).
- Work on branches and open PRs; don't push tags or change repo visibility unless asked.
