using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Cxt1024Bridge
{
    internal sealed class ProfileControl
    {
        internal string Id, Title;
        internal ProfileControl(string id, string title) { Id = id; Title = title; }
    }
    internal sealed class ProfileChoice
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        internal ProfileChoice(string id, string title) { Id = id; Title = title; }
        public override string ToString() { return Title; }
    }

    // Descriptions and finite identifiers only. This class never sends input or runs commands.
    internal static class WindowsLayerProfiles
    {
        internal static readonly string[] Devices = { "keyboard", "xbox" };
        private static readonly string[] Buttons = { "a", "b", "x", "y", "up", "down", "left", "right", "lb", "rb", "l3", "r3" };
        private static readonly string[] ButtonTitles = { "A", "B", "X", "Y", "十字键 ↑", "十字键 ↓", "十字键 ←", "十字键 →", "LB 左肩键", "RB 右肩键", "L3 左摇杆按下", "R3 右摇杆按下" };
        private static readonly string[] Gestures = { "逆时针", "按下", "顺时针" };
        private static readonly string[] LayerNames = { "通用 / 浏览器", "Codex / ChatGPT", "终端 / tmux", "Notion", "Obsidian", "Blender", "Total Commander", "通讯" };
        private static readonly string[][] ButtonLabels = {
            new[] { "PixPin 截图", "PixPin 贴图", "撤销", "重做", "剪切", "复制", "粘贴", "纯文本粘贴", "新标签", "恢复关闭标签", "查找", "CopyQ 剪贴板历史" },
            new[] { "任务 1", "任务 2", "任务 3", "分叉任务", "任务 4", "任务 5", "任务 6", "快速聊天开 / 关", "显示 / 隐藏侧栏", "显示 / 隐藏侧面板", "Codex 内打开 ChatGPT", "普通新任务" },
            new[] { "右侧新建 pane", "下方新建 pane", "左右 pane 切换", "上下 pane 切换", "缩放当前 pane", "横竖均分布局", "轮换 pane 位置", "window 选择器", "暂存 / 恢复 Agent 输入", "tmux copy-mode", "搜索终端历史", "上一个 pane" },
            new[] { "新页面", "新标签", "页内查找", "切换深浅色", "评论", "复制块", "块菜单", "复制页面链接", "删除块", "待办块", "代码块", "全屏" },
            new[] { "新笔记", "快速切换", "库内搜索", "命令面板", "内部链接", "切换任务", "改名 / 移动", "复制笔记链接", "关闭", "今日日记", "模板 / 大纲", "编辑 / 阅读" },
            new[] { "对象 / 编辑模式", "挤出", "移动", "旋转", "缩放", "内插面", "倒角", "复制", "撤销", "重做", "添加", "操作搜索" },
            new[] { "复制", "移动", "新目录", "批量改名", "Linux 路径", "文件名", "完整路径", "快速搜索", "删除", "比较", "同步目录", "压缩 / 解压" },
            new[] { "上一会话", "下一会话", "跳到未读", "搜索", "复制", "纯文本粘贴", "回复待适配", "发送", "清空消息输入框", "表情", "输入换行", "空置" }
        };
        private static readonly string[][] EncoderLabels = {
            new[] { "后退", "地址栏", "前进", "上一个标签", "关闭标签", "下一个标签", "缩小", "恢复 100%", "放大", "加速向上滚动", "刷新", "加速向下滚动" },
            new[] { "降低推理强度", "Astra Ultra + Fast", "提高推理强度", "上一个相邻任务", "Astra High + Standard", "下一个相邻任务", "光标前移一词", "聚焦输入框", "光标后移一词", "对话加速向上滚动", "跳到对话底部", "对话加速向下滚动" },
            new[] { "pane 边界向左 5 格", "均衡 pane 宽度", "pane 边界向右 5 格", "pane 边界向上 5 格", "均衡 pane 高度", "pane 边界向下 5 格", "上一个 window", "回到上一个 window", "下一个 window", "加速向上滚动", "跳到终端底部", "加速向下滚动" },
            new[] { "页面后退", "上一级", "页面前进", "上一个标签", "快速搜索", "下一个标签", "横向向左滚动", "切换页面全宽", "横向向右滚动", "页面向上滚动", "跳到顶部", "页面向下滚动" },
            new[] { "历史后退", "反向链接", "历史前进", "上一个标签", "恢复关闭标签", "下一个标签", "缩小", "恢复 100%", "放大", "内容向上滚动", "跳到顶部", "内容向下滚动" },
            new[] { "绕视角左", "前视图", "绕视角右", "绕视角上", "顶视图", "绕视角下", "上一帧", "播放 / 暂停", "下一帧", "原生滚轮向上", "框选中", "原生滚轮向下" },
            new[] { "目录后退", "上级目录", "目录前进", "左面板焦点", "交换面板", "右面板焦点", "上一个标签", "锁定标签", "下一个标签", "列表向上滚动", "快速查看", "列表向下滚动" },
            new[] { "光标前移一词", "行首", "光标后移一词", "向左选择一词", "全选", "向右选择一词", "缩小", "恢复 100%", "放大", "内容向上滚动", "跳到底部", "内容向下滚动" }
        };
        internal static string LayerName(int layer) { CheckLayer(layer); return "L" + (layer == 0 ? 8 : layer) + " · " + LayerNames[layer]; }
        internal static List<ProfileControl> Controls(string device)
        {
            CheckDevice(device);
            var result = new List<ProfileControl>();
            if (device == "keyboard")
            {
                for (int i = 1; i <= 12; i++) result.Add(new ProfileControl("button." + i, "键 " + i + " · 短按"));
                for (int i = 1; i <= 4; i++) for (int g = 0; g < 3; g++) result.Add(new ProfileControl("encoder." + i + "." + g, "K" + i + " · " + Gestures[g]));
            }
            else
            {
                result.Add(new ProfileControl("leftstick", "左摇杆")); result.Add(new ProfileControl("rightstick", "右摇杆"));
                for (int i = 0; i < Buttons.Length; i++) result.Add(new ProfileControl(Buttons[i], ButtonTitles[i]));
                for (int i = 0; i < Buttons.Length; i++) result.Add(new ProfileControl("rt." + Buttons[i], "RT + " + ButtonTitles[i]));
            }
            return result;
        }
        internal static Dictionary<string, string> Defaults(string device, int layer)
        {
            CheckDevice(device); CheckLayer(layer);
            var m = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ProfileControl c in Controls(device)) m[c.Id] = device == "keyboard" ? c.Id : "none";
            if (device == "keyboard") return m;
            m["leftstick"] = "stick.mouse"; m["rightstick"] = "stick.scroll";
            m["a"] = "key.enter"; m["b"] = "key.escape"; m["x"] = "mouse.left"; m["y"] = "mouse.right";
            m["l3"] = "bridge.help"; m["r3"] = "mouse.middle";
            m["lb"] = "navigation.previous"; m["rb"] = "navigation.next";
            m["rt.a"] = "edit.copy"; m["rt.b"] = "edit.paste"; m["rt.x"] = "edit.undo"; m["rt.y"] = "edit.redo";
            m["rt.lb"] = "encoder.1.0"; m["rt.rb"] = "encoder.1.2";
            m["rt.up"] = "key.pageup"; m["rt.down"] = "key.pagedown"; m["rt.left"] = "key.home"; m["rt.right"] = "key.end";
            foreach (string d in new[] { "up", "down", "left", "right" }) m[d] = "key." + d;
            switch (layer)
            {
                case 1:
                    m["r3"] = "key.tab"; m["rt.r3"] = "mouse.middle";
                    m["rt.l3"] = "codex.composer"; m["rt.up"] = "encoder.1.2"; m["rt.down"] = "encoder.1.0";
                    m["rt.left"] = "button.9"; m["rt.right"] = "button.10"; m["rt.lb"] = "encoder.3.0"; m["rt.rb"] = "encoder.3.2"; break;
                case 2:
                    m["rt.l3"] = "button.5"; m["rt.x"] = "button.10"; m["rt.y"] = "button.9";
                    foreach (string d in new[] { "up", "down", "left", "right" }) m["rt." + d] = "tmux.pane." + d;
                    m["rt.lb"] = "tmux.session.previous"; m["rt.rb"] = "tmux.session.next"; break;
                case 3:
                    m["rt.l3"] = "encoder.2.1"; m["rt.lb"] = "button.1"; m["rt.rb"] = "button.2";
                    m["rt.left"] = "button.10"; m["rt.right"] = "button.11"; break;
                case 4:
                    m["rt.l3"] = "button.2"; m["rt.lb"] = "button.1"; m["rt.rb"] = "button.4";
                    m["rt.left"] = "button.12"; m["rt.right"] = "button.10"; break;
                case 5:
                    m["rt.l3"] = "button.1"; m["rightstick"] = "stick.blender";
                    m["rt.up"] = "button.3"; m["rt.down"] = "button.4"; m["rt.left"] = "button.5"; m["rt.right"] = "button.12";
                    m["rt.lb"] = "encoder.3.0"; m["rt.rb"] = "encoder.3.2"; break;
                case 6:
                    m["rt.l3"] = "files.open"; m["rt.lb"] = "button.3"; m["rt.rb"] = "button.8";
                    m["rt.up"] = "encoder.1.1"; m["rt.down"] = "encoder.4.1"; m["rt.left"] = "button.4"; m["rt.right"] = "button.5"; break;
                case 7: m["rt.l3"] = "button.4"; m["rt.lb"] = "button.10"; m["rt.rb"] = "button.11"; break;
                default: m["rt.l3"] = "button.9"; m["rt.left"] = "button.10"; m["rt.right"] = "button.11"; break;
            }
            return m;
        }
        internal static List<ProfileChoice> Choices(string device, string control, int layer)
        {
            CheckDevice(device); CheckLayer(layer);
            if (!Controls(device).Exists(c => c.Id == control)) throw new ArgumentException("未知控制键：" + control);
            var result = new List<ProfileChoice>();
            Add(result, "none", "不执行动作");
            if (control == "leftstick" || control == "rightstick")
            {
                Add(result, "stick.mouse", "移动鼠标"); Add(result, "stick.scroll", "双向滚动");
                if (layer == 5) Add(result, "stick.blender", "Blender 旋转视角");
                return result;
            }
            if (device == "xbox") { Add(result, "mouse.left", "鼠标左键 / 按住拖拽"); Add(result, "mouse.right", "鼠标右键"); Add(result, "mouse.middle", "鼠标中键"); }
            Add(result, "bridge.help", "打开本层设置 / 按键说明"); Add(result, "bridge.chooser", "任务 / 已有会话选择面板");
            string[] keys = { "enter", "escape", "up", "down", "left", "right", "pageup", "pagedown", "home", "end", "tab", "space" };
            string[] titles = { "回车", "Esc", "↑", "↓", "←", "→", "Page Up", "Page Down", "Home", "End", "Tab", "空格" };
            for (int i = 0; i < keys.Length; i++) Add(result, "key." + keys[i], titles[i]);
            Add(result, "edit.copy", layer == 2 ? "终端复制 · Ctrl+Shift+C" : "复制 · Ctrl+C"); Add(result, "edit.paste", layer == 2 ? "终端粘贴 · Ctrl+Shift+V" : "粘贴 · Ctrl+V");
            if (layer != 2) { Add(result, "edit.undo", "撤销 · Ctrl+Z"); Add(result, "edit.redo", "重做（按当前应用）"); }
            Add(result, "navigation.previous", "本层：上一个 / 后退"); Add(result, "navigation.next", "本层：下一个 / 前进");
            if (layer == 1) Add(result, "codex.composer", "聚焦聊天输入框");
            if (layer == 6) Add(result, "files.open", "打开 Total Commander 所选项目");
            if (layer == 2)
            {
                Add(result, "tmux.pane.up", "上方 pane"); Add(result, "tmux.pane.down", "下方 pane"); Add(result, "tmux.pane.left", "左方 pane"); Add(result, "tmux.pane.right", "右方 pane");
                Add(result, "tmux.session.previous", "上一个已有 session"); Add(result, "tmux.session.next", "下一个已有 session");
            }
            for (int i = 1; i <= 12; i++) Add(result, "button." + i, "键 " + i + " · " + ButtonLabels[layer][i - 1]);
            for (int i = 1; i <= 4; i++) for (int g = 0; g < 3; g++) Add(result, "encoder." + i + "." + g, "K" + i + " " + Gestures[g] + " · " + EncoderLabels[layer][(i - 1) * 3 + g]);
            return result;
        }
        internal static string Title(string action, string device, string control, int layer)
        {
            ProfileChoice choice = Choices(device, control, layer).Find(c => c.Id == action);
            return choice == null ? action : choice.Title;
        }
        internal static bool ValidAction(string device, string control, int layer, string action) { return Choices(device, control, layer).Exists(c => c.Id == action); }
        private static void Add(List<ProfileChoice> target, string id, string title) { target.Add(new ProfileChoice(id, title)); }
        private static void CheckDevice(string device) { if (device != "keyboard" && device != "xbox") throw new ArgumentException("未知设备：" + device); }
        private static void CheckLayer(int layer) { if (layer < 0 || layer > 7) throw new ArgumentOutOfRangeException("layer"); }
    }

    internal sealed class WindowsProfileDocument
    {
        public int Version { get; set; }
        public string Platform { get; set; }
        public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Bindings { get; set; }
        public bool XboxEnabled { get; set; }
        public double DeadZone { get; set; }
        public double CursorSpeed { get; set; }
        public double ScrollSpeed { get; set; }
        public double PrecisionScale { get; set; }
        public bool InvertScroll { get; set; }
        public List<string> PausedProcesses { get; set; }
        public WindowsProfileDocument()
        {
            Version = 1; Platform = "windows";
            Bindings = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
            XboxEnabled = true; DeadZone = .16; CursorSpeed = 1500; ScrollSpeed = 750; PrecisionScale = .25;
            PausedProcesses = new List<string> { "steam", "GameBar", "GameBarFTServer" };
        }
        internal void Validate()
        {
            if (Version != 1 || Platform != "windows") throw new ArgumentException("需要 Windows Bridge 版本 1 配置；Mac 配置不能直接导入。");
            if (!Range(DeadZone, .05, .4) || !Range(CursorSpeed, 200, 4000) || !Range(ScrollSpeed, 50, 2000) || !Range(PrecisionScale, .1, .7)) throw new ArgumentException("摇杆参数超出允许范围。");
            if (PausedProcesses == null || PausedProcesses.Count > 200) throw new ArgumentException("暂停进程列表无效。");
            foreach (string process in PausedProcesses)
            {
                if (string.IsNullOrWhiteSpace(process) || process.Length > 128 || process.Trim() != process || process.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0) throw new ArgumentException("暂停列表只接受进程名，例如 game.exe；不能填写路径或通配符。");
                foreach (char c in process) if (char.IsControl(c)) throw new ArgumentException("暂停进程名包含控制字符。");
            }
            if (Bindings == null) throw new ArgumentException("按键配置不能为空。");
            foreach (var device in Bindings)
            {
                var controls = WindowsLayerProfiles.Controls(device.Key);
                if (device.Value == null) throw new ArgumentException("设备配置为空。");
                foreach (var entry in device.Value)
                {
                    int layer;
                    if (!int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out layer) || layer < 0 || layer > 7 || layer.ToString(CultureInfo.InvariantCulture) != entry.Key) throw new ArgumentException("未知层：" + entry.Key);
                    if (entry.Value == null) throw new ArgumentException("层配置为空。");
                    foreach (var binding in entry.Value)
                        if (!controls.Exists(c => c.Id == binding.Key) || !WindowsLayerProfiles.ValidAction(device.Key, binding.Key, layer, binding.Value)) throw new ArgumentException(WindowsLayerProfiles.LayerName(layer) + " 的 " + binding.Key + " 动作无效。");
                }
            }
        }
        internal WindowsProfileDocument Clone()
        {
            var result = new WindowsProfileDocument { Version = Version, Platform = Platform, XboxEnabled = XboxEnabled, DeadZone = DeadZone, CursorSpeed = CursorSpeed, ScrollSpeed = ScrollSpeed, PrecisionScale = PrecisionScale, InvertScroll = InvertScroll, PausedProcesses = PausedProcesses == null ? null : new List<string>(PausedProcesses) };
            if (Bindings == null) { result.Bindings = null; return result; }
            foreach (var device in Bindings)
            {
                if (device.Value == null) { result.Bindings[device.Key] = null; continue; }
                var layers = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                foreach (var layer in device.Value) layers[layer.Key] = layer.Value == null ? null : new Dictionary<string, string>(layer.Value, StringComparer.Ordinal);
                result.Bindings[device.Key] = layers;
            }
            return result;
        }
        private static bool Range(double value, double min, double max) { return !double.IsNaN(value) && !double.IsInfinity(value) && value >= min && value <= max; }
    }

    internal sealed class WindowsProfileStore
    {
        private readonly object _gate = new object();
        private WindowsProfileDocument _document = new WindowsProfileDocument();
        private string _loadError;
        internal string Path { get; private set; }
        internal string LoadError { get { lock (_gate) return _loadError; } }
        internal WindowsProfileDocument Document { get { lock (_gate) return _document.Clone(); } }
        internal event Action Changed;
        internal WindowsProfileStore(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            if (!File.Exists(Path)) return;
            try { _document = Read(Path); }
            catch (Exception e) { _document.XboxEnabled = false; _loadError = "配置未加载：" + e.Message + " 键盘使用默认映射，Xbox 已暂停；原文件已保留。"; }
        }
        internal string Action(string device, string control, int layer)
        {
            lock (_gate) return Resolve(_document, device, control, layer);
        }
        internal static string Resolve(WindowsProfileDocument document, string device, string control, int layer)
        {
            var defaults = WindowsLayerProfiles.Defaults(device, layer);
            Dictionary<string, Dictionary<string, string>> layers;
            Dictionary<string, string> bindings;
            string action;
            if (document.Bindings.TryGetValue(device, out layers) && layers.TryGetValue(layer.ToString(CultureInfo.InvariantCulture), out bindings) && bindings.TryGetValue(control, out action)) return action;
            return defaults.TryGetValue(control, out action) ? action : "none";
        }
        internal void Replace(WindowsProfileDocument next)
        {
            if (next == null) throw new ArgumentNullException("next");
            WindowsProfileDocument copy = next.Clone(); copy.Validate();
            string text = Serialize(copy);
            lock (_gate)
            {
                AtomicWrite(Path, text, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), "profiles.windows.previous.json"));
                _document = copy; _loadError = null;
            }
            Action changed = Changed; if (changed != null) changed();
        }
        internal void Set(string device, string control, int layer, string action)
        {
            WindowsProfileDocument next = Document;
            Put(next, device, control, layer, action); Replace(next);
        }
        internal static void Put(WindowsProfileDocument document, string device, string control, int layer, string action)
        {
            if (!WindowsLayerProfiles.ValidAction(device, control, layer, action)) throw new ArgumentException("动作无效。");
            Dictionary<string, Dictionary<string, string>> layers;
            if (!document.Bindings.TryGetValue(device, out layers)) document.Bindings[device] = layers = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            string key = layer.ToString(CultureInfo.InvariantCulture);
            Dictionary<string, string> bindings;
            if (!layers.TryGetValue(key, out bindings)) layers[key] = bindings = new Dictionary<string, string>(StringComparer.Ordinal);
            bindings[control] = action;
        }
        internal void Reset(string device, int layer)
        {
            WindowsProfileDocument next = Document;
            Dictionary<string, Dictionary<string, string>> layers;
            if (next.Bindings.TryGetValue(device, out layers)) layers.Remove(layer.ToString(CultureInfo.InvariantCulture));
            Replace(next);
        }
        internal void Import(string source) { Replace(Read(source)); }
        internal void Export(string destination) { WriteExport(destination, Document); }
        internal static void WriteExport(string destination, WindowsProfileDocument document)
        {
            document.Validate(); AtomicWrite(System.IO.Path.GetFullPath(destination), Serialize(document), null);
        }
        internal static WindowsProfileDocument Read(string path)
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new ArgumentException("配置文件超过 1 MB。");
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        internal static string Serialize(WindowsProfileDocument document) { document.Validate(); return Serializer().Serialize(document); }
        internal static WindowsProfileDocument Parse(string text)
        {
            if (text == null || text.Length > 1024 * 1024) throw new ArgumentException("配置文件过大或为空。");
            JavaScriptSerializer serializer = Serializer();
            var root = serializer.DeserializeObject(text) as Dictionary<string, object>;
            if (root == null || !root.ContainsKey("Version") || !root.ContainsKey("Platform") || !root.ContainsKey("Bindings")) throw new ArgumentException("缺少 Windows 配置格式标识。");
            var allowed = new HashSet<string>(new[] { "Version", "Platform", "Bindings", "XboxEnabled", "DeadZone", "CursorSpeed", "ScrollSpeed", "PrecisionScale", "InvertScroll", "PausedProcesses" }, StringComparer.Ordinal);
            foreach (string key in root.Keys) if (!allowed.Contains(key)) throw new ArgumentException("未知配置字段：" + key);
            foreach (string key in allowed) if (!root.ContainsKey(key)) throw new ArgumentException("缺少配置字段：" + key);
            WindowsProfileDocument document = serializer.Deserialize<WindowsProfileDocument>(text);
            if (document == null) throw new ArgumentException("配置不能为空。"); document.Validate(); return document;
        }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 16 }; }
        private static void AtomicWrite(string path, string text, string backup)
        {
            string directory = System.IO.Path.GetDirectoryName(path); Directory.CreateDirectory(directory);
            string temporary = System.IO.Path.Combine(directory, ".profiles-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                // Same-volume replacement is atomic and retains the last complete file as the backup.
                if (File.Exists(path)) File.Replace(temporary, path, backup);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
