<p align="center"><img src="docs/icon-256.png" width="96" alt=""></p>

# RigScope

A free Windows app that checks a gaming PC **for competitive FPS games**. It tells you what's worth changing, what's already right, and which popular tweaks are myths. It goes deepest on **Counter-Strike 2**, and also covers VALORANT, Apex Legends, Overwatch 2, Fortnite, Rainbow Six Siege and Marvel Rivals. A storage map shows what fills your drives.

**RigScope only reads.** It never changes a setting, deletes a file, edits the registry or touches a running game. It doesn't need admin rights.

## What it checks

| Area | Examples |
|---|---|
| In-game (CS2 reads your config) | Reflex, V-Sync, FPS cap and where it's set, refresh rate CS2 uses, dynamic shadows, MSAA, FSR, Boost Player Contrast, packet-loss buffering, obsolete launch options |
| Display | Monitors running below their max refresh rate, G-SYNC/FreeSync vs. uncapped |
| Graphics driver | Driver age, Low Latency Mode vs. Reflex, Resizable BAR, PCIe slot width |
| Windows | Game Mode, background recording, Memory Integrity, HAGS, power plan |
| Background apps | Several RGB/peripheral suites at once, Docker/WSL VMs, double recorders, overlays, cloud sync |
| Network | Wi-Fi vs. wired, Energy Efficient Ethernet, early Intel I225-V revisions, bufferbloat |
| Hardware and BIOS | XMP/EXPO off, single-channel RAM, old BIOS, AMD dual-CCD X3D, Intel hybrid CPUs, laptops on battery |
| Storage | Game on a hard drive, low free space, and a drive map with cleanup notes |

Each finding shows the current value on your PC, the recommendation, why, expected impact, **how strong the evidence is**, sources, step-by-step instructions and how to undo. Things that are personal preference (stretched vs. native, G-SYNC vs. uncapped) are marked as preferences, not verdicts.

It works out what's in each PC: Intel/AMD CPUs, NVIDIA/AMD/Intel GPUs, laptops, multiple drives and monitors, Steam libraries on any drive, Riot, Epic, Battle.net, EA and Ubisoft installs.

## Is it safe with anti-cheat?

Yes. RigScope doesn't inject into games, read game memory, load drivers or run in the background. It reads files (such as CS2's `cs2_video.txt`), the registry, WMI and `nvidia-smi` — the same things Windows Settings and Task Manager show. That's why it works next to VAC, FACEIT and Riot Vanguard.

## Privacy

- No account, no telemetry.
- The only time RigScope goes online is to check for updates: once when it starts, and when you press **Settings › Check for updates**. It asks GitHub for the latest version and release notes, and sends nothing about your PC. Turn off **Check for updates when RigScope starts** in Settings and it never goes online on its own. If you download an update, you pick where it's saved, RigScope checks it against the published SHA-256, and you start it yourself. Nothing is installed or replaced automatically.
- RigScope writes two things on its own: `%LOCALAPPDATA%\RigScope\settings.json` (your Settings choices) and a small summary of each storage scan in `%LOCALAPPDATA%\RigScope\scans`, so the next scan can show what changed. Delete that folder any time.
- **Share report** removes your user name, PC name, Steam account ids, hardware serial ids and email addresses by default.

## Download

Get `RigScope.exe` from [Releases](../../releases). It's a single file that doesn't need .NET installed.

The exe isn't code-signed yet, so Windows SmartScreen may say "Windows protected your PC". Click **More info › Run anyway**, or build it yourself from source (below). The SHA-256 checksum is listed with each release.

## Command line

```
rigscope check [game]                Run the checks in the terminal (cs2, valorant, apex, …)
rigscope snapshot [file]             Save what RigScope sees about this PC as JSON
rigscope scan [path] [--notes]       Folder sizes and cleanup notes
```

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet test
dotnet run --project src/RigScope.App
dotnet publish src/RigScope.App -c Release -r win-x64 -o publish
```

## How it's built

```
Probes (read-only)  →  SystemSnapshot (JSON)  →  rules engine  →  findings
                                                 ↑
                         advice/advice-pack.json (data, not code)
```

- `src/RigScope.Core/Probes` read facts about the PC. A failing probe is reported, never fatal.
- `advice/advice-pack.json` holds every recommendation as data: a condition on the snapshot, outcomes, text, impact, evidence and sources. Fixing advice or adding a rule doesn't need C#. The path and condition syntax is documented in `src/RigScope.Core/Rules/SnapshotQuery.cs`.
- `tests/` run every rule against several made-up PCs (Intel+NVIDIA desktop, AMD X3D+Radeon, hybrid laptop on battery, Wi-Fi without games, empty), so rules are checked across hardware, not just one machine.

## Contributing advice

Good advice for RigScope is **measured**. When you add or change a rule:

1. Link a source that measured it (benchmarks, vendor docs, Blur Busters, etc.) and set `evidence` honestly. Forum anecdotes are `low`.
2. Say what the impact is and on what (FPS, 1% lows, latency, visibility).
3. Give steps a person can follow in Windows or the game menus, and how to undo them. No registry hacks or tweak scripts.
4. Run `dotnet test`.

## License

MIT. See [LICENSE](LICENSE).
