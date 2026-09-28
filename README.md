# PPG Mod Enabler

A single-exe Windows tool that switches local C# mods back on in People Playground's
safe-mode build (Steam build 25467926, 22 Sept 2026). User-facing instructions are in
[README.txt](README.txt); this file covers how it works.

Download the exe from the [Releases](../../releases) page.

> **Mods are code.** With mods switched on, any mod you load can do anything on your PC that
> you can do. That is how the September 2026 Workshop worm spread. This tool backs up and
> checks your mods and keeps the game's own suspicious-code check on, but it cannot make an
> untrusted mod safe.

Not affiliated with or endorsed by Studio Minus. People Playground's own files are not
included here; the tool edits your installed copy and keeps the original.

## What the safe-mode build changed

The update did not remove the mod system. It stubbed four places in `Assembly-CSharp.dll`:

| Method | Safe-mode behaviour | Patched behaviour |
|---|---|---|
| `ModLoader.Compile` | returns "Safe mode is enabled" | calls the still-present `TryCompileMod` in try/catch, sets `HasErrors`, `Suspicious`, `Errors` |
| `ModLoader.LoadModAt` | marks every mod broken | leaves `HasErrors`/`Errors` clear |
| `ModificationManager.Invoke` | `return false` | finds the entry class, calls static `Main`/`OnLoad`/`OnUnload` with `ModAPI.Metadata` set (same pattern as the game's `PerformAfterSpawn`), logs exceptions |
| `ModEntryBehaviour.UpdateUi` | "Safe mode is enabled. Mods cannot load." | mod description, or the compile error |

Everything else (the Roslyn compiler in `ppgModCompiler`, its suspicious-identifier scan and
remote risk list, `Assembly.Load`, `ModAPI`) is untouched. Workshop loading and uploading
stay off.

The patcher only patches when every method, field, type shape and instruction it touches is
exactly as expected (including that `TryCompileMod` still switches on the game's
`RejectShadyCode` check). It also knows the SHA-256 of the tested DLL and asks before patching
any other file. Patched files carry a `PPGModEnablerPatch` marker type, and the original is kept
as `Assembly-CSharp.dll.ppgme-original`.

## Safety rules the code follows

- Nothing the user owns is deleted or overwritten. Copies refuse existing targets and are
  verified with SHA-256, use `\\?\` paths, and do not follow folder links. Mods are switched
  off by renaming `mod.json`.
- A verified backup (Mods, Workshop downloads, Contraptions, `externalactives`,
  `config.json`, the DLL) is made before any change; a failed backup aborts everything and
  removes only its own unfinished copy.
- Workshop mods are copied into `Mods` with every `mod.json` written last (already switched
  off for suspicious mods), so a failed copy can never leave a mod the game would load.
- The DLL is swapped with `File.Replace` and verified; a failed write restores the original.
- The mod scan is a text heuristic: `\u` escapes decoded, plain `using` lines ignored,
  serious rules run on the whole text (so comment/string tricks cannot hide code from them),
  every file listed in `Scripts` is checked as code even outside the mod folder. It is a
  second opinion next to the game's own check, not a guarantee.

## Build

```
cd src
dotnet build -c Release
```

Needs the .NET SDK (6 or newer). Output: `dist\PPG-Mod-Enabler.exe` (.NET Framework 4.8,
Mono.Cecil merged in with ILRepack).

Options for testing: `--game <dir>`, `--workshop <dir|none>`, `--backup-root <dir>`,
`--action enable|scan|undo`, `--no-pause`.
