import Foundation
import AppKit
import IOKit.hid

let support=FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/CXT12E4Bridge")
let logLock=NSLock()
func log(_ text:String) {
    logLock.lock();defer{logLock.unlock()}
    try? FileManager.default.createDirectory(at:support,withIntermediateDirectories:true)
    let path=support.appendingPathComponent("bridge.log")
    if let size=(try? path.resourceValues(forKeys:[.fileSizeKey]))?.fileSize, size>2_000_000 {
        let old=support.appendingPathComponent("bridge.previous.log");try? FileManager.default.removeItem(at:old);try? FileManager.default.moveItem(at:path,to:old)
    }
    if !FileManager.default.fileExists(atPath:path.path) {FileManager.default.createFile(atPath:path.path,contents:nil)}
    if let f=try? FileHandle(forWritingTo:path) {defer{try? f.close()};_ = try? f.seekToEnd();try? f.write(contentsOf:Data("\(ISO8601DateFormatter().string(from:Date())) \(text)\n".utf8))}
}

func runProcess(_ executable:String,_ args:[String],timeout:Double=4)->(Int32,Data) {
    let p=Process();p.executableURL=URL(fileURLWithPath:executable);p.arguments=args
    let out=Pipe();p.standardOutput=out;p.standardError=FileHandle.nullDevice
    var env=ProcessInfo.processInfo.environment
    env["PATH"]="\(FileManager.default.homeDirectoryForCurrentUser.path)/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin"
    p.environment=env
    let done=DispatchSemaphore(value:0);p.terminationHandler={_ in done.signal()}
    do {try p.run()} catch {return (-1,Data())}
    // Drain concurrently so a full output pipe cannot stall the child.
    final class Output {var data=Data()}
    let value=Output();let reader=DispatchGroup();reader.enter()
    DispatchQueue.global().async {value.data=out.fileHandleForReading.readDataToEndOfFile();reader.leave()}
    if done.wait(timeout:.now()+timeout) == .timedOut {p.terminate();if done.wait(timeout:.now()+0.3) == .timedOut {kill(p.processIdentifier,SIGKILL);_ = done.wait(timeout:.now()+0.3)}}
    _ = reader.wait(timeout:.now()+0.5)
    return (p.isRunning ? -2:p.terminationStatus,value.data)
}

func helper(_ args:[String])->[String:Any] {
    guard let path=Bundle.main.path(forResource:"bridge_helper",ofType:"py") else{return ["error":"helper missing"]}
    let (code,data)=runProcess("/usr/bin/python3",[path]+args,timeout:6)
    let result=(try? JSONSerialization.jsonObject(with:data)) as? [String:Any] ?? ["error":"helper exited \(code)"]
    if let error=result["error"] {log("helper \(args.first ?? ""): \(error)")}
    return result
}

struct Foreground: Equatable {
    var pid:pid_t=0;var bundle="";var window:String="";var layer=0;var generation=0
}
final class ContextStore {
    private let lock=NSLock();private var value=Foreground()
    private var focusedWindow:AXUIElement?
    func get()->Foreground {lock.lock();defer{lock.unlock()};return value}
    func set(_ v:Foreground,window:AXUIElement?=nil) {lock.lock();value=v;focusedWindow=window;lock.unlock()}
    func valid(_ v:Foreground)->Bool {
        lock.lock();let s=value,expected=focusedWindow;lock.unlock()
        guard s.generation==v.generation,s.pid==v.pid,NSWorkspace.shared.frontmostApplication?.processIdentifier==v.pid else{return false}
        // A different window in the same app can become active between polling
        // and a queued action. Compare AX identity at the actual execution edge.
        guard let expected=expected else{return true}
        let root=AXUIElementCreateApplication(v.pid);AXUIElementSetMessagingTimeout(root,0.05)
        guard let current=axValue(root,"AXFocusedWindow"),CFGetTypeID(current)==AXUIElementGetTypeID() else{return false}
        return CFEqual(expected,current)
    }
}
func autoLayer(_ b:String)->Int {
    if ["com.openai.codex","com.openai.chat"].contains(b) {return 1}
    if ["com.mitchellh.ghostty","com.apple.Terminal","com.googlecode.iterm2","com.github.wez.wezterm","dev.warp.Warp-Stable"].contains(b) {return 2}
    if b=="notion.id" {return 3};if b=="md.obsidian" {return 4};if b=="org.blenderfoundation.blender" {return 5}
    if b=="com.apple.finder" {return 6}
    if ["com.tencent.xinWeChat","com.tencent.WeWorkMac","ru.keepcoder.Telegram","com.tencent.qq"].contains(b) {return 7}
    return 0
}

final class HidBridge {
    let manager=IOHIDManagerCreate(kCFAllocatorDefault,IOOptionBits(0x8) /* kIOHIDManagerOptionIndependentDevices */)
    var devices:[IOHIDDevice]=[];var buffers:[UnsafeMutablePointer<UInt8>]=[]
    var raw:IOHIDDevice?;var sequence:UInt8=0;var verified=false
    var lastReply=0.0;var pending:[UInt8:UInt8]=[:];var inputCount=0;var reconnectAt=0.0
    var onReport:([UInt8])->Void={_ in};var onDisconnect:()->Void={}
    func start() {
        IOHIDManagerSetDeviceMatching(manager,[kIOHIDVendorIDKey:0x5754,kIOHIDProductIDKey:0xc401] as CFDictionary)
        let ptr=Unmanaged.passUnretained(self).toOpaque()
        IOHIDManagerRegisterDeviceMatchingCallback(manager,{ctx,_,_,d in guard let ctx=ctx else{return};Unmanaged<HidBridge>.fromOpaque(ctx).takeUnretainedValue().attach(d)},ptr)
        IOHIDManagerRegisterDeviceRemovalCallback(manager,{ctx,_,_,d in guard let ctx=ctx else{return};Unmanaged<HidBridge>.fromOpaque(ctx).takeUnretainedValue().remove(d)},ptr)
        IOHIDManagerScheduleWithRunLoop(manager,CFRunLoopGetMain(),CFRunLoopMode.commonModes.rawValue)
        IOHIDManagerOpen(manager,0)
        if let ds=IOHIDManagerCopyDevices(manager) as? Set<IOHIDDevice> {for d in ds {attach(d)}}
    }
    func attach(_ d:IOHIDDevice) {
        if devices.contains(where:{$0==d}) {return}
        let page=IOHIDDeviceGetProperty(d,kIOHIDPrimaryUsagePageKey as CFString) as? Int ?? 0
        let usage=IOHIDDeviceGetProperty(d,kIOHIDPrimaryUsageKey as CFString) as? Int ?? 0
        let version=IOHIDDeviceGetProperty(d,kIOHIDVersionNumberKey as CFString) as? Int ?? 0
        let isRaw=page==0xff60 && usage==0x61 && version==0x100
        guard isRaw else{return} // Keyboard input is captured by Karabiner, not a root Bridge.
        if raw != nil {return}
        let result=IOHIDDeviceOpen(d,0)
        guard result==kIOReturnSuccess else {log("HID open page=\(page) error=\(String(format:"%08x",result))");return}
        let buffer=UnsafeMutablePointer<UInt8>.allocate(capacity:64);buffers.append(buffer);devices.append(d)
        IOHIDDeviceRegisterInputReportCallback(d,buffer,64,{ctx,ret,sender,_,_,bytes,n in
            guard let ctx=ctx, ret==kIOReturnSuccess else{return}
            let me=Unmanaged<HidBridge>.fromOpaque(ctx).takeUnretainedValue()
            let p=Array(UnsafeBufferPointer(start:bytes,count:n))
            if p.count==32 && Array(p[0..<5])==[0x43,0x31,0x32,0x45,1] {
                if let expected=me.pending.removeValue(forKey:p[13]), p[5]==expected|0x80 {
                    if !me.verified {log("firmware v1 handshake verified")};me.verified=true;me.lastReply=ProcessInfo.processInfo.systemUptime
                }
            } else if p.count==8 {me.inputCount+=1;me.onReport(p)}
        },Unmanaged.passUnretained(self).toOpaque())
        IOHIDDeviceScheduleWithRunLoop(d,CFRunLoopGetMain(),CFRunLoopMode.commonModes.rawValue)
        raw=d;send(layer:0,tasks:[],terminals:[],ping:true)
    }
    func remove(_ d:IOHIDDevice) {
        if raw==d {raw=nil;verified=false;pending.removeAll();onDisconnect();log("CXT raw disconnected")}
        devices.removeAll{$0==d};IOHIDDeviceUnscheduleFromRunLoop(d,CFRunLoopGetMain(),CFRunLoopMode.commonModes.rawValue);IOHIDDeviceClose(d,0)
    }
    func retry() {if let ds=IOHIDManagerCopyDevices(manager) as? Set<IOHIDDevice> {for d in ds {attach(d)}}}
    func send(layer:Int,tasks:[UInt8],terminals:[UInt8],ping:Bool=false) {
        guard let raw=raw else{return}
        sequence &+= 1;let command:UInt8 = ping || !verified ? 1:2
        var p=lightingPacket(layer:layer,tasks:tasks,terminals:terminals,sequence:sequence,command:command)
        pending[sequence]=command
        let r=IOHIDDeviceSetReport(raw,kIOHIDReportTypeOutput,0,&p,p.count)
        if r != kIOReturnSuccess {log("HID write failed \(r)");remove(raw)}
    }
    func stop() {for d in devices {IOHIDDeviceClose(d,0)};IOHIDManagerClose(manager,0)}
}
