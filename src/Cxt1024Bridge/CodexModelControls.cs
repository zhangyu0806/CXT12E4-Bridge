using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Cxt1024Bridge
{
    internal sealed partial class ChatGptAutomation
    {
        // This diagnostic deliberately reports only model-control metadata:
        // no window tree, task title, conversation, composer, or account data.
        internal Dictionary<string, object> InspectModelControls(string action)
        {
            if (action == "controls") return InspectApplicationControls();
            var result = new Dictionary<string, object>();
            result["scope"] = "model-effort-speed-only";
            result["beforeSelector"] = SafeName(FindReasoningSelector(GetRoot()));
            if (action == "k1") result["actionSucceeded"] = SetReasoningUltraAndFast();
            else if (action == "k2") result["actionSucceeded"] = SetReasoningHighAndStandard();
            else if (action == "up") result["actionSucceeded"] = AdjustReasoning(true);
            else if (action == "down") result["actionSucceeded"] = AdjustReasoning(false);
            else if (action != "inspect" && action != "models")
                throw new ArgumentException("Expected inspect, controls, models, k1, k2, up, or down.");
            AutomationElement selector = FindReasoningSelector(GetRoot());
            result["selector"] = SafeName(selector);
            if (selector == null)
                return result;
            result["selectorEnabled"] = SafeEnabled(selector);
            result["selectorCanExpandCollapse"] = SupportsPattern(selector, ExpandCollapsePattern.Pattern);
            result["selectorCanInvoke"] = SupportsPattern(selector, InvokePattern.Pattern);
            if (!EnsureExpanded(selector))
                return result;
            try
            {
                AutomationElement slider = WaitForCompactReasoningSlider(600);
                result["expandedSelector"] = SafeName(selector);
                result["compactSlider"] = slider != null;
                if (slider != null)
                {
                    result["sliderName"] = SafeName(slider);
                    result["sliderEnabled"] = SafeEnabled(slider);
                    result["sliderHelp"] = SafeHelpText(slider);
                    result["sliderRank"] = ReadCompactReasoningRank(GetRoot());
                }
                AutomationElement fast = WaitForCompactFastToggle(700);
                if (fast != null)
                {
                    object pattern;
                    result["speedAction"] = SafeName(fast);
                    if (fast.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
                        result["fastState"] = ((TogglePattern)pattern).Current.ToggleState.ToString();
                }
                AutomationElement switcher = FindModelListToggle(GetRoot());
                result["modelListToggle"] = switcher != null;
                if (action == "models" && switcher != null && Invoke(switcher))
                {
                    Thread.Sleep(250);
                    AutomationElement astra = FindAstraModelOption(GetRoot());
                    result["astraOption"] = astra == null ? null : SafeName(astra);
                    result["astraEnabled"] = SafeEnabled(astra);
                    result["astraHelp"] = SafeHelpText(astra);
                    result["astraType"] = astra == null ? null : SafeControlType(astra);
                    AutomationElement sol = FindSolModelOption(GetRoot());
                    result["solOption"] = sol == null ? null : SafeName(sol);
                    result["solEnabled"] = SafeEnabled(sol);
                    result["solHelp"] = SafeHelpText(sol);
                    result["solType"] = sol == null ? null : SafeControlType(sol);
                }
            }
            finally
            {
                result["beforeCloseState"] = InspectExpansionState(selector);
                Collapse(selector);
                result["afterFirstCloseState"] = InspectExpansionState(selector);
                result["afterFirstCloseSelector"] = SafeName(FindReasoningSelector(GetRoot()));
                CloseIntelligenceMenu();
                result["afterSecondCloseState"] = InspectExpansionState(FindReasoningSelector(GetRoot()));
            }
            result["closedSelector"] = SafeName(FindReasoningSelector(GetRoot()));
            result["menuStillVisible"] = FindCompactReasoningSlider(GetRoot()) != null;
            return result;
        }

        private static string InspectExpansionState(AutomationElement element)
        {
            try
            {
                object pattern;
                return element != null && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern)
                    ? ((ExpandCollapsePattern)pattern).Current.ExpandCollapseState.ToString() : "unavailable";
            }
            catch (Exception ex) { return ex.GetType().Name; }
        }

        internal static int ParseCompactAstraPosition(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 160) return -1;
            Match match = Regex.Match(text, @"^(?:GPT-6 Astra|6 Astra|Astra) [^\r\n]{1,48}?(?:，第\s*([1-6])\s*项，共\s*6\s*项。|,\s*([1-6])\s+of\s+6\.)$", RegexOptions.CultureInvariant);
            if (!match.Success) return -1;
            return int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value,
                System.Globalization.CultureInfo.InvariantCulture) - 1;
        }

        internal static int ParseCompactSolPosition(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 160) return -1;
            Match match = Regex.Match(text, @"^(?:GPT-6 Sol|6 Sol|Sol) [^\r\n]{1,48}?(?:，第\s*([1-6])\s*项，共\s*6\s*项。|,\s*([1-6])\s+of\s+6\.)$", RegexOptions.CultureInvariant);
            if (!match.Success) return -1;
            return int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value,
                System.Globalization.CultureInfo.InvariantCulture) - 1;
        }

        private static int ReadCompactReasoningRank(AutomationElement root, bool astra = true)
        {
            if (root == null) return -1;
            AutomationElementCollection statuses = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.StatusBar));
            for (int i = 0; i < statuses.Count; i++)
            {
                if (SafeClassName(statuses[i]).IndexOf("KeyboardAnnouncement", StringComparison.Ordinal) < 0)
                    continue;
                AutomationElementCollection texts = statuses[i].FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                for (int j = 0; j < texts.Count; j++)
                {
                    int rank = astra ? ParseCompactAstraPosition(SafeName(texts[j])) : ParseCompactSolPosition(SafeName(texts[j]));
                    if (rank >= 0) return rank;
                }
            }
            return -1;
        }

        private Dictionary<string, object> InspectApplicationControls()
        {
            var result = new Dictionary<string, object>();
            result["scope"] = "control-capabilities-only";
            AutomationElement root = GetRoot();
            result["windowFound"] = root != null;
            result["modelSelector"] = SafeName(FindReasoningSelector(root));
            result["sidebarToggle"] = SafeName(FindSidebarToggle(root));
            result["ordinaryNewTask"] = FindTopLevelNewTaskButton(root) != null;
            result["quickChat"] = FindButton(root, TemporaryTaskButtonNames) != null;
            result["browserAddress"] = FindBuiltInBrowserUrlInput(root) != null;
            AutomationElement composer = FindComposer(root);
            result["composerFound"] = composer != null;
            result["composerIsProseMirror"] = SafeClassName(composer).IndexOf("ProseMirror", StringComparison.OrdinalIgnoreCase) >= 0;
            List<AutomationElement> tasks = FindSidebarTasks(root);
            int selected = 0;
            for (int i = 0; i < tasks.Count; i++)
                if (SidebarSelectionScore(SafeClassName(tasks[i])) >= 3) selected++;
            result["visibleSidebarTasks"] = tasks.Count;
            result["selectedSidebarTasks"] = selected;
            AutomationElement container = FindConversationScrollContainer(true);
            result["conversationScroll"] = container != null;
            var scrollControls = new List<string>();
            if (root != null)
            {
                AutomationElementCollection scrolls = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
                for (int i = 0; i < scrolls.Count; i++)
                    scrollControls.Add(SafeControlType(scrolls[i]) + ":" + SafeClassName(scrolls[i]));
            }
            result["scrollControls"] = scrollControls;
            var supportedButtons = new List<string>();
            if (root != null)
            {
                AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                for (int i = 0; i < buttons.Count; i++)
                    if (!SafeOffscreen(buttons[i]) && IsForkButtonName(SafeName(buttons[i])))
                    { supportedButtons.Add(SafeName(buttons[i])); break; }
            }
            result["forkButtons"] = supportedButtons;
            result["uiInvoked"] = false;
            return result;
        }

        private static string SafeHelpText(AutomationElement element)
        {
            try { return element == null ? string.Empty : element.Current.HelpText ?? string.Empty; }
            catch { return string.Empty; }
        }

        internal static bool IsAstraModelLabel(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            string trimmed = name.Trim();
            return trimmed.Equals("GPT-6 Astra", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("6 Astra", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("Astra", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsAstraReasoningSelectorName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 96 ||
                name.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                return false;
            string trimmed = name.Trim();
            string[] prefixes = { "GPT-6 Astra ", "6 Astra ", "Astra " };
            for (int i = 0; i < prefixes.Length; i++)
            {
                if (trimmed.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    string effort = trimmed.Substring(prefixes[i].Length).Trim();
                    return NameEquals(effort, "Light", "Low", "Medium", "High", "Extra High", "XHigh", "Max", "Maximum", "Ultra",
                        "轻度", "低", "中", "高", "极高", "最高", "极高 更快消耗使用额度");
                }
            }
            return false;
        }

        internal static bool AstraReasoningMatches(string selectorName, int desiredRank)
        {
            return IsAstraReasoningSelectorName(selectorName) &&
                   desiredRank >= 0 && desiredRank <= UltraReasoningRank &&
                   GetReasoningRank(selectorName) == desiredRank;
        }

        private bool EnsureAstraModelSelected()
        {
            return EnsureModelSelected(true);
        }

        private bool EnsureModelSelected(bool astra)
        {
            if (!PrepareForReasoningInteraction())
                return ModelActionFailed("could not prepare foreground model interaction");
            CloseIntelligenceMenu();
            AutomationElement selector = FindReasoningSelector(GetRoot());
            bool alreadySelected = astra ? IsAstraReasoningSelectorName(SafeName(selector)) : IsSolReasoningSelectorName(SafeName(selector));
            // A closed explicit Astra label already proves model selection.
            // Avoid opening/closing the same popup twice for every detent.
            if (alreadySelected && SafeEnabled(selector)) return ModelContextCurrent();
            if (selector == null || !SafeEnabled(selector) || !EnsureExpanded(selector))
                return ModelActionFailed("model selector missing, disabled, or failed to expand");
            bool restoreFast = false;
            bool selected = false;
            try
            {
                Thread.Sleep(120);
                // In explicit-model mode Codex labels the expanded trigger
                // "Select effort". Default Power mode may span models; pin
                // Astra through its radio option before using that slider.
                if (alreadySelected && NameEquals(SafeName(FindReasoningSelector(GetRoot())), "Select effort", "选择强度", "选择推理强度"))
                    return true;
                AutomationElement switcher = FindModelListToggle(GetRoot());
                // Ultra+Fast hides the model-list toggle in current Codex.
                // Temporarily use Standard, pin Astra explicitly, then restore
                // Fast before applying the requested reasoning/speed preset.
                if (switcher == null)
                {
                    AutomationElement fast = FindCompactFastToggle(GetRoot());
                    object pattern;
                    if (fast == null || !fast.TryGetCurrentPattern(TogglePattern.Pattern, out pattern) ||
                        ((TogglePattern)pattern).Current.ToggleState != ToggleState.On)
                        return false;
                    restoreFast = true;
                    if (!SetCompactFastMode(fast, false))
                        return false;
                    switcher = FindModelListToggle(GetRoot());
                }
                if (switcher == null || !Invoke(switcher))
                    return false;
                AutomationElement target = null;
                int started = Environment.TickCount;
                do
                {
                    target = astra ? FindAstraModelOption(GetRoot()) : FindSolModelOption(GetRoot());
                    if (target != null)
                        break;
                    Thread.Sleep(40);
                }
                while (unchecked(Environment.TickCount - started) < 700);
                if (target == null)
                {
                    Logger.Warn((astra ? "GPT-6 Astra" : "GPT-6 Sol") + " was not available in the Codex model list.");
                    return false;
                }
                // Locked model rows open access offers instead of selecting a
                // model. Do not invoke them even if the UI exposes enabled=true.
                string help = SafeHelpText(target);
                if (help.IndexOf("locked", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    help.IndexOf("锁定", StringComparison.Ordinal) >= 0)
                    return false;
                selected = Invoke(target);
                Thread.Sleep(120);
            }
            finally
            {
                Collapse(selector);
                CloseIntelligenceMenu();
                if (restoreFast && !SetFastMode())
                    selected = false;
            }
            if (!selected)
                return false;
            selector = FindReasoningSelector(GetRoot());
            if (!(astra ? IsAstraReasoningSelectorName(SafeName(selector)) : IsSolReasoningSelectorName(SafeName(selector))))
            {
                Logger.Warn("Requested GPT-6 model selection could not be verified; reasoning action cancelled.");
                return false;
            }
            return true;
        }

        private bool VerifyAstraReasoningSelection(int desiredRank)
        {
            return VerifyModelReasoningSelection(true, desiredRank);
        }

        private bool VerifyModelReasoningSelection(bool astra, int desiredRank)
        {
            int started = Environment.TickCount;
            do
            {
                string name = SafeName(FindReasoningSelector(GetRoot()));
                if (ModelReasoningMatches(name, astra, desiredRank))
                {
                    RememberReasoningRank(desiredRank);
                    return true;
                }
                Thread.Sleep(40);
            }
            while (unchecked(Environment.TickCount - started) < 700);
            Logger.Warn((astra ? "Astra" : "Sol") + " reasoning selection did not match the requested rank " + desiredRank + ".");
            return false;
        }

        private static AutomationElement FindModelListToggle(AutomationElement root)
        {
            if (root == null)
                return null;
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                new OrCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "Select model"),
                    new PropertyCondition(AutomationElement.NameProperty, "选择模型")));
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < elements.Count; i++)
                if (!SafeOffscreen(elements[i]) && SafeEnabled(elements[i]))
                    return elements[i];
            return null;
        }

        private static AutomationElement FindAstraModelOption(AutomationElement root)
        {
            if (root == null)
                return null;
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.NameProperty, "GPT-6 Astra"),
                new PropertyCondition(AutomationElement.NameProperty, "6 Astra"),
                new PropertyCondition(AutomationElement.NameProperty, "Astra"));
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < elements.Count; i++)
            {
                AutomationElement element = elements[i];
                if (IsAstraModelLabel(SafeName(element)) && !SafeOffscreen(element) && SafeEnabled(element) &&
                    (element.Current.ControlType == ControlType.MenuItem ||
                     element.Current.ControlType == ControlType.RadioButton) && SupportsInvoke(element))
                    return element;
            }
            return null;
        }

        internal static bool IsSolModelLabel(string name)
        {
            return NameEquals(name, "GPT-6 Sol", "6 Sol");
        }

        internal static bool IsSolReasoningSelectorName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 96 || name.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                return false;
            string trimmed = name.Trim();
            string[] prefixes = { "GPT-6 Sol ", "6 Sol " };
            for (int i = 0; i < prefixes.Length; i++)
                if (trimmed.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                    return GetReasoningRank(trimmed.Substring(prefixes[i].Length).Trim()) >= 0;
            return false;
        }

        private static bool ModelReasoningMatches(string selectorName, bool astra, int rank)
        {
            return (astra ? IsAstraReasoningSelectorName(selectorName) : IsSolReasoningSelectorName(selectorName)) &&
                   GetReasoningRank(selectorName) == rank;
        }

        private static AutomationElement FindSolModelOption(AutomationElement root)
        {
            if (root == null) return null;
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.NameProperty, "GPT-6 Sol"),
                new PropertyCondition(AutomationElement.NameProperty, "6 Sol"));
            AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
            for (int i = 0; i < elements.Count; i++)
            {
                AutomationElement element = elements[i];
                if (!SafeOffscreen(element) && SafeEnabled(element) &&
                    (element.Current.ControlType == ControlType.MenuItem ||
                     element.Current.ControlType == ControlType.RadioButton) && SupportsInvoke(element))
                    return element;
            }
            return null;
        }
    }
}
