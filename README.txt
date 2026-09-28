PPG MOD ENABLER 1.0.0
=====================

Switches C# mods back on in People Playground's "safe mode" version
(the Steam update from 22 September 2026, build 25467926), after the
Workshop malware incident.

Read this first
---------------
Mods are small programs. Once they are switched on, any mod you load can do
anything on your PC that you can do. That is exactly how the September 2026
worm spread: it hid inside mods. This tool makes a checked backup, points out
mods that look risky, and keeps the game's own suspicious-code check switched
on, but it CANNOT promise that a mod is safe. Only load mods you trust.

What it does
------------
1. Finds People Playground through Steam (or asks you for the folder).
2. Makes a full backup and checks every copied file byte for byte:
   your Mods folder, your Workshop downloads, your contraptions, your mod
   on/off list, the game settings and the game file it is about to change.
   Backups go to:  C:\Users\<you>\PPG Mod Backups\<date and time>\
   If the backup fails, it stops and changes nothing.
3. Checks every mod's code for warning signs (starting programs, using the
   internet, touching Discord or Steam account files, shipping hidden .dll
   files, files changed on or after 21 Sept 2026 when the worm spread, ...)
   and saves a report next to the backup.
4. Offers to switch off the mods that look suspicious. Switching off only
   renames the mod's mod.json file so the game skips it. Nothing is deleted.
5. Offers to copy your Workshop mods into the Mods folder. The safe-mode game
   no longer loads Workshop mods by itself. The originals stay where they are,
   and suspicious ones are copied switched off.
6. Changes ONE game file (People Playground_Data\Managed\Assembly-CSharp.dll)
   so the mod loader works again. The original is kept next to it as
   Assembly-CSharp.dll.ppgme-original, and another copy is in the backup.

It never deletes mods and never overwrites a mod file.

How to use
----------
1. Close People Playground.
2. Double-click PPG-Mod-Enabler.exe.
3. Type 1 and press Enter, then answer the questions.
4. Start the game. Mods compile when the game starts, so the first start can
   take a while. Open the Mods menu to switch single mods on or off, then load
   a map.

Windows may warn that the exe is from an unknown publisher (it is not signed).
Click "More info" and then "Run anyway". Some antivirus programs dislike any
tool that changes a game file; that is expected for a patcher.

Menu options
------------
1) Switch mods on       backup, check, patch (described above)
2) Only check my mods   prints and saves the report, changes nothing
3) Undo                 puts the original game file back and can switch the
                        mods it switched off back on
4) Quit

Undo without this tool: in Steam, right-click People Playground > Properties >
Installed Files > Verify integrity of game files. That downloads the original
game file again. Your mods are not touched by that.

Good to know
------------
- A Steam update of the game replaces the patched file, so mods switch off
  again. Run the tool again afterwards. If the update changed the game's mod
  code, the tool says so and changes nothing.
- To switch a mod back on by hand, rename
  "mod.json.switched-off-by-ppg-mod-enabler" back to "mod.json".
- The game's own check still runs. If it marks a mod SUSPICIOUS and offers
  "Accept risk", that means the mod uses things like file access or
  reflection. Only accept if you trust where the mod came from.
- The "Upload" button in the Mods menu does nothing (Workshop uploads are
  switched off by the game).
- The game itself rewrites a mod's mod.json (same settings, different layout)
  when it compiles or switches that mod. That is normal game behaviour; the
  original files are in the backup.
- Not enough space for the backup? The tool offers to leave out the Workshop
  downloads (it never changes them anyway). Or start it from a command prompt
  with:  PPG-Mod-Enabler.exe --backup-root "D:\some folder"
- If a copy of a Workshop mod fails halfway, the unfinished copy has no
  mod.json, so the game ignores it. The tool tells you where it is.
- If the game has been updated to a version this tool was not tested on, it
  asks before patching, and refuses if the game's mod code changed.
- The mod check is a text scan. It also looks inside comments, so a mod that
  only mentions something risky in a comment can be flagged too. Read the
  reasons in the report and decide.
- A log of every run is saved in the backups folder.
