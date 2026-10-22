import Foundation
import AppKit

// Own each binding from physical down to up. RT/layer changes cannot rebind a held mouse button.
final class ControllerCoordinator {
    struct Press {let action:String;let context:Foreground;let began:Double;var nextRepeat:Double;var fired=false;var cancelled=false}
    let profiles:ProfileStore
    let input:ControllerInput
    let engine:ControllerEngine
    private let clock:()->Double
    var context:()->Foreground={Foreground()}
    var beforeInput:()->Void={}
    var onAction:(String,Foreground,Double)->Void={_,_,_ in}
    var onHelp:()->Void={}
    var onChooser:()->Void={}
    var onNotice:(String)->Void={_ in}
    var panelInput:(String,Bool)->Bool={_,_ in false}
    private(set) var connected=false
    private(set) var controllerName="未连接"
    private(set) var reports=0
    private var presses:[String:Press]=[:]
    private var held:Set<String>=[]
    private var blockedHeld:Set<String>=[]
    private var axes=[Double](repeating:0,count:6)
    private var sampleButtonsReleased=false
    private var timer:Timer?
    private var sleepObserver:NSObjectProtocol?
    private var wakeObserver:NSObjectProtocol?
    private var sleeping=false
    private var lastEffective=false
    private var requireAllNeutral=true
    private var requireLayerNeutral=false
    private var requireMouseNeutral=true
    private var lastAxisModes:[String]=[]
    private var lastOrbit=0.0
    private var panelVisible=false
    init(profiles:ProfileStore,input:ControllerInput=ControllerInput(),engine:ControllerEngine=ControllerEngine(),clock:@escaping ()->Double={ProcessInfo.processInfo.systemUptime}) {
        self.profiles=profiles;self.input=input;self.engine=engine;self.clock=clock
    }
    var enabled:Bool {profiles.document.xboxEnabled}
    var applicationPaused:Bool {profiles.document.pausedApplications.contains(context().bundle)}
    var effective:Bool {connected && enabled && !sleeping && !applicationPaused && !panelVisible}
    var status:String { !connected ? "Xbox：未连接" : !enabled ? "Xbox：已暂停" : applicationPaused ? "Xbox：此应用已暂停" : "Xbox：已连接 · Menu 长按暂停" }
    func start() {
        configure()
        input.onConnection={ [weak self] yes,name in self?.connectionChanged(yes,name:name) }
        input.onAxes={ [weak self] lx,ly,rx,ry,lt,rt,released in
            self?.axesChanged(lx:lx,ly:ly,rx:rx,ry:ry,lt:lt,rt:rt,controlsReleased:released)
        }
        input.onButton={ [weak self] name,down in self?.button(name,down:down) }
        sleepObserver=NSWorkspace.shared.notificationCenter.addObserver(forName:NSWorkspace.willSleepNotification,object:nil,queue:.main) { [weak self] _ in self?.sleeping=true;self?.cancelAll();self?.syncEnabled() }
        wakeObserver=NSWorkspace.shared.notificationCenter.addObserver(forName:NSWorkspace.didWakeNotification,object:nil,queue:.main) { [weak self] _ in self?.sleeping=false;self?.cancelAll();self?.syncEnabled() }
        timer=Timer(timeInterval:0.025,repeats:true){ [weak self] _ in self?.tick() };RunLoop.main.add(timer!,forMode:.common)
        input.start()
    }
    func configure() {
        cancelAll()
        let d=profiles.document
        engine.deadZone=d.deadZone;engine.cursorSpeed=d.cursorSpeed;engine.scrollSpeed=d.scrollSpeed;engine.precisionScale=d.precisionScale;engine.invertScroll=d.invertScroll
        input.neutralDeadZone=d.deadZone
        lastAxisModes=axisModes()
        syncEnabled()
    }
    func connectionChanged(_ yes:Bool,name:String) {
        cancelAll();connected=yes;controllerName=name
        // The input adapter separately requires all controls to be physically
        // neutral after connection. Old-device ownership cannot survive here.
        held.removeAll();blockedHeld.removeAll()
        syncEnabled();onNotice(status)
    }
    func axesChanged(lx:Double,ly:Double,rx:Double,ry:Double,lt:Double,rt:Double,controlsReleased:Bool?=nil) {
        syncEnabled();axes=[lx,ly,rx,ry,lt,rt];sampleButtonsReleased=controlsReleased ?? held.isEmpty;reports+=1;updateAxes(physicalSample:true)
    }
    func syncEnabled() {
        let value=effective
        if value != lastEffective {cancelAll();engine.setEnabled(value);lastEffective=value}
    }
    func cancelAll() {
        presses.removeAll();blockedHeld.formUnion(held);axes=Array(repeating:0,count:6);engine.suspend();requireAllNeutral=true;requireLayerNeutral=true;requireMouseNeutral=true
    }
    func contextChanged() {
        for name in presses.keys where !(presses[name]?.action.hasPrefix("mouse.") ?? false) {presses[name]?.cancelled=true}
        let modes=axisModes()
        if modes != lastAxisModes {requireMouseNeutral=true;lastAxisModes=modes}
        requireLayerNeutral=true;syncEnabled();updateAxes()
    }
    func setPanelVisible(_ visible:Bool) {panelVisible=visible;cancelAll();syncEnabled()}
    func toggle() {
        var d=profiles.document;d.xboxEnabled.toggle()
        do {try profiles.replace(d);onNotice(status)} catch {onNotice(error.localizedDescription)}
    }
    func button(_ name:String,down:Bool) {
        beforeInput()
        let now=clock()
        reports+=1
        if down {guard !held.contains(name) else{return};held.insert(name)} else {held.remove(name);blockedHeld.remove(name)}
        if name=="rt" || name=="lt" {if down,!blockedHeld.isEmpty {blockedHeld.insert(name)};return}
        // Releases always balance the original mouse down, even after focus/config changes.
        if !down,let press=presses.removeValue(forKey:name) {
            if press.action.hasPrefix("mouse."),!presses.values.contains(where:{$0.action==press.action}) {engine.mouseButton(String(press.action.dropFirst(6)),down:false)}
            else if name=="menu",!press.fired,!press.cancelled {onChooser()}
            return
        }
        guard connected,!sleeping,!applicationPaused else{return}
        if panelInput(name,down) {return}
        guard down else{return}
        if name=="menu" {
            // In a paused game, Menu remains the game's native pause control.
            // Global Bridge pause can still be reversed with Menu elsewhere.
            guard connected,!sleeping,!applicationPaused else{return}
            presses[name]=Press(action:"bridge.menu",context:context(),began:now,nextRepeat:now+0.85);return
        }
        syncEnabled();guard effective else{return}
        if !blockedHeld.isEmpty {blockedHeld.insert(name);return}
        let c=context(),control=held.contains("rt") ? "rt."+name:name
        let action=profiles.action("xbox",control,c.layer)
        presses[name]=Press(action:action,context:c,began:now,nextRepeat:now+0.4)
        if action.hasPrefix("mouse.") {engine.mouseButton(String(action.dropFirst(6)),down:true)}
        else {fire(action,c,now)}
    }
    private func fire(_ action:String,_ c:Foreground,_ now:Double) {
        switch action {
        case "none":break
        case "bridge.help":onHelp()
        case "bridge.chooser":onChooser()
        default:onAction(action,c,now)
        }
    }
    func tick() {
        syncEnabled()
        let now=clock()
        for name in Array(presses.keys) {
            guard var p=presses[name],!p.cancelled else{continue}
            if name=="menu",!p.fired,now-p.began>=0.85 {
                p.fired=true;presses[name]=p
                // Store the consumed key again after onChange cancels held state.
                toggle();p.cancelled=true;presses[name]=p;held.insert(name)
            } else if effective,now>=p.nextRepeat,["key.up","key.down","key.left","key.right","key.pageup","key.pagedown"].contains(p.action) {
                p.nextRepeat=now+0.09;presses[name]=p;fire(p.action,p.context,now)
            }
        }
        guard effective,!requireLayerNeutral,now-lastOrbit>=0.14 else{return}
        let c=context();guard c.layer==5 else{return}
        var x=0.0,y=0.0
        if profiles.action("xbox","leftstick",5)=="stick.blender" {x+=axes[0];y+=axes[1]}
        if profiles.action("xbox","rightstick",5)=="stick.blender" {x+=axes[2];y+=axes[3]}
        guard max(abs(x),abs(y))>0.45 else{return}
        lastOrbit=now
        onAction(abs(x)>abs(y) ? "encoder.1.\(x>0 ? 2:0)":"encoder.2.\(y>0 ? 0:2)",c,now)
    }
    private func updateAxes(physicalSample:Bool=false) {
        let d=profiles.document
        let sticksNeutral=hypot(axes[0],axes[1])<=d.deadZone && hypot(axes[2],axes[3])<=d.deadZone
        // A reset requires a real all-controls-neutral sample, not the synthetic
        // zero axes from cancelAll. Ordinary focus changes retain active drags.
        if physicalSample,sticksNeutral,sampleButtonsReleased,held.isEmpty,blockedHeld.isEmpty,axes[4]<0.1,axes[5]<0.1 {requireAllNeutral=false}
        guard !requireAllNeutral else{return}
        if physicalSample,sticksNeutral {requireLayerNeutral=false;requireMouseNeutral=false}
        let layer=context().layer
        var mx=0.0,my=0.0,sx=0.0,sy=0.0
        for (control,index) in [("leftstick",0),("rightstick",2)] {
            let mode=profiles.action("xbox",control,layer)
            if mode=="stick.mouse",!requireMouseNeutral {mx+=axes[index];my+=axes[index+1]}
            if mode=="stick.scroll",!requireLayerNeutral {sx+=axes[index];sy+=axes[index+1]}
        }
        engine.setAxes(lx:max(-1,min(1,mx)),ly:max(-1,min(1,my)),rx:max(-1,min(1,sx)),ry:max(-1,min(1,sy)),precision:axes[4]>0.2)
    }
    private func axisModes()->[String] {let layer=context().layer;return [profiles.action("xbox","leftstick",layer),profiles.action("xbox","rightstick",layer)]}
    func stop() {
        cancelAll();input.stop();engine.stop();timer?.invalidate();timer=nil
        for token in [sleepObserver,wakeObserver].compactMap({$0}) {NSWorkspace.shared.notificationCenter.removeObserver(token)}
    }
}

func controllerCoordinatorSelfTest() {
    var checks=0
    func expect(_ value:Bool,_ message:String) {checks+=1;if !value {fatalError("Controller coordinator test: \(message)")}}
    let directory=FileManager.default.temporaryDirectory.appendingPathComponent("cxt-controller-router-"+UUID().uuidString)
    defer {try? FileManager.default.removeItem(at:directory)}
    let profiles=ProfileStore(url:directory.appendingPathComponent("profiles.json"))
    var now=0.0,point=CGPoint(x:300,y:300)
    var output:[String]=[],actions:[String]=[],chooserCount=0
    var foreground=Foreground(pid:123,bundle:"com.openai.codex",window:"one",layer:1,generation:1)
    let engine=ControllerEngine(startTimer:false,output:{event in
        switch event {
        case let .button(name,down,_,_):output.append("\(name):\(down)")
        case let .move(next,_,drag):point=next;output.append("move:"+(drag ?? "none"))
        case .scroll:output.append("scroll")
        }
    },location:{point},displays:{[CGRect(x:0,y:0,width:1920,height:1080)]},permitted:{true},clock:{now})
    let coordinator=ControllerCoordinator(profiles:profiles,engine:engine,clock:{now})
    coordinator.context={foreground};coordinator.onAction={action,_,_ in actions.append(action)}
    coordinator.onChooser={chooserCount+=1}
    profiles.onChange={ [weak coordinator] in coordinator?.configure() }
    func axes(_ lx:Double=0,_ ly:Double=0,_ rx:Double=0,_ ry:Double=0) {coordinator.axesChanged(lx:lx,ly:ly,rx:rx,ry:ry,lt:0,rt:0)}
    func reset() {
        coordinator.connectionChanged(false,name:"Test Xbox");coordinator.setPanelVisible(false)
        var document=BridgeProfileDocument();document.xboxEnabled=true
        try! profiles.replace(document)
        foreground=Foreground(pid:123,bundle:"com.openai.codex",window:"one",layer:1,generation:1)
        coordinator.contextChanged();coordinator.connectionChanged(true,name:"Test Xbox");axes()
        output=[];actions=[];chooserCount=0;now+=2
        coordinator.panelInput={_,_ in false};coordinator.onChooser={chooserCount+=1}
    }
    reset()
    coordinator.button("a",down:true);coordinator.button("a",down:false)
    coordinator.button("b",down:true);coordinator.button("b",down:false)
    expect(actions==["key.enter","key.escape"] && output.isEmpty,"A confirms and B cancels without mouse output")
    coordinator.button("y",down:true);coordinator.button("y",down:false)
    expect(output==["right:true","right:false"],"Y emits balanced right click")
    reset()
    coordinator.button("rt",down:true);coordinator.button("l3",down:true)
    coordinator.button("rt",down:false);coordinator.button("l3",down:false)
    expect(actions==["codex.composer"] && output.isEmpty,"RT+L3 retains layer action without a release-time help action")
    reset()
    coordinator.button("rt",down:true);coordinator.button("a",down:true)
    coordinator.button("rt",down:false);coordinator.button("a",down:false)
    expect(actions==["edit.copy"] && output.isEmpty,"RT-before-A performs copy and never clicks on modifier release")
    reset()
    coordinator.button("x",down:true);coordinator.button("rt",down:true);coordinator.button("x",down:false);coordinator.button("rt",down:false)
    expect(output==["left:true","left:false"] && actions.isEmpty,"X-before-RT retains original mouse ownership")
    reset()
    try! profiles.set("xbox","a",1,"mouse.left");axes()
    coordinator.button("x",down:true);coordinator.button("a",down:true);coordinator.button("x",down:false)
    expect(output==["left:true"],"two physical owners do not release shared mouse early")
    coordinator.button("a",down:false)
    expect(output==["left:true","left:false"],"last physical owner releases shared mouse")
    reset()
    coordinator.button("x",down:true);axes(1);engine.tick(delta:1.0/60)
    foreground.window="two";foreground.generation+=1;coordinator.contextChanged();engine.tick(delta:1.0/60)
    expect(output.filter{$0=="move:left"}.count==2,"same mouse mode permits cross-window dragging")
    coordinator.button("x",down:false)
    expect(output.last=="left:false","foreground change cannot strand mouse down")
    reset()
    coordinator.button("up",down:true);foreground.generation+=1;coordinator.contextChanged();now+=1;coordinator.tick()
    expect(actions==["key.up"],"context switch cancels key repeat")
    coordinator.button("up",down:false)
    reset()
    try! profiles.set("xbox","leftstick",2,"stick.scroll");axes();axes(1);engine.tick(delta:1.0/60)
    let oldPoint=point
    foreground.layer=2;foreground.generation+=1;coordinator.contextChanged();axes(1);engine.tick(delta:1.0/60)
    expect(point==oldPoint && output==["move:none"],"mouse-to-scroll mode switch waits for physical neutral")
    axes();axes(1);engine.tick(delta:1.0/60)
    expect(output.last=="scroll","new stick mode starts after neutral")
    foreground.layer=1;foreground.generation+=1;coordinator.contextChanged();axes(1);engine.tick(delta:1.0/60)
    expect(point==oldPoint,"scroll-to-mouse cannot jump before neutral")
    axes();axes(1);engine.tick(delta:1.0/60)
    expect(point.x>oldPoint.x,"mouse resumes after actual neutral")
    reset()
    coordinator.button("menu",down:true);coordinator.connectionChanged(false,name:"Test Xbox");coordinator.button("menu",down:false)
    expect(chooserCount==0,"disconnect Menu release cannot become tap")
    reset()
    coordinator.button("menu",down:true);now+=0.9;coordinator.tick();coordinator.button("menu",down:false)
    expect(!coordinator.enabled && chooserCount==0,"Menu hold pauses without opening chooser")
    coordinator.button("menu",down:true);now+=0.9;coordinator.tick();coordinator.button("menu",down:false)
    expect(coordinator.enabled && chooserCount==0,"Menu hold resumes without short action")
    output=[];axes(1);engine.tick(delta:1.0/60)
    expect(output.isEmpty,"resumed deflected stick waits for neutral")
    axes();axes(1);engine.tick(delta:1.0/60)
    expect(output==["move:none"],"neutral permits resumed pointer")
    coordinator.configure();foreground.generation+=1;coordinator.contextChanged();output=[]
    axes(1);engine.tick(delta:1.0/60)
    expect(output.isEmpty,"synthetic reset zero cannot satisfy physical neutral gate")
    reset()
    coordinator.onChooser={chooserCount+=1;coordinator.setPanelVisible(true)}
    var selected=0,visible=true
    coordinator.panelInput={name,down in
        guard visible else{return false}
        if name=="a",down {selected+=1;visible=false;coordinator.setPanelVisible(false)}
        return true
    }
    // Keep the panel hook inactive until Menu's release opens the panel.
    visible=false
    coordinator.onChooser={chooserCount+=1;visible=true;coordinator.setPanelVisible(true)}
    coordinator.button("menu",down:true);coordinator.button("menu",down:false)
    expect(chooserCount==1 && !coordinator.effective,"Menu tap opens panel and disables desktop output")
    axes(1);engine.tick(delta:1.0/60);coordinator.button("a",down:true);coordinator.button("a",down:false)
    expect(selected==1 && output.isEmpty && actions.isEmpty,"panel confirm cannot leak Enter or analog movement")
    axes();coordinator.button("a",down:true);coordinator.button("a",down:false)
    expect(actions==["key.enter"] && output.isEmpty,"fresh A confirms after panel consumed earlier press")
    reset()
    coordinator.button("rt",down:true);coordinator.configure();axes()
    coordinator.button("x",down:true);coordinator.button("x",down:false)
    expect(actions.isEmpty && output.isEmpty,"reconfigure cannot reinterpret a held modifier")
    coordinator.button("rt",down:false);axes();coordinator.button("x",down:true);coordinator.button("x",down:false)
    expect(output==["left:true","left:false"],"release of old modifier permits fresh base binding")
    coordinator.button("rt",down:true);coordinator.configure();axes();output=[]
    coordinator.button("b",down:true);coordinator.button("rt",down:false)
    coordinator.button("x",down:true);coordinator.button("x",down:false)
    expect(output.isEmpty,"new buttons pressed during quarantine also require release")
    coordinator.button("b",down:false);axes();coordinator.button("x",down:true);coordinator.button("x",down:false)
    expect(output==["left:true","left:false"],"all released buttons leave quarantine")
    reset()
    coordinator.configure()
    // Adapter publishes axes before button transitions. A newly held button in
    // that same sample must not accidentally satisfy the all-neutral barrier.
    coordinator.axesChanged(lx:0,ly:0,rx:0,ry:0,lt:0,rt:0,controlsReleased:false)
    coordinator.button("x",down:true);axes(1);engine.tick(delta:1.0/60)
    expect(output.isEmpty,"new physical button in neutral-axis sample cannot bypass reset barrier")
    coordinator.button("x",down:false);axes();coordinator.button("x",down:true);coordinator.button("x",down:false)
    expect(output==["left:true","left:false"],"neutral physical sample restores normal clicks")
    reset()
    coordinator.button("rt",down:true);coordinator.configure()
    axes();axes(1);engine.tick(delta:1.0/60)
    expect(output.isEmpty,"configuration reset requires held RT to be released before pointer resumes")
    coordinator.button("rt",down:false)
    axes(1);engine.tick(delta:1.0/60)
    expect(output.isEmpty,"releasing RT with deflected stick still waits for neutral")
    coordinator.axesChanged(lx:0,ly:0,rx:0,ry:0,lt:0.25,rt:0)
    coordinator.axesChanged(lx:1,ly:0,rx:0,ry:0,lt:0.25,rt:0);engine.tick(delta:1.0/60)
    expect(output.isEmpty,"partially squeezed LT also blocks restart")
    axes();axes(1);engine.tick(delta:1.0/60)
    expect(output==["move:none"],"fully released controls and neutral sticks resume movement")
    reset()
    coordinator.button("x",down:true);coordinator.configure();output=[]
    axes();axes(1);engine.tick(delta:1.0/60)
    expect(output.isEmpty,"configuration reset with held mouse button cannot resume pointer")
    coordinator.button("x",down:false);axes();axes(1);engine.tick(delta:1.0/60)
    expect(output==["move:none"],"released mouse owner permits fresh movement")
    reset()
    foreground.bundle="com.valvesoftware.steam";coordinator.contextChanged()
    coordinator.button("menu",down:true);now+=1;coordinator.tick();coordinator.button("menu",down:false)
    coordinator.button("a",down:true);coordinator.button("a",down:false);axes(1);engine.tick(delta:1.0/60)
    expect(coordinator.enabled && chooserCount==0 && output.isEmpty && actions.isEmpty,"game pause excludes Menu as well as ordinary controls")
    var panelEvents=0
    coordinator.panelInput={_,_ in panelEvents+=1;return true}
    coordinator.setPanelVisible(true)
    coordinator.button("a",down:true);coordinator.button("a",down:false)
    expect(panelEvents==0,"excluded foreground cannot control a stale panel")
    foreground.bundle="com.openai.codex";coordinator.contextChanged()
    coordinator.connectionChanged(false,name:"Test Xbox")
    coordinator.button("b",down:true);coordinator.button("b",down:false)
    expect(panelEvents==0,"disconnected controller cannot control a panel")
    coordinator.stop()
    print("PASS \(checks) controller routing/ownership checks")
}
