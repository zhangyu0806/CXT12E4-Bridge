using System;
using System.Collections.Generic;
using System.Globalization;

namespace Cxt1024Bridge
{
    internal static class BridgeSpecification
    {
        internal static Dictionary<string, object> Describe()
        {
            var layers = new List<object>();
            AddLayer(layers, 1, new[] { "任务1", "任务2", "任务3", "分叉任务", "任务4", "任务5", "任务6", "快速聊天开/关", "显示/隐藏侧栏", "显示/隐藏侧面板", "Codex内打开ChatGPT", "普通任务（不归属项目）/K12组合" }, new[] { "推理强度（最高止）/按下紫色极高并确保Fast", "相邻任务前后/按下回到高并确保普通速度", "逐词移动/聚焦输入", "加速内容滚动/到底部" });
            AddLayer(layers, 2, new[] { "直接在当前window右侧新建pane", "直接在当前window下方新建pane", "直接在当前window内左右pane切换", "直接在当前window内上下pane切换", "直接缩放当前pane", "两个及以上pane横竖均分切换", "直接轮换pane位置", "当前客户端window选择器", "当前pane Agent暂存/恢复输入", "当前pane tmux copy-mode", "稳定搜索终端历史", "直接回上一个pane/K12组合" }, new[] { "直接调整pane边界左/右5格/均衡宽度", "直接调整pane边界上/下5格/均衡高度", "直接切换window前后/上一个window", "真实多事件加速滚动/滚到底部" });
            AddLayer(layers, 3, new[] { "新页面", "新标签", "查找", "切换深浅色", "评论", "复制块", "块菜单", "复制页面链接", "删除块", "待办", "代码块", "全屏/K12组合" }, new[] { "历史/上级", "标签/快速查找", "横向滚动/全宽", "纵向滚动/顶部" });
            AddLayer(layers, 4, new[] { "新笔记", "快速切换", "库搜索", "命令面板", "内部链接", "切换任务", "改名移动", "复制笔记链接", "关闭", "今日日记", "模板（未配置时大纲）", "编辑阅读/K12组合" }, new[] { "历史/反向链接", "标签/恢复", "缩放/复位", "内容滚动/顶部" });
            AddLayer(layers, 5, new[] { "对象/编辑模式", "挤出（编辑模式）", "移动", "旋转", "缩放", "内插面（编辑模式）", "倒角（编辑模式）", "复制", "撤销", "重做", "添加", "操作搜索/K12组合" }, new[] { "左右环绕视角/前视图", "上下环绕视角/顶视图", "上一帧下一帧/播放暂停", "加速原生滚轮（视口缩放）/框选中" });
            AddLayer(layers, 6, new[] { "复制", "移动", "新目录", "批量改名", "Linux路径", "文件名", "完整路径", "快速搜索", "删除", "比较", "同步", "压缩解压/K12组合" }, new[] { "历史/上级", "面板焦点/交换", "标签/锁定", "列表滚动/快速查看" });
            AddLayer(layers, 7, new[] { "上一会话", "下一会话", "未读", "搜索", "复制", "纯文本粘贴", "回复待适配（不发送）", "发送", "清空", "表情", "输入换行", "空置/K12组合" }, new[] { "按词移动/行首", "按词选择/全选", "缩放/复位", "内容滚动/到底部" });
            AddLayer(layers, 0, new[] { "PixPin截图", "PixPin贴图", "撤销", "重做", "剪切", "复制", "粘贴", "纯文本粘贴", "新标签", "恢复关闭标签", "查找", "CopyQ/K12组合" }, new[] { "后退前进/地址栏", "标签切换/关闭标签", "缩放/恢复100%", "加速纵向滚动/刷新" });

            var colors = new List<object>();
            for (int displayLayer = 1; displayLayer <= 8; displayLayer++)
            {
                int i = BridgeDefinitions.ToInternalLayer(displayLayer);
                LayerDefinition definition = BridgeDefinitions.GetLayer(i);
                colors.Add(new Dictionary<string, object>
                {
                    { "layer", definition.Number },
                    { "name", definition.Name },
                    { "color", definition.HexColor },
                    { "indicatorKey", definition.IndicatorKey },
                    { "brightnessPercent", 100 }
                });
            }

            return new Dictionary<string, object>
            {
                { "schemaVersion", 4 },
                { "generatedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "writesSent", false },
                { "kbtRequired", false },
                { "codexIntegration", new Dictionary<string, object>
                    {
                        { "catalogPrimary", "official Codex App Server thread/list over stable stdio JSONL" },
                        { "catalogFallback", "read-only local state database with a 30-second retry cooldown" },
                        { "liveTaskState", "shared rollout event logs; an independent App Server reports ChatGPT Desktop threads as notLoaded" },
                        { "desktopActions", "existing codex:// deep links, UI Automation, and keyboard shortcuts remain in place" },
                        { "writeScope", "catalog integration is read-only; it never starts, resumes, forks, interrupts, or modifies a thread" },
                        { "diagnostic", "--codex-app-server-probe" }
                    }
                },
                { "hardwareTriggerKeymap", KeyboardConfiguration.Describe() },
                { "autoLayerProcesses", new Dictionary<string, object>
                    {
                        { "L1", "ChatGPT.exe" },
                        { "L2", "WindowsTerminal.exe" },
                        { "L3", "Notion.exe" },
                        { "L4", "Obsidian.exe" },
                        { "L5", "blender.exe only (Blender default keymap)" },
                        { "L6", "TOTALCMD64.exe / TOTALCMD.exe" },
                        { "L7", "Weixin / WeChat / Telegram / QQ" },
                        { "L8", "browsers and unmatched applications; one fixed shortcut profile, internal index 0" },
                        { "fallback", "L8" }
                    }
                },
                { "taskbarAppChord", "hold key12, then key1..9 => Win+1..9; key10 => Win+0 (taskbar slot 10); key11 => invoke taskbar slot 11 directly; chord suppresses both tap actions and dispatches only after both physical keys are released; prefix foreground/layer/context must remain unchanged; a selector pressed after the 650ms long action fired cannot create a second chord" },
                { "key12LongPress", "key12 is global tmux session slot 4; hold 650ms to switch that live session, while any key1..11 taskbar chord keeps priority" },
                { "key4ChatGptChord", "removed; L1 key4 forks the current latest response below 650ms and enters global tmux session slot 6 at 650ms" },
                { "l1NavigationControls", "key9 toggles the left sidebar; K1 rotates effort levels only through Max (Chinese UI: 最高) and presses to select the bottom purple Ultra option with full access confirmation, then deterministically ensures Fast speed without toggling an existing Fast selection off; K2 rotates across adjacent tasks and presses to reset reasoning to High, then deterministically ensures Standard speed without toggling an existing Standard selection back to Fast" },
                { "manualLayerSelection", "tray menu keeps direct manual L1..L8 selection; foreground HWND change resumes automatic selection" },
                { "l8FixedProfile", "browser and general functions share identical bindings in all L8 applications; unsupported application shortcuts are not translated" },
                { "l5ModelingProfile", "Blender default keymap only; manual L5 selection still rejects non-Blender foreground processes; put pointer over the intended editor, leave text fields and use the required object/edit mode; K4 sends accelerated native wheel (viewport zoom, panel scroll)" },
                { "speechInput", "disabled on the control surface; Typeless Right Alt is used from the main keyboard" },
                { "terminalSessionLights", new Dictionary<string, object>
                    {
                        { "layers", "L1..L8" },
                        { "sessions", "tmux session names beginning 01..06" },
                        { "physicalKeys", "9,10,11,12,8,4" },
                        { "states", "off / idle-white / thinking-deep-blue-animation / completed-deep-green / waiting-amber / error-red" },
                        { "multiPaneAggregate", "error > waiting > thinking > completed > idle" },
                        { "layerMarker", "L1..L7 use physical keys1..7; L8 uses key8. Only L4/key4 and L8/key8 overlay tmux LEDs; other markers overlay task LEDs. Restore live state after each pair of pulses." },
                        { "sources", "live tmux pane/session inventory plus matching OpenCode, Claude Code, and Grok state reports" },
                        { "inventoryFailure", "retain last verified inventory on transient transport failure with bounded retry backoff; clear LEDs only on confirmed no-server; never use cached inventory alone to switch a session" },
                        { "hold", "on every layer, hold the illuminated physical key for 650ms to switch the most recently active tmux client to that live session and focus the last observed Windows Terminal window; an absent session fails closed" }
                    }
                },
                { "globalTaskHold", "on every layer, hold physical keys 1,2,3,5,6,7 for 650ms to switch task slots 1..6 and focus ChatGPT; short taps retain current-layer actions" },
                { "colors", colors },
                { "layers", layers },
                { "rgbOutput", "CXT 12E4 custom QMK Raw HID firmware provides six ChatGPT task LEDs and six tmux session LEDs globally; after 40 minutes without Windows keyboard or mouse input the Bridge pauses its heartbeat so every LED turns off, and restores live state on the next input" }
            };
        }

        private static void AddLayer(List<object> target, int number, string[] keys, string[] encoders)
        {
            target.Add(new Dictionary<string, object>
            {
                { "layer", BridgeDefinitions.GetLayer(number).Number },
                { "name", BridgeDefinitions.GetLayer(number).Name },
                { "keys1To12", keys },
                { "encodersK1ToK4", encoders }
            });
        }
    }
}
