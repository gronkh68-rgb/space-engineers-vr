# Space Engineers VR

Play **Space Engineers 1** in virtual reality through OpenXR (SteamVR, Meta/Oculus, Virtual Desktop, Windows Mixed Reality). A plugin for the [Pulsar](https://github.com/SpaceGT/Pulsar) plugin loader.

Status: **early access**. Tested with a Steam Frame over SteamVR and with a Quest 2.

## Features
- Head-tracked stereo view in the game's normal graphics.
- Head steers like the mouse; the left stick moves relative to your gaze, the right stick turns (and pitches with the jetpack).
- The character's hands follow the controllers; tools and weapons sit in the right hand and aim where the controller points.
- Block placement with the right hand: hold the left grip to freeze the block, turn the wrist to rotate it, X / B change the variant, Y opens the colour menu, the right stick (or D-pad) moves the block closer or further away.
- Floating menus with a laser pointer and an on-screen keyboard for text fields.
- **Own VR HUD** drawn by the plugin from the game's data, independent of HUD mods: status bars, tool bar with the game's icons, block info with the *functional* and *hacked* marks, component list, indicators (inertial dampeners, light, antenna, helmet, jetpack, magnetic boots, gravity, temperature, outside oxygen, radiation, bottles), ammunition of the hand weapon, notifications and item pickups, and a chat that wraps and scrolls like the game's. Distance and opacity are adjustable. Mod HUDs that draw near the camera (BuildInfo ...) can be hidden, shown floating, or left alone.
- World-space GPS markers.
- **Body zones** for controllers without a D-pad (Touch, Pico, ...): hold a hand near the head, shoulder, chest or hip and press trigger or grip. Each zone can be switched off in the settings.
- In-game settings (Pulsar: plugin list, Space Engineers VR, settings), key bindings and HUD layout screens.
- Cockpits: sticks fly, grips roll; the hotbar is moved with a cursor.
- Bindings for the Steam Frame controller (native profile), Touch (Quest, Rift), Index, Pico, WMR and generic controllers.

Works in multiplayer (sort of): the plugin has no multiplayer features of its own.

## Controls

### Oculus / Touch (Quest, Rift)
No D-pad: while a grip is held on foot, the right stick works as one (up F, down Z, right L, left J).

| Control | Action |
|---|---|
| Left stick | Move / fly. Click: sprint |
| Right stick left / right | Turn. Up / down with jetpack: pitch. Click: jetpack on / off |
| A / B | Jump (up) / crouch (down) |
| X tap / hold | Use / terminal |
| Y tap / hold | Inertial dampeners / inventory |
| Menu (left) | Game menu |
| Right trigger / left trigger | Use tool, fire / secondary action |
| Grips (in a ship) | Roll left / right |
| Left grip held, block in hand | Right stick up / down: move block; wrist: rotate block; X / B / Y: previous variant / next variant / colour menu |
| Left grip held, jetpack, no block | Right stick left / right: roll (Q / E) |
| Right grip held | Right stick up / down / left: F10 / Alt+F10 / Shift+F10; A / B / X / Y: MMB / Shift+MMB / Ctrl+MMB / Alt+MMB (build planner) |

Body zones (hand in the zone, then trigger or grip):

| Zone | Trigger | Grip |
|---|---|---|
| Right of the head | Flashlight | Helmet |
| Left of the head | GPS markers on / off | Antenna (O) |
| Left shoulder | Block menu (G) | Hide HUD |
| Right shoulder | - | Landing gear |
| Chest | Inventory | Terminal |
| Right hip | - | Next hotbar slot |
| Left hip | - | Previous hotbar slot |

The Oculus button is reserved by the system. Everything can be changed in the settings under *Key bindings*.

### Steam Frame
Left stick moves, right stick turns, A / B jump / crouch, X (right controller, left button) key G, Y inventory, LB / RB previous / next hotbar slot, D-pad: F / Z / L / J, view button: game menu, triggers and grips as above. The zone and grip combinations above work too.

## Install
Install **Pulsar**, open the plugin list, enable **Space Engineers VR** and start the game with your headset's OpenXR runtime active (SteamVR: Settings, OpenXR, "Set SteamVR as OpenXR runtime"). Without a headset the game starts normally.

Settings: in the game (plugin list, Space Engineers VR, settings) or `%AppData%\SpaceEngineers\SeVr.cfg` (`RenderScale` sets the resolution). Log: `SeVr.log` next to it.

## Build
`dotnet build src/SeVr.csproj -c Release -p:Platform=x64` (set `-p:GameBin=...` if Space Engineers is not in the default Steam folder). The input tables in `sheets/` are the source of truth for `src/Generated/InputMap.cs`; regenerate with `tools/gen.ps1`.

## Licence
MIT, see `LICENSE`. `assets/openxr_loader.dll` is the Khronos OpenXR loader (Apache-2.0 / MIT).
