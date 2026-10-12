# Space VR

Play **Space Engineers 1** in virtual reality through OpenXR (SteamVR, Meta/Oculus, Virtual Desktop, Windows Mixed Reality). A plugin for the [Pulsar](https://github.com/SpaceGT/Pulsar) plugin loader.

Status: **early access**. Tested with a Steam Frame over SteamVR and with a Quest 2.

## Features
- Head-tracked stereo view in the game's normal graphics.
- Head steers like the mouse; the left stick moves relative to your gaze, the right stick turns (eased in and out, `TurnSmooth` in the settings file) and pitches with the jetpack.
- The character's hands follow the controllers; tools and weapons sit in the right hand and aim where the controller points. What you can use or select (doors, terminals, cockpits, blocks) is found with the right hand too, there is no crosshair by default.
- Block placement with the right hand: hold the left grip to freeze the block, turn the wrist to rotate it, X / B change the variant, Y opens the colour menu, the right stick moves the block closer or further away.
- **Hand menu**: status, tool bar, block info and indicators in one block at the left hand (small *Menu* button on the back of the hand), so the view stays free for flying. Rows in front of you are still available (`HudMode=rows`).
- **Own VR HUD** drawn by the plugin from the game's data, independent of HUD mods: status bars, tool bar with the game's icons, block info with the *functional* and *hacked* marks, component list, indicators, ammunition of the hand weapon, notifications and item pickups, chat. Distance and opacity are adjustable. Mod HUDs that draw near the camera (BuildInfo ...) can be hidden, shown floating, or left alone.
- **Edit mode** (button on the left hand): every HUD part can be picked up with the right grip, moved, scaled (right stick) and tied to the hand, the head or its default place. The layout is saved.
- **Buttons on the back of the hands**: left hand *Menu*, *Edit*, *Reset*, *Chat*, *K*, *G*, *I* (terminal, G menu, inventory) and *?* (help); right hand *F10*, *Alt+F10*, *Shift+F10*. Point at them with the other hand's laser and pull the trigger.
- **Cockpit HUD**: tool bar and a button bar (P landing gear, Z dampeners, L lights) fixed in the cockpit. Every cockpit type keeps its own layout.
- **Help window** with the controls and hints, shown at the start, switched with the *?* button.
- Floating menus (terminal, inventory ...) tilted like a tablet, with laser pointers on both hands, an on-screen keyboard, page buttons for the G menu and a production multiplier (x1 / x10 / x100 / x1000) in the terminal's production tab.
- World-space GPS markers.
- **Body zones** for controllers without a D-pad (Touch, Pico, ...): hold a hand near the head, shoulder, chest or hip and press trigger or grip. Each zone can be switched off in the settings.
- The monitor shows what you see in VR (`Mirror=vr`) or the flat game.
- In-game settings (Pulsar: plugin list, Space VR, settings), key bindings and HUD layout screens.
- Bindings for the Steam Frame controller (native profile), Touch (Quest, Rift), Index, Pico, WMR and generic controllers.

No multiplayer features of its own. In online games (not hosting) the character's head is steered to the hand's direction while firing, so the server shoots where the hand points (`HandAimOnline`).

## Controls

### Oculus / Touch (Quest, Rift)
| Control | Action |
|---|---|
| Left stick | Move / fly. Click: sprint |
| Right stick left / right | Turn. Up / down with jetpack: pitch. Click: jetpack on / off |
| A / B | Jump (up) / crouch (down) |
| X / Y | Use / inertial dampeners |
| Menu (left) | Game menu |
| Right trigger / left trigger | Use tool, fire / secondary action |
| Grips (in a ship) | Roll left / right |
| Left grip held, block in hand | Right stick up / down: move block; wrist: rotate block; X / B / Y: previous variant / next variant / colour menu |
| Left grip held, jetpack, no block | Right stick left / right: roll (Q / E) |
| Right grip held | A / B / X / Y: MMB / Shift+MMB / Ctrl+MMB / Alt+MMB (build planner); right stick up: key F |

Body zones (hand in the zone, then trigger or grip):

| Zone | Trigger | Grip |
|---|---|---|
| Right of the head | Flashlight | Helmet |
| Right of the head, A / B | A: key V | B: Escape |
| Left of the head | Signals on / off | Broadcasting |
| Left shoulder | Block menu (G) | Hide HUD |
| Right shoulder | - | Landing gear |
| Chest | Inventory | - |
| Right hip | - | Next hotbar slot |
| Left hip | - | Previous hotbar slot |

The terminal is on the *K* button, the G menu on *G*, the inventory on *I* (and the chest zone). The Oculus button is reserved by the system. Everything can be changed in the settings under *Key bindings*.

### Steam Frame
Left stick moves, right stick turns, A / B jump / crouch, X (right controller, left button) key G, Y inventory, LB / RB previous / next hotbar slot, D-pad: F / Z / L / J, view button: game menu, triggers and grips as above. The zone and grip combinations above work too.

## Install
Install **Pulsar**, open the plugin list, enable **Space VR** and start the game with your headset's OpenXR runtime active (SteamVR: Settings, OpenXR, "Set SteamVR as OpenXR runtime"). Without a headset the game starts normally.

Settings: in the game (plugin list, Space VR, settings) or `SeVr.cfg` in the game's user data folder (`%AppData%\SpaceEngineers`, or the folder given with `-appdata`; `RenderScale` sets the resolution). Log: `SeVr.log` next to it.

## Build
`dotnet build src/SeVr.csproj -c Release -p:Platform=x64` (set `-p:GameBin=...` if Space Engineers is not in the default Steam folder). The input tables in `sheets/` are the source of truth for `src/Generated/InputMap.cs`; regenerate with `tools/gen.ps1`.

## Licence
MIT, see `LICENSE`. `assets/openxr_loader.dll` is the unmodified x64 loader of the official Khronos OpenXR.Loader 1.1.63 package (Apache-2.0, see `assets/LICENSE-OpenXR-Loader.txt` and `assets/README.txt`).
