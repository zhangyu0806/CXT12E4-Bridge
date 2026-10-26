import Foundation
import AppKit

func axValue(_ e:AXUIElement,_ name:String)->CFTypeRef? {var v:CFTypeRef?;AXUIElementCopyAttributeValue(e,name as CFString,&v);return v}
func axString(_ e:AXUIElement,_ name:String)->String {axValue(e,name) as? String ?? ""}
func axLabel(_ e:AXUIElement)->String {let d=axString(e,"AXDescription");return d.isEmpty ? axString(e,"AXTitle"):d}
func axNodes(_ pid:pid_t)->[AXUIElement] {
    let root=AXUIElementCreateApplication(pid);AXUIElementSetMessagingTimeout(root,0.12)
    AXUIElementSetAttributeValue(root,"AXManualAccessibility" as CFString,kCFBooleanTrue)
    var q:[AXUIElement]=[root];var i=0;let until=Date(timeIntervalSinceNow:0.8)
    while i<q.count && q.count<2500 && Date()<until {
        let n=q[i];i+=1
        if let children=axValue(n,"AXChildren") as? [AXUIElement] {q.append(contentsOf:children)}
    }
    return q
}

func axSubtree(_ root:AXUIElement,limit:Int=180,until:Double=ProcessInfo.processInfo.systemUptime+0.8,allowPartial:Bool=false)->[AXUIElement] {
    var queue=[root],nodes:[AXUIElement]=[],seen=Set<AXUIElement>(),index=0
    while index<queue.count && nodes.count<limit && ProcessInfo.processInfo.systemUptime<until {
        let node=queue[index];index+=1
        guard seen.insert(node).inserted else{continue}
        nodes.append(node)
        if let children=axValue(node,"AXChildren") as? [AXUIElement] {queue.append(contentsOf:children)}
    }
    return allowPartial || index==queue.count ? nodes:[]
}

func axModelNodes(_ pid:pid_t,until:Double=ProcessInfo.processInfo.systemUptime+0.8)->[AXUIElement] {
    // Reserve part of the caller's deadline for identifying/pressing controls.
    // A long transcript must not consume the entire menu-opening budget.
    let sampleUntil=min(until,ProcessInfo.processInfo.systemUptime+0.35)
    let root=AXUIElementCreateApplication(pid);AXUIElementSetMessagingTimeout(root,0.12)
    // Electron supports this opt-in; newer native shells may reject it. Read
    // the focused window directly instead of treating a menu-bar-only tree as
    // a complete page. Never enable EnhancedUserInterface or move the window.
    _=AXUIElementSetAttributeValue(root,"AXManualAccessibility" as CFString,kCFBooleanTrue)
    guard let value=axValue(root,"AXFocusedWindow"),CFGetTypeID(value)==AXUIElementGetTypeID() else{return []}
    let window=unsafeBitCast(value,to:AXUIElement.self)
    AXUIElementSetMessagingTimeout(window,0.12)
    return axSubtree(window,limit:6000,until:sampleUntil,allowPartial:true)
}

func axPowerMenu(in nodes:[AXUIElement],until:Double)->[AXUIElement] {
    guard var menu=nodes.first(where:{ProcessInfo.processInfo.systemUptime<until && axString($0,"AXRole")=="AXMenuItem" && ["强度","Power"].contains(axLabel($0))}) else{return []}
    for _ in 0..<6 where ProcessInfo.processInfo.systemUptime<until {
        if let parent=axValue(menu,"AXParent"),CFGetTypeID(parent)==AXUIElementGetTypeID() {menu=unsafeBitCast(parent,to:AXUIElement.self)} else{break}
        if ["AXWindow","AXApplication"].contains(axString(menu,"AXRole")) {break}
        let nodes=axSubtree(menu,until:until)
        // Ultra/Fast hides the model-list entry but retains the speed toggle.
        if nodes.contains(where:{ProcessInfo.processInfo.systemUptime<until && axString($0,"AXRole")=="AXMenuItem" && ["选择模型","Select model","启用快速模式","Enable fast mode","启用标准模式","Enable standard mode"].contains(axLabel($0))}) {return nodes}
    }
    return []
}

func axPowerMenu(_ pid:pid_t)->[AXUIElement] {
    let until=ProcessInfo.processInfo.systemUptime+0.8
    return axPowerMenu(in:axModelNodes(pid,until:until),until:until)
}

func axUltraFullAccessDialog(_ pid:pid_t)->[AXUIElement] {
    guard var ancestor=axModelNodes(pid).first(where:{axString($0,"AXRole")=="AXButton" && ["使用完整访问权限","Use full access","Use full access permissions"].contains(axLabel($0))}) else{return []}
    for _ in 0..<7 {
        guard let parent=axValue(ancestor,"AXParent"),CFGetTypeID(parent)==AXUIElementGetTypeID() else{break}
        ancestor=unsafeBitCast(parent,to:AXUIElement.self)
        let nodes=axSubtree(ancestor)
        let namedDialog=isUltraFullAccessDialogName(axLabel(ancestor))
        let heading=nodes.contains{e in axString(e,"AXRole")=="AXHeading" && axSubtree(e).contains{isUltraFullAccessDialogName(axLabel($0)) || isUltraFullAccessDialogName(axString($0,"AXValue"))}}
        if namedDialog || heading {return nodes}
    }
    return []
}

let keyCodes:[String:CGKeyCode] = ["a":0,"s":1,"d":2,"f":3,"h":4,"g":5,"z":6,"x":7,"c":8,"v":9,"b":11,"q":12,"w":13,"e":14,"r":15,"y":16,"t":17,"1":18,"2":19,"3":20,"4":21,"6":22,"5":23,"=":24,"9":25,"7":26,"-":27,"8":28,"0":29,"]":30,"o":31,"u":32,"[":33,"i":34,"p":35,"return":36,"l":37,"j":38,"'":39,"k":40,";":41,"\\":42,",":43,"/":44,"n":45,"m":46,".":47,"tab":48,"space":49,"backspace":51,"escape":53,"delete":117,"home":115,"end":119,"pageup":116,"pagedown":121,"left":123,"right":124,"down":125,"up":126,"f2":120,"f3":99,"f5":96,"f6":97,"f7":98,"f8":100,"f11":103,"kp4":86,"kp6":88,"kp8":91,"kp2":84,"kp1":83,"kp7":89,"kpdecimal":65]
let cmd=CGEventFlags.maskCommand, opt=CGEventFlags.maskAlternate, shift=CGEventFlags.maskShift, ctrl=CGEventFlags.maskControl

func reasoningTarget(_ initial:ModelSetting?,delta:Int,preset:String?,solModel:String)->ModelSetting? {
    if let preset=preset {
        guard ["high","ultra"].contains(preset) else{return nil}
        return ModelSetting(model:"GPT-6 Astra",effort:preset=="ultra" ? 5:2)
    }
    guard let old=initial,["GPT-6 Sol","GPT-6.1 Sol","GPT-6 Astra"].contains(old.model) else{return nil}
    let sol=old.model=="GPT-6 Astra" ? solModel:old.model
    guard ["GPT-6 Sol","GPT-6.1 Sol"].contains(sol) else{return nil}
    let base=old.model=="GPT-6 Astra" ? 5:0
    let value=old.effort==5 ? base+4:max(0,min(9,base+old.effort+delta))
    return ModelSetting(model:value<5 ? sol:"GPT-6 Astra",effort:value%5)
}

func actionsSelfTest() {
    var count=0
    func check(_ condition:Bool,_ message:String) {count+=1;if !condition {fatalError(message)}}
    for sol in ["GPT-6 Sol","GPT-6.1 Sol"] {
        for effort in 0...4 {
            let start=ModelSetting(model:sol,effort:effort)
            check(reasoningTarget(start,delta:-1,preset:nil,solModel:"GPT-6 Sol")==ModelSetting(model:sol,effort:max(0,effort-1)),"Sol rotation preserves exact model")
            let expected=effort<4 ? ModelSetting(model:sol,effort:effort+1):ModelSetting(model:"GPT-6 Astra",effort:0)
            check(reasoningTarget(start,delta:1,preset:nil,solModel:"GPT-6 Sol")==expected,"Sol boundary advances into Astra")
        }
        check(reasoningTarget(ModelSetting(model:"GPT-6 Astra",effort:0),delta:-1,preset:nil,solModel:sol)==ModelSetting(model:sol,effort:4),"Astra returns to remembered Sol version")
        check(reasoningTarget(ModelSetting(model:sol,effort:5),delta:-1,preset:nil,solModel:sol)==ModelSetting(model:sol,effort:4),"Ultra rotation returns to Max")
    }
    check(reasoningTarget(nil,delta:0,preset:"ultra",solModel:"GPT-6 Sol")==ModelSetting(model:"GPT-6 Astra",effort:5),"Explicit Ultra preset")
    check(reasoningTarget(nil,delta:0,preset:"high",solModel:"GPT-6 Sol")==ModelSetting(model:"GPT-6 Astra",effort:2),"Explicit High preset")
    check(reasoningTarget(nil,delta:1,preset:nil,solModel:"GPT-6 Sol")==nil,"Unknown current setting stops")
    check(reasoningTarget(ModelSetting(model:"GPT-6.2 Sol",effort:2),delta:1,preset:nil,solModel:"GPT-6 Sol")==nil,"Unknown model stops")
    check(reasoningTarget(ModelSetting(model:"GPT-6 Astra",effort:4),delta:1,preset:nil,solModel:"GPT-6 Sol")==ModelSetting(model:"GPT-6 Astra",effort:4),"Upper boundary stops at Max")
    print("PASS \(count) model target assertions")
}

final class Actions {
    let context:ContextStore
    var notify:(String)->Void={_ in}
    var taskSnapshot:()->[[String:Any]]={[]}
    var onTaskOpened:(String,String)->Void={_,_ in}
    var terminalBundle:()->String={"com.mitchellh.ghostty"}
    var lastWheelTime=0.0;var wheelStreak=0;var quickOpen=false
    var rememberedSolModel="GPT-6 Sol"
    init(context:ContextStore) {self.context=context}
    func key(_ name:String,_ flags:CGEventFlags=[],_ c:Foreground,global:Bool=false) {
        guard context.valid(c), AXIsProcessTrusted(), let code=keyCodes[name] else{return}
        let source=CGEventSource(stateID:.privateState)
        guard let down=CGEvent(keyboardEventSource:source,virtualKey:code,keyDown:true),let up=CGEvent(keyboardEventSource:source,virtualKey:code,keyDown:false) else{return}
        down.flags=flags;up.flags=flags
        if global {down.post(tap:.cghidEventTap);usleep(12000);up.post(tap:.cghidEventTap)}
        else {down.post(tap:.cghidEventTap);usleep(12000);up.post(tap:.cghidEventTap)}
    }
    func text(_ value:String,_ c:Foreground) {
        guard context.valid(c), AXIsProcessTrusted() else{return}
        // CGEvent text packets are bounded; preserve surrogate pairs across chunks.
        let chars=Array(value.utf16);var index=0
        while index<chars.count && context.valid(c) {
            var end=min(chars.count,index+20)
            if end<chars.count && (0xD800...0xDBFF).contains(chars[end-1]) {end-=1}
            let chunk=Array(chars[index..<end])
            for pressed in [true,false] {
                if let event=CGEvent(keyboardEventSource:CGEventSource(stateID:.privateState),virtualKey:0,keyDown:pressed) {
                    event.keyboardSetUnicodeString(stringLength:chunk.count,unicodeString:chunk);event.flags=[];event.post(tap:.cghidEventTap)
                }
            }
            index=end
        }
    }

    func wheel(_ clockwise:Bool,_ c:Foreground,horizontal:Bool=false) {
        guard context.valid(c),AXIsProcessTrusted() else{return}
        let now=ProcessInfo.processInfo.systemUptime
        wheelStreak = now-lastWheelTime<0.14 ? min(12,wheelStreak+1):0;lastWheelTime=now
        let delta:Int32=Int32(3+wheelStreak)*(clockwise ? -1:1)
        if let e=CGEvent(scrollWheelEvent2Source:CGEventSource(stateID:.privateState),units:.line,wheelCount:2,wheel1:horizontal ? 0:delta,wheel2:horizontal ? delta:0,wheel3:0) {e.flags=[];e.post(tap:.cghidEventTap)}
    }
    @discardableResult func click(_ labels:[String],_ c:Foreground,contains:Bool=false,last:Bool=false)->Bool {
        guard context.valid(c) else{return false}
        let nodes=axNodes(c.pid).filter{e in
            let role=axString(e,"AXRole"),label=axLabel(e)
            return ["AXButton","AXMenuItem","AXCheckBox","AXRadioButton","AXPopUpButton","AXLink"].contains(role) && labels.contains{contains ? label.localizedCaseInsensitiveContains($0):label==$0}
        }
        guard let e=last ? nodes.last:nodes.first,context.valid(c) else{return false}
        return AXUIElementPerformAction(e,kAXPressAction as CFString) == .success
    }
    func fail(_ message:String) {log(message);notify(message)}
    func openApp(_ name:String) {_ = runProcess("/usr/bin/open",["-a",name],timeout:3)}
    func prepareTaskNavigation() {
        // Capture the registered window before AppKit can clamp it on activation.
        // This is optional and bounded: task navigation still works without HS.
        guard !NSRunningApplication.runningApplications(withBundleIdentifier:"org.hammerspoon.Hammerspoon").isEmpty,
              let cli=["/opt/homebrew/bin/hs","/usr/local/bin/hs"].first(where:{FileManager.default.isExecutableFile(atPath:$0)}) else{return}
        let command="if wm and type(wm.prepareTaskNavigation) == 'function' then return hs.json.encode(wm.prepareTaskNavigation()) else return '{}' end"
        let (code,data)=runProcess(cli,["-q","-t","0.6","-c",command],timeout:0.8)
        if code != 0 {log("window layout preparation unavailable (\(code)); continuing navigation");return}
        if let status=(try? JSONSerialization.jsonObject(with:data)) as? [String:Any],status["armed"] as? Bool == true {
            log("window layout armed for task navigation")
        }
    }
    func openTask(_ slot:Int,_ c:Foreground) {
        let tasks=taskSnapshot();guard slot>=1,slot<=tasks.count else {fail("该任务槽暂时没有任务");return}
        openTaskRecord(tasks[slot-1],c)
    }
    func openTaskRecord(_ task:[String:Any],_ c:Foreground) {
        guard context.valid(c),let id=task["id"] as? String,UUID(uuidString:id) != nil else {fail("任务身份无效或前台已改变");return}
        let revision=task["revision"] as? String ?? ""
        prepareTaskNavigation()
        guard context.valid(c) else{return}
        let (code,_)=runProcess("/usr/bin/open",["codex://threads/\(id)"],timeout:3)
        if code==0 {
            for _ in 0..<12 {
                if NSWorkspace.shared.frontmostApplication?.bundleIdentifier=="com.openai.codex" {onTaskOpened(id,revision);return}
                NSRunningApplication.runningApplications(withBundleIdentifier:"com.openai.codex").first?.activate();usleep(50000)
            }
            fail("任务已打开，但未确认 Codex 获得焦点")
        } else {fail("Codex 任务深链接打开失败")}
    }
    @discardableResult func tmux(_ action:String,_ c:Foreground,focus:Bool=false)->Bool {
        guard context.valid(c) else{return false}
        let result=helper(["tmux",action]+(focus ? ["--allow-recent"]:[]))
        if let error=result["error"] as? String {fail(error);return false}
        guard result["ok"] as? Bool == true else{fail("未确认 tmux 操作结果");return false}
        if focus,result["focusTerminal"] as? Bool == true {
            // Follow the actual tmux client's ancestors, not the last-used terminal app.
            var pid=Int32(result["clientPID"] as? String ?? "") ?? 0
            var app:NSRunningApplication?
            for _ in 0..<12 where pid>1 {
                if let candidate=NSRunningApplication(processIdentifier:pid),candidate.activationPolicy == .regular {app=candidate;break}
                let (code,data)=runProcess("/bin/ps",["-p",String(pid),"-o","ppid="],timeout:0.3)
                guard code==0,let parent=Int32(String(data:data,encoding:.utf8)?.trimmingCharacters(in:.whitespacesAndNewlines) ?? ""),parent>1,parent != pid else{break}
                pid=parent
            }
            guard context.valid(c) else{return true}
            guard let app=app else{fail("会话已切换；无法确定对应终端窗口，请手动切回终端");return true}
            app.activate()
            if (result["attachedClients"] as? Int ?? 1)>1 {
                notify("已切换最近活动的 tmux 客户端；多窗口时请确认终端窗口")
            }
        }
        return true
    }
    func dock(_ slot:Int,_ c:Foreground) {
        guard context.valid(c) else{return}
        let path=FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".config/karabiner/dock-slots.txt")
        if let data=try? String(contentsOf:path,encoding:.utf8),let line=data.split(separator:"\n").first(where:{$0.split(separator:" ").first==Substring(String(slot))}) {
            let name=line.split(separator:" ",maxSplits:1).dropFirst().joined(separator:" ")
            if !name.isEmpty {openApp(name);return}
        }
        // Finder is not included in persistent-apps; configured slots take precedence.
        if let apps=UserDefaults(suiteName:"com.apple.dock")?.array(forKey:"persistent-apps") as? [[String:Any]],slot<=apps.count,
           let tile=apps[slot-1]["tile-data"] as? [String:Any],let file=tile["file-data"] as? [String:Any],let url=file["_CFURLString"] as? String {
            _ = runProcess("/usr/bin/open",[url]);return
        }
        fail("尚未配置第 \(slot) 个应用启动位")
    }
    func perform(_ action:InputAction,_ c:Foreground) {
        let maxAge=action.kind == .encoder && action.gesture != 1 ? 0.25:0.5
        guard context.valid(c),ProcessInfo.processInfo.systemUptime-action.time<maxAge else{return}
        log("action L\(c.layer==0 ? 8:c.layer) \(action.kind.rawValue) \(action.number) gesture=\(action.gesture)")
        switch action.kind {
        case .dock:dock(action.number,c)
        case .hold:
            if let i=taskKeys.firstIndex(of:action.number) {openTask(i+1,c)}
            else if let i=terminalKeys.firstIndex(of:action.number) {tmux("session:\(i+1)",c,focus:true)}
        case .tap:button(action.number,c)
        case .encoder:encoder(action.number,action.gesture,c)
        }
    }
    func performNamed(_ id:String,_ c:Foreground) {
        guard context.valid(c) else{return}
        let parts=id.split(separator:".")
        if parts.count==2,parts[0]=="button",let n=Int(parts[1]),(1...12).contains(n) {button(n,c);return}
        if parts.count==3,parts[0]=="encoder",let n=Int(parts[1]),let gesture=Int(parts[2]),(1...4).contains(n),(0...2).contains(gesture) {encoder(n,gesture,c);return}
        switch id {
        case "none":return
        case "key.enter":key("return",[],c)
        case "key.escape":key("escape",[],c)
        case "key.up","key.down","key.left","key.right","key.tab","key.space","key.backspace","key.pageup","key.pagedown","key.home","key.end":key(String(parts[1]),[],c)
        case "edit.copy":key("c",cmd,c)
        case "edit.paste":key("v",cmd,c)
        case "edit.undo":key("z",cmd,c)
        case "edit.redo":key("z",[cmd,shift],c)
        case "navigation.previous","navigation.next":navigate(id.hasSuffix("next"),c)
        case "tmux.pane.up","tmux.pane.down","tmux.pane.left","tmux.pane.right":
            guard c.layer==2 else{fail("此动作仅用于终端层");return}
            tmux("pane-"+String(parts[2]),c)
        case "tmux.session.previous","tmux.session.next","session.previous","session.next":
            guard c.layer==2 else{fail("此动作仅用于终端层");return}
            tmux(id.hasSuffix("next") ? "next-session":"previous-session",c)
        case "codex.composer":
            guard ["com.openai.codex","com.openai.chat"].contains(c.bundle) else{fail("请先切回 Codex 或 ChatGPT");return}
            focusComposer(c)
        case "finder.open":
            guard c.bundle=="com.apple.finder" else{fail("请先切回 Finder");return}
            key("o",cmd,c)
        default:fail("未知动作：\(id)")
        }
    }
    func navigate(_ forward:Bool,_ c:Foreground) {
        switch c.layer {
        case 1:
            guard c.bundle=="com.openai.codex" else{fail("ChatGPT 桌面版尚未提供已验证的相邻任务切换");return}
            prepareTaskNavigation();key(forward ? "right":"left",[cmd,opt],c)
        case 2:tmux(forward ? "next-window":"previous-window",c)
        case 3:key(forward ? "]":"[",cmd,c)
        case 5:encoder(1,forward ? 2:0,c)
        case 7:key(forward ? "down":"up",opt,c)
        default:key("tab",forward ? ctrl:[ctrl,shift],c)
        }
    }
    func button(_ n:Int,_ c:Foreground) {
        guard context.valid(c),(1...12).contains(n) else{return}
        switch c.layer {
        case 1:
            if let i=taskKeys.firstIndex(of:n) {openTask(i+1,c);return}
            if c.bundle=="com.openai.chat" {
                switch n {
                case 9:if !click(["显示边栏","隐藏边栏","显示侧边栏","隐藏侧边栏","Show Sidebar","Hide Sidebar","Toggle Sidebar"],c) {fail("未找到 ChatGPT 侧栏按钮")}
                case 12:if !click(["新聊天","新建聊天","New chat","New Chat"],c) {fail("未找到 ChatGPT 新聊天按钮")}
                default:fail("此键为 Codex 专属操作；ChatGPT 桌面版不发送该快捷键")
                };return
            }
            switch n {
            case 4:if !click(["从这里创建聊天分支","分叉任务","分叉聊天","在新聊天中继续","Fork chat","Fork thread","Continue in new chat from here"],c,contains:true,last:true) {fail("当前回复未提供分叉按钮")}
            case 8:if quickOpen {key("escape",[],c);quickOpen=false} else {key("n",[cmd,opt],c);quickOpen=true}
            case 9:key("b",cmd,c)
            case 10:key("b",[cmd,opt],c)
            case 11:codexBrowser(c)
            case 12:prepareTaskNavigation();key("o",[cmd,opt],c)
            default:break
            }
        case 2:
            if n==11 {key("f",cmd,c)}
            else {let actions=["split-right","split-down","horizontal","vertical","zoom","layout","rotate","windows","stash","copy","","last-pane"];tmux(actions[n-1],c)}
        case 3:
            let names=["n","t","f","l","m","d","/","l","backspace","4","8","f"]
            let flags:[CGEventFlags]=[cmd,cmd,cmd,[cmd,shift],[cmd,shift],cmd,cmd,cmd,[],[cmd,opt],[cmd,opt],[ctrl,cmd]]
            key(names[n-1],flags[n-1],c)
        case 4:
            switch n {
            case 5:text("[[",c)
            case 8:obsidianCommand(["复制 Obsidian 链接","Copy Obsidian URL"],c)
            case 10:obsidianCommand(["打开今天的日记","Open today's daily note"],c)
            case 11:obsidianCommand(["插入模板","Insert template","显示大纲","Show outline"],c)
            default:
                let mapping:[Int:(String,CGEventFlags)]=[1:("n",cmd),2:("o",cmd),3:("f",[cmd,shift]),4:("p",cmd),6:("return",cmd),7:("f2",[]),9:("w",cmd),12:("e",cmd)]
                if let (k,f)=mapping[n] {key(k,f,c)}
            }
        case 5:
            let names=["tab","e","g","r","s","i","b","d","z","z","a","f3"]
            let flags:[CGEventFlags]=[[],[],[],[],[],[],ctrl,shift,cmd,[cmd,shift],shift,[]]
            key(names[n-1],flags[n-1],c)
        case 6:finder(n,c)
        case 7:
            switch n {
            case 1:key("up",opt,c)
            case 2:key("down",opt,c)
            case 3:if c.bundle=="ru.keepcoder.Telegram" {key("down",[ctrl,shift],c)} else {fail("此通讯客户端的未读快捷键尚未验证")}
            case 4:key("f",cmd,c)
            case 5:key("c",cmd,c)
            case 6:plainPaste(c)
            case 7,12:break
            case 8:if focusedMessageComposer(c) != nil {key("return",[],c)}
            case 9:clearComposer(c)
            case 10:key("space",[ctrl,cmd],c,global:true)
            case 11:if focusedMessageComposer(c) != nil {key("return",shift,c)}
            default:break
            }
        default:
            switch n {
            case 1:key("c",[ctrl,opt],c,global:true)
            case 2:key("v",[ctrl,opt],c,global:true)
            case 3:key("z",cmd,c)
            case 4:key("z",[cmd,shift],c)
            case 5:key("x",cmd,c)
            case 6:key("c",cmd,c)
            case 7:key("v",cmd,c)
            case 8:plainPaste(c)
            case 9:key("t",cmd,c)
            case 10:key("t",[cmd,shift],c)
            case 11:key("f",cmd,c)
            case 12:_ = runProcess("/usr/bin/open",["raycast://extensions/raycast/clipboard-history/clipboard-history"])
            default:break
            }
        }
    }
    func plainPaste(_ c:Foreground) {
        // Unicode insertion leaves clipboard history and the original rich clipboard intact.
        if let value=NSPasteboard.general.string(forType:.string) {text(value,c)}
    }
    func encoder(_ n:Int,_ gesture:Int,_ c:Foreground) {
        guard context.valid(c),(1...4).contains(n),(0...2).contains(gesture) else{return}
        let press=gesture==1,cw=gesture==2
        if n==4 && !press {wheel(cw,c);return}
        if c.layer==1 {
            if c.bundle=="com.openai.chat" && (n==1 || n==2) {fail("ChatGPT 桌面版不使用 Codex 的型号和任务快捷键");return}
            switch n {
            case 1:reasoning(c,delta:press ? 0:(cw ? 1:-1),preset:press ? "ultra":nil)
            case 2:if press {reasoning(c,delta:0,preset:"high")} else {prepareTaskNavigation();key(cw ? "right":"left",[cmd,opt],c)}
            case 3:if press {focusComposer(c)} else {key(cw ? "right":"left",opt,c)}
            case 4:if !click(["滚动到底部","跳转到最新消息","Scroll to bottom","Jump to latest"],c,contains:true) {key("end",cmd,c)}
            default:break
            };return
        }
        if c.layer==2 {
            if n==4 {if tmux("bottom",c) {key("end",cmd,c)};return}
            let names=n==1 ? ["resize-left","width","resize-right"]:n==2 ? ["resize-up","height","resize-down"]:["previous-window","last-window","next-window"]
            tmux(names[gesture],c);return
        }
        if c.layer==5 {
            let names=n==1 ? ["kp4","kp1","kp6"]:n==2 ? ["kp8","kp7","kp2"]:n==3 ? ["left","space","right"]:["","kpdecimal",""]
            key(names[gesture],[],c);return
        }
        if c.layer==6 {
            if n==1 {key(press ? "up":cw ? "]":"[",cmd,c)}
            if n==2 {key(press ? "tab":cw ? "right":"left",press ? ctrl:[],c)}
            if n==3 {key(press ? "t":"tab",press ? cmd:cw ? ctrl:[ctrl,shift],c)}
            if n==4 {key("space",[],c)}
            return
        }
        if c.layer==3 {
            if n==1 {key(press ? "u":cw ? "]":"[",press ? [cmd,shift]:cmd,c)}
            if n==2 {key(press ? "p":"tab",press ? cmd:cw ? ctrl:[ctrl,shift],c)}
            if n==3 {if press {notionWidth(c)} else {wheel(cw,c,horizontal:true)}}
            if n==4 {key("home",cmd,c)}
            return
        }
        if c.layer==7 {
            if n==1 {key(press ? "left":cw ? "right":"left",press ? cmd:opt,c)}
            if n==2 {key(press ? "a":cw ? "right":"left",press ? cmd:[opt,shift],c)}
            if n==3 {key(press ? "0":cw ? "=":"-",cmd,c)}
            if n==4 {key("end",cmd,c)}
            return
        }
        switch n {
        case 1:
            if press && c.layer==4 {obsidianCommand(["显示反向链接","Show backlinks"],c)}
            else {key(press ? "l":cw ? "]":"[",cmd,c)}
        case 2:if press {key(c.layer==4 ? "t":"w",c.layer==4 ? [cmd,shift]:cmd,c)} else {key("tab",cw ? ctrl:[ctrl,shift],c)}
        case 3:key(press ? "0":cw ? "=":"-",cmd,c)
        case 4:key(c.layer==4 ? "home":"r",cmd,c)
        default:break
        }
    }
    func focusComposer(_ c:Foreground) {
        guard context.valid(c) else{return}
        let nodes=axNodes(c.pid).filter{["AXTextArea"].contains(axString($0,"AXRole"))}
        guard let e=nodes.last else{fail("找不到当前聊天输入框");return}
        AXUIElementSetAttributeValue(e,kAXFocusedAttribute as CFString,kCFBooleanTrue)
    }
    func focusedMessageComposer(_ c:Foreground)->AXUIElement? {
        guard context.valid(c) else{return nil}
        let root=AXUIElementCreateApplication(c.pid)
        guard let value=axValue(root,"AXFocusedUIElement"),CFGetTypeID(value)==AXUIElementGetTypeID() else{fail("请先点击消息输入框");return nil}
        let field=unsafeBitCast(value,to:AXUIElement.self)
        // Search fields and conversation lists must never receive Select All + Delete.
        var textSettable:DarwinBoolean=false,valueSettable:DarwinBoolean=false
        AXUIElementIsAttributeSettable(field,"AXSelectedText" as CFString,&textSettable)
        AXUIElementIsAttributeSettable(field,"AXValue" as CFString,&valueSettable)
        guard axString(field,"AXRole")=="AXTextArea",(axValue(field,"AXEnabled") as? NSNumber)?.boolValue != false,
              textSettable.boolValue || valueSettable.boolValue || (axValue(field,"AXEditable") as? NSNumber)?.boolValue == true else{fail("请先聚焦可编辑的消息输入框");return nil}
        return field
    }
    func clearComposer(_ c:Foreground) {
        guard let field=focusedMessageComposer(c) else{return}
        let root=AXUIElementCreateApplication(c.pid)
        key("a",cmd,c)
        guard context.valid(c),let stillFocused=axValue(root,"AXFocusedUIElement"),CFGetTypeID(stillFocused)==AXUIElementGetTypeID(),CFEqual(stillFocused,field) else{return}
        key("backspace",[],c)
    }
    func codexBrowser(_ c:Foreground) {
        // New browser tab then verify the actual URL field before inserting anything.
        key("t",cmd,c);usleep(160000)
        guard context.valid(c) else{return}
        key("l",cmd,c);usleep(100000)
        let root=AXUIElementCreateApplication(c.pid)
        guard let value=axValue(root,"AXFocusedUIElement"),CFGetTypeID(value)==AXUIElementGetTypeID() else{fail("没有找到 Codex 浏览器地址栏");return}
        let field=unsafeBitCast(value,to:AXUIElement.self)
        let label=axLabel(field)+axString(field,"AXPlaceholderValue")
        guard ["AXTextField","AXComboBox"].contains(axString(field,"AXRole")),["地址","URL","url","Search or enter","搜索或输入"].contains(where:label.contains) else{fail("未确认浏览器地址栏，已取消输入");return}
        text("https://chatgpt.com/",c);key("return",[],c)
    }
    func obsidianCommand(_ names:[String],_ c:Foreground) {
        key("p",cmd,c);usleep(130000)
        guard context.valid(c) else{return}
        let root=AXUIElementCreateApplication(c.pid)
        guard let v=axValue(root,"AXFocusedUIElement"),CFGetTypeID(v)==AXUIElementGetTypeID() else{return}
        let field=unsafeBitCast(v,to:AXUIElement.self)
        guard axString(field,"AXRole")=="AXTextField" else{fail("Obsidian 命令面板未打开");return}
        // Search through the palette only; never send a blind Enter to an editor.
        text(names[0],c);usleep(140000)
        if !click(names,c,contains:true) {fail("命令未找到：\(names[0])；已保留命令面板供选择")}
    }
    func notionWidth(_ c:Foreground) {
        if !click(["更多","More","页面操作","Page actions"],c,contains:true) {fail("未找到 Notion 页面菜单");return}
        usleep(150000)
        if !click(["全宽","Full width"],c,contains:true) {fail("未找到 Notion 全宽选项")}
    }
    func finder(_ n:Int,_ c:Foreground) {
        switch n {
        case 1:key("c",cmd,c)
        case 2:key("v",[cmd,opt],c)
        case 3:key("n",[cmd,shift],c)
        case 4:if !click(["重新命名…","重命名…","Rename…","Rename"],c) {key("return",[],c)}
        case 5,7:key("c",[cmd,opt],c) // POSIX paths replace Windows/WSL conversion.
        case 6:
            let urls=finderSelection(c)
            guard !urls.isEmpty,context.valid(c) else{return}
            NSPasteboard.general.clearContents();NSPasteboard.general.setString(urls.map{$0.lastPathComponent}.joined(separator:"\n"),forType:.string)
        case 8:key("f",cmd,c)
        case 9:key("backspace",cmd,c) // Move to Trash, never permanent delete.
        case 10:compareFiles(c)
        case 11:syncFolders(c)
        case 12:
            if !click(["压缩","Compress"],c,contains:true) {fail("请先在 Finder 选中文件，再从菜单压缩")}
        default:break
        }
    }
    func finderSelection(_ c:Foreground)->[URL] {
        guard context.valid(c) else{return []}
        let (code,data)=runProcess("/usr/bin/osascript",["-l","JavaScript","-e","JSON.stringify(Application('Finder').selection().map(x => x.url()))"],timeout:30)
        guard code==0,context.valid(c),let values=(try? JSONSerialization.jsonObject(with:data)) as? [String] else{fail("无法读取 Finder 选择；首次使用请允许控制 Finder");return []}
        return values.compactMap{URL(string:$0)}.filter{$0.isFileURL}
    }
    func compareFiles(_ c:Foreground) {
        let urls=finderSelection(c);guard urls.count==2 else{fail("请在 Finder 中选择两个文件进行比较");return}
        let executable="/Applications/Cursor.app/Contents/Resources/app/bin/cursor"
        if FileManager.default.isExecutableFile(atPath:executable) {
            _=runProcess(executable,["--diff",urls[0].path,urls[1].path],timeout:5)
        } else {fail("比较文件需要 Cursor；当前未找到它的命令行入口")}
    }
    func syncFolders(_ c:Foreground) {
        let urls=finderSelection(c).map{$0.resolvingSymlinksInPath().standardizedFileURL}
        guard urls.count==2,urls.allSatisfy({(try? $0.resourceValues(forKeys:[.isDirectoryKey]))?.isDirectory==true}) else{fail("请在 Finder 中选择两个目录");return}
        let a=urls[0].path,b=urls[1].path
        guard a != b,!a.hasPrefix(b+"/"),!b.hasPrefix(a+"/") else{fail("不能同步相同目录或互相包含的目录");return}
        let (code,data)=runProcess("/usr/bin/rsync",["-ani","--",a+"/",b+"/"],timeout:15)
        guard code==0,context.valid(c) else{fail("无法生成同步预览");return}
        let preview=String(data:data,encoding:.utf8) ?? ""
        let approved:Bool=DispatchQueue.main.sync {
            let alert=NSAlert();alert.messageText="复制更新目录"
            alert.informativeText="来源：\(a)\n目标：\(b)\n目标中已有的其他文件会保留；被替换文件会备份。"
            let scroll=NSScrollView(frame:NSRect(x:0,y:0,width:580,height:240));scroll.hasVerticalScroller=true
            let text=NSTextView(frame:scroll.bounds);text.isEditable=false;text.font=NSFont.monospacedSystemFont(ofSize:11,weight:.regular)
            text.string=preview.isEmpty ? "没有需要更新的文件。":String(preview.prefix(20000));scroll.documentView=text;alert.accessoryView=scroll
            alert.addButton(withTitle:"取消");alert.addButton(withTitle:"复制更新")
            NSApp.activate(ignoringOtherApps:true)
            return alert.runModal() == .alertSecondButtonReturn
        }
        guard approved else{return}
        let backup=support.appendingPathComponent("folder-sync-backups/"+String(Int(Date().timeIntervalSince1970)))
        try? FileManager.default.createDirectory(at:backup,withIntermediateDirectories:true)
        let (result,_)=runProcess("/usr/bin/rsync",["-a","--backup","--backup-dir="+backup.path,"--",a+"/",b+"/"],timeout:55)
        if result==0 {notify("目录复制更新完成");log("Finder folder copy completed; replaced files backed up")}
        else {fail("同步未完成，已复制的文件保留；请查看目录后重试")}
    }
    func powerSetting(_ nodes:[AXUIElement])->ModelSetting? {
        for e in nodes where axString(e,"AXRole")=="AXStaticText" {
            if let result=ModelSetting.parse(axString(e,"AXValue")) {return result}
        }
        return nil
    }
    func openPowerMenu(_ c:Foreground)->[AXUIElement] {
        let until=ProcessInfo.processInfo.systemUptime+1.2
        var opened=false,confirmed=false,reason="deadline",samples:[Int]=[]
        defer {if !confirmed {log("model menu unavailable: \(reason), opened=\(opened), samples=\(samples)")}}
        repeat {
            guard context.current(c) else{reason="context changed before read";return []}
            let nodes=axModelNodes(c.pid,until:until)
            samples.append(nodes.count)
            let menu=axPowerMenu(in:nodes,until:until)
            guard context.current(c) else{reason="context changed after read";return []}
            guard ProcessInfo.processInfo.systemUptime<until else{return []}
            if !menu.isEmpty,context.valid(c) {confirmed=true;return menu}
            if menu.isEmpty,!opened,nodes.contains(where:{ProcessInfo.processInfo.systemUptime<until && axString($0,"AXRole")=="AXPopUpButton" && (ModelSetting.parse(axLabel($0)) != nil || ["选择强度","Select effort"].contains(axLabel($0)))}) {
                if ProcessInfo.processInfo.systemUptime<until,context.valid(c) {
                    // The native shell can acknowledge AXPress without opening
                    // this popup. Use its registered shortcut only after the
                    // exact composer selector and active window are confirmed.
                    key("m",[ctrl,shift],c)
                    opened=true
                }
            }
            // Opening is a toggle: send it once, then only read. A missing
            // page must not send a blind shortcut into another input surface.
            usleep(70000)
        } while ProcessInfo.processInfo.systemUptime<until
        return []
    }
    func clickModelControl(_ labels:[String],_ c:Foreground)->Bool {
        guard context.valid(c) else{return false}
        let nodes=axModelNodes(c.pid)
        guard let control=nodes.first(where:{["AXButton","AXMenuItem","AXCheckBox","AXRadioButton"].contains(axString($0,"AXRole")) && labels.contains(axLabel($0))}),context.valid(c) else{return false}
        return AXUIElementPerformAction(control,kAXPressAction as CFString) == .success
    }
    func stepPower(_ direction:Int,_ c:Foreground)->Bool {
        guard context.valid(c),let control=axPowerMenu(c.pid).first(where:{axString($0,"AXRole")=="AXMenuItem" && ["强度","Power"].contains(axLabel($0))}) else{return false}
        guard context.valid(c) else{return false}
        _=AXUIElementSetAttributeValue(control,kAXFocusedAttribute as CFString,kCFBooleanTrue)
        let root=AXUIElementCreateApplication(c.pid)
        func isFocused()->Bool {
            if (axValue(control,"AXFocused") as? NSNumber)?.boolValue==true {return true}
            guard let focused=axValue(root,"AXFocusedUIElement"),CFGetTypeID(focused)==AXUIElementGetTypeID() else{return false}
            return CFEqual(focused,control)
        }
        if !isFocused() {
            guard context.valid(c) else{return false}
            AXUIElementPerformAction(control,kAXPressAction as CFString);usleep(50000)
        }
        guard context.valid(c) else{return false}
        guard isFocused() else{
            if let focused=axValue(root,"AXFocusedUIElement"),CFGetTypeID(focused)==AXUIElementGetTypeID() {let e=unsafeBitCast(focused,to:AXUIElement.self);log("Power focus: "+axString(e,"AXRole")+" "+axLabel(e))}
            return false
        }
        key(direction>0 ? "right":"left",[],c);usleep(130000)
        return true
    }
    func checkUltraPermissionPrompt(_ c:Foreground)->Bool {
        // Choosing reasoning effort never grants a new execution permission.
        let deadline=Date(timeIntervalSinceNow:1.2)
        repeat {
            guard context.valid(c),c.bundle=="com.openai.codex" else{return false}
            let dialog=axUltraFullAccessDialog(c.pid)
            if !dialog.isEmpty {
                fail("Ultra 需要完整访问权限，请在 Codex 中自行确认；Bridge 未代为授权")
                return false
            }
            usleep(50000)
        } while Date()<deadline
        return context.valid(c)
    }
    func reasoning(_ c:Foreground,delta:Int,preset:String?) {
        guard c.bundle=="com.openai.codex",context.valid(c) else{fail("此强度控制仅支持 Codex");return}
        // This Codex version exposes Power as a keyboard-accessible menu item,
        // with the exact current model/effort in its live status announcement.
        var menu=openPowerMenu(c)
        guard context.valid(c) else{return}
        guard !menu.isEmpty else{fail("Codex 模型菜单暂不可读，请停留在聊天页稍后重试");return}
        func unconfirmed(_ message:String) {
            if axModelNodes(c.pid).contains(where:{["使用完整访问权限","Use full access"].contains(axLabel($0))}) {
                fail("请在 Codex 中自行确认权限；Bridge 未代为授权")
            } else {fail(message)}
        }
        let initial=powerSetting(menu)
        if let initial=initial,["GPT-6 Sol","GPT-6.1 Sol"].contains(initial.model) {rememberedSolModel=initial.model}
        guard let target=reasoningTarget(initial,delta:delta,preset:preset,solModel:rememberedSolModel) else{fail("当前型号或档位无法精确识别，请先选择支持的 Sol 或 Astra");return}
        if initial?.model != target.model {
            guard clickModelControl(["选择模型","Select model"],c) else{fail("未找到型号列表");return}
            usleep(130000)
            guard clickModelControl([target.model,String(target.model.dropFirst(4))],c) else{fail("未找到精确型号 \(target.model)");return}
            usleep(130000);menu=openPowerMenu(c)
        }
        for _ in 0..<6 {
            guard let current=powerSetting(menu),current.model==target.model else{unconfirmed("无法确认选中的型号");return}
            if current==target {break}
            guard stepPower(target.effort>current.effort ? 1:-1,c) else{fail("无法聚焦 Codex 强度控件");return}
            if preset=="ultra" && current.effort==4 {
                guard checkUltraPermissionPrompt(c) else{return}
                menu=openPowerMenu(c)
            } else {menu=axPowerMenu(c.pid)}
            guard powerSetting(menu) != current else{unconfirmed("强度未改变");return}
        }
        guard powerSetting(menu)==target else{unconfirmed("目标型号或档位未确认");return}
        if let preset=preset {
            let desired=preset=="ultra"
            let enableFast=["启用快速模式","Enable fast mode"],enableStandard=["启用标准模式","Enable standard mode"]
            func fastState(_ nodes:[AXUIElement])->Bool? {
                if nodes.contains(where:{axString($0,"AXRole")=="AXMenuItem" && enableStandard.contains(axLabel($0))}) {return true}
                if nodes.contains(where:{axString($0,"AXRole")=="AXMenuItem" && enableFast.contains(axLabel($0))}) {return false}
                return nil
            }
            guard let fast=fastState(menu) else{fail("无法识别 Codex 速度设置");return}
            if fast != desired {
                guard clickModelControl(desired ? enableFast:enableStandard,c) else{fail("无法设置 Codex 速度");return}
                usleep(130000);menu=openPowerMenu(c)
            }
            guard fastState(menu)==desired && powerSetting(menu)==target else{fail("型号、强度或速度结果未确认");return}
        }
        key("escape",[],c)
        log("model controls verified \(target.model) effort=\(target.effort) preset=\(preset ?? "rotation")")
    }
}
