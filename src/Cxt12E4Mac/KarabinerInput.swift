import Foundation
import Darwin

// Karabiner already owns the privileged HID capture. Its native datagram API preserves
// down/up ordering without spawning a shell or adding a global keyboard hook.
final class KarabinerInput {
    let path=support.appendingPathComponent("input.sock").path
    var fd:Int32 = -1
    var source:DispatchSourceRead?
    var callback:(Int,Bool)->Void={_,_ in}
    var count=0
    var ready:Bool {fd>=0}
    func start() {
        guard path.utf8.count<104 else{log("input socket path too long");return}
        fd=Darwin.socket(AF_UNIX,SOCK_DGRAM,0)
        guard fd>=0 else{return}
        unlink(path)
        var address=sockaddr_un();address.sun_family=sa_family_t(AF_UNIX);address.sun_len=UInt8(MemoryLayout<sockaddr_un>.size)
        withUnsafeMutablePointer(to:&address.sun_path) { ptr in
            ptr.withMemoryRebound(to:CChar.self,capacity:104) {dest in _=path.withCString{strlcpy(dest,$0,104)}}
        }
        let result=withUnsafePointer(to:&address) {$0.withMemoryRebound(to:sockaddr.self,capacity:1){Darwin.bind(fd,$0,socklen_t(MemoryLayout<sockaddr_un>.size))}}
        guard result==0 else{Darwin.close(fd);fd = -1;log("input socket bind failed");return}
        chmod(path,0o600);_ = fcntl(fd,F_SETFL,O_NONBLOCK)
        let stream=DispatchSource.makeReadSource(fileDescriptor:fd,queue:.main)
        stream.setEventHandler{[weak self] in self?.receive()};stream.resume();source=stream
        log("Karabiner input receiver ready")
    }
    func receive() {
        var bytes=[UInt8](repeating:0,count:1024)
        while true {
            let n=Darwin.recv(fd,&bytes,bytes.count,0);if n<=0 {break}
            guard let value=try? JSONSerialization.jsonObject(with:Data(bytes.prefix(n))) as? [String:Any],value["cxt"] as? Int==1,
                  let key=value["key"] as? Int,(0...12).contains(key),let down=value["down"] as? Bool else{continue}
            count+=1;callback(key,down)
        }
    }
    func stop() {source?.cancel();source=nil;if fd>=0 {Darwin.close(fd);fd = -1};unlink(path)}
}
