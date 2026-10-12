# Changelog

## 0.5.1 (early access)

Fixed
- The button cluster on the left hand has its own part id; with `OwnHud=false` a cut-out HUD piece no longer lands in the shared atlas.
- `VrControls.Build()` creates its input sources under the lock on both threads.
- The number of layers per frame is limited to what the runtime reports (`maxLayerCount`); extra parts are left out instead of failing the frame.
- Removed the unused `LoadAssets(string)`.

## 0.5.0 (early access)

New
- **Hand menu** (default): status, tool bar, block info and indicators in one block at the left hand, opened with a small button on the back of the hand. No crosshair: what you can use or select is found with the right hand. `HudMode=rows` brings back the rows in front of you.
- **Edit mode**: pick up any HUD part with the right grip, move it, scale it with the right stick and tie it to the hand, the head or its default place. Saved in the settings file.
- **Buttons on the back of the hands**: Menu, Edit, Reset, Chat, K, G, I, ? (left hand), F10, Alt+F10, Shift+F10 (right hand). Pointed at with the other hand's laser; both hands have a laser.
- **Cockpit HUD**: tool bar and a button bar (P landing gear, Z dampeners, L lights) fixed in the cockpit; every cockpit type keeps its own layout, also for the other HUD parts while seated.
- **Help window** with the controls and hints, on by default, switched with the ? button.
- Menu windows tilted like a tablet, a strip below them (size, lock, keyboard, page buttons for the G menu), a floating keyboard, and a production multiplier (x1 / x10 / x100 / x1000) in the terminal's production tab.
- Body zone right of the head: A = key V, B = Escape. The chest zone only opens the inventory. Body zones also work in third person.
- Touch: X and Y act at once (terminal and inventory have their own buttons); grip + right stick up is key F; the F10 combinations moved to the buttons on the right hand.
- The monitor shows what you see in VR (`Mirror=vr`).
- Online (not hosting): while firing, the character's head follows the hand's direction (`HandAimOnline`).
- The turn stick eases in and out (`TurnSmooth`).
- The helmet chip shows open / closed.

Changed
- The plugin is called **Space VR** (assembly `Space VR.dll`).
- Settings, log and captures are stored in the game's user data folder (`MyFileSystem.UserDataPath`, follows `-appdata`).
- The official OpenXR loader 1.1.63 is shipped (x64 release build of the Khronos package, Apache-2.0).
- Plugin list entry: the loader is a file asset (`Placement="Bin"`), the plugin is limited to the .NET Framework runtime.
- Performance: panels are repainted only when something changed, small parts share one swapchain.

Fixed
- The control table is rebuilt into new tables and swapped in, so a rebinding during a VR session cannot break the game's input thread.
- When the OpenXR session cannot be created, the window gets its normal size and VSync back.
- The marker colour icons are no longer written to the temp folder.
- More than a dozen panels no longer exhaust the runtime's swapchain and layer limits.

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

