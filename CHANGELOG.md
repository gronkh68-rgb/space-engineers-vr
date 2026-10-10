# Changelog

## 0.4.0 (early access)

New
- **Own VR HUD**, drawn by the plugin from the game's data (independent of HUD mods): status bars, tool bar with the game's icons, block info with *functional* / *hacked* marks, component list, indicators (dampeners, light, antenna, helmet, jetpack, magnetic boots, gravity, temperature, outside oxygen, radiation, bottles), hand weapon ammunition, notifications and item pickups, chat (wraps, scrolls, sized to its text). Laid out in rows below the view, with HUD distance, opacity and sideways settings. "HUD: own / cut out" in the settings switches back to pieces cut out of the game's HUD picture.
- **In-game settings** (Pulsar: plugin list, Space Engineers VR, settings), a key bindings screen (rebind every control, choose the controller layout) and a HUD layout screen.
- **Body zones** for controllers without a D-pad (Quest, Pico, ...): hand near the head, shoulders, chest or hips plus trigger or grip. Each zone can be switched off. On by default for every controller.
- On Touch controllers a grip held on foot turns the right stick into a D-pad (F / Z / L / J).
- Steam Frame controller: vibration when a hand enters a zone.
- **Mod HUDs** (BuildInfo and others that draw near the camera) are hidden by default; options: hide, float in front of the head, leave alone. A chat note appears when BuildInfo is detected.
- Hands: `Hands back` and `Hands down` settings, hand rotation, hands at the grip; hands follow while moving fast (the head offset is only measured while slow).
- GPS markers: the plugin's own markers are the default; the game's own sprites are an experimental option.
- HUD notifications, chat, ammunition and temperature level use the game's own data.
- Tool bar in a cockpit: LB / RB move the cursor, stick click activates.
- Settings file keys: `HandBackCm`, `HandDownCm`, `HudYawDeg`, `HudDistance`, `HudOpacity`, `ModHudMode`, `OwnHud`, `ZonesOff`, `GuideHands`.

Fixed
- Streaks between crossed hands, wrong block rotation directions, build mode buttons, freeze zoom direction.
- Several crash and glitch fixes while loading worlds.
- Head and HUD orientation use the average of both eyes (displays can be turned outwards).
## 0.3.0 (early access)

New
- On-screen keyboard (QWERTZ with umlauts) for text fields in menus: it appears when a text box has focus, press keys with the laser and the right trigger.
- Right grip held on foot: D-pad up / down / left = F10 / Alt+F10 / Shift+F10; A / B / X / Y = middle mouse button / Shift+MMB / Ctrl+MMB / Alt+MMB (build planner actions).
- `Runtime=steamvr` (or a path to a runtime .json) in `SeVr.cfg` forces that OpenXR runtime for this game only.
- `EyeSmooth=<seconds>` in `SeVr.cfg` (off by default) smooths the eye point against head-bone sway.
- D-pad left sends J (helmet), X sends G.

Fixed
- Crash and corrupted icons / glitching buttons while a world loads: the right-eye pass now waits for the game's parallel sprite task and discards its result (the two tasks used to race on the sprite renderer's lists and buffers).
- Grey screen and a character that no longer moved after loading another world: invalid (NaN) body data of a freshly spawned character is no longer fed into the head steering.
- A test setting inherited through a launcher (Steam) could switch the plugin into its simulated no-headset mode. The test mode now needs a `sevr.fake` file next to the plugin as well.
- Alt / Shift / Ctrl combinations now reach the game (it asks for the generic and the left keys).
- The build planner is queried through `MyControllerHelper.IsControl`, which is now answered too.

## 0.2.0 (early access)

First version in the plugin list: head-tracked stereo view, head steers like the mouse, hands follow the controllers, tools and weapons aim with the right controller, hand-based block placement and rotation, floating menus with a laser pointer, world-space GPS markers, cockpit hotbar cursor, Steam Frame controller bindings.

