import Foundation

enum OptionalDockTarget: Equatable {
    case unavailable
    case resolved(String)
    case failed
}

// The optional keyboard integration owns its ordering policy. Query it without
// launching, then let Actions recheck the foreground before opening the app.
func optionalDockTarget(_ slot:Int,directory:URL,
                        run:(String,[String])->(Int32,Data) = {runProcess($0,$1,timeout:4)}) -> OptionalDockTarget {
    let script=directory.appendingPathComponent("dock-n.sh")
    let resolver=directory.appendingPathComponent("dock-launcher.py")
    guard FileManager.default.fileExists(atPath:script.path),FileManager.default.fileExists(atPath:resolver.path) else{return .unavailable}
    let (status,data)=run("/bin/bash",[script.path,"--resolve",String(slot)])
    guard status==0,let result=(try? JSONSerialization.jsonObject(with:data)) as? [String:Any],
          result["slot"] as? Int == slot,let source=result["source"] as? String,["fixed","dock"].contains(source),
          let target=result["target"] as? String,!target.isEmpty else{return .failed}
    return .resolved(target)
}

func dockLauncherSelfTest() {
    let directory=FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try! FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
    defer {try? FileManager.default.removeItem(at:directory)}
    var calls=0
    let unexpected:(String,[String])->(Int32,Data)={_,_ in calls+=1;return (0,Data())}
    precondition(optionalDockTarget(5,directory:directory,run:unexpected) == .unavailable)
    for name in ["dock-n.sh","dock-launcher.py"] {try! Data().write(to:directory.appendingPathComponent(name))}
    let target="/Applications/Dia App 'quoted'.app"
    let valid=try! JSONSerialization.data(withJSONObject:["slot":5,"source":"dock","target":target])
    let resolved=optionalDockTarget(5,directory:directory) {exe,args in
        calls+=1
        precondition(exe=="/bin/bash" && args==[directory.appendingPathComponent("dock-n.sh").path,"--resolve","5"])
        return (0,valid)
    }
    precondition(resolved == .resolved(target) && calls==1)
    precondition(optionalDockTarget(6,directory:directory,run:{_,_ in (0,valid)}) == .failed)
    precondition(optionalDockTarget(5,directory:directory,run:{_,_ in (1,valid)}) == .failed)
    precondition(optionalDockTarget(5,directory:directory,run:{_,_ in (0,Data("not JSON".utf8))}) == .failed)
    precondition(optionalDockTarget(5,directory:directory,run:{_,_ in (0,Data("{}".utf8))}) == .failed)
    print("PASS Dock launcher shared lookup, exact argv and fail-closed response checks")
}
