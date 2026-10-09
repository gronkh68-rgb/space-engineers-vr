using System;
using Sandbox.Graphics.GUI;

namespace SeVr.Vr
{
    internal static class MenuState
    {
        /// <summary>True when a full menu (not the in-game HUD) has focus, so the flat screen is shown on a floating quad.</summary>
        public static bool IsMenuOpen()
        {
            try
            {
                var s = MyScreenManager.GetScreenWithFocus();
                if (s == null) return true; // main menu / loading
                string n = s.GetType().Name;
                return !(n.Contains("GamePlay") || n.Contains("Hud"));
            }
            catch { return false; }
        }
    }
}
