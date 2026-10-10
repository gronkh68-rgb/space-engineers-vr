# Changelog

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
