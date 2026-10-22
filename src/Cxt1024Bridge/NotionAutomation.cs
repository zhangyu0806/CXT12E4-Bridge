using System;
using System.Threading;
using System.Windows.Automation;

namespace Cxt1024Bridge
{
    internal static class NotionAutomation
    {
        internal static bool ToggleFullWidth(IntPtr window, Func<bool> contextIsCurrent)
        {
            try
            {
                AutomationElement root;
                Func<bool> guard;
                if (!ApplicationInputSafety.TryBindWindowRoot(window, "notion", contextIsCurrent, out root, out guard))
                    return false;
                AutomationElement actionButton = FindPageActionButton(root);
                if (actionButton == null || !guard())
                    return false;
                return ToggleFullWidthSequence(
                    guard,
                    delegate { return ApplicationInputSafety.IsWithinRoot(actionButton, root) && guard() && Expand(actionButton, guard); },
                    delegate { return FindItem(root, "全宽", "Full width"); },
                    delegate(AutomationElement item)
                    {
                        return guard() && ApplicationInputSafety.IsWithinRoot(item, root) && guard() && Invoke(item, guard);
                    },
                    delegate
                    {
                        if (guard() && ApplicationInputSafety.IsWithinRoot(actionButton, root) && guard())
                            Collapse(actionButton, guard);
                    },
                    Thread.Sleep);
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to toggle Notion full width in the original window", ex);
                return false;
            }
        }

        // Injectable stage orchestration; tests need no UIA or real windows.
        internal static bool ToggleFullWidthSequence<T>(Func<bool> guard, Func<bool> expand,
            Func<T> findItem, Func<T, bool> invoke, Action collapse, Action<int> wait) where T : class
        {
            if (!guard() || !expand() || !guard())
                return false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (!guard())
                    return false;
                T item = findItem();
                if (!guard())
                    return false;
                if (item != null)
                    return invoke(item);
                wait(40);
                if (!guard())
                    return false;
            }
            if (guard())
                collapse();
            return false;
        }

        private static AutomationElement FindPageActionButton(AutomationElement root)
        {
            if (root == null)
                return null;
            System.Windows.Rect bounds = root.Current.BoundingRectangle;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants, condition);
            AutomationElement best = null;
            double bestX = double.MinValue;
            for (int i = 0; i < buttons.Count; i++)
            {
                try
                {
                    string name = buttons[i].Current.Name ?? string.Empty;
                    System.Windows.Rect buttonBounds = buttons[i].Current.BoundingRectangle;
                    bool matchingName = name == "操作" || name == "Actions" || name == "More actions";
                    if (!matchingName || buttons[i].Current.IsOffscreen || buttonBounds.Top > bounds.Top + 160)
                        continue;
                    if (buttonBounds.Left > bestX)
                    {
                        best = buttons[i];
                        bestX = buttonBounds.Left;
                    }
                }
                catch
                {
                }
            }
            return best;
        }

        private static AutomationElement FindItem(AutomationElement root, params string[] names)
        {
            // Never scan the desktop, sibling HWNDs or another Notion process.
            // A separately hosted popup fails closed if absent from this root.
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            for (int i = 0; i < elements.Count; i++)
            {
                try
                {
                    if (elements[i].Current.IsOffscreen || !elements[i].Current.IsEnabled)
                        continue;
                    string name = elements[i].Current.Name ?? string.Empty;
                    for (int j = 0; j < names.Length; j++)
                    {
                        if (string.Equals(name, names[j], StringComparison.OrdinalIgnoreCase) && HasActionPattern(elements[i]))
                            return elements[i];
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private static bool HasActionPattern(AutomationElement element)
        {
            object pattern;
            return element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern) ||
                element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern) ||
                element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern);
        }

        private static bool Expand(AutomationElement element, Func<bool> guard)
        {
            try
            {
                object pattern;
                if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                    return false;
                var expandable = (ExpandCollapsePattern)pattern;
                if (expandable.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                {
                    if (!guard())
                        return false;
                    expandable.Expand();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Collapse(AutomationElement element, Func<bool> guard)
        {
            try
            {
                object pattern;
                if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                {
                    var expandable = (ExpandCollapsePattern)pattern;
                    if (expandable.Current.ExpandCollapseState == ExpandCollapseState.Expanded && guard())
                        expandable.Collapse();
                }
            }
            catch
            {
            }
        }

        private static bool Invoke(AutomationElement element, Func<bool> guard)
        {
            try
            {
                object pattern;
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                {
                    if (!guard()) return false;
                    ((InvokePattern)pattern).Invoke();
                    return true;
                }
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
                {
                    if (!guard()) return false;
                    ((TogglePattern)pattern).Toggle();
                    return true;
                }
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                {
                    if (!guard()) return false;
                    ((SelectionItemPattern)pattern).Select();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Notion UI Automation invoke failed", ex);
            }
            return false;
        }
    }
}
