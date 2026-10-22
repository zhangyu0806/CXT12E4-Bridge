import AppKit
import CoreText

enum LayerBadge {
    // Indexed by firmware layer: logical 0 is displayed as L8, as on Windows.
    static let colors:[UInt32] = [0x7088B8,0xC000C8,0xFF1464,0xFF6000,0x8000FF,0xFFDC00,0x00C8FF,0x00FF00]
    static let size = NSSize(width:18,height:18)
    static let images:[NSImage] = (0..<8).map { layer in
        let image=NSImage(size:size)
        for pixels in [18,36,54] {
            let rep=NSBitmapImageRep(cgImage:bitmap(layer:layer,pixels:pixels))
            rep.size=size;image.addRepresentation(rep)
        }
        // Template images lose the layer color when the menu bar applies tint.
        image.isTemplate=false
        image.accessibilityDescription="CXT 12E4 L\(number(layer)) \(layerNames[layer])"
        return image
    }

    static func number(_ layer:Int)->Int {layer==0 ? 8:layer}

    static func bitmap(layer:Int,pixels:Int)->CGImage {
        let rgb=colors[layer]
        let components=[CGFloat((rgb>>16)&255),CGFloat((rgb>>8)&255),CGFloat(rgb&255)].map{$0/255}
        let linear=components.map{$0<=0.03928 ? $0/12.92:pow(($0+0.055)/1.055,2.4)}
        let luminance=0.2126*linear[0]+0.7152*linear[1]+0.0722*linear[2]
        let foreground:CGFloat=luminance>0.179 ? 0:1
        let space=CGColorSpace(name:CGColorSpace.sRGB)!
        let context=CGContext(data:nil,width:pixels,height:pixels,bitsPerComponent:8,bytesPerRow:0,
                              space:space,bitmapInfo:CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.scaleBy(x:CGFloat(pixels)/32,y:CGFloat(pixels)/32)
        context.setFillColor(CGColor(colorSpace:space,components:components+[1])!)
        context.fill(CGRect(x:0,y:0,width:32,height:32))
        let text=NSAttributedString(string:String(number(layer)),attributes:[
            NSAttributedString.Key(kCTFontAttributeName as String):CTFontCreateWithName("Arial-BoldMT" as CFString,25,nil),
            NSAttributedString.Key(kCTForegroundColorAttributeName as String):CGColor(colorSpace:space,components:[foreground,foreground,foreground,1])!
        ])
        let line=CTLineCreateWithAttributedString(text)
        let bounds=CTLineGetBoundsWithOptions(line,.useGlyphPathBounds)
        context.textPosition=CGPoint(x:(32-bounds.width)/2-bounds.minX,y:(32-bounds.height)/2-bounds.minY)
        CTLineDraw(line,context)
        return context.makeImage()!
    }
}
