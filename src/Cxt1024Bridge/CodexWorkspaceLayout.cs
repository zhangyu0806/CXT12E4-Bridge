using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace Cxt1024Bridge
{
    internal enum BrowserWorkspaceLayout { Unknown, Full, Split }

    internal sealed partial class ChatGptAutomation
    {
        private int _workspaceRequest;

        private void QueueBrowserSplit(IntPtr window, string url, Func<bool> context, int request)
        {
            // Native URL dispatch is asynchronous. Keep UIA and its waits off
            // the input queue so task keys remain responsive while a page opens.
            ThreadPool.QueueUserWorkItem(delegate
            {
                var elapsed = Stopwatch.StartNew();
                Func<bool> guard = delegate
                {
                    return elapsed.ElapsedMilliseconds < 3500 &&
                        Interlocked.CompareExchange(ref _workspaceRequest, 0, 0) == request && context();
                };
                try
                {
                    bool split = EnsureBrowserSplit(guard,
                        delegate { return ReadBrowserLayout(window, url); },
                        delegate
                        {
                            AutomationElement target = FindWorkspaceLayoutControl(GetRoot(window), true);
                            // Re-read the exact state after lookup: never blindly
                            // toggle a panel that has already entered split view.
                            return target != null && guard() && IsEnterSplitName(SafeName(target)) && Invoke(target);
                        }, Thread.Sleep);
                    Logger.Info(split ? "Codex browser right split view confirmed." :
                        "Codex browser split view not confirmed (context changed, unavailable UI, or timeout).");
                }
                catch (Exception ex) { Logger.Error("Unable to confirm Codex browser split view", ex); }
            });
        }

        internal static bool EnsureBrowserSplit(Func<bool> context, Func<BrowserWorkspaceLayout> read,
            Func<bool> enterSplit, Action<int> wait)
        {
            bool requested = false;
            bool confirmed = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (context == null || !context()) return false;
                BrowserWorkspaceLayout layout = read();
                if (!context()) return false;
                // An already-open tab may be split before the new native URL
                // request arrives and changes it to full. Observe the entire
                // bounded arrival window, not just the first pre-existing state.
                confirmed = layout == BrowserWorkspaceLayout.Split;
                if (layout == BrowserWorkspaceLayout.Full && !requested)
                {
                    if (!enterSplit()) return false;
                    requested = true;
                }
                if (attempt < 19) wait(100);
            }
            return confirmed;
        }

        internal static bool IsEnterSplitName(string name)
        {
            return name == "Enter split view" || name == "进入分屏视图" || name == "進入分割檢視" ||
                name == "Exit full view" || name == "退出完整视图" || name == "退出完整檢視";
        }

        internal static bool IsHideWorkspaceTabsName(string name)
        {
            return name == "Hide tabs" || name == "隐藏标签页" || name == "隱藏分頁";
        }

        internal static bool IsBrowserAddress(string value, string requestedUrl)
        {
            Uri actual, requested;
            if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(requestedUrl, UriKind.Absolute, out requested)) return false;
            if (!value.Contains("://")) value = "https://" + value;
            return Uri.TryCreate(value, UriKind.Absolute, out actual) &&
                (actual.Scheme == "https" || actual.Scheme == "http") &&
                actual.Host.Equals(requested.Host, StringComparison.OrdinalIgnoreCase) &&
                actual.AbsolutePath.TrimEnd('/') == requested.AbsolutePath.TrimEnd('/');
        }

        private static BrowserWorkspaceLayout ReadBrowserLayout(IntPtr window, string url)
        {
            AutomationElement root = GetRoot(window);
            if (root == null) return BrowserWorkspaceLayout.Unknown;
            Rect bounds = SafeBounds(root);
            AutomationElementCollection addresses = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));
            bool targetLoaded = false;
            foreach (AutomationElement address in addresses)
            {
                if (SafeOffscreen(address) || !IsWorkspaceHeader(bounds, SafeBounds(address), false)) continue;
                object pattern;
                if (address.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) &&
                    IsBrowserAddress(((ValuePattern)pattern).Current.Value, url)) { targetLoaded = true; break; }
            }
            if (!targetLoaded) return BrowserWorkspaceLayout.Unknown;
            if (FindWorkspaceLayoutControl(root, false) != null) return BrowserWorkspaceLayout.Split;
            if (FindWorkspaceLayoutControl(root, true) != null) return BrowserWorkspaceLayout.Full;
            return BrowserWorkspaceLayout.Unknown;
        }

        private static AutomationElement FindWorkspaceLayoutControl(AutomationElement root, bool enterSplit)
        {
            if (root == null) return null;
            string[] names = enterSplit ? new[] { "Enter split view", "进入分屏视图", "進入分割檢視",
                "Exit full view", "退出完整视图", "退出完整檢視" } :
                new[] { "Hide tabs", "隐藏标签页", "隱藏分頁" };
            var conditions = new List<Condition>();
            foreach (string name in names) conditions.Add(new PropertyCondition(AutomationElement.NameProperty, name));
            var namesCondition = new OrCondition(conditions.ToArray());
            AutomationElementCollection found = root.FindAll(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), namesCondition));
            Rect bounds = SafeBounds(root);
            var candidates = new List<AutomationElement>();
            foreach (AutomationElement button in found)
                if (!SafeOffscreen(button) && SafeEnabled(button) && SupportsInvoke(button) &&
                    IsWorkspaceHeader(bounds, SafeBounds(button), true)) candidates.Add(button);
            return candidates.Count == 1 ? candidates[0] : null;
        }

        internal static bool IsWorkspaceHeader(Rect root, Rect target, bool layoutButton)
        {
            if (root.IsEmpty || target.IsEmpty || target.Top < root.Top || target.Left < root.Left ||
                target.Right > root.Right || target.Bottom > root.Top + (layoutButton ? 110 : 190)) return false;
            return !layoutButton || (target.Right >= root.Right - 180 && target.Width <= 100);
        }
    }
}
