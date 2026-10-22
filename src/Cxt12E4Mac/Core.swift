import Foundation

let taskKeys = [1,2,3,5,6,7]
let terminalKeys = [9,10,11,12,8,4]
let layerNames = ["通用 / 浏览器","Codex","终端 / tmux","Notion","Obsidian","Blender","Finder","通讯"]

func isUltraFullAccessDialogName(_ name:String)->Bool {
    name.localizedCaseInsensitiveContains("Ultra") &&
    (name.contains("完整访问权限") || name.localizedCaseInsensitiveContains("full access"))
}

struct ModelSetting: Equatable {
    let model:String
    let effort:Int // low, medium, high, xhigh, max, ultra
    static func parse(_ value:String)->ModelSetting? {
        let label=value.components(separatedBy:CharacterSet(charactersIn:",，")).first?.trimmingCharacters(in:.whitespacesAndNewlines) ?? ""
        let aliases=[["轻度","低","Light","Low"],["中","标准","Medium","Standard"],["高","深度","High","Extended"],["极高","Extra High","XHigh"],["最高","Max","Maximum"],["Ultra"]]
        for model in ["GPT-6.1 Sol","GPT-6 Astra","GPT-6 Sol"] {
            for prefix in [model+" ",String(model.dropFirst(4))+" "] where label.hasPrefix(prefix) {
                let effort=String(label.dropFirst(prefix.count))
                if let index=aliases.firstIndex(where:{$0.contains{effort.caseInsensitiveCompare($0) == .orderedSame}}) {return ModelSetting(model:model,effort:index)}
            }
        }
        return nil
    }
}

struct InputAction: Equatable {
    enum Kind: String { case tap, hold, encoder, dock }
    var kind: Kind
    var number: Int
    var gesture: Int = 1 // CCW=0, press=1, CW=2
    var context: Int
    var time: Double
}

// Consumes the device's reconstructed pressed-key state and private encoder marker.
// Ownership survives cancellation until release. Karabiner consumes the physical triggers.
final class Decoder {
    static let holdDelay = 0.650
    static let reverseChordWindow = 0.250
    struct Press {
        var encoder: Bool
        var down = true
        var cancelled = false
        var fired = false
        var holdEligible = false
        var time: Double
        var context: Int
        var sequence: Int
    }
    var presses: [Int:Press] = [:]
    var nextSequence = 0
    var observed: Set<Int> = []
    var marker = false
    var quarantine = false
    var chord: (selector:Int, context:Int, time:Double)?
    var emit: (InputAction)->Void = {_ in}
    func cancel() { for key in presses.keys { presses[key]?.cancelled = true }; chord = nil }
    func reset() { presses.removeAll(); observed.removeAll(); nextSequence = 0; marker = false; quarantine = false; chord = nil }
    private func beginChord(selector:Int, now:Double, context:Int) {
        chord = (selector,context,now)
        presses[12]?.cancelled = true
        presses[12]?.holdEligible = false
        presses[selector]?.cancelled = true
        presses[selector]?.holdEligible = false
    }
    func report(keys:Set<Int>, control:Bool, now:Double, context:Int) {
        let markerStarted = control && !marker
        let previous = observed; observed = keys; marker = control
        if quarantine { if keys.isEmpty && !control { reset() }; return }
        // Encoder packets reuse F13...F24. If their RCtrl marker arrives while
        // a physical button owns one of those codes, there may be no new key
        // edge at all. Do not mistake the old button for a held task/session
        // trigger; wait for a completely released board before accepting input.
        if markerStarted && presses.values.contains(where:{!$0.encoder && $0.down}) {
            quarantine = true; cancel(); return
        }
        for k in presses.keys where presses[k]?.context != context { presses[k]?.cancelled = true }
        if let c=chord, c.context != context {chord=nil}
        for k in keys.subtracting(previous).sorted() {
            if let old = presses[k], old.encoder != control || !old.down { quarantine = true; cancel(); return }
            if presses.values.contains(where:{$0.encoder && $0.down}) { quarantine = true; cancel(); return }
            nextSequence += 1
            presses[k] = Press(encoder:control,time:now,context:context,sequence:nextSequence)
            if chord != nil { presses[k]?.cancelled = true; continue }
            guard !control else {continue}

            if k != 12, let prefix=presses[12], !prefix.encoder, prefix.down, prefix.context == context {
                // A consumed or already-held 12 owns later selectors until release.
                // They must not escape as ordinary taps or start another hold.
                presses[k]?.cancelled = true
                if !prefix.cancelled && !prefix.fired && now-prefix.time < Self.holdDelay {
                    beginChord(selector:k,now:now,context:context)
                }
                continue
            }
            if k == 12 {
                // Match Windows' selector-first grace period. Sequence, not key
                // number, chooses the first physical selector. Sorting new keys
                // makes a simultaneous snapshot deterministic without losing it.
                let selectors=presses.filter {key,p in
                    let age=now-p.time
                    return key != 12 && !p.encoder && p.down && keys.contains(key) &&
                        !p.cancelled && !p.fired && p.context == context &&
                        age >= 0 && age <= Self.reverseChordWindow
                }.sorted {$0.value.sequence < $1.value.sequence}
                if let owner=selectors.first?.key {
                    for (selector,_) in selectors {
                        presses[selector]?.cancelled = true
                        presses[selector]?.holdEligible = false
                    }
                    beginChord(selector:owner,now:now,context:context)
                    continue
                }
            }
            // Windows grants one hold owner per task/session group. An overlapping
            // second key can still tap, but never inherits ownership after release.
            let group=taskKeys.contains(k) ? taskKeys:terminalKeys
            let eligible = !group.contains {other in
                guard other != k, keys.contains(other), let p=presses[other] else{return false}
                return !p.encoder && p.down
            }
            presses[k]?.holdEligible = eligible
        }
        for k in previous.subtracting(keys).sorted() {
            guard var p = presses[k] else { continue }; p.down = false; presses[k] = p
            if !p.encoder {
                let held=now-p.time >= Self.holdDelay
                if !p.cancelled && !p.fired && p.context == context && (!held || p.holdEligible) {
                    emit(InputAction(kind:held ? .hold : .tap,number:k,context:context,time:now))
                }
                presses.removeValue(forKey:k)
            }
        }
        if !control {
            for k in presses.keys.sorted() {
                guard let p = presses[k], p.encoder, !p.down else {continue}
                presses.removeValue(forKey:k)
                if !p.cancelled && p.context == context {
                    let gesture=(k-1)%3, encoded=(k-1)/3
                    let physical = gesture == 1 ? encoded+1 : [4,3,1,2][encoded]
                    // Press may be held. Only rotation packets expire after 250ms.
                    if gesture == 1 || now-p.time <= 0.250 {
                        emit(InputAction(kind:.encoder,number:physical,gesture:gesture,context:context,time:now))
                    }
                }
            }
        }
        if let c=chord, !keys.contains(12), !keys.contains(c.selector), !control {
            chord=nil
            if c.context == context {emit(InputAction(kind:.dock,number:c.selector,context:context,time:now))}
        }
    }
    func tick(now:Double, context:Int) {
        for k in presses.keys.sorted() {
            guard var p=presses[k], p.down, !p.encoder, !p.cancelled, !p.fired, p.holdEligible, p.context == context,
                  now-p.time >= Self.holdDelay else {continue}
            p.fired=true; presses[k]=p
            emit(InputAction(kind:.hold,number:k,context:context,time:now))
        }
    }
}

func lightingPacket(layer:Int,tasks:[UInt8],terminals:[UInt8],sequence:UInt8,command:UInt8=2)->[UInt8] {
    var p=[UInt8](repeating:0,count:32)
    p.replaceSubrange(0..<6,with:[0x43,0x31,0x32,0x45,1,command]);p[6]=UInt8(max(0,min(7,layer)));p[13]=sequence
    for i in 0..<6 {p[7+i]=i<tasks.count ? tasks[i] : 0; p[15+i]=i<terminals.count ? terminals[i] : 0}
    return p
}

func selfTest() {
    var checks=0
    func expect(_ condition: @autoclosure ()->Bool,_ name:String) { checks+=1; if !condition() {fputs("FAIL: \(name)\n",stderr);exit(1)} }
    final class Actions { var values:[InputAction]=[]; var isEmpty:Bool { values.isEmpty }; var count:Int {values.count}; subscript(_ i:Int)->InputAction {values[i]}; func map<T>(_ f:(InputAction)->T)->[T] {values.map(f)}; func contains(_ f:(InputAction)->Bool)->Bool {values.contains(where:f)} }
    func run(_ body:(Decoder,Actions)->Void) {let d=Decoder();let a=Actions();d.emit={a.values.append($0)};body(d,a)}
    run {d,a in d.report(keys:[1],control:false,now:0,context:1);d.report(keys:[],control:false,now:0.2,context:1);expect(a.map(\.kind)==[.tap],"tap on release")}
    run {d,a in d.report(keys:[1],control:false,now:0,context:1);d.tick(now:0.65,context:1);d.report(keys:[],control:false,now:1,context:1);expect(a.map(\.kind)==[.hold],"one long hold, no tap")}
    run {d,a in d.report(keys:[12],control:false,now:0,context:1);d.report(keys:[12,3],control:false,now:0.1,context:1);d.report(keys:[12],control:false,now:0.2,context:1);expect(a.isEmpty,"chord waits for both releases");d.report(keys:[],control:false,now:0.3,context:1);expect(a.map(\.kind)==[.dock] && a[0].number==3,"dock chord")}
    run {d,a in d.report(keys:[12],control:false,now:0,context:1);d.tick(now:0.7,context:1);d.report(keys:[12,3],control:false,now:0.8,context:1);d.report(keys:[],control:false,now:0.9,context:1);expect(!a.contains{$0.kind == .dock},"hold cannot become chord")}
    run {d,a in d.report(keys:[1],control:true,now:0,context:1);d.report(keys:[],control:true,now:0.01,context:1);expect(a.isEmpty,"wait for marker release");d.report(keys:[],control:false,now:0.02,context:1);expect(a.count==1 && a[0].number==4 && a[0].gesture==0,"PCB K4 normalization")}
    run {d,a in d.report(keys:[2],control:true,now:0,context:1);d.report(keys:[],control:false,now:1,context:1);expect(a.count==1 && a[0].number==1,"held encoder press remains K1")}
    run {d,a in d.report(keys:[1],control:false,now:0,context:1);d.cancel();d.report(keys:[],control:false,now:0.1,context:1);expect(a.isEmpty,"A B A cancels")}
    run {d,a in d.report(keys:[1],control:false,now:0,context:1);d.report(keys:[],control:false,now:0.1,context:2);expect(a.isEmpty,"focus change cancels")}
    run {d,a in d.report(keys:[1],control:true,now:0,context:1);d.report(keys:[1,2],control:true,now:0.1,context:1);d.report(keys:[],control:false,now:0.2,context:1);expect(a.isEmpty,"ambiguous overlap fails closed")}
    for physicalKey in 1...12 {
        run {d,a in
            d.report(keys:[physicalKey],control:false,now:0,context:1)
            d.report(keys:[physicalKey],control:true,now:0.1,context:1)
            d.tick(now:0.7,context:1)
            d.report(keys:[],control:true,now:0.8,context:1)
            d.report(keys:[],control:false,now:0.9,context:1)
            expect(a.isEmpty,"encoder marker cannot reuse physical key \(physicalKey) as a hold or tap")
            d.report(keys:[physicalKey],control:false,now:1,context:1)
            d.report(keys:[],control:false,now:1.1,context:1)
            expect(a.count==1 && a[0].kind == .tap && a[0].number==physicalKey,"all-up rearms key \(physicalKey) after encoder collision")
        }
    }
    run {d,a in
        d.report(keys:[12],control:false,now:0,context:1)
        d.report(keys:[12,3],control:false,now:0.1,context:1)
        d.report(keys:[12,3],control:true,now:0.2,context:1)
        d.report(keys:[],control:false,now:0.3,context:1)
        expect(a.isEmpty,"encoder overlap also cancels a pending application chord")
    }
    func expectActions(_ actions:Actions,_ expected:[String],_ name:String) {
        expect(actions.map{"\($0.kind.rawValue):\($0.number)"} == expected,name)
    }
    // Exercise both physical key groups and every launch slot, independent of
    // edge order and which key is released first. Holding a chord must not leak
    // either original key's 650 ms action.
    for selector in 1...11 {
        for order in ["prefix-first","selector-first","same-snapshot"] {
            for releasePrefixFirst in [true,false] {
                run {d,a in
                    if order == "same-snapshot" {
                        d.report(keys:[selector,12],control:false,now:0,context:1)
                    } else {
                        d.report(keys:[order == "prefix-first" ? 12:selector],control:false,now:0,context:1)
                        d.report(keys:[selector,12],control:false,now:0.1,context:1)
                    }
                    d.tick(now:0.75,context:1)
                    let remaining=releasePrefixFirst ? selector:12
                    d.report(keys:[remaining],control:false,now:0.8,context:1)
                    expect(a.isEmpty,"\(order) slot \(selector) waits for both releases (prefix first=\(releasePrefixFirst))")
                    d.report(keys:[],control:false,now:0.9,context:1)
                    d.tick(now:1.6,context:1)
                    expectActions(a,["dock:\(selector)"],"\(order) slot \(selector) emits exactly one launch (prefix first=\(releasePrefixFirst))")
                }
            }
        }
    }
    for delay in [0.249,0.250,0.251] {
        run {d,a in
            d.report(keys:[3],control:false,now:0,context:1)
            d.report(keys:[3,12],control:false,now:delay,context:1)
            d.report(keys:[],control:false,now:0.4,context:1)
            expectActions(a,delay<=0.250 ? ["dock:3"]:["tap:3","tap:12"],"reverse chord boundary \(delay)")
        }
    }
    run {d,a in
        d.report(keys:[3],control:false,now:0,context:1)
        d.report(keys:[3,1],control:false,now:0.1,context:1)
        d.report(keys:[3,1,12],control:false,now:0.2,context:1)
        d.tick(now:0.9,context:1)
        d.report(keys:[],control:false,now:1,context:1)
        expectActions(a,["dock:3"],"reverse chord keeps first physical selector, not lowest key")
    }
    run {d,a in
        d.report(keys:[12],control:false,now:0,context:1)
        d.report(keys:[12,3],control:false,now:0.1,context:1)
        d.report(keys:[12,3,1],control:false,now:0.2,context:1)
        d.report(keys:[],control:false,now:0.9,context:1)
        expectActions(a,["dock:3"],"later forward selector cannot steal chord or leak a tap")
    }
    for timerFired in [true,false] {
        run {d,a in
            d.report(keys:[12],control:false,now:0,context:1)
            if timerFired {d.tick(now:0.65,context:1)}
            d.report(keys:[12,3],control:false,now:0.7,context:1)
            d.report(keys:[3],control:false,now:0.8,context:1)
            d.tick(now:1.5,context:1)
            d.report(keys:[],control:false,now:1.6,context:1)
            expectActions(a,["hold:12"],"late selector suppressed even after prefix release (timer fired=\(timerFired))")
        }
    }
    for group in [taskKeys,terminalKeys] {
        let first=group[0],second=group[1]
        run {d,a in
            d.report(keys:[first],control:false,now:0,context:1)
            d.report(keys:[first,second],control:false,now:0.1,context:1)
            d.tick(now:0.8,context:1)
            d.report(keys:[second],control:false,now:0.9,context:1)
            d.tick(now:1.6,context:1)
            d.report(keys:[],control:false,now:1.7,context:1)
            expectActions(a,["hold:\(first)"],"group \(first) first hold owner cannot be inherited")
        }
        run {d,a in
            d.report(keys:[first,second],control:false,now:0,context:1)
            d.tick(now:0.65,context:1)
            d.report(keys:[],control:false,now:0.8,context:1)
            expectActions(a,["hold:\(first)"],"simultaneous group \(first) has one deterministic hold owner")
        }
        run {d,a in
            d.report(keys:[first],control:false,now:0,context:1)
            d.report(keys:[first,second],control:false,now:0.1,context:1)
            d.report(keys:[first],control:false,now:0.2,context:1)
            d.report(keys:[],control:false,now:0.8,context:1)
            expectActions(a,["tap:\(second)","hold:\(first)"],"group \(first) preserves second short tap and delayed hold")
        }
        run {d,a in
            d.report(keys:[first],control:false,now:0,context:1)
            d.report(keys:[first,second],control:false,now:0.1,context:1)
            d.report(keys:[second],control:false,now:0.2,context:1)
            d.report(keys:[],control:false,now:1,context:1)
            expectActions(a,["tap:\(first)"],"group \(first) release fallback also respects hold ownership")
            d.report(keys:[second],control:false,now:2,context:1)
            d.tick(now:2.7,context:1)
            d.report(keys:[],control:false,now:2.8,context:1)
            expectActions(a,["tap:\(first)","hold:\(second)"],"group \(first) fresh press acquires ownership")
        }
    }
    run {d,a in
        d.report(keys:[1,9],control:false,now:0,context:1)
        d.tick(now:0.65,context:1)
        d.report(keys:[],control:false,now:0.8,context:1)
        expectActions(a,["hold:1","hold:9"],"task and terminal hold groups remain independent like Windows")
    }
    run {d,a in
        d.report(keys:[1],control:false,now:0,context:1)
        d.report(keys:[2],control:false,now:0.2,context:1)
        d.tick(now:0.9,context:1)
        d.report(keys:[],control:false,now:1,context:1)
        expectActions(a,["tap:1","hold:2"],"released key in same snapshot does not retain hold ownership")
    }
    for reset in [false,true] {
        run {d,a in
            d.report(keys:[3],control:false,now:0,context:1)
            d.report(keys:[3,12],control:false,now:0.1,context:1)
            if reset {d.reset()} else {d.cancel()}
            d.tick(now:0.8,context:1)
            d.report(keys:[],control:false,now:0.9,context:1)
            expect(a.isEmpty,reset ? "disconnect discards pending chord":"focus cancellation discards pending chord")
            d.report(keys:[1],control:false,now:1,context:2)
            d.report(keys:[],control:false,now:1.1,context:2)
            expectActions(a,["tap:1"],reset ? "fresh input works after reconnect reset":"fresh input works after focus cancellation")
        }
    }
    run {d,a in
        d.report(keys:[3],control:false,now:0,context:1)
        d.report(keys:[3,12],control:false,now:0.1,context:1)
        d.report(keys:[3,12],control:false,now:0.2,context:2)
        d.report(keys:[],control:false,now:0.3,context:1)
        expect(a.isEmpty,"A B A cannot revive a cancelled chord even without external cancel")
    }
    run {d,a in
        d.report(keys:[1,2],control:false,now:0,context:1)
        d.reset()
        d.tick(now:0.8,context:1)
        d.report(keys:[],control:false,now:0.9,context:1)
        expect(a.isEmpty,"disconnect clears hold ownership and suppresses stale release")
        d.report(keys:[2],control:false,now:1,context:2)
        d.tick(now:1.7,context:2)
        d.report(keys:[],control:false,now:1.8,context:2)
        expectActions(a,["hold:2"],"reconnected key obtains fresh hold ownership")
    }
    run {d,a in
        d.report(keys:[1],control:true,now:0,context:1)
        d.report(keys:[],control:false,now:0.251,context:1)
        expect(a.isEmpty,"stale encoder rotation still expires")
        d.report(keys:[2],control:true,now:1,context:1)
        d.reset()
        d.report(keys:[],control:false,now:2,context:1)
        expect(a.isEmpty,"disconnect clears held encoder packet")
    }
    let p=lightingPacket(layer:2,tasks:[1,2,3,4,5,0],terminals:[5,4,3,2,1,0],sequence:255)
    expect(p.count==32 && p[13]==255 && p[7]==1 && p[15]==5 && p[6]==2,"v11 packet offsets")
    expect(ModelSetting.parse("GPT-6 Astra 极高，第 4 项，共 6 项。") == ModelSetting(model:"GPT-6 Astra",effort:3),"accessible power announcement")
    expect(ModelSetting.parse("6 Sol High, 3 of 6.") == ModelSetting(model:"GPT-6 Sol",effort:2),"English power announcement")
    expect(ModelSetting.parse("GPT-6 Astra 深度，第 3 项，共 6 项。") == ModelSetting(model:"GPT-6 Astra",effort:2),"compact High label")
    expect(ModelSetting.parse("GPT-6 Astra Standard, 2 of 6.") == ModelSetting(model:"GPT-6 Astra",effort:1),"compact Medium label")
    for (effort,label) in ["Low","Medium","High","XHigh","Max","Ultra"].enumerated() {
        expect(ModelSetting.parse("GPT-6.1 Sol \(label)") == ModelSetting(model:"GPT-6.1 Sol",effort:effort),"GPT-6.1 Sol English effort \(label)")
        expect(ModelSetting.parse("6.1 Sol \(label), \(effort+1) of 6.") == ModelSetting(model:"GPT-6.1 Sol",effort:effort),"GPT-6.1 Sol compact effort \(label)")
    }
    for (effort,label) in ["轻度","中","高","极高","最高","Ultra"].enumerated() {
        expect(ModelSetting.parse("GPT-6.1 Sol \(label)，第 \(effort+1) 项，共 6 项。") == ModelSetting(model:"GPT-6.1 Sol",effort:effort),"GPT-6.1 Sol Chinese effort \(label)")
    }
    expect(ModelSetting.parse("GPT-6.2 Sol High")==nil && ModelSetting.parse("GPT-6 Astra unknown")==nil,"unknown model or effort is rejected")
    expect(isUltraFullAccessDialogName("是否启用 Ultra 搭配完整访问权限？"),"Chinese Ultra access dialog")
    expect(isUltraFullAccessDialogName("Enable Ultra with full access?"),"English Ultra access dialog")
    expect(!isUltraFullAccessDialogName("允许执行这个命令？") && !isUltraFullAccessDialogName("Use full access"),"unrelated approval is rejected")
    print("PASS \(checks) input/protocol checks")
}
