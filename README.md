# Space Engineers VR

Play **Space Engineers 1** in virtual reality through OpenXR (SteamVR, Meta/Oculus, Virtual Desktop, Windows Mixed Reality). A plugin for the [Pulsar](https://github.com/SpaceGT/Pulsar) plugin loader.

Status: **early access**. Tested so far with a Steam Frame over SteamVR.

## Features
- Head-tracked stereo view in the game's normal graphics.
- Head steers like the mouse; the left stick moves relative to your gaze, the right stick turns (and pitches with the jetpack).
- The character's hands follow the controllers; tools and weapons sit in the right hand and aim where the controller points.
- Block placement with the right hand: hold the left grip to freeze the block, turn the wrist to rotate it, X / B change the variant, Y opens the colour menu, the D-pad moves the block closer or further away.
- Floating menus with a laser pointer and an on-screen keyboard for text fields; world-space GPS markers; head-locked HUD.
- Right grip held on foot: D-pad = F10 / Alt+F10 / Shift+F10, A / B / X / Y = MMB / Shift+MMB / Ctrl+MMB / Alt+MMB (build planner).
- Cockpits: sticks fly, grips roll, LB / RB move a hotbar cursor, right stick click activates the slot. The D-pad sends the keys F, Z, L and G.
- Bindings for the Steam Frame controller (native profile), Touch, Index, Pico, WMR and generic controllers.

Single-player view only: the plugin has no multiplayer features of its own.

## Install
Install **Pulsar**, open the plugin list, enable **Space Engineers VR** and start the game with your headset's OpenXR runtime active (SteamVR: Settings, OpenXR, "Set SteamVR as OpenXR runtime"). Without a headset the game starts normally.

Settings: `%AppData%\SpaceEngineers\SeVr.cfg` (`RenderScale` sets the resolution). Log: `SeVr.log` next to it.

## Build
`dotnet build src/SeVr.csproj -c Release -p:Platform=x64` (set `-p:GameBin=...` if Space Engineers is not in the default Steam folder). The input tables in `sheets/` are the source of truth for `src/Generated/InputMap.cs`; regenerate with `tools/gen.ps1`.

## Licence
MIT, see `LICENSE`. `assets/openxr_loader.dll` is the Khronos OpenXR loader (Apache-2.0 / MIT).

