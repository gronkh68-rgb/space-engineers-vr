using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Screens.Helpers;

namespace SeVr.Vr
{
    /// <summary>
    /// Hotbar navigation in a cockpit: the D-pad moves a cursor over the slots that hold an item (shown as the staged slot),
    /// a separate button activates the slot under the cursor. The game's own next/previous keys activate immediately.
    /// </summary>
    internal static class VrToolbar
    {
        static int cursor = -1;
        static PropertyInfo staged;
        static int logCount;

        static MyToolbar Current { get { return MyToolbarComponent.CurrentToolbar; } }

        public static void Move(int dir)
        {
            try
            {
                var tb = Current;
                if (tb == null) return;
                if (cursor < 0) cursor = tb.SelectedSlot ?? tb.StagedSelectedSlot ?? (dir > 0 ? -1 : tb.SlotCount);
                int n = dir > 0 ? tb.GetNextValidSlotWithItem(cursor) : tb.GetPreviousValidSlotWithItem(cursor);
                if (n < 0) return;
                cursor = n;
                if (staged == null) staged = AccessTools.Property(typeof(MyToolbar), "StagedSelectedSlot");
                staged.GetSetMethod(true).Invoke(tb, new object[] { (int?)cursor });
                if (logCount++ < 6) Plugin.Log("toolbar: cursor " + cursor);
            }
            catch (Exception e) { if (logCount++ < 6) Plugin.Log("toolbar move: " + e.Message); }
        }

        public static void Activate()
        {
            try
            {
                var tb = Current;
                if (tb == null || cursor < 0) return;
                tb.ActivateItemAtSlot(cursor, false, true, true);
            }
            catch (Exception e) { if (logCount++ < 6) Plugin.Log("toolbar activate: " + e.Message); }
        }

        static int lastEquipped = -1;

        /// <summary>On foot: put the tool or weapon away, or take the last one out again.</summary>
        public static void ToggleHolster()
        {
            try
            {
                var tb = Current;
                if (tb == null) return;
                int? sel = tb.SelectedSlot;
                if (sel.HasValue) { lastEquipped = sel.Value; tb.Unselect(true); }
                else if (lastEquipped >= 0) tb.ActivateItemAtSlot(lastEquipped, false, true, true);
            }
            catch (Exception e) { if (logCount++ < 6) Plugin.Log("toolbar holster: " + e.Message); }
        }

        public static void Reset() { cursor = -1; }
    }
}
