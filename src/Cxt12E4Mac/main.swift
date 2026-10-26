import Foundation
import AppKit
import IOKit.hid

if CommandLine.arguments.contains("--self-test") {selfTest();actionsSelfTest();profileSelfTest();controllerInputSelfTest();controllerEngineSelfTest();controllerCoordinatorSelfTest();exit(0)}
if CommandLine.arguments.contains("--dump-default-profiles") {
    let values=(0...7).map { layer -> [String:Any] in
        let mappings=LayerProfiles.devices.map { device -> [String:Any] in
            ["device":device,"controls":LayerProfiles.controls(device).map { control -> [String:String] in
                let id=LayerProfiles.defaults(device,layer)[control.id] ?? "none"
                return ["control":control.id,"label":control.title,"action":id,"title":LayerProfiles.title(id,device,control.id,layer)]
            }]
        }
        return ["layer":layer==0 ? 8:layer,"name":layerNames[layer],"devices":mappings]
    }
    if let data=try? JSONSerialization.data(withJSONObject:values,options:[.prettyPrinted,.sortedKeys]) {FileHandle.standardOutput.write(data)}
    exit(0)
}
if CommandLine.arguments.contains("--status") {
    if let data=try? Data(contentsOf:support.appendingPathComponent("runtime.json")) {FileHandle.standardOutput.write(data)}
    exit(0)
}
if CommandLine.arguments.contains("--dump-ax") || CommandLine.arguments.contains("--inspect-model") || CommandLine.arguments.contains("--inspect-model-list") {
    try? Data((CommandLine.arguments.contains("--inspect-model-list") ? "model-list":CommandLine.arguments.contains("--inspect-model") ? "model":"dump").utf8).write(to:support.appendingPathComponent("diagnostic-request"),options:.atomic);exit(0)
}
try? FileManager.default.createDirectory(at:support,withIntermediateDirectories:true)
let instanceLock=Darwin.open(support.appendingPathComponent("instance.lock").path,O_CREAT | O_RDWR,0o600)
if instanceLock<0 || flock(instanceLock,LOCK_EX | LOCK_NB) != 0 {exit(0)}
let app=NSApplication.shared
app.setActivationPolicy(.accessory)
final class BridgeDelegate:NSObject,NSApplicationDelegate {
    var item:NSStatusItem!;let statusLine=NSMenuItem(title:"正在连接键盘…",action:nil,keyEquivalent:"")
    let noticeLine=NSMenuItem(title:"",action:nil,keyEquivalent:"")
    let input=KarabinerInput();var inputKeys:Set<Int>=[];var inputMarker=false
    let hid=HidBridge();let decoder=Decoder();let contexts=ContextStore()
    lazy var actions=Actions(context:contexts)
    let profiles=ProfileStore()
    lazy var controller=ControllerCoordinator(profiles:profiles)
    lazy var settings=ProfileSettings(store:profiles)
    let chooser=ControllerChooser()
    let controllerStatusLine=NSMenuItem(title:"Xbox：未连接",action:nil,keyEquivalent:"")
    var panelReturnApp:NSRunningApplication?
    let work=DispatchQueue(label:"cxt.actions",qos:.userInitiated)
    let poll=DispatchQueue(label:"cxt.status",qos:.utility)
    var tasks:[[String:Any]]=[];var terminals:[[String:Any]?]=Array(repeating:nil,count:6)
    var dataLock=NSLock();var polling=false;var manual:Int?;var generation=0
    var lastTerminal="com.mitchellh.ghostty";var tick=0;var lastInput=ProcessInfo.processInfo.systemUptime
    var holdTimer:Timer?;var updateTimer:Timer?;var observer:NSObjectProtocol?;var axWindow:AXUIElement?
    var sleeping=false;var lastFront:pid_t=0;var noticeUntil=Date.distantPast
    var terminationSignals:[DispatchSourceSignal]=[];var terminationRequested=false
    func applicationDidFinishLaunching(_ notification:Notification) {
        try? FileManager.default.createDirectory(at:support,withIntermediateDirectories:true)
        log("Bridge starting")
        // launchd uses SIGTERM for updates/logout. Route it through AppKit so
        // controller.stop() can balance synthetic mouse-down events first.
        for number in [SIGTERM,SIGINT] {
            Darwin.signal(number,SIG_IGN)
            let source=DispatchSource.makeSignalSource(signal:number,queue:.main)
            source.setEventHandler { [weak self] in
                guard let self=self,!self.terminationRequested else{return}
                self.terminationRequested=true;NSApp.terminate(nil)
            }
            terminationSignals.append(source);source.resume()
        }
        item=NSStatusBar.system.statusItem(withLength:NSStatusItem.squareLength)
        item.button?.imagePosition = .imageOnly
        item.button?.imageScaling = .scaleNone
        item.button?.image=LayerBadge.images[0]
        rebuildMenu()
        actions.notify={ [weak self] message in DispatchQueue.main.async {self?.noticeLine.title=message;self?.noticeUntil=Date(timeIntervalSinceNow:8)} }
        actions.taskSnapshot={ [weak self] in guard let self=self else{return []};self.dataLock.lock();defer{self.dataLock.unlock()};return self.tasks }
        actions.terminalBundle={ [weak self] in guard let self=self else{return "com.mitchellh.ghostty"};self.dataLock.lock();defer{self.dataLock.unlock()};return self.lastTerminal }
        actions.onTaskOpened={ [weak self] id,revision in self?.poll.async { _=helper(["ack-task",id,revision]) } }
        decoder.emit={ [weak self] action in
            guard let self=self else{return}
            let c=self.contexts.get();guard c.generation==action.context else{return}
            if action.kind == .tap || action.kind == .encoder {
                let control=action.kind == .tap ? "button.\(action.number)":"encoder.\(action.number).\(action.gesture)"
                self.dispatchMapped(self.profiles.action("keyboard",control,c.layer),c,time:action.time,maxAge:action.kind == .encoder && action.gesture != 1 ? 0.25:0.5,device:"keyboard")
            } else {self.work.async {self.actions.perform(action,c)}}
        }
        hid.onReport={ [weak self] bytes in
            guard let self=self else{return};self.refreshContext()
            self.lastInput=ProcessInfo.processInfo.systemUptime
            if bytes.dropFirst(2).contains(1) {self.decoder.cancel();return} // USB rollover error
            let keys=Set(bytes.dropFirst(2).filter{$0>=104 && $0<=115}.map{Int($0)-103})
            self.decoder.report(keys:keys,control:bytes[0]&0x10 != 0,now:self.lastInput,context:self.generation)
        }
        hid.onDisconnect={ [weak self] in self?.decoder.reset();self?.inputKeys.removeAll();self?.inputMarker=false }
        observer=NSWorkspace.shared.notificationCenter.addObserver(forName:NSWorkspace.didActivateApplicationNotification,object:nil,queue:.main) { [weak self] notification in
            guard let self=self else{return}
            if let active=notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
               active.processIdentifier != ProcessInfo.processInfo.processIdentifier {
                // A deliberate app switch dismisses the selector without
                // returning focus to the app that originally opened it.
                self.chooser.close(restoreFocus:false)
            }
            self.refreshContext(force:true)
        }
        NSWorkspace.shared.notificationCenter.addObserver(forName:NSWorkspace.willSleepNotification,object:nil,queue:.main) { [weak self] _ in self?.sleeping=true;self?.decoder.reset();self?.inputKeys.removeAll();self?.inputMarker=false }
        NSWorkspace.shared.notificationCenter.addObserver(forName:NSWorkspace.didWakeNotification,object:nil,queue:.main) { [weak self] _ in self?.sleeping=false;self?.lastInput=ProcessInfo.processInfo.systemUptime;self?.hid.retry() }
        input.callback={ [weak self] key,down in
            guard let self=self else{return};self.refreshContext()
            self.lastInput=ProcessInfo.processInfo.systemUptime
            if key==0 {self.inputMarker=down} else if down {self.inputKeys.insert(key)} else {self.inputKeys.remove(key)}
            self.decoder.report(keys:self.inputKeys,control:self.inputMarker,now:self.lastInput,context:self.generation)
        }
        profiles.onChange={ [weak self] in guard let self=self else{return};self.generation+=1;self.decoder.cancel();self.refreshContext();self.controller.configure() }
        controller.context={ [weak self] in self?.contexts.get() ?? Foreground() }
        controller.beforeInput={ [weak self] in self?.refreshContext() }
        controller.onAction={ [weak self] id,c,time in self?.dispatchMapped(id,c,time:time,device:"xbox") }
        controller.onHelp={ [weak self] in self?.openSettings(nil) }
        controller.onChooser={ [weak self] in self?.openChooser(nil) }
        controller.onNotice={ [weak self] text in self?.noticeLine.title=text;self?.noticeUntil=Date(timeIntervalSinceNow:8) }
        controller.panelInput={ [weak self] button,down in
            guard let self=self else{return false}
            return self.chooser.handle(button,down:down) || self.settings.handleController(button,down:down)
        }
        chooser.onClose={ [weak self] restoreFocus in
            self?.controller.setPanelVisible(false)
            if restoreFocus {self?.restorePanelApp()}
        }
        chooser.onSelect={ [weak self] kind,record in
            guard let self=self else{return};self.chooser.close()
            self.afterPanelFocus {
                let c=self.contexts.get()
                self.work.async {
                    if kind=="task" {self.actions.openTaskRecord(record,c)}
                    else if let id=record["id"] as? String {_=self.actions.tmux("session-id:"+id,c,focus:true)}
                }
            }
        }
        settings.onClose={ [weak self] in self?.restorePanelApp() }
        permissions(nil);input.start();hid.start();refreshContext();controller.start()
        holdTimer=Timer(timeInterval:0.025,repeats:true){[weak self] _ in guard let self=self else{return};let now=ProcessInfo.processInfo.systemUptime;if self.decoder.presses.values.contains(where:{!$0.fired && !$0.cancelled && now-$0.time>=Decoder.holdDelay}) {self.refreshContext()};self.decoder.tick(now:now,context:self.generation)}
        updateTimer=Timer(timeInterval:0.25,repeats:true){[weak self] _ in self?.update()}
        RunLoop.main.add(holdTimer!,forMode:.common);RunLoop.main.add(updateTimer!,forMode:.common)
        poll.async { _=helper(["reset"]) }
        update()
    }
    func refreshContext(force:Bool=false) {
        guard let a=NSWorkspace.shared.frontmostApplication else{return}
        if a.bundleIdentifier=="com.zhangyu.cxt12e4.bridge" {return}
        var window:AXUIElement?
        if AXIsProcessTrusted() {
            let e=AXUIElementCreateApplication(a.processIdentifier);AXUIElementSetMessagingTimeout(e,0.05)
            var v:CFTypeRef?
            let result=AXUIElementCopyAttributeValue(e,"AXFocusedWindow" as CFString,&v)
            if result == .success,let v=v,CFGetTypeID(v)==AXUIElementGetTypeID() {window=unsafeBitCast(v,to:AXUIElement.self)}
            else if result == .cannotComplete,a.processIdentifier==lastFront {
                // A busy renderer timing out is not a window switch. Keep the
                // last identity; each action still validates it before input.
                window=axWindow
            }
        }
        let windowChanged:Bool
        if let old=axWindow,let new=window {windowChanged = !CFEqual(old,new)} else {windowChanged = (axWindow != nil) != (window != nil)}
        let changed=a.processIdentifier != lastFront || windowChanged || force
        if changed {generation+=1;decoder.cancel();if a.processIdentifier != lastFront {manual=nil};axWindow=window;lastFront=a.processIdentifier;work.async {self.actions.quickOpen=false}}
        let bundle=a.bundleIdentifier ?? "",automatic=autoLayer(bundle)
        if automatic==2 {dataLock.lock();lastTerminal=bundle;dataLock.unlock()}
        contexts.set(Foreground(pid:a.processIdentifier,bundle:bundle,window:"",layer:manual ?? automatic,generation:generation),window:window)
        if changed {controller.contextChanged()}
    }
    func update() {
        tick+=1;refreshContext()
        let c=contexts.get(),now=ProcessInfo.processInfo.systemUptime
        if tick%8==0 {hid.retry()}
        if tick%4==0 && !polling {
            polling=true
            poll.async { [weak self] in
                let result=helper(["status"])
                DispatchQueue.main.async {
                    guard let self=self else{return};self.polling=false
                    if let ts=result["tasks"] as? [[String:Any]] {
                        self.dataLock.lock();self.tasks=ts;self.dataLock.unlock()
                        self.terminals=(result["terminals"] as? [Any] ?? []).map{$0 as? [String:Any]}
                    }
                }
            }
        }
        let systemIdle=CGEventSource.secondsSinceLastEventType(.combinedSessionState,eventType:CGEventType(rawValue:UInt32.max)!)
        if tick%2==0 && !sleeping && min(systemIdle,now-lastInput)<2400 {
            hid.send(layer:c.layer,tasks:tasks.map{UInt8($0["state"] as? Int ?? 1)},terminals:terminals.map{UInt8($0?["state"] as? Int ?? 0)})
        }
        let ax=AXIsProcessTrusted(),input=IOHIDCheckAccess(kIOHIDRequestTypeListenEvent)==kIOHIDAccessTypeGranted
        let connected=hid.verified && now-hid.lastReply<3
        statusLine.title = !ax || !input ? "需要权限：\(!ax ? "辅助功能 " : "")\(!input ? "输入监控" : "")" : !self.input.ready ? "等待 Karabiner 输入连接…" : !connected ? "等待灯光协议回应…":"L\(c.layer==0 ? 8:c.layer) \(layerNames[c.layer]) · \(self.input.count>0 ? "已连接":"等待第一次按键")"
        controllerStatusLine.title=controller.status
        let badgeLabel="CXT 12E4 L\(LayerBadge.number(c.layer)) \(layerNames[c.layer])"
        item.button?.title=""
        item.button?.image=LayerBadge.images[c.layer]
        item.button?.setAccessibilityLabel(badgeLabel)
        item.button?.toolTip="\(badgeLabel) [\(manual == nil ? "自动":"手动")]\n\(statusLine.title)\n\(controller.status)"
        if let menu=item.menu {
            menu.items.first(where:{$0.action == #selector(automatic(_:))})?.state=manual == nil ? .on:.off
            for entry in menu.items where entry.action == #selector(selectLayer(_:)) {entry.state=entry.tag==c.layer ? .on:.off}
        }
        if Date()>noticeUntil {noticeLine.title="任务 \(tasks.count) / tmux \(terminals.compactMap{$0}.count)"}
        if tick%4==0 {
            var state:[String:Any] = ["pid":ProcessInfo.processInfo.processIdentifier,"accessibility":ax,"inputMonitoring":input,"inputReceiverReady":self.input.ready,"inputTransport":"Karabiner send_user_command","firmwareVerified":connected,"layer":c.layer==0 ? 8:c.layer,"foreground":c.bundle,"inputReports":self.input.count,"taskCount":tasks.count,"taskStates":tasks.map{$0["state"] as? Int ?? 0},"terminalCount":terminals.compactMap{$0}.count,"terminalStates":terminals.map{$0?["state"] as? Int ?? 0},"terminalNames":terminals.map{$0?["name"] as? String ?? ""},"updatedAt":ISO8601DateFormatter().string(from:Date())]
            state["controllerConnected"]=controller.connected;state["controllerEnabled"]=controller.enabled;state["controllerEffective"]=controller.effective;state["controllerName"]=controller.controllerName;state["controllerReports"]=controller.reports;state["profileError"]=profiles.loadError ?? ""
            if let d=try? JSONSerialization.data(withJSONObject:state,options:[.prettyPrinted,.sortedKeys]) {try? d.write(to:support.appendingPathComponent("runtime.json"),options:.atomic)}
            let request=support.appendingPathComponent("diagnostic-request")
            if FileManager.default.fileExists(atPath:request.path) {let command=try? String(contentsOf:request,encoding:.utf8);try? FileManager.default.removeItem(at:request);dumpAX(openModel:command=="model" || command=="model-list",modelList:command=="model-list")}
        }
    }
    func rebuildMenu() {
        let menu=NSMenu();menu.addItem(statusLine);menu.addItem(controllerStatusLine);menu.addItem(noticeLine);menu.addItem(.separator())
        let auto=NSMenuItem(title:"自动切层",action:#selector(automatic(_:)),keyEquivalent:"");auto.target=self;menu.addItem(auto)
        for n in 1...8 {let i=n==8 ? 0:n;let m=NSMenuItem(title:"L\(n) \(layerNames[i])",action:#selector(selectLayer(_:)),keyEquivalent:"");m.tag=i;m.target=self;menu.addItem(m)}
        menu.addItem(.separator())
        for (title,selector) in [("分层按键设置…",#selector(openSettings(_:))),("任务 / 会话选择…",#selector(openChooser(_:))),("暂停 / 恢复 Xbox",#selector(toggleController(_:))),("在当前应用暂停 / 恢复 Xbox",#selector(toggleApplicationPause(_:))),("检查权限",#selector(permissions(_:))),("打开日志",#selector(openLog(_:))),("保存界面诊断",#selector(diagnostics(_:))),("使用说明",#selector(openGuide(_:))),("快捷键总表",#selector(openShortcutGuide(_:))),("离线说明",#selector(openOfflineGuide(_:)))] {let m=NSMenuItem(title:title,action:selector,keyEquivalent:"");m.target=self;menu.addItem(m)}
        let quit=NSMenuItem(title:"退出 Bridge",action:#selector(quitBridge(_:)),keyEquivalent:"q");quit.target=self;menu.addItem(quit);item.menu=menu
    }
    @objc func automatic(_ sender:Any?) {manual=nil;generation+=1;decoder.cancel();refreshContext();controller.contextChanged()}
    @objc func selectLayer(_ sender:NSMenuItem) {manual=sender.tag;generation+=1;decoder.cancel();var c=contexts.get();c.layer=sender.tag;c.generation=generation;contexts.set(c,window:axWindow);controller.contextChanged()}
    func dispatchMapped(_ id:String,_ c:Foreground,time:Double,maxAge:Double=0.5,device:String="keyboard") {
        guard contexts.valid(c),ProcessInfo.processInfo.systemUptime-time<maxAge else{return}
        if id=="bridge.help" {showSettings(device:device);return}
        if id=="bridge.chooser" {openChooser(nil);return}
        guard id != "none" else{return}
        work.async {
            guard self.contexts.valid(c),ProcessInfo.processInfo.systemUptime-time<maxAge else{return}
            self.actions.performNamed(id,c)
        }
    }
    func rememberPanelApp() {
        if let front=NSWorkspace.shared.frontmostApplication,front.processIdentifier != ProcessInfo.processInfo.processIdentifier {panelReturnApp=front}
    }
    func restorePanelApp() {
        if NSWorkspace.shared.frontmostApplication?.processIdentifier==ProcessInfo.processInfo.processIdentifier {panelReturnApp?.activate()}
    }
    func afterPanelFocus(_ completion:@escaping ()->Void) {
        guard let target=panelReturnApp else{return}
        let deadline=ProcessInfo.processInfo.systemUptime+1.2
        func attempt() {
            if NSWorkspace.shared.frontmostApplication?.processIdentifier==target.processIdentifier {self.refreshContext(force:true);completion();return}
            guard ProcessInfo.processInfo.systemUptime<deadline else{self.actions.notify("未能恢复原应用焦点，请重试选择");return}
            DispatchQueue.main.asyncAfter(deadline:.now()+0.05,execute:attempt)
        }
        attempt()
    }
    func showSettings(device:String) {
        chooser.close(restoreFocus:false)
        rememberPanelApp();decoder.cancel();controller.cancelAll();settings.show(device:device,layer:contexts.get().layer)
    }
    @objc func openSettings(_ sender:Any?) {showSettings(device:"xbox")}
    @objc func openChooser(_ sender:Any?) {
        if chooser.isVisible {chooser.close();return}
        if settings.isVisible {settings.activate();return}
        rememberPanelApp();decoder.cancel();controller.setPanelVisible(true)
        chooser.show(tasks:tasks,terminals:terminals,layer:contexts.get().layer)
    }
    @objc func toggleController(_ sender:Any?) {controller.toggle()}
    @objc func toggleApplicationPause(_ sender:Any?) {
        let bundle=contexts.get().bundle;guard !bundle.isEmpty else{return}
        var d=profiles.document
        if d.pausedApplications.contains(bundle) {d.pausedApplications.removeAll{$0==bundle}}
        else {d.pausedApplications.append(bundle)}
        do {try profiles.replace(d);actions.notify("\(bundle)：Xbox \(controller.applicationPaused ? "已暂停":"已恢复")")}
        catch {actions.notify(error.localizedDescription)}
    }
    @objc func permissions(_ sender:Any?) {
        _=AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String:true] as CFDictionary)
        _=IOHIDRequestAccess(kIOHIDRequestTypeListenEvent)
        if sender != nil {NSWorkspace.shared.open(URL(string:"x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)}
    }
    @objc func openLog(_ sender:Any?) {NSWorkspace.shared.open(support.appendingPathComponent("bridge.log"))}
    @objc func openGuide(_ sender:Any?) {NSWorkspace.shared.open(URL(string:"https://cxt12e4.11166666.xyz/mac/index.html")!)}
    @objc func openShortcutGuide(_ sender:Any?) {NSWorkspace.shared.open(URL(string:"https://cxt12e4.11166666.xyz/")!)}
    @objc func openOfflineGuide(_ sender:Any?) {if let u=Bundle.main.url(forResource:"使用说明",withExtension:"html") {NSWorkspace.shared.open(u)}}
    @objc func diagnostics(_ sender:Any?) {dumpAX()}
    func dumpAX(openModel:Bool=false,modelList:Bool=false) {
        let c=contexts.get()
        work.async {
            if openModel {
                guard c.bundle=="com.openai.codex",!self.actions.openPowerMenu(c).isEmpty,self.contexts.valid(c) else{return}
            }
            if modelList { _=self.actions.clickModelControl(["选择模型","Select model"],c);usleep(150000) }
            let nodes=openModel ? axModelNodes(c.pid):axNodes(c.pid)
            let controls=nodes.filter{["AXButton","AXSlider","AXCheckBox","AXPopUpButton","AXMenuItem","AXTextField","AXComboBox"].contains(axString($0,"AXRole"))}.map{["role":axString($0,"AXRole"),"label":axLabel($0),"help":axString($0,"AXHelp"),"identifier":axString($0,"AXIdentifier"),"value":["AXSlider","AXCheckBox"].contains(axString($0,"AXRole")) ? String(describing:axValue($0,"AXValue") ?? "" as CFString):"","min":String(describing:axValue($0,"AXMinValue") ?? "" as CFString),"max":String(describing:axValue($0,"AXMaxValue") ?? "" as CFString)]}
            if let data=try? JSONSerialization.data(withJSONObject:controls,options:[.prettyPrinted,.sortedKeys]) {try? data.write(to:support.appendingPathComponent("ax-controls.json"),options:.atomic)}
            if openModel {
                let menu=axPowerMenu(c.pid).map{["role":axString($0,"AXRole"),"label":axLabel($0),"value":axString($0,"AXValue"),"help":axString($0,"AXHelp"),"roleDescription":axString($0,"AXRoleDescription")]}
                if let data=try? JSONSerialization.data(withJSONObject:menu,options:[.prettyPrinted,.sortedKeys]) {try? data.write(to:support.appendingPathComponent("ax-menu.json"),options:.atomic)}
            }
            if openModel && c.layer==1 {self.actions.key("escape",[],c);if modelList {self.actions.key("escape",[],c)}}
        }
    }
    @objc func quitBridge(_ sender:Any?) {
        // KeepAlive uses SuccessfulExit=false, so an intentional quit stays quit.
        NSApp.terminate(nil)
    }
    func applicationWillTerminate(_ notification:Notification) {controller.stop();input.stop();hid.stop();log("Bridge stopped")}
}
let delegate=BridgeDelegate();app.delegate=delegate;app.run()
