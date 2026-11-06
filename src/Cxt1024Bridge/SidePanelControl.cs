using System;
using System.Collections.Generic;

namespace Cxt1024Bridge
{
    // Shared by the UIA adapter and isolated tests. Never sends keyboard input.
    internal static class SidePanelControl
    {
        internal static bool IsToggle(string name, bool button, bool visible, bool enabled, bool invokable)
        {
            if (!button || !visible || !enabled || !invokable) return false;
            // thread.sidePanel.toggle in the app's English / zh-CN / zh-TW resources.
            // Do not match generic Changes tabs, sidebar buttons or conversation text.
            return string.Equals(name, "Toggle side panel", StringComparison.OrdinalIgnoreCase) ||
                name == "显示/隐藏侧边面板" || name == "切換側邊面板";
        }

        internal static T FindUnique<T>(IEnumerable<T> items, Func<T, bool> matches) where T : class
        {
            T found = null;
            foreach (T item in items)
            {
                if (!matches(item)) continue;
                if (found != null) return null;
                found = item;
            }
            return found;
        }

        internal static bool Dispatch<T>(Func<bool> context, Func<T> find, Func<T, bool> invoke) where T : class
        {
            if (context == null || !context()) return false;
            T target = find();
            if (target == null || !context()) return false;
            // Exactly one invocation. No shortcut fallback or retry after an uncertain result.
            return invoke(target);
        }
    }
}
