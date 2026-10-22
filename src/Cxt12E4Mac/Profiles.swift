import Foundation

struct BindingChoice { let id:String; let title:String }
struct DeviceControl { let id:String; let title:String }

enum LayerProfiles {
    static let devices = ["keyboard", "xbox"]
    static let buttons:[String] = ["a","b","x","y","up","down","left","right","lb","rb","l3","r3"]
    static let buttonTitles = ["A", "B", "X", "Y", "十字键 ↑", "十字键 ↓", "十字键 ←", "十字键 →", "LB 左肩键", "RB 右肩键", "L3 左摇杆按下", "R3 右摇杆按下"]
    static func controls(_ device:String)->[DeviceControl] {
        if device == "keyboard" {
            return (1...12).map {DeviceControl(id:"button.\($0)",title:"键 \($0) · 短按")} + (1...4).flatMap {n in (0...2).map {g in DeviceControl(id:"encoder.\(n).\(g)",title:"K\(n) · \(["逆时针","按下","顺时针"][g])")} }
        }
        return [DeviceControl(id:"leftstick",title:"左摇杆"),DeviceControl(id:"rightstick",title:"右摇杆")] + zip(buttons,buttonTitles).map{DeviceControl(id:$0.0,title:$0.1)} + zip(buttons,buttonTitles).map{DeviceControl(id:"rt."+$0.0,title:"RT + "+$0.1)}
    }
    static func defaults(_ device:String,_ layer:Int)->[String:String] {
        if device == "keyboard" {return Dictionary(uniqueKeysWithValues:controls(device).map{($0.id,$0.id)})}
        var m = ["leftstick":"stick.mouse","rightstick":"stick.scroll","a":"key.enter","b":"key.escape","x":"mouse.left","y":"mouse.right","rt.l3":"key.enter","l3":"bridge.help","r3":"mouse.middle","lb":"navigation.previous","rb":"navigation.next","rt.a":"edit.copy","rt.b":"edit.paste","rt.x":"edit.undo","rt.y":"edit.redo","rt.lb":"encoder.1.0","rt.rb":"encoder.1.2","rt.up":"key.pageup","rt.down":"key.pagedown","rt.left":"key.home","rt.right":"key.end"]
        for direction in ["up","down","left","right"] {m[direction]="key."+direction}
        switch layer {
        case 1:
            m["r3"]="key.tab";m["rt.r3"]="mouse.middle"
            m["rt.l3"]="codex.composer";m["rt.up"]="encoder.1.2";m["rt.down"]="encoder.1.0"
            m["rt.left"]="button.9";m["rt.right"]="button.10";m["rt.lb"]="encoder.3.0";m["rt.rb"]="encoder.3.2"
        case 2:
            m["rt.l3"]="button.5"
            for d in ["up","down","left","right"] {m["rt."+d]="tmux.pane."+d}
            m["rt.lb"]="tmux.session.previous";m["rt.rb"]="tmux.session.next"
        case 3:
            m["rt.l3"]="encoder.2.1";m["rt.lb"]="button.1";m["rt.rb"]="button.2"
            m["rt.left"]="button.10";m["rt.right"]="button.11"
        case 4:
            m["rt.l3"]="button.2";m["rt.lb"]="button.1";m["rt.rb"]="button.4"
            m["rt.left"]="button.12";m["rt.right"]="button.10"
        case 5:
            m["rt.l3"]="button.1";m["rightstick"]="stick.blender"
            m["rt.up"]="button.3";m["rt.down"]="button.4";m["rt.left"]="button.5";m["rt.right"]="button.12"
            m["rt.lb"]="encoder.3.0";m["rt.rb"]="encoder.3.2"
        case 6:
            m["rt.l3"]="finder.open";m["rt.lb"]="button.3";m["rt.rb"]="button.8"
            m["rt.up"]="encoder.1.1";m["rt.down"]="encoder.4.1";m["rt.left"]="button.4";m["rt.right"]="button.5"
        case 7:
            m["rt.l3"]="button.4";m["rt.lb"]="button.10";m["rt.rb"]="button.11"
        default: m["rt.l3"]="button.9";m["rt.left"]="button.10";m["rt.right"]="button.11"
        }
        return m
    }
    static func choices(_ device:String,_ control:String,_ layer:Int)->[BindingChoice] {
        if control == "leftstick" || control == "rightstick" {
            var items=[BindingChoice(id:"none",title:"停用"),BindingChoice(id:"stick.mouse",title:"移动鼠标"),BindingChoice(id:"stick.scroll",title:"双向滚动")]
            if layer == 5 {items.append(BindingChoice(id:"stick.blender",title:"Blender 旋转视角"))}
            return items
        }
        var items=[BindingChoice(id:"none",title:"不执行动作")]
        if device == "xbox" {items += [BindingChoice(id:"mouse.left",title:"鼠标左键 / 按住拖拽"),BindingChoice(id:"mouse.right",title:"鼠标右键"),BindingChoice(id:"mouse.middle",title:"鼠标中键")]}
        items += [BindingChoice(id:"bridge.help",title:"打开本层设置 / 按键说明"),BindingChoice(id:"bridge.chooser",title:"任务 / 会话选择面板")]
        for (id,title) in [("enter","回车"),("escape","Esc"),("up","↑"),("down","↓"),("left","←"),("right","→"),("pageup","Page Up"),("pagedown","Page Down"),("home","Home"),("end","End"),("tab","Tab"),("space","空格")] {items.append(BindingChoice(id:"key."+id,title:title))}
        for (id,title) in [("copy","复制"),("paste","粘贴"),("undo","撤销"),("redo","重做")] {items.append(BindingChoice(id:"edit."+id,title:title))}
        items += [BindingChoice(id:"navigation.previous",title:"本层：上一个 / 后退"),BindingChoice(id:"navigation.next",title:"本层：下一个 / 前进")]
        if layer == 1 {items.append(BindingChoice(id:"codex.composer",title:"聚焦聊天输入框"))}
        if layer == 6 {items.append(BindingChoice(id:"finder.open",title:"打开 Finder 所选项目"))}
        if layer == 2 {
            for (id,title) in [("pane.up","上方 pane"),("pane.down","下方 pane"),("pane.left","左方 pane"),("pane.right","右方 pane"),("session.previous","上一个已有 session"),("session.next","下一个已有 session")] {items.append(BindingChoice(id:"tmux."+id,title:title))}
        }
        for n in 1...12 {items.append(BindingChoice(id:"button.\(n)",title:"键\(n) · "+buttonLabels[layer]![n-1]))}
        for n in 1...4 {for g in 0...2 {items.append(BindingChoice(id:"encoder.\(n).\(g)",title:"K\(n) \(["逆转","按下","顺转"][g]) · "+encoderLabels[layer]![n-1][g]))}}
        return items
    }
    static func title(_ id:String,_ device:String,_ control:String,_ layer:Int)->String {choices(device,control,layer).first{$0.id==id}?.title ?? id}
    // Filled from the audited Mac manual; identifiers execute the shared Actions methods.
    static let buttonLabels:[Int:[String]] = [
        0: ["PixPin 截图", "PixPin 贴图", "撤销", "重做", "剪切", "复制", "粘贴", "纯文本粘贴", "新标签", "恢复关闭标签", "查找", "剪贴板历史 / K12组合"],
        1: ["任务 1", "任务 2", "任务 3", "分叉任务", "任务 4", "任务 5", "任务 6", "快速聊天开 / 关", "显示 / 隐藏侧栏", "显示 / 隐藏审阅面板", "Codex 内打开 ChatGPT", "Codex 独立聊天 / ChatGPT 新聊天"],
        2: ["右侧新建 pane", "下方新建 pane", "左右 pane 切换", "上下 pane 切换", "缩放当前 pane", "切换pane均分布局", "轮换 pane 位置", "window 选择器", "暂存 / 恢复 Agent 输入", "tmux copy-mode", "搜索终端历史", "上一个 pane / K12组合"],
        3: ["新页面", "新标签", "页内查找", "切换深浅色", "评论", "复制块", "块菜单", "复制页面链接", "删除块", "待办块", "代码块", "全屏 / K12组合"],
        4: ["新笔记", "快速切换", "库内搜索", "命令面板", "内部链接", "切换任务", "改名 / 移动", "复制笔记链接", "关闭", "今日日记", "模板 / 大纲", "编辑 / 阅读 / K12组合"],
        5: ["对象 / 编辑模式", "挤出", "移动", "旋转", "缩放", "内插面", "倒角", "复制", "撤销", "重做", "添加", "操作搜索 / K12组合"],
        6: ["复制", "移动已复制项目", "新建文件夹", "重命名", "复制 POSIX 路径", "复制文件名", "复制完整路径", "搜索", "移到废纸篓", "比较两个文件", "目录复制更新", "压缩 / K12组合"],
        7: ["上一会话", "下一会话", "跳到未读", "搜索", "复制", "纯文本粘贴", "回复待适配", "发送", "清空当前消息输入框", "表情与符号", "输入换行", "空置 / K12组合"]
    ]
    static let encoderLabels:[Int:[[String]]] = [
        0: [["后退 · ⌘+[", "地址栏 · ⌘+L", "前进 · ⌘+]"], ["上一个标签 · Ctrl+Shift+Tab", "关闭标签 · ⌘+W", "下一个标签 · Ctrl+Tab"], ["缩小 · ⌘+Minus", "恢复100% · ⌘+0", "放大 · ⌘+Plus"], ["加速向上滚动", "刷新 · ⌘+R", "加速向下滚动"]],
        1: [["降低强度；边界切换 Sol / Astra", "Astra Ultra + Fast", "提高强度；边界切换 Sol / Astra"], ["上一个相邻任务", "Astra 高 / High + 普通 / Standard", "下一个相邻任务"], ["光标向前移动一词", "聚焦输入框", "光标向后移动一词"], ["对话加速向上滚动", "跳到对话底部", "对话加速向下滚动"]],
        2: [["pane边界向左5格", "均衡pane宽度", "pane边界向右5格"], ["pane边界向上5格", "均衡pane高度", "pane边界向下5格"], ["当前session上一个window", "回到上一个window", "当前session下一个window"], ["向上真实加速滚动", "退出 copy-mode，再跳到终端底部", "向下真实加速滚动"]],
        3: [["页面后退", "上一级 · ⌘+Shift+U", "页面前进"], ["上一个标签", "快速搜索 · ⌘+P", "下一个标签"], ["宽表格向左滚动", "切换页面全宽", "宽表格向右滚动"], ["页面向上滚动", "跳到顶部", "页面向下滚动"]],
        4: [["历史后退", "显示反向链接", "历史前进"], ["上一个标签", "恢复关闭标签", "下一个标签"], ["缩小", "恢复 100%", "放大"], ["内容向上滚动", "跳到顶部", "内容向下滚动"]],
        5: [["绕视角左 · Numpad4", "前视 · Numpad1", "绕视角右 · Numpad6"], ["绕视角上 · Numpad8", "顶视 · Numpad7", "绕视角下 · Numpad2"], ["上一帧 · Left", "播放 / 暂停 · Space", "下一帧 · Right"], ["无修饰键加速向上滚轮", "框选中 · NumpadDecimal", "无修饰键加速向下滚轮"]],
        6: [["目录后退 · ⌘+[", "上级目录 · ⌘+↑", "目录前进 · ⌘+]"], ["向左移动选择", "Ctrl+Tab", "向右移动选择"], ["上一个标签", "新标签 · ⌘+T", "下一个标签"], ["向上加速滚动", "快速查看 · Space", "向下加速滚动"]],
        7: [["光标前移一词", "行首 Home", "光标后移一词"], ["向左选择一词", "全选", "向右选择一词"], ["缩小", "恢复 100%", "放大"], ["内容向上滚动", "跳到底部", "内容向下滚动"]]
    ]
}

struct BridgeProfileDocument:Codable {
    var version=1
    var bindings:[String:[String:[String:String]]] = [:]
    var xboxEnabled=true
    var deadZone=0.16
    var cursorSpeed=1500.0
    var scrollSpeed=750.0
    var precisionScale=0.25
    var invertScroll=false
    var pausedApplications=["com.valvesoftware.steam", "com.apple.Games"]
    func validated() throws ->BridgeProfileDocument {
        func fail(_ text:String)throws {throw NSError(domain:"BridgeProfiles",code:1,userInfo:[NSLocalizedDescriptionKey:text])}
        guard version==1 else {try fail("不支持此配置版本");return self}
        guard deadZone.isFinite, (0.05...0.4).contains(deadZone),cursorSpeed.isFinite,(200...4000).contains(cursorSpeed),scrollSpeed.isFinite,(50...2000).contains(scrollSpeed),precisionScale.isFinite,(0.1...0.7).contains(precisionScale) else {try fail("摇杆参数超出范围");return self}
        guard pausedApplications.count<=200,pausedApplications.allSatisfy({!$0.isEmpty && $0.count<200 && !$0.contains("\n")}) else {try fail("暂停应用列表无效");return self}
        for (device,layers) in bindings {
            guard LayerProfiles.devices.contains(device) else {try fail("未知设备：\(device)");return self}
            for (key,controls) in layers {
                guard let layer=Int(key),(0...7).contains(layer),String(layer)==key else {try fail("未知层：\(key)");return self}
                for (control,action) in controls {
                    guard LayerProfiles.controls(device).contains(where:{$0.id==control}),LayerProfiles.choices(device,control,layer).contains(where:{$0.id==action}) else {try fail("L\(layer==0 ? 8:layer) 的 \(control) 动作无效");return self}
                }
            }
        }
        return self
    }
}

final class ProfileStore {
    let url:URL
    private(set) var document=BridgeProfileDocument()
    private(set) var loadError:String?
    var onChange:()->Void = {}
    init(url:URL=support.appendingPathComponent("profiles.json")) {
        self.url=url
        if FileManager.default.fileExists(atPath:url.path) {
            do {document=try JSONDecoder().decode(BridgeProfileDocument.self,from:Data(contentsOf:url)).validated()}
            catch {document.xboxEnabled=false;loadError="配置未加载：\(error.localizedDescription)；键盘使用默认映射，Xbox 已暂停";log(loadError!)}
        }
    }
    func action(_ device:String,_ control:String,_ layer:Int)->String {
        document.bindings[device]?[String(layer)]?[control] ?? LayerProfiles.defaults(device,layer)[control] ?? "none"
    }
    func replace(_ next:BridgeProfileDocument) throws {
        let valid=try next.validated()
        let encoder=JSONEncoder();encoder.outputFormatting=[.prettyPrinted,.sortedKeys]
        let data=try encoder.encode(valid)
        try FileManager.default.createDirectory(at:url.deletingLastPathComponent(),withIntermediateDirectories:true)
        if FileManager.default.fileExists(atPath:url.path) {
            let backup=url.deletingLastPathComponent().appendingPathComponent("profiles.previous.json")
            try Data(contentsOf:url).write(to:backup,options:.atomic)
        }
        try data.write(to:url,options:.atomic)
        document=valid;loadError=nil;onChange()
    }
    func set(_ device:String,_ control:String,_ layer:Int,_ action:String)throws {
        var next=document
        var layers=next.bindings[device] ?? [:];var controls=layers[String(layer)] ?? [:]
        controls[control]=action;layers[String(layer)]=controls;next.bindings[device]=layers
        try replace(next)
    }
    func reset(_ device:String,_ layer:Int)throws {
        var next=document;next.bindings[device]?[String(layer)]=nil;try replace(next)
    }
}

func profileSelfTest() {
    var checks=0
    func check(_ value:Bool,_ message:String){checks+=1;if !value {fputs("FAIL profile: \(message)\n",stderr);exit(1)}}
    for device in LayerProfiles.devices {for layer in 0...7 {
        for (control,id) in LayerProfiles.defaults(device,layer) {check(LayerProfiles.choices(device,control,layer).contains{$0.id==id},"valid default \(device) L\(layer) \(control) \(id)")}
    }}
    let primaryActions=["button.9","codex.composer","button.5","encoder.2.1","button.2","button.1","finder.open","button.4"]
    for layer in 0...7 {
        let defaults=LayerProfiles.defaults("xbox",layer)
        for (control,action) in [("a","key.enter"),("b","key.escape"),("x","mouse.left"),("y","mouse.right")] {
            check(defaults[control]==action,"consistent face button L\(layer) \(control)")
        }
        check(defaults["rt.l3"]==primaryActions[layer],"layer primary action retained on RT+L3 L\(layer)")
        check(defaults["l3"]=="bridge.help","L3 still opens settings L\(layer)")
    }
    var bad=BridgeProfileDocument();bad.bindings=["xbox":["2":["a":"shell.rm"]]]
    check((try? bad.validated()) == nil,"reject arbitrary command")
    bad=BridgeProfileDocument();bad.bindings=["xbox":["1":["rightstick":"stick.blender"]]]
    check((try? bad.validated()) == nil,"restrict layer-specific stick mode")
    bad=BridgeProfileDocument();bad.deadZone = .nan
    check((try? bad.validated()) == nil,"reject nonfinite axis setting")
    let dir=FileManager.default.temporaryDirectory.appendingPathComponent("cxt-profile-test-"+UUID().uuidString)
    defer {try? FileManager.default.removeItem(at:dir)}
    let a=ProfileStore(url:dir.appendingPathComponent("profiles.json"))
    do {
        try a.set("xbox","x",3,"button.1")
        let b=ProfileStore(url:a.url)
        check(b.action("xbox","x",3)=="button.1","persistent override")
        check(b.action("xbox","x",1)=="mouse.left","layer isolation")
        check(b.action("keyboard","button.1",3)=="button.1","device isolation")
        try b.reset("xbox",3)
        check(b.action("xbox","x",3)=="mouse.left","reset only selected layer")
        let saved=try Data(contentsOf:b.url)
        var invalid=b.document;invalid.bindings=["keyboard":["0":["button.1":"mouse.left"]]]
        do {try b.replace(invalid);check(false,"invalid import rejected")} catch {check(try Data(contentsOf:b.url)==saved,"failed import preserves saved configuration")}
        try Data("broken json".utf8).write(to:b.url)
        let damaged=ProfileStore(url:b.url)
        check(damaged.loadError != nil && !damaged.document.xboxEnabled,"corrupt configuration pauses Xbox")
        check(try Data(contentsOf:b.url)==Data("broken json".utf8),"corrupt original retained for recovery")
    } catch {check(false,error.localizedDescription)}
    print("PASS \(checks) profile checks")
}
