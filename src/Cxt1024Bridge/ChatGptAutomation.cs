using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Automation;

namespace Cxt1024Bridge
{
    internal sealed partial class ChatGptAutomation
    {
        internal const int HighReasoningRank = 2;
        internal const int MaximumRotaryReasoningRank = 4;
        internal const int UltraReasoningRank = 5;
        private readonly ChatGptTaskSlotRegistry _slotRegistry;
        private readonly QuickChatToggleState _quickChatToggle = new QuickChatToggleState();
        private AutomationElement _conversationScrollContainer;
        private int _lastReasoningRank = -1;
        private int _lastReasoningSelectionAt;
        [ThreadStatic] private static Func<bool> _modelContext;
        private volatile Func<bool> _lastModelTaskGuard;

        // Read on the input thread without doing UIA there. Validation happens
        // on the dispatcher. Old turns may outlive 250ms only with this anchor.
        internal Func<bool> CaptureModelTaskGuard() { return _lastModelTaskGuard; }

        internal bool RunModelAction(Func<bool> context, Func<bool> action)
        {
            if (!context()) return false;
            AutomationElement selected = null;
            foreach (AutomationElement task in FindSidebarTasks(GetRoot()))
                if (SidebarSelectionScore(SafeClassName(task)) >= 3)
                {
                    if (selected != null) { selected = null; break; }
                    selected = task;
                }
            Func<bool> taskGuard = selected == null ? (Func<bool>)null : delegate
            {
                // Do not reacquire by task title: titles are not identities.
                return !SafeOffscreen(selected) && SidebarSelectionScore(SafeClassName(selected)) >= 3;
            };
            _lastModelTaskGuard = taskGuard;
            Func<bool> previous = _modelContext;
            bool cancelled = false;
            _modelContext = delegate
            {
                if (!cancelled) cancelled = !context() || (taskGuard != null && !taskGuard());
                return !cancelled;
            };
            try { return ModelContextCurrent() && action() && ModelContextCurrent(); }
            finally { _modelContext = previous; }
        }

        private static bool ModelContextCurrent()
        {
            return _modelContext == null || _modelContext();
        }

        internal ChatGptAutomation()
            : this(new ChatGptTaskSlotRegistry())
        {
        }

        internal ChatGptAutomation(ChatGptTaskSlotRegistry slotRegistry)
        {
            if (slotRegistry == null)
                throw new ArgumentNullException("slotRegistry");
            _slotRegistry = slotRegistry;
        }

        internal Dictionary<string, object> Probe()
        {
            var result = new Dictionary<string, object>();
            IntPtr window = FindWindow();
            result["windowFound"] = window != IntPtr.Zero;
            result["windowHandle"] = "0x" + window.ToInt64().ToString("X", CultureInfo.InvariantCulture);
            AutomationElement root = GetRoot();
            result["rootBounds"] = DescribeBounds(SafeBounds(root), SafeBounds(root));
            result["listItemDiagnostics"] = DescribeListItems(root);
            var catalogSlots = new List<object>();
            List<ChatGptThreadSlot> recentThreads = ChatGptThreadCatalog.ReadRecent(6);
            for (int i = 0; i < recentThreads.Count; i++)
            {
                catalogSlots.Add(new Dictionary<string, object>
                {
                    { "slot", i + 1 },
                    { "id", recentThreads[i].Id },
                    { "title", recentThreads[i].Title },
                    { "recencyAtMs", recentThreads[i].RecencyAtMilliseconds },
                    { "rolloutAvailable", !string.IsNullOrWhiteSpace(recentThreads[i].RolloutPath) }
                });
            }
            result["microStyleRecentSlots"] = catalogSlots;
            var taskNames = new List<object>();
            List<AutomationElement> tasks = FindSidebarTasks(root);
            string selectedTask = null;
            int selectedScore = 0;
            for (int i = 0; i < tasks.Count; i++)
            {
                taskNames.Add(SafeName(tasks[i]));
                int score = SidebarSelectionScore(SafeClassName(tasks[i]));
                if (score > selectedScore)
                {
                    selectedScore = score;
                    selectedTask = SafeName(tasks[i]);
                }
            }
            result["taskSlotsFound"] = tasks.Count;
            result["taskSlots"] = taskNames;
            result["selectedSidebarTask"] = selectedScore >= 3 ? selectedTask : null;
            AutomationElement reasoning = FindReasoningSelector(root);
            result["reasoningSelector"] = reasoning == null ? null : SafeName(reasoning);
            System.Windows.Rect rootBounds = SafeBounds(root);
            result["topLevelNewTaskButton"] = DescribeElement(FindTopLevelNewTaskButton(root), rootBounds);
            result["temporaryTaskButton"] = DescribeElement(FindButton(root, TemporaryTaskButtonNames), rootBounds);
            result["sidebarToggleButton"] = DescribeElement(FindSidebarToggle(root), rootBounds);
            result["taskState"] = ReadTaskState().ToString();
            result["writesSent"] = false;
            result["uiInvoked"] = false;
            return result;
        }

        private static List<object> DescribeListItems(AutomationElement root)
        {
            var result = new List<object>();
            if (root == null)
                return result;
            try
            {
                System.Windows.Rect rootBounds = SafeBounds(root);
                var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
                AutomationElementCollection items = root.FindAll(TreeScope.Descendants, condition);
                for (int i = 0; i < items.Count; i++)
                {
                    AutomationElement item = items[i];
                    string name = SafeName(item);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    AutomationElement button = FindDescendantButton(item, name);
                    AutomationElement parent = SafeParent(item);
                    AutomationElement grandparent = SafeParent(parent);
                    var detail = new Dictionary<string, object>();
                    detail["name"] = name;
                    detail["isOffscreen"] = SafeOffscreen(item);
                    detail["item"] = DescribeElement(item, rootBounds);
                    detail["button"] = DescribeElement(button, rootBounds);
                    detail["parent"] = DescribeElement(parent, rootBounds);
                    detail["grandparent"] = DescribeElement(grandparent, rootBounds);
                    detail["buttonSupportsInvoke"] = button != null && SupportsInvoke(button);
                    result.Add(detail);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to describe ChatGPT list items", ex);
            }
            return result;
        }

        private static Dictionary<string, object> DescribeElement(AutomationElement element, System.Windows.Rect rootBounds)
        {
            if (element == null)
                return null;
            var result = new Dictionary<string, object>();
            result["name"] = SafeName(element);
            result["controlType"] = SafeControlType(element);
            result["automationId"] = SafeAutomationId(element);
            result["className"] = SafeClassName(element);
            result["bounds"] = DescribeBounds(SafeBounds(element), rootBounds);
            return result;
        }

        private static Dictionary<string, object> DescribeBounds(System.Windows.Rect bounds, System.Windows.Rect rootBounds)
        {
            if (bounds.IsEmpty)
                return null;
            var result = new Dictionary<string, object>();
            result["left"] = Math.Round(bounds.Left, 1);
            result["top"] = Math.Round(bounds.Top, 1);
            result["width"] = Math.Round(bounds.Width, 1);
            result["height"] = Math.Round(bounds.Height, 1);
            result["relativeLeft"] = rootBounds.IsEmpty ? 0.0 : Math.Round(bounds.Left - rootBounds.Left, 1);
            result["relativeTop"] = rootBounds.IsEmpty ? 0.0 : Math.Round(bounds.Top - rootBounds.Top, 1);
            return result;
        }

        internal bool SwitchTaskSlot(int slot)
        {
            return SwitchTaskSlot(slot, false);
        }

        internal bool SwitchTaskSlot(int slot, bool focusWindow)
        {
            if (slot < 1 || slot > 6)
                throw new ArgumentOutOfRangeException("slot");

            try
            {
                ChatGptThreadSlot assigned = _slotRegistry.GetSlot(slot);
                if (assigned != null)
                    return OpenThreadDeepLink(assigned, focusWindow);
                Logger.Warn("ChatGPT stable task slot " + slot + " is unavailable.");
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to read ChatGPT task slots from its local catalog; falling back to UI Automation", ex);
            }

            AutomationElement root = GetRoot();
            if (root == null)
                return false;

            List<AutomationElement> tasks = FindSidebarTasks(root);
            if (slot > tasks.Count)
            {
                Logger.Warn("ChatGPT task slot " + slot + " is unavailable; only " + tasks.Count + " sidebar tasks were found.");
                return false;
            }
            bool invoked = Invoke(tasks[slot - 1]);
            if (invoked && focusWindow)
                FocusWindow();
            return invoked;
        }

        internal bool OpenThreadDeepLink(ChatGptThreadSlot thread, bool focusWindow)
        {
            if (thread == null)
                return false;
            Guid parsed;
            if (!Guid.TryParse(thread.Id, out parsed))
                return false;
            IntPtr previousForeground = NativeMethods.GetForegroundWindow();
            if (focusWindow && !FocusWindow())
                Logger.Warn("ChatGPT could not be focused before opening the requested task; retrying after the deep link.");
            var start = new ProcessStartInfo("codex://threads/" + thread.Id)
            {
                UseShellExecute = true
            };
            Process.Start(start);
            Logger.Info(
                "ChatGPT task slot opened via deep link: " + ShortTitle(thread.Title) +
                " (" + thread.Id + "); focus=" + focusWindow);

            ThreadPool.QueueUserWorkItem(delegate
            {
                if (focusWindow)
                {
                    int[] delays = { 90, 180, 320 };
                    for (int i = 0; i < delays.Length; i++)
                    {
                        Thread.Sleep(delays[i]);
                        if (FocusWindow())
                        {
                            Logger.Info("ChatGPT foreground activation confirmed after task switch.");
                            return;
                        }
                    }
                    Logger.Warn("ChatGPT task switched, but Windows kept another application in the foreground.");
                    return;
                }
                Thread.Sleep(220);
                IntPtr chatGptWindow = FindWindow();
                if (previousForeground != IntPtr.Zero && previousForeground != chatGptWindow)
                {
                    NativeMethods.SetForegroundWindow(previousForeground);
                    Thread.Sleep(260);
                    NativeMethods.SetForegroundWindow(previousForeground);
                }
            });
            return true;
        }

        private static string ShortTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "(untitled)";
            string normalized = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= 72 ? normalized : normalized.Substring(0, 72) + "…";
        }

        internal bool FocusWindow()
        {
            IntPtr window = FindWindow();
            if (window == IntPtr.Zero)
                return false;
            return ForegroundWindowActivator.Activate(window);
        }

        internal bool ToggleFastMode()
        {
            return SetSpeedMode(null);
        }

        internal bool SetFastMode()
        {
            return SetSpeedMode(true);
        }

        internal bool SetStandardMode()
        {
            return SetSpeedMode(false);
        }

        private bool SetSpeedMode(bool? requestedFast)
        {
            if (!PrepareForReasoningInteraction())
                return false;
            AutomationElement root = GetRoot();
            AutomationElement selector = FindReasoningSelector(root);
            if (selector == null || !EnsureExpanded(selector))
                return false;
            try
            {
                AutomationElement compactToggle = WaitForCompactFastToggle(450);
                if (compactToggle != null)
                    return SetCompactFastMode(compactToggle, requestedFast);

                AutomationElement speed = WaitForSpeedRow(700);
                if (speed == null || !EnsureExpanded(speed))
                    return false;
                bool currentlyFast = IsFastSpeedName(SafeName(speed));
                bool currentlyStandard = IsStandardSpeedName(SafeName(speed));
                if (!currentlyFast && !currentlyStandard)
                    return false;
                bool targetFast = ResolveSpeedTarget(currentlyFast, requestedFast);
                if (targetFast == currentlyFast)
                    return true;
                AutomationElement target = WaitForSpeedOption(targetFast, 600);
                return target != null && Invoke(target);
            }
            finally
            {
                CloseIntelligenceMenu();
            }
        }

        internal static bool ResolveSpeedTarget(bool currentlyFast, bool? requestedFast)
        {
            return requestedFast.HasValue ? requestedFast.Value : !currentlyFast;
        }

        internal static bool IsSpeedRowName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string trimmed = name.Trim();
            return trimmed.StartsWith("Speed ", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("速度", StringComparison.Ordinal);
        }

        internal static bool IsFastSpeedName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string trimmed = name.Trim();
            return trimmed.Equals("Fast", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("Fast ", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.EndsWith(" Fast", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.IndexOf("快速", StringComparison.Ordinal) >= 0;
        }

        internal static bool IsStandardSpeedName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string trimmed = name.Trim();
            return trimmed.Equals("Standard", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("Standard ", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.EndsWith(" Standard", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.IndexOf("标准", StringComparison.Ordinal) >= 0 ||
                   trimmed.Equals("普通", StringComparison.Ordinal) ||
                   trimmed.StartsWith("普通 ", StringComparison.Ordinal) ||
                   trimmed.EndsWith(" 普通", StringComparison.Ordinal);
        }

        private static readonly string[] GlobalNewTaskButtonNames =
        {
            "New chat",
            "New task",
            "New conversation",
            "新任务",
            "新建聊天",
            "新对话"
        };

        private static readonly string[] RecentSectionButtonNames =
        {
            "Recent",
            "Recents",
            "最近"
        };

        private static readonly string[] TemporaryTaskButtonNames =
        {
            "Quick chat",
            "Temporary chat",
            "Quick conversation",
            "快速聊天",
            "临时聊天"
        };

        internal static bool IsGlobalNewTaskButtonName(string name)
        {
            return NameEquals(name, GlobalNewTaskButtonNames);
        }

        internal static bool IsTemporaryTaskButtonName(string name)
        {
            return NameEquals(name, TemporaryTaskButtonNames);
        }

        internal static bool IsTopLevelNewTaskButtonGeometry(
            System.Windows.Rect candidateBounds,
            System.Windows.Rect recentSectionBounds)
        {
            if (candidateBounds.IsEmpty || recentSectionBounds.IsEmpty ||
                candidateBounds.Width <= 0 || candidateBounds.Height <= 0 ||
                recentSectionBounds.Width <= 0 || recentSectionBounds.Height <= 0)
                return false;

            double candidateCenterY = candidateBounds.Top + (candidateBounds.Height / 2.0);
            double recentCenterY = recentSectionBounds.Top + (recentSectionBounds.Height / 2.0);
            double verticalTolerance = Math.Max(8.0, recentSectionBounds.Height * 0.45);
            return Math.Abs(candidateCenterY - recentCenterY) <= verticalTolerance &&
                   candidateBounds.Left >= recentSectionBounds.Right - 6.0;
        }

        private static AutomationElement FindTopLevelNewTaskButton(AutomationElement root)
        {
            AutomationElement recentSection = FindButton(root, RecentSectionButtonNames);
            System.Windows.Rect recentBounds = SafeBounds(recentSection);
            if (recentSection == null || recentBounds.IsEmpty)
                return null;

            try
            {
                var condition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.Button);
                AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants, condition);
                AutomationElement best = null;
                double bestVerticalDistance = double.MaxValue;
                for (int i = 0; i < buttons.Count; i++)
                {
                    AutomationElement candidate = buttons[i];
                    if (SafeOffscreen(candidate) ||
                        !IsGlobalNewTaskButtonName(SafeName(candidate)))
                        continue;
                    System.Windows.Rect candidateBounds = SafeBounds(candidate);
                    if (!IsTopLevelNewTaskButtonGeometry(candidateBounds, recentBounds))
                        continue;
                    double verticalDistance = Math.Abs(
                        (candidateBounds.Top + candidateBounds.Height / 2.0) -
                        (recentBounds.Top + recentBounds.Height / 2.0));
                    if (verticalDistance < bestVerticalDistance)
                    {
                        best = candidate;
                        bestVerticalDistance = verticalDistance;
                    }
                }
                return best;
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to locate the top-level ChatGPT new-task button", ex);
                return null;
            }
        }

        internal bool ToggleTemporaryTask()
        {
            if (_quickChatToggle.ConsumeCloseRequest())
            {
                try
                {
                    InputSender.Tap(Vk.Escape);
                    Logger.Info("Closed ChatGPT Quick chat with Escape.");
                    return true;
                }
                catch
                {
                    _quickChatToggle.MarkOpened();
                    throw;
                }
            }

            if (InvokeButton(GetRoot(), TemporaryTaskButtonNames))
            {
                _quickChatToggle.MarkOpened();
                Logger.Info("Opened ChatGPT Quick chat from its visible UI button.");
                return true;
            }
            Logger.Warn("ChatGPT Quick chat button was not found; refusing the obsolete Ctrl+Shift+N shortcut.");
            return false;
        }

        internal bool OpenSideChat()
        {
            InputSender.Chord(Vk.S, Vk.LeftControl, Vk.LeftAlt);
            return true;
        }

        internal bool OpenAttachmentMenu()
        {
            return InvokeButton(
                GetRoot(),
                "Attach files or connect apps",
                "Add files and more",
                "添加文件或连接应用",
                "添加文件及更多");
        }

        internal bool ToggleCurrentTaskPin()
        {
            InputSender.Chord(Vk.P, Vk.LeftControl, Vk.LeftAlt);
            return true;
        }

        internal bool OpenScheduled()
        {
            return InvokeButton(GetRoot(), "Scheduled", "计划任务", "定时任务");
        }

        internal bool ToggleBottomPanel()
        {
            InputSender.Chord(Vk.J, Vk.LeftControl);
            return true;
        }

        internal bool ToggleSidePanel()
        {
            InputSender.Chord(Vk.B, Vk.LeftControl, Vk.LeftAlt);
            return true;
        }

        internal bool CopyLatestResponse()
        {
            AutomationElement root = GetRoot();
            if (root == null)
                return false;
            try
            {
                var condition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.Button);
                AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants, condition);
                AutomationElement latest = null;
                double latestTop = double.MinValue;
                for (int i = 0; i < buttons.Count; i++)
                {
                    AutomationElement button = buttons[i];
                    if (!NameEquals(SafeName(button), "Copy message", "复制消息", "复制回复") ||
                        SafeOffscreen(button) || !SafeEnabled(button) || !SupportsInvoke(button))
                        continue;
                    System.Windows.Rect bounds = SafeBounds(button);
                    if (bounds.IsEmpty || bounds.Top <= latestTop)
                        continue;
                    latest = button;
                    latestTop = bounds.Top;
                }
                if (latest == null)
                {
                    Logger.Warn("No visible ChatGPT response copy button was found.");
                    return false;
                }
                return Invoke(latest);
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to copy the latest ChatGPT response", ex);
                return false;
            }
        }

        internal bool ForkCurrentTask()
        {
            AutomationElement root = GetRoot();
            if (root == null)
                return false;
            try
            {
                var condition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.Button);
                AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants, condition);
                AutomationElement latest = null;
                double latestTop = double.MinValue;
                for (int i = 0; i < buttons.Count; i++)
                {
                    AutomationElement button = buttons[i];
                    string name = SafeName(button);
                    if (!IsForkButtonName(name) ||
                        SafeOffscreen(button) ||
                        !SafeEnabled(button) ||
                        !SupportsInvoke(button))
                        continue;
                    System.Windows.Rect bounds = SafeBounds(button);
                    if (bounds.IsEmpty || bounds.Top <= latestTop)
                        continue;
                    latest = button;
                    latestTop = bounds.Top;
                }
                if (latest == null)
                {
                    Logger.Warn("ChatGPT current task cannot be forked because no completed response fork button is visible.");
                    return false;
                }
                bool invoked = Invoke(latest);
                if (invoked)
                    Logger.Info("ChatGPT current task forked from the latest completed response.");
                return invoked;
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to fork the current ChatGPT task", ex);
                return false;
            }
        }

        internal static bool IsForkButtonName(string name)
        {
            return NameEquals(
                name,
                "Create chat branch from here",
                "Create a chat branch from here",
                "Continue in new chat from here",
                "Continue in a new chat from here",
                "从这里创建聊天分支",
                "从此处创建聊天分支",
                "从此处在新聊天中继续",
                "从这里在新聊天中继续");
        }

        private static AutomationElement FindBuiltInBrowserUrlInput(AutomationElement root)
        {
            if (root == null)
                return null;
            try
            {
                var condition = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                AutomationElementCollection inputs = root.FindAll(TreeScope.Descendants, condition);
                for (int i = inputs.Count - 1; i >= 0; i--)
                {
                    AutomationElement input = inputs[i];
                    if (IsBuiltInBrowserUrlInputName(SafeName(input)) &&
                        !SafeOffscreen(input) &&
                        SafeEnabled(input))
                        return input;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            return null;
        }

        internal static bool IsBuiltInBrowserUrlInputName(string name)
        {
            return NameEquals(
                name,
                "Search or enter URL",
                "Search or enter a URL",
                "Search or enter address",
                "搜索或输入网址",
                "Input URL",
                "输入 URL");
        }

        internal bool StopOrClearComposer()
        {
            AutomationElement root = GetRoot();
            AutomationElement stop = FindButton(root, "Stop", "Stop generating", "停止", "停止生成");
            if (stop != null)
                return Invoke(stop);
            FocusComposer();
            InputSender.Chord(Vk.A, Vk.LeftControl);
            InputSender.Tap(Vk.Back);
            return true;
        }

        internal bool ToggleSidebar()
        {
            AutomationElement root = GetRoot();
            AutomationElement toggle = FindSidebarToggle(root);
            return toggle != null && Invoke(toggle);
        }

        private static AutomationElement FindSidebarToggle(AutomationElement root)
        {
            if (root == null)
                return null;
            var condition = new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Button);
            AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants, condition);
            AutomationElement classFallback = null;
            for (int i = 0; i < buttons.Count; i++)
            {
                AutomationElement button = buttons[i];
                if (SafeOffscreen(button) || !SafeEnabled(button) || !SupportsInvoke(button))
                    continue;
                string name = SafeName(button);
                string className = SafeClassName(button);
                if (IsSidebarToggleIdentity(name, className, false))
                    return button;
                if (classFallback == null && IsSidebarToggleIdentity(name, className, true))
                    classFallback = button;
            }
            return classFallback;
        }

        internal static bool IsSidebarToggleIdentity(string name, string className, bool allowClassFallback)
        {
            if (NameEquals(
                name,
                "Hide sidebar",
                "Show sidebar",
                "隐藏边栏",
                "显示边栏",
                "隐藏侧边栏",
                "显示侧边栏"))
                return true;
            return allowClassFallback &&
                !string.IsNullOrWhiteSpace(className) &&
                className.IndexOf("group/sidebar-trigger", StringComparison.Ordinal) >= 0;
        }

        internal bool NavigateBack()
        {
            return InvokeButton(GetRoot(), "Back", "后退");
        }

        internal bool NavigateForward()
        {
            return InvokeButton(GetRoot(), "Forward", "前进");
        }

        internal bool NavigateAdjacentSidebarTask(bool forward)
        {
            AutomationElement root = GetRoot();
            if (root == null)
                return false;
            try
            {
                // The broad .sidebar-item query also includes project headings
                // such as CXT12E4, AINovel and PRD. FindSidebarTasks keeps only
                // ListItem -> same-name invokable Button task rows.
                List<AutomationElement> tasks = FindSidebarTasks(root);
                var scores = new List<int>(tasks.Count);
                for (int i = 0; i < tasks.Count; i++)
                    scores.Add(SidebarSelectionScore(SafeClassName(tasks[i])));
                int targetIndex = AdjacentSidebarTaskIndex(scores, forward);
                if (targetIndex < 0)
                    return false;
                bool invoked = Invoke(tasks[targetIndex]);
                if (invoked)
                {
                    ClearReasoningSelectionMemory();
                    string targetName = SafeName(tasks[targetIndex]);
                    // Let the authoritative selection catch up before the next
                    // queued detent. Never remember titles as task identities:
                    // duplicate titles and manual switches must remain safe.
                    int started = Environment.TickCount;
                    while (SidebarSelectionScore(SafeClassName(tasks[targetIndex])) < 3 &&
                        unchecked(Environment.TickCount - started) < 500)
                    {
                        Thread.Sleep(25);
                    }
                    Logger.Info("ChatGPT sidebar task selected: " + targetName);
                }
                return invoked;
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to navigate adjacent ChatGPT sidebar task", ex);
                return false;
            }
        }

        internal static int AdjacentSidebarTaskIndex(IList<int> selectionScores, bool forward)
        {
            if (selectionScores == null)
                return -1;
            int selected = -1;
            int bestScore = 2;
            bool ambiguous = false;
            for (int i = 0; i < selectionScores.Count; i++)
            {
                if (selectionScores[i] > bestScore)
                {
                    selected = i;
                    bestScore = selectionScores[i];
                    ambiguous = false;
                }
                else if (selectionScores[i] == bestScore && selected >= 0)
                    ambiguous = true;
            }
            if (selected < 0 || ambiguous)
                return -1;
            int target = selected + (forward ? 1 : -1);
            return target >= 0 && target < selectionScores.Count ? target : -1;
        }

        private static bool IsSidebarTaskName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            return !string.Equals(name, "New chat", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(name, "Sites", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(name, "Scheduled", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(name, "Plugins", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(name, "Open profile menu", StringComparison.OrdinalIgnoreCase);
        }

        internal static int SidebarSelectionScore(string className)
        {
            // Current Codex/ChatGPT builds append this exact class token to the
            // active thread.  The two longer Tailwind variants also contain the
            // same text, so a substring check would mark every thread selected.
            int score = HasCssClassToken(className, "bg-primary-ghost-hover") ? 10 : 0;
            const string marker = "bg-token-list-hover-background";
            int offset = 0;
            while (!string.IsNullOrEmpty(className))
            {
                int index = className.IndexOf(marker, offset, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    break;
                score++;
                offset = index + marker.Length;
            }
            return score;
        }

        private static bool HasCssClassToken(string className, string token)
        {
            if (string.IsNullOrWhiteSpace(className) || string.IsNullOrEmpty(token))
                return false;
            string[] tokens = className.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (string.Equals(tokens[i], token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal bool FocusComposer()
        {
            AutomationElement root = GetRoot();
            if (root == null)
                return false;
            AutomationElement edit = FindComposer(root);
            if (edit == null)
                return false;
            try
            {
                edit.SetFocus();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to focus the ChatGPT composer", ex);
                return false;
            }
        }

        internal bool ScrollConversation(bool upward)
        {
            return ScrollConversation(upward ? 120 : -120);
        }

        internal bool ScrollConversation(int wheelDelta)
        {
            if (wheelDelta == 0)
                return true;

            IntPtr window = FindWindow();
            AutomationElement root = GetRoot();
            System.Windows.Rect bounds = SafeBounds(root);
            if (window != IntPtr.Zero && !bounds.IsEmpty)
            {
                System.Windows.Rect composerBounds = SafeBounds(FindComposer(root));
                int x;
                int y;
                if (!composerBounds.IsEmpty)
                {
                    x = (int)Math.Round(composerBounds.Left + composerBounds.Width * 0.50);
                    y = (int)Math.Round(Math.Max(bounds.Top + 80, composerBounds.Top - 300));
                }
                else
                {
                    x = (int)Math.Round(bounds.Left + bounds.Width * 0.67);
                    y = (int)Math.Round(bounds.Top + bounds.Height * 0.72);
                }
                int packedPoint = unchecked(
                    (int)(ushort)(short)x |
                    ((int)(ushort)(short)y << 16));
                int packedWheel = unchecked(((int)(short)wheelDelta) << 16);
                if (NativeMethods.PostMessage(
                    window,
                    NativeMethods.WmMouseWheel,
                    new IntPtr(packedWheel),
                    new IntPtr(packedPoint)))
                    return true;
            }

            AutomationElement container = FindConversationScrollContainer(false);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    object pattern;
                    if (container != null && container.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern))
                    {
                        var scroll = (ScrollPattern)pattern;
                        if (scroll.Current.VerticallyScrollable)
                        {
                            ScrollAmount amount = wheelDelta > 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement;
                            int steps = Math.Max(1, Math.Min(12, Math.Abs(wheelDelta) / 120));
                            for (int step = 0; step < steps; step++)
                                scroll.Scroll(ScrollAmount.NoAmount, amount);
                            return true;
                        }
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                _conversationScrollContainer = null;
                container = FindConversationScrollContainer(true);
            }
            return false;
        }

        internal bool ScrollConversationToBottom()
        {
            AutomationElement root = GetRoot();
            AutomationElement button = FindButton(
                root,
                "Scroll to bottom",
                "Jump to the latest message",
                "Jump to latest message",
                "滚动到底部",
                "跳转到最新消息");
            if (button != null && Invoke(button))
                return true;

            AutomationElement container = FindConversationScrollContainer(false);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    object pattern;
                    if (container != null && container.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern))
                    {
                        var scroll = (ScrollPattern)pattern;
                        if (scroll.Current.VerticallyScrollable)
                        {
                            scroll.SetScrollPercent(ScrollPattern.NoScroll, 100.0);
                            return true;
                        }
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                _conversationScrollContainer = null;
                container = FindConversationScrollContainer(true);
            }

            return false;
        }

        private static AutomationElement FindComposer(AutomationElement root)
        {
            if (root == null)
                return null;
            AutomationElement fallback = null;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            AutomationElementCollection edits = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < edits.Count; i++)
            {
                AutomationElement edit = edits[i];
                if (SafeOffscreen(edit))
                    continue;
                if (fallback == null)
                    fallback = edit;
                string className = SafeClassName(edit);
                string name = SafeName(edit);
                if (className.IndexOf("ProseMirror", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("ChatGPT", StringComparison.OrdinalIgnoreCase) >= 0)
                    return edit;
            }
            return fallback;
        }

        internal bool AdjustReasoning(bool increase)
        {
            var adjustment = new ReasoningAdjustment();
            adjustment.Add(increase);
            return AdjustReasoning(adjustment);
        }

        internal bool AdjustReasoning(ReasoningAdjustment adjustment)
        {
            if (adjustment == null || !PrepareForReasoningInteraction())
                return false;
            AutomationElement selector = FindReasoningSelector(GetRoot());
            if (selector == null)
                return false;
            bool astra = IsAstraReasoningSelectorName(SafeName(selector));
            bool sol = IsSolReasoningSelectorName(SafeName(selector));
            // A generic Power or unrelated model has no unambiguous place in
            // this two-model ladder. Start from Astra only after pinning it.
            if (!astra && !sol)
            {
                if (!EnsureAstraModelSelected()) return false;
                astra = true;
                selector = FindReasoningSelector(GetRoot());
            }
            int currentRank = GetReasoningRank(SafeName(selector));
            if (!EnsureExpanded(selector))
                return false;
            AutomationElement compactSlider = WaitForCompactReasoningSlider(450);
            if (compactSlider != null)
                currentRank = ReadCompactReasoningRank(GetRoot(), astra);
            CloseIntelligenceMenu();
            if (currentRank < 0)
                return ModelActionFailed("current model effort is unavailable");
            int target = adjustment.Target(astra, currentRank);
            if (target < 0)
                return ModelActionFailed("current model effort is outside the rotary ladder");
            bool targetAstra = target >= ReasoningAdjustment.AstraOffset;
            int targetRank = targetAstra ? target - ReasoningAdjustment.AstraOffset : target;
            return SetReasoningRankForModel(targetAstra, targetRank);
        }

        private bool SetReasoningRankForModel(bool astra, int desiredRank)
        {
            if (desiredRank < 0 || desiredRank > (astra ? UltraReasoningRank : MaximumRotaryReasoningRank) ||
                !EnsureModelSelected(astra))
                return false;
            AutomationElement selector = FindReasoningSelector(GetRoot());
            if (selector == null) return false;
            int currentRank = GetReasoningRank(SafeName(selector));
            if (!EnsureExpanded(selector)) return false;
            AutomationElement compactSlider = WaitForCompactReasoningSlider(450);
            if (compactSlider != null)
            {
                bool changed = SelectCompactReasoningRank(compactSlider, desiredRank, astra);
                CloseIntelligenceMenu();
                return changed && VerifyModelReasoningSelection(astra, desiredRank);
            }

            List<AutomationElement> options;
            if (!OpenReasoningOptions(selector, ref currentRank, out options))
                return false;
            // The Chinese label for Extra High can overlap the purple Ultra
            // row. Without the compact slider's numbered status, fail closed.
            if (desiredRank == 3) { CloseIntelligenceMenu(); return ModelActionFailed("ambiguous Extra High fallback"); }
            AutomationElement target = options.Find(delegate(AutomationElement option)
            {
                return GetReasoningRank(SafeName(option)) == desiredRank;
            });
            if (target != null)
            {
                bool changed = SelectReasoningOption(target);
                CloseIntelligenceMenu();
                return changed && VerifyModelReasoningSelection(astra, desiredRank);
            }
            CloseIntelligenceMenu();
            return ModelActionFailed("requested reasoning option missing");
        }

        internal bool SetReasoningHigh()
        {
            return SetReasoningRank(HighReasoningRank);
        }

        internal bool SetReasoningUltra()
        {
            return SetReasoningRank(UltraReasoningRank);
        }

        internal bool SetReasoningUltraAndFast()
        {
            if (!SetReasoningUltra())
                return false;
            if (!SetFastMode())
            {
                Logger.Warn("Ultra was selected, but the ChatGPT speed control could not be set to Fast.");
                return false;
            }
            bool verified = VerifyAstraReasoningSelection(UltraReasoningRank);
            if (verified)
                Logger.Info("L1 K1 selected GPT-6 Astra Ultra with full-access handling and ensured Fast speed.");
            return verified;
        }

        internal bool SetReasoningHighAndStandard()
        {
            if (!SetReasoningHigh())
                return false;
            if (!SetStandardMode())
            {
                Logger.Warn("High was selected, but the ChatGPT speed control could not be set to Standard.");
                return false;
            }
            bool verified = VerifyAstraReasoningSelection(HighReasoningRank);
            if (verified)
                Logger.Info("L1 K2 selected GPT-6 Astra High reasoning and ensured Standard speed.");
            return verified;
        }

        private bool SetReasoningRank(int desiredRank)
        {
            if (desiredRank < 0 || desiredRank > UltraReasoningRank || !EnsureAstraModelSelected())
                return false;
            AutomationElement root = GetRoot();
            AutomationElement selector = FindReasoningSelector(root);
            if (selector == null)
                return false;

            int currentRank = GetReasoningRank(SafeName(selector));
            if (!EnsureExpanded(selector))
                return false;
            AutomationElement compactSlider = WaitForCompactReasoningSlider(450);
            if (compactSlider != null)
            {
                bool selected = SelectCompactReasoningRank(compactSlider, desiredRank);
                CloseIntelligenceMenu();
                return selected && VerifyAstraReasoningSelection(desiredRank);
            }

            List<AutomationElement> options;
            if (!OpenReasoningOptions(selector, ref currentRank, out options))
                return false;
            AutomationElement target = null;
            for (int i = 0; i < options.Count; i++)
            {
                int rank = GetReasoningRank(SafeName(options[i]));
                if (rank == desiredRank)
                {
                    target = options[i];
                    break;
                }
            }

            bool changed = target != null && SelectReasoningOption(target);
            CloseIntelligenceMenu();
            return changed && VerifyAstraReasoningSelection(desiredRank);
        }

        private bool PrepareForReasoningInteraction()
        {
            if (!ModelContextCurrent()) return false;
            // Every encoder gesture contains a private RightCtrl marker.  Wait
            // for its key-up before sending slider arrow keys so the marker
            // cannot modify or pre-empt the Codex command.
            if (!InputSender.WaitForKeyRelease(Vk.RightControl, 200))
            {
                Logger.Warn("Reasoning action skipped because the private RightCtrl encoder marker did not release within 200ms.");
                return false;
            }
            return ModelContextCurrent() && FocusWindow() && ModelContextCurrent();
        }

        private bool SelectCompactReasoningRank(AutomationElement slider, int desiredRank, bool astra = true)
        {
            if (!ModelContextCurrent() || slider == null || desiredRank < 0 || desiredRank > UltraReasoningRank)
                return false;
            try
            {
                slider.SetFocus();
                Thread.Sleep(40);
                IntPtr expectedWindow = FindWindow();
                if (expectedWindow == IntPtr.Zero || NativeMethods.GetForegroundWindow() != expectedWindow)
                    return ModelActionFailed("slider focus changed before adjustment");

                // The accessible live status exposes "position of 6" even
                // though the slider has no RangeValuePattern. Acknowledge each
                // detent before sending another; fixed 22ms bursts lose steps
                // when the app has not committed the previous React state.
                int current = ReadCompactReasoningRank(GetRoot(expectedWindow), astra);
                if (current < 0)
                    return ModelActionFailed("six-stop model slider status unavailable");
                for (int i = 0; current != desiredRank && i < UltraReasoningRank + 1; i++)
                {
                    if (!IsReasoningSliderFocused(expectedWindow))
                        return ModelActionFailed("slider focus changed before detent " + i);
                    int next = current + (desiredRank > current ? 1 : -1);
                    InputSender.Tap(next > current ? Vk.Right : Vk.Left);
                    if (next == UltraReasoningRank && !ConfirmUltraFullAccessIfPrompted())
                        return false;
                    int started = Environment.TickCount;
                    do
                    {
                        Thread.Sleep(40);
                        if (NativeMethods.GetForegroundWindow() != expectedWindow)
                            return ModelActionFailed("foreground changed while awaiting slider result");
                        current = ReadCompactReasoningRank(GetRoot(expectedWindow), astra);
                        // Confirming Ultra can dismiss the popup. Its closed
                        // model+effort label is then the observable result.
                        if (current < 0 && astra && next == UltraReasoningRank &&
                            AstraReasoningMatches(SafeName(FindReasoningSelector(GetRoot(expectedWindow))), next))
                            current = next;
                        if (current == next) break;
                    }
                    while (unchecked(Environment.TickCount - started) < 1000);
                    if (current != next)
                        return ModelActionFailed("slider did not acknowledge requested rank " + next);
                }
                bool completed = current == desiredRank;
                if (completed)
                    RememberReasoningRank(desiredRank);
                return completed;
            }
            catch (ElementNotAvailableException)
            {
                return ModelActionFailed("slider element replaced during adjustment");
            }
            catch (InvalidOperationException)
            {
                return ModelActionFailed("slider operation unavailable");
            }
        }

        private static bool ModelActionFailed(string stage)
        {
            Logger.Warn("Model action cancelled: " + stage + ".");
            return false;
        }

        private static bool IsReasoningSliderFocused(IntPtr expectedWindow)
        {
            if (!ModelContextCurrent() || NativeMethods.GetForegroundWindow() != expectedWindow)
                return false;
            AutomationElement focused = AutomationElement.FocusedElement;
            return focused != null && SafeEnabled(focused) && !SafeOffscreen(focused) &&
                IsCompactReasoningSliderIdentity(SafeName(focused), SafeClassName(focused)) &&
                NativeMethods.GetForegroundWindow() == expectedWindow;
        }

        private bool SelectReasoningOption(AutomationElement option)
        {
            if (option == null)
                return false;
            int rank = GetReasoningRank(SafeName(option));
            if (!Invoke(option))
                return false;
            bool completed = rank != 5 || ConfirmUltraFullAccessIfPrompted();
            if (completed)
                RememberReasoningRank(rank);
            return completed;
        }

        internal static int ResolveReasoningRank(
            int parsedRank,
            int rememberedRank,
            int rememberedElapsedMilliseconds,
            bool increase)
        {
            // The current Chinese UI gives both Extra High and the bottom
            // purple Ultra choice the visible selector text "极高". Preserve a
            // recent Bridge selection so a downward detent from Ultra reaches
            // Max instead of jumping to High.
            if (parsedRank == 3 &&
                rememberedRank == 5 &&
                rememberedElapsedMilliseconds >= 0 &&
                rememberedElapsedMilliseconds <= 10000 &&
                parsedRank != rememberedRank)
                return rememberedRank;

            return parsedRank;
        }

        internal static bool IsRotaryReasoningRank(int rank)
        {
            return rank >= 0 && rank <= MaximumRotaryReasoningRank;
        }

        private void RememberReasoningRank(int rank)
        {
            if (rank < 0)
                return;
            _lastReasoningRank = rank;
            _lastReasoningSelectionAt = Environment.TickCount;
        }

        internal void ClearReasoningSelectionMemory()
        {
            _lastReasoningRank = -1;
            _lastReasoningSelectionAt = 0;
        }

        private static bool ConfirmUltraFullAccessIfPrompted()
        {
            AutomationElement dialog = WaitForUltraFullAccessDialog(1200);
            if (dialog == null)
                return true;

            AutomationElement fullAccess = FindButton(
                dialog,
                "使用完整访问权限",
                "Use full access",
                "Use full access permissions");
            if (fullAccess == null || !Invoke(fullAccess))
            {
                Logger.Warn("Ultra permission dialog appeared, but the full-access choice was unavailable.");
                return false;
            }

            int started = Environment.TickCount;
            do
            {
                if (FindUltraFullAccessDialog(GetRoot()) == null)
                {
                    Logger.Info("Ultra enabled with full access permission.");
                    return true;
                }
                Thread.Sleep(40);
            }
            while (unchecked(Environment.TickCount - started) < 1000);

            Logger.Warn("Ultra full-access choice was invoked, but its confirmation dialog remained open.");
            return false;
        }

        private static AutomationElement WaitForUltraFullAccessDialog(int timeoutMilliseconds)
        {
            int started = Environment.TickCount;
            do
            {
                AutomationElement dialog = FindUltraFullAccessDialog(GetRoot());
                if (dialog != null)
                    return dialog;
                Thread.Sleep(40);
            }
            while (unchecked(Environment.TickCount - started) < timeoutMilliseconds);
            return null;
        }

        private static AutomationElement FindUltraFullAccessDialog(AutomationElement root)
        {
            if (root == null)
                return null;
            var condition = new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Window);
            AutomationElementCollection windows = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < windows.Count; i++)
            {
                if (!SafeOffscreen(windows[i]) &&
                    IsUltraFullAccessDialogName(SafeName(windows[i])))
                    return windows[i];
            }
            return null;
        }

        internal static bool IsUltraFullAccessDialogName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            return name.IndexOf("Ultra", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (name.IndexOf("完整访问权限", StringComparison.Ordinal) >= 0 ||
                 name.IndexOf("full access", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool OpenReasoningOptions(
            AutomationElement selector,
            ref int currentRank,
            out List<AutomationElement> options)
        {
            options = new List<AutomationElement>();
            if (!EnsureExpanded(selector))
                return false;

            // Newer Codex builds expose the selector as "5.6 Sol High/高" and
            // either show the effort choices directly or place them under an
            // "Effort/推理强度" row.  Older builds used only the English row.
            AutomationElement effort = WaitForReasoningEffortRow(450);
            if (effort == null)
            {
                options = FindReasoningOptions(GetRoot());
                if (options.Count > 0)
                    return true;

                // Codex 26.814 opens the compact Power/强度 slider first.  Its
                // Advanced/高级 toggle reveals the model-preserving Effort row.
                AutomationElement advanced = WaitForAdvancedReasoningToggle(450);
                if (advanced != null && Invoke(advanced))
                    effort = WaitForReasoningEffortRow(700);
            }
            if (effort != null)
            {
                int rowRank = GetReasoningRank(SafeName(effort));
                if (rowRank >= 0)
                    currentRank = rowRank;
                if (!EnsureExpanded(effort))
                {
                    Collapse(selector);
                    return false;
                }
            }

            options = WaitForReasoningOptions(700);
            if (options.Count > 0)
                return true;

            Collapse(selector);
            return false;
        }

        internal static bool IsAdvancedReasoningToggleName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string trimmed = name.Trim();
            return trimmed.Equals("Show advanced options", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("Advanced", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("显示高级模式选项", StringComparison.Ordinal) ||
                   trimmed.Equals("高级", StringComparison.Ordinal);
        }

        private static AutomationElement FindAdvancedReasoningToggle(AutomationElement root)
        {
            if (root == null)
                return null;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < elements.Count; i++)
            {
                if (!SafeOffscreen(elements[i]) &&
                    IsAdvancedReasoningToggleName(SafeName(elements[i])) &&
                    SupportsPattern(elements[i], InvokePattern.Pattern))
                    return elements[i];
            }
            return null;
        }

        private static AutomationElement WaitForAdvancedReasoningToggle(int timeoutMilliseconds)
        {
            int started = Environment.TickCount;
            do
            {
                AutomationElement result = FindAdvancedReasoningToggle(GetRoot());
                if (result != null)
                    return result;
                Thread.Sleep(40);
            }
            while (unchecked(Environment.TickCount - started) < timeoutMilliseconds);
            return null;
        }

        internal bool OpenConversationSearch()
        {
            InputSender.Chord(Vk.K, Vk.LeftControl);
            return true;
        }

        internal AiTaskState ReadTaskState()
        {
            AutomationElement root = GetRoot();
            if (root == null)
                return AiTaskState.Idle;
            bool waiting = false;
            var buttonCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants, buttonCondition);
            for (int i = 0; i < buttons.Count; i++)
            {
                if (SafeOffscreen(buttons[i]))
                    continue;
                string name = SafeName(buttons[i]);
                if (NameEquals(name, "Stop", "Stop generating", "停止", "停止生成"))
                    return AiTaskState.Thinking;
                if (NameEquals(name, "Approve", "Allow", "Continue", "批准", "允许", "继续"))
                    waiting = true;
            }
            if (waiting)
                return AiTaskState.WaitingForInput;
            if (FindNamedElement(root, "Something went wrong", "An error occurred", "出现错误") != null)
                return AiTaskState.Error;
            return AiTaskState.Idle;
        }

        private static IntPtr FindWindow()
        {
            Process[] processes = Process.GetProcessesByName("ChatGPT");
            try
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    if (processes[i].MainWindowHandle != IntPtr.Zero)
                        return processes[i].MainWindowHandle;
                }
            }
            finally
            {
                for (int i = 0; i < processes.Length; i++)
                    processes[i].Dispose();
            }
            return IntPtr.Zero;
        }

        private static AutomationElement GetRoot()
        {
            return GetRoot(FindWindow());
        }

        private static AutomationElement GetRoot(IntPtr window)
        {
            if (window == IntPtr.Zero)
                return null;
            try
            {
                return AutomationElement.FromHandle(window);
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to access the ChatGPT automation tree", ex);
                return null;
            }
        }

        private static List<AutomationElement> FindSidebarTasks(AutomationElement root)
        {
            var result = new List<AutomationElement>();
            if (root == null)
                return result;
            try
            {
                System.Windows.Rect rootBounds = root.Current.BoundingRectangle;
                // The task sidebar begins at the far-left edge. A wider threshold also
                // captures list items rendered inside the conversation (for example,
                // artifact/file links), which caused the first task keys to invoke the
                // conversation body instead of a sidebar task.
                double sidebarItemLeftLimit = rootBounds.Left + Math.Min(420.0, rootBounds.Width * 0.22);
                var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
                AutomationElementCollection items = root.FindAll(TreeScope.Descendants, condition);
                var candidates = new List<TaskCandidate>();
                for (int i = 0; i < items.Count; i++)
                {
                    AutomationElement item = items[i];
                    string name = SafeName(item);
                    if (string.IsNullOrWhiteSpace(name) || SafeOffscreen(item))
                        continue;
                    System.Windows.Rect bounds = SafeBounds(item);
                    if (bounds.IsEmpty || bounds.Left > sidebarItemLeftLimit)
                        continue;
                    AutomationElement button = FindDescendantButton(item, name);
                    if (button == null || !SafeEnabled(button) || !SupportsInvoke(button))
                        continue;
                    candidates.Add(new TaskCandidate { Element = button, Top = bounds.Top, Left = bounds.Left, Name = name });
                }
                candidates.Sort(delegate(TaskCandidate left, TaskCandidate right)
                {
                    int top = left.Top.CompareTo(right.Top);
                    return top != 0 ? top : left.Left.CompareTo(right.Left);
                });

                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < candidates.Count; i++)
                {
                    string key = candidates[i].Name + "|" + candidates[i].Top.ToString("F0", CultureInfo.InvariantCulture);
                    if (seen.Add(key))
                        result.Add(candidates[i].Element);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to enumerate ChatGPT task slots", ex);
            }
            return result;
        }

        private static AutomationElement FindDescendantButton(AutomationElement parent, string name)
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, name));
            return parent.FindFirst(TreeScope.Descendants, condition);
        }

        private static bool InvokeButton(AutomationElement root, params string[] names)
        {
            AutomationElement button = FindButton(root, names);
            return button != null && Invoke(button);
        }

        private static AutomationElement FindButton(AutomationElement root, params string[] names)
        {
            if (root == null || names == null)
                return null;
            for (int i = 0; i < names.Length; i++)
            {
                var condition = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, names[i]));
                AutomationElement button = root.FindFirst(TreeScope.Descendants, condition);
                if (button != null && !SafeOffscreen(button))
                    return button;
            }
            return null;
        }

        private static bool InvokeButtonContaining(AutomationElement root, string text)
        {
            if (root == null)
                return false;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < buttons.Count; i++)
            {
                string name = SafeName(buttons[i]);
                if (!SafeOffscreen(buttons[i]) && name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    return Invoke(buttons[i]);
            }
            return false;
        }

        private static AutomationElement FindReasoningSelector(AutomationElement root)
        {
            if (root == null)
                return null;
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true));
            AutomationElement result = MatchReasoningSelector(root.FindAll(TreeScope.Descendants, condition));
            if (result != null) return result;
            // Older app versions may expose only InvokePattern.
            return MatchReasoningSelector(root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        }

        private static AutomationElement MatchReasoningSelector(AutomationElementCollection buttons)
        {
            AutomationElement fallback = null;
            for (int i = 0; i < buttons.Count; i++)
            {
                AutomationElement button = buttons[i];
                if (SafeOffscreen(button))
                    continue;
                string name = SafeName(button);
                if (IsReasoningSelectorName(name))
                    return button;
                if (IsGenericReasoningSelectorName(name))
                    fallback = button;
            }
            return fallback;
        }

        internal static bool IsReasoningSelectorName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || GetReasoningRank(name) < 0)
                return false;
            string trimmed = name.Trim();
            if (trimmed.Length > 96 || trimmed.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                return false;
            if (trimmed.IndexOf("Astra", StringComparison.OrdinalIgnoreCase) >= 0)
                return IsAstraReasoningSelectorName(trimmed);
            return trimmed.StartsWith("Effort ", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("Reasoning effort ", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("推理强度", StringComparison.Ordinal) ||
                   trimmed.IndexOf("GPT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   trimmed.IndexOf("Sol", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   trimmed.IndexOf("Terra", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   trimmed.IndexOf("Luna", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsGenericReasoningSelectorName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string trimmed = name.Trim();
            return trimmed.Equals("Reasoning", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("Reasoning effort", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("Effort", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("Select effort", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("Select model", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("推理", StringComparison.Ordinal) ||
                   trimmed.Equals("推理强度", StringComparison.Ordinal) ||
                   trimmed.Equals("选择强度", StringComparison.Ordinal) ||
                   trimmed.Equals("选择模型", StringComparison.Ordinal);
        }

        internal static bool IsCompactReasoningSliderIdentity(string name, string className)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(className))
                return false;
            string trimmed = name.Trim();
            bool recognizedName = trimmed.Equals("Strength", StringComparison.OrdinalIgnoreCase) ||
                                  trimmed.Equals("Power", StringComparison.OrdinalIgnoreCase) ||
                                  trimmed.Equals("Effort", StringComparison.OrdinalIgnoreCase) ||
                                  trimmed.Equals("Reasoning effort", StringComparison.OrdinalIgnoreCase) ||
                                  trimmed.Equals("强度", StringComparison.Ordinal) ||
                                  trimmed.Equals("推理强度", StringComparison.Ordinal);
            return recognizedName &&
                   className.IndexOf("SliderKeyboardControl", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static AutomationElement FindCompactReasoningSlider(AutomationElement root)
        {
            if (root == null)
                return null;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < elements.Count; i++)
            {
                AutomationElement element = elements[i];
                if (!SafeOffscreen(element) &&
                    IsCompactReasoningSliderIdentity(SafeName(element), SafeClassName(element)) &&
                    SupportsPattern(element, InvokePattern.Pattern))
                    return element;
            }
            return null;
        }

        private static AutomationElement WaitForCompactReasoningSlider(int timeoutMilliseconds)
        {
            int started = Environment.TickCount;
            do
            {
                AutomationElement result = FindCompactReasoningSlider(GetRoot());
                if (result != null)
                    return result;
                Thread.Sleep(40);
            }
            while (unchecked(Environment.TickCount - started) < timeoutMilliseconds);
            return null;
        }

        private static List<AutomationElement> FindReasoningOptions(AutomationElement root)
        {
            var result = new List<AutomationElement>();
            if (root == null)
                return result;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < elements.Count; i++)
            {
                string name = SafeName(elements[i]);
                if (!SafeOffscreen(elements[i]) &&
                    !IsReasoningEffortRow(name) &&
                    GetReasoningRank(name) >= 0 &&
                    SupportsPattern(elements[i], InvokePattern.Pattern))
                    result.Add(elements[i]);
            }
            result.Sort(delegate(AutomationElement left, AutomationElement right)
            {
                return GetReasoningRank(SafeName(left)).CompareTo(GetReasoningRank(SafeName(right)));
            });
            return result;
        }

        private static List<AutomationElement> WaitForReasoningOptions(int timeoutMilliseconds)
        {
            int started = Environment.TickCount;
            List<AutomationElement> result;
            do
            {
                result = FindReasoningOptions(GetRoot());
                if (result.Count > 0)
                    return result;
                Thread.Sleep(40);
            }
            while (unchecked(Environment.TickCount - started) < timeoutMilliseconds);
            return result;
        }

        private static bool IsReasoningEffortRow(string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                (name.StartsWith("Effort ", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("Reasoning effort ", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("推理强度", StringComparison.Ordinal));
        }

        private static AutomationElement FindReasoningEffortRow(AutomationElement root)
        {
            if (root == null)
                return null;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < elements.Count; i++)
            {
                if (!SafeOffscreen(elements[i]) && IsReasoningEffortRow(SafeName(elements[i])))
                    return elements[i];
            }
            return null;
        }

        private static AutomationElement WaitForReasoningEffortRow(int timeoutMilliseconds)
        {
            int started = Environment.TickCount;
            do
            {
                AutomationElement result = FindReasoningEffortRow(GetRoot());
                if (result != null)
                    return result;
                List<AutomationElement> directOptions = FindReasoningOptions(GetRoot());
                if (directOptions.Count > 0)
                    return null;
                Thread.Sleep(40);
            }
            while (unchecked(Environment.TickCount - started) < timeoutMilliseconds);
            return null;
        }

        private static AutomationElement FindAdjacentReasoningOption(
            List<AutomationElement> options,
            int currentRank,
            bool increase,
            int maximumRank)
        {
            AutomationElement best = null;
            int bestRank = increase ? int.MaxValue : int.MinValue;
            for (int i = 0; i < options.Count; i++)
            {
                int rank = GetReasoningRank(SafeName(options[i]));
                if (rank < 0 || rank > maximumRank)
                    continue;
                if (increase && rank > currentRank && rank < bestRank)
                {
                    best = options[i];
                    bestRank = rank;
                }
                else if (!increase && rank < currentRank && rank > bestRank)
                {
                    best = options[i];
                    bestRank = rank;
                }
            }
            return best;
        }

        private static AutomationElement FindReasoningBoundaryOption(
            List<AutomationElement> options,
            bool highest,
            int maximumRank)
        {
            AutomationElement best = null;
            int bestRank = highest ? int.MinValue : int.MaxValue;
            for (int i = 0; i < options.Count; i++)
            {
                int rank = GetReasoningRank(SafeName(options[i]));
                if (rank < 0 || rank > maximumRank)
                    continue;
                if ((highest && rank > bestRank) || (!highest && rank < bestRank))
                {
                    best = options[i];
                    bestRank = rank;
                }
            }
            return best;
        }

        private static AutomationElement FindSpeedRow(AutomationElement root)
        {
            if (root == null)
                return null;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < elements.Count; i++)
            {
                if (!SafeOffscreen(elements[i]) && IsSpeedRowName(SafeName(elements[i])))
                    return elements[i];
            }
            return null;
        }

        internal static bool IsCompactFastToggleName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string trimmed = name.Trim();
            return trimmed.Equals("Enable fast mode", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("Enable standard mode", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("Fast mode", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("启用快速模式", StringComparison.Ordinal) ||
                   trimmed.Equals("启用标准模式", StringComparison.Ordinal) ||
                   trimmed.Equals("快速模式", StringComparison.Ordinal);
        }

        private static AutomationElement FindCompactFastToggle(AutomationElement root)
        {
            if (root == null)
                return null;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox);
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < elements.Count; i++)
            {
                AutomationElement element = elements[i];
                if (!SafeOffscreen(element) &&
                    IsCompactFastToggleName(SafeName(element)) &&
                    SupportsPattern(element, TogglePattern.Pattern))
                    return element;
            }
            return null;
        }

        private static AutomationElement WaitForCompactFastToggle(int timeoutMilliseconds)
        {
            int started = Environment.TickCount;
            do
            {
                AutomationElement result = FindCompactFastToggle(GetRoot());
                if (result != null)
                    return result;
                Thread.Sleep(40);
            }
            while (unchecked(Environment.TickCount - started) < timeoutMilliseconds);
            return null;
        }

        private static bool SetCompactFastMode(AutomationElement toggle, bool? requestedFast)
        {
            try
            {
                object patternObject;
                if (toggle == null ||
                    !toggle.TryGetCurrentPattern(TogglePattern.Pattern, out patternObject))
                    return false;
                var pattern = (TogglePattern)patternObject;
                ToggleState state = pattern.Current.ToggleState;
                if (state == ToggleState.Indeterminate)
                    return false;
                bool currentlyFast = state == ToggleState.On;
                bool targetFast = ResolveSpeedTarget(currentlyFast, requestedFast);
                if (targetFast == currentlyFast)
                    return true;
                if (!ModelContextCurrent()) return false;
                pattern.Toggle();
                int started = Environment.TickCount;
                do
                {
                    Thread.Sleep(40);
                    AutomationElement current = FindCompactFastToggle(GetRoot());
                    object currentPattern;
                    if (current != null && current.TryGetCurrentPattern(TogglePattern.Pattern, out currentPattern) &&
                        ((TogglePattern)currentPattern).Current.ToggleState == (targetFast ? ToggleState.On : ToggleState.Off))
                        return true;
                }
                while (unchecked(Environment.TickCount - started) < 700);
                return false;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static AutomationElement FindSpeedOption(AutomationElement root, bool fast)
        {
            if (root == null)
                return null;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < elements.Count; i++)
            {
                string name = SafeName(elements[i]);
                if (!SafeOffscreen(elements[i]) &&
                    !IsSpeedRowName(name) &&
                    (fast ? IsFastSpeedName(name) : IsStandardSpeedName(name)) &&
                    SupportsInvoke(elements[i]))
                    return elements[i];
            }
            return null;
        }

        private static AutomationElement WaitForSpeedRow(int timeoutMilliseconds)
        {
            int started = Environment.TickCount;
            do
            {
                AutomationElement result = FindSpeedRow(GetRoot());
                if (result != null)
                    return result;
                Thread.Sleep(40);
            }
            while (unchecked(Environment.TickCount - started) < timeoutMilliseconds);
            return null;
        }

        private static AutomationElement WaitForSpeedOption(bool fast, int timeoutMilliseconds)
        {
            int started = Environment.TickCount;
            do
            {
                AutomationElement result = FindSpeedOption(GetRoot(), fast);
                if (result != null)
                    return result;
                Thread.Sleep(40);
            }
            while (unchecked(Environment.TickCount - started) < timeoutMilliseconds);
            return null;
        }

        internal static int GetReasoningRank(string name)
        {
            if (string.IsNullOrEmpty(name))
                return -1;
            string normalized = name.Replace("-", string.Empty).Replace(" ", string.Empty);
            if (IsUltraReasoningOptionName(name))
                return 5;
            if (normalized.EndsWith("极高", StringComparison.Ordinal))
                return 3;
            if (normalized.EndsWith("最高", StringComparison.Ordinal))
                return 4;
            if (normalized.EndsWith("高", StringComparison.Ordinal))
                return 2;
            if (normalized.EndsWith("中", StringComparison.Ordinal))
                return 1;
            if (normalized.EndsWith("轻度", StringComparison.Ordinal) ||
                normalized.EndsWith("低", StringComparison.Ordinal) ||
                normalized.EndsWith("极低", StringComparison.Ordinal))
                return 0;
            if (normalized.IndexOf("ultra", StringComparison.OrdinalIgnoreCase) >= 0)
                return 5;
            if (normalized.IndexOf("xhigh", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("extrahigh", StringComparison.OrdinalIgnoreCase) >= 0)
                return 3;
            if (normalized.IndexOf("maximum", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("max", StringComparison.OrdinalIgnoreCase) >= 0)
                return 4;
            if (normalized.IndexOf("high", StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;
            if (normalized.IndexOf("medium", StringComparison.OrdinalIgnoreCase) >= 0)
                return 1;
            if (normalized.IndexOf("light", StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("low", StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;
            return -1;
        }

        internal static bool IsUltraReasoningOptionName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string normalized = name.Replace("-", string.Empty).Replace(" ", string.Empty);
            if (normalized.IndexOf("ultra", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return normalized.IndexOf("极高", StringComparison.Ordinal) >= 0 &&
                   normalized.IndexOf("更快消耗", StringComparison.Ordinal) >= 0 &&
                   normalized.IndexOf("使用额度", StringComparison.Ordinal) >= 0;
        }

        private AutomationElement FindConversationScrollContainer(bool refresh)
        {
            if (!refresh && _conversationScrollContainer != null)
                return _conversationScrollContainer;
            AutomationElement root = GetRoot();
            if (root == null)
                return null;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group);
            AutomationElementCollection groups = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < groups.Count; i++)
            {
                string className = SafeClassName(groups[i]);
                if (className.IndexOf("thread-scroll-container", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                object pattern;
                try
                {
                    if (groups[i].TryGetCurrentPattern(ScrollPattern.Pattern, out pattern) &&
                        ((ScrollPattern)pattern).Current.VerticallyScrollable)
                    {
                        _conversationScrollContainer = groups[i];
                        return groups[i];
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private static AutomationElement FindNamedElement(AutomationElement root, params string[] names)
        {
            if (root == null)
                return null;
            for (int i = 0; i < names.Length; i++)
            {
                AutomationElement element = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, names[i]));
                if (element != null && !SafeOffscreen(element))
                    return element;
            }
            return null;
        }

        private static bool NameEquals(string value, params string[] candidates)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                if (string.Equals(value, candidates[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool Invoke(AutomationElement element)
        {
            if (!ModelContextCurrent() || element == null)
                return false;
            try
            {
                object pattern;
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    return true;
                }
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                {
                    ((SelectionItemPattern)pattern).Select();
                    return true;
                }
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
                {
                    ((TogglePattern)pattern).Toggle();
                    return true;
                }
                if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                {
                    var expandable = (ExpandCollapsePattern)pattern;
                    if (expandable.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                        expandable.Collapse();
                    else
                        expandable.Expand();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("ChatGPT UI Automation invoke failed", ex);
            }
            return false;
        }

        private static bool SupportsInvoke(AutomationElement element)
        {
            try
            {
                object pattern;
                return element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern) ||
                       element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern) ||
                       element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern) ||
                       element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern);
            }
            catch
            {
                return false;
            }
        }

        private static bool SupportsPattern(AutomationElement element, AutomationPattern automationPattern)
        {
            try
            {
                object pattern;
                return element.TryGetCurrentPattern(automationPattern, out pattern);
            }
            catch
            {
                return false;
            }
        }

        private static bool EnsureExpanded(AutomationElement element)
        {
            bool reasoningMenu = IsReasoningSelectorName(SafeName(element)) || IsGenericReasoningSelectorName(SafeName(element));
            IntPtr window = FindWindow();
            Func<bool> guard = delegate
            {
                return ModelContextCurrent() && window != IntPtr.Zero && NativeMethods.GetForegroundWindow() == window;
            };
            try
            {
                bool expanded = RetryReplacedMenu(guard, delegate
                {
                    if (reasoningMenu) element = FindReasoningSelector(GetRoot(window));
                    if (element == null) return false;
                    object pattern;
                    if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                        return guard() && Invoke(element);
                    var expandable = (ExpandCollapsePattern)pattern;
                    return ChangeMenuExpansion(true,
                        delegate { return guard() && ReadMenuExpanded(element, reasoningMenu); },
                        delegate { if (guard()) expandable.Expand(); }, Thread.Sleep);
                }, delegate { return reasoningMenu && OpenReasoningMenuByKeyboard(); });
                if (expanded && reasoningMenu) Thread.Sleep(160);
                return expanded && guard();
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to expand ChatGPT menu", ex);
                return false;
            }
        }

        internal static bool RetryReplacedMenu(Func<bool> guard, Func<bool> attempt, Func<bool> fallback)
        {
            for (int i = 0; i < 2; i++)
            {
                if (!guard()) return false;
                try
                {
                    if (attempt()) return guard();
                    break;
                }
                catch (ElementNotAvailableException) { }
            }
            return guard() && fallback() && guard();
        }

        private static void Collapse(AutomationElement element)
        {
            if (!ModelContextCurrent()) return;
            try
            {
                bool reasoningMenu = IsReasoningSelectorName(SafeName(element)) || IsGenericReasoningSelectorName(SafeName(element));
                if (reasoningMenu)
                    element = FindReasoningSelector(GetRoot());
                object pattern;
                if (element != null && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                {
                    if (reasoningMenu && ReadMenuExpanded(element, true) && DismissReasoningMenu(element))
                        return;
                    var expandable = (ExpandCollapsePattern)pattern;
                    if (!ChangeMenuExpansion(
                        false,
                        delegate { return ReadMenuExpanded(element, reasoningMenu); },
                        delegate { expandable.Collapse(); }, Thread.Sleep) && !(reasoningMenu && DismissReasoningMenu(element)))
                        Logger.Warn("Model menu did not close within the bounded wait.");
                }
            }
            catch
            {
            }
        }

        private static bool ReadMenuExpanded(AutomationElement element, bool reasoningMenu)
        {
            // React can replace the trigger. The original UIA element may keep
            // reporting Expanded after a fresh query already sees Collapsed.
            if (reasoningMenu)
            {
                element = FindReasoningSelector(GetRoot());
                string name = SafeName(element);
                if (NameEquals(name, "Select effort", "选择强度", "选择推理强度"))
                    return ResolveReasoningMenuExpanded(name, false, false);
                if (IsAstraReasoningSelectorName(name) || IsSolReasoningSelectorName(name))
                    return ResolveReasoningMenuExpanded(name, FindCompactReasoningSlider(GetRoot()) != null, false);
            }
            object pattern;
            if (element == null || !element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
                throw new ElementNotAvailableException();
            return ((ExpandCollapsePattern)pattern).Current.ExpandCollapseState == ExpandCollapseState.Expanded;
        }

        internal static bool ResolveReasoningMenuExpanded(string name, bool sliderVisible, bool nativeExpanded)
        {
            if (NameEquals(name, "Select effort", "选择强度", "选择推理强度")) return true;
            if (IsAstraReasoningSelectorName(name) || IsSolReasoningSelectorName(name)) return sliderVisible;
            return nativeExpanded;
        }

        // Send one UIA request and wait for its observed state before retrying
        // by a different route. A successful API call alone is not a postcondition.
        internal static bool ChangeMenuExpansion(bool expanded, Func<bool> readExpanded,
            Action requestChange, Action<int> delay)
        {
            if (readExpanded() == expanded)
                return true;
            requestChange();
            for (int attempt = 0; attempt < 20; attempt++)
            {
                delay(40);
                if (readExpanded() == expanded)
                    return true;
            }
            return false;
        }

        private static bool DismissReasoningMenu(AutomationElement selector)
        {
            if (!ModelContextCurrent()) return false;
            // Codex 26.915 exposes Collapse but can leave its model popup open.
            // Escape is allowed only on a known control inside the visible model
            // popup in foreground Codex, never blindly into the current app.
            selector = FindReasoningSelector(GetRoot());
            IntPtr expectedWindow = FindWindow();
            if (expectedWindow == IntPtr.Zero || NativeMethods.GetForegroundWindow() != expectedWindow ||
                selector == null || SafeOffscreen(selector) || !SafeEnabled(selector) ||
                !NameEquals(SafeName(selector), "Select effort", "选择强度", "选择推理强度", "Power", "强度"))
                return false;
            AutomationElement menuFocus = FindCompactReasoningSlider(GetRoot(expectedWindow)) ??
                FindModelListToggle(GetRoot(expectedWindow)) ?? FindAstraModelOption(GetRoot(expectedWindow)) ??
                FindSolModelOption(GetRoot(expectedWindow));
            if (menuFocus == null || SafeOffscreen(menuFocus) || !SafeEnabled(menuFocus))
                return false;
            menuFocus.SetFocus();
            Thread.Sleep(40);
            if (!ModelContextCurrent() || NativeMethods.GetForegroundWindow() != expectedWindow ||
                !Automation.Compare(AutomationElement.FocusedElement, menuFocus))
                return false;
            if (!ReadMenuExpanded(selector, true))
                return true;
            InputSender.Tap(Vk.Escape);
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(40);
                AutomationElement current = FindReasoningSelector(GetRoot(expectedWindow));
                if (current != null && !ReadMenuExpanded(current, true))
                    return true;
            }
            return false;
        }

        private static bool OpenReasoningMenuByKeyboard()
        {
            if (!ModelContextCurrent()) return false;
            IntPtr window = FindWindow();
            AutomationElement selector = FindReasoningSelector(GetRoot(window));
            if (window == IntPtr.Zero || NativeMethods.GetForegroundWindow() != window ||
                selector == null || SafeOffscreen(selector) || !SafeEnabled(selector) ||
                !IsReasoningSelectorName(SafeName(selector)))
                return false;
            selector.SetFocus();
            Thread.Sleep(40);
            if (ReadMenuExpanded(selector, true)) return true;
            if (!ModelContextCurrent() || NativeMethods.GetForegroundWindow() != window ||
                !Automation.Compare(AutomationElement.FocusedElement, selector))
                return false;
            InputSender.Tap(Vk.Space);
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(40);
                if (NativeMethods.GetForegroundWindow() != window) return false;
                if (ReadMenuExpanded(selector, true)) return true;
            }
            return false;
        }

        private static void CloseIntelligenceMenu()
        {
            // An effort commit replaces the trigger and can invalidate the UIA
            // reference between lookup and Collapse/SetFocus. Retry by identity,
            // checking the fresh closed state before every dismissal attempt.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Thread.Sleep(100);
                AutomationElement selector = FindReasoningSelector(GetRoot());
                if (selector == null) continue;
                try
                {
                    if (!ReadMenuExpanded(selector, true)) return;
                    Collapse(selector);
                    if (!ReadMenuExpanded(selector, true)) return;
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
            }
            Logger.Warn("Fresh model menu close could not be verified.");
        }

        private static string SafeName(AutomationElement element)
        {
            if (element == null)
                return string.Empty;
            try { return element.Current.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeAutomationId(AutomationElement element)
        {
            if (element == null)
                return string.Empty;
            try { return element.Current.AutomationId ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeClassName(AutomationElement element)
        {
            if (element == null)
                return string.Empty;
            try { return element.Current.ClassName ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeControlType(AutomationElement element)
        {
            if (element == null)
                return string.Empty;
            try { return element.Current.ControlType == null ? string.Empty : element.Current.ControlType.ProgrammaticName; }
            catch { return string.Empty; }
        }

        private static AutomationElement SafeParent(AutomationElement element)
        {
            if (element == null)
                return null;
            try { return TreeWalker.ControlViewWalker.GetParent(element); }
            catch { return null; }
        }

        private static bool SafeOffscreen(AutomationElement element)
        {
            try { return element.Current.IsOffscreen; }
            catch { return true; }
        }

        private static bool SafeEnabled(AutomationElement element)
        {
            if (element == null)
                return false;
            try { return element.Current.IsEnabled; }
            catch { return false; }
        }

        private static System.Windows.Rect SafeBounds(AutomationElement element)
        {
            try { return element.Current.BoundingRectangle; }
            catch { return System.Windows.Rect.Empty; }
        }

        private sealed class TaskCandidate
        {
            internal AutomationElement Element;
            internal double Top;
            internal double Left;
            internal string Name;
        }
    }

    internal sealed class ChatGptStatusMonitor : IDisposable
    {
        private readonly ChatGptAutomation _automation;
        private readonly Action<AiTaskState> _onState;
        private readonly Timer _timer;
        private AiTaskState _lastStableState;
        private int _polling;
        private int _completionGeneration;

        internal ChatGptStatusMonitor(ChatGptAutomation automation, Action<AiTaskState> onState)
        {
            _automation = automation;
            _onState = onState;
            _timer = new Timer(Poll, null, 500, 1000);
        }

        private void Poll(object state)
        {
            if (Interlocked.Exchange(ref _polling, 1) != 0)
                return;
            try
            {
                AiTaskState observed = _automation.ReadTaskState();
                if (_lastStableState == AiTaskState.Thinking && observed == AiTaskState.Idle)
                {
                    int generation = Interlocked.Increment(ref _completionGeneration);
                    _onState(AiTaskState.Completed);
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        Thread.Sleep(1400);
                        if (generation == _completionGeneration)
                            _onState(AiTaskState.Idle);
                    });
                }
                else if (observed != _lastStableState)
                {
                    Interlocked.Increment(ref _completionGeneration);
                    _onState(observed);
                }
                _lastStableState = observed;
            }
            catch (Exception ex)
            {
                Logger.Error("ChatGPT status polling failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }

    internal sealed class QuickChatToggleState
    {
        private readonly object _gate = new object();
        private bool _openedByBridge;

        internal void MarkOpened()
        {
            lock (_gate)
                _openedByBridge = true;
        }

        internal bool ConsumeCloseRequest()
        {
            lock (_gate)
            {
                if (!_openedByBridge)
                    return false;
                _openedByBridge = false;
                return true;
            }
        }
    }
}
