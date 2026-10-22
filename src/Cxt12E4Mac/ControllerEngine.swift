import AppKit
import ApplicationServices

struct ControllerMotion {
    // Radial dead zone avoids the diagonal speed increase of independent axes.
    static func vector(x: Double, y: Double, deadZone: Double, exponent: Double = 1.7) -> CGVector {
        guard x.isFinite, y.isFinite else { return .zero }
        let length = hypot(x, y), dead = min(0.9, max(0, deadZone))
        guard length > dead else { return .zero }
        let magnitude = pow((min(1, length) - dead) / (1 - dead), max(0.1, exponent))
        return CGVector(dx: x / length * magnitude, dy: y / length * magnitude)
    }

    // Quartz coordinates have their origin at the primary display's top left.
    // CGDisplayBounds, unlike NSScreen.frame, already uses the same coordinates.
    static func constrained(_ point: CGPoint, displays: [CGRect]) -> CGPoint {
        guard !displays.isEmpty else { return point }
        if displays.contains(where: { $0.contains(point) }) { return point }
        return displays.map { rect in
            CGPoint(x: min(max(point.x, rect.minX), rect.maxX - 1),
                    y: min(max(point.y, rect.minY), rect.maxY - 1))
        }.min(by: { hypot($0.x - point.x, $0.y - point.y) < hypot($1.x - point.x, $1.y - point.y) }) ?? point
    }
}

enum ControllerMouseOutput {
    case move(CGPoint, CGVector, String?)
    case button(String, Bool, CGPoint, Int)
    case scroll(Int32, Int32)
}

// All mutation belongs to the main thread. Output, clock, permission and geometry
// are injectable so the complete engine can be tested without moving the mouse.
final class ControllerEngine {
    var deadZone = 0.18
    var cursorSpeed = 1100.0 // Quartz points per second at full deflection.
    var scrollSpeed = 850.0 // Pixel scrolling per second at full deflection.
    var precisionScale = 0.20
    var invertScroll = false
    private(set) var enabled = false
    private(set) var awaitingNeutral = true
    private var axes = (lx: 0.0, ly: 0.0, rx: 0.0, ry: 0.0)
    private var precision = false
    private var held: Set<String> = []
    private var clickCounts: [String: Int] = [:]
    private var clicks: [String: (time: Double, point: CGPoint, count: Int)] = [:]
    private var scrollRemainder = CGVector.zero
    private var timer: Timer?
    private var lastTick: Double?
    private var sleepReasons: Set<String> = []
    private var systemSuspended: Bool { !sleepReasons.isEmpty }
    private var workspaceObservers: [NSObjectProtocol] = []
    private let emit: (ControllerMouseOutput) -> Void
    private let location: () -> CGPoint
    private let displays: () -> [CGRect]
    private let permitted: () -> Bool
    private let clock: () -> Double
    private let doubleClickInterval: Double

    init(startTimer: Bool = true,
         output: ((ControllerMouseOutput) -> Void)? = nil,
         location: @escaping () -> CGPoint = { CGEvent(source: nil)?.location ?? .zero },
         displays: @escaping () -> [CGRect] = ControllerEngine.activeDisplays,
         permitted: @escaping () -> Bool = { AXIsProcessTrusted() },
         clock: @escaping () -> Double = { ProcessInfo.processInfo.systemUptime },
         doubleClickInterval: Double = NSEvent.doubleClickInterval) {
        self.emit = output ?? Self.post
        self.location = location; self.displays = displays; self.permitted = permitted
        self.clock = clock; self.doubleClickInterval = doubleClickInterval
        if startTimer {
            let timer = Timer(timeInterval: 1.0 / 60.0, repeats: true) { [weak self] _ in
                guard let self = self else { return }
                let now = self.clock(), delta = self.lastTick.map { now - $0 } ?? 1.0 / 60.0
                self.lastTick = now
                self.tick(delta: delta)
            }
            RunLoop.main.add(timer, forMode: .common); self.timer = timer
            let center = NSWorkspace.shared.notificationCenter
            let sleepEvents: [(Notification.Name, String)] = [(NSWorkspace.willSleepNotification, "system"), (NSWorkspace.screensDidSleepNotification, "display"), (NSWorkspace.sessionDidResignActiveNotification, "session")]
            let wakeEvents: [(Notification.Name, String)] = [(NSWorkspace.didWakeNotification, "system"), (NSWorkspace.screensDidWakeNotification, "display"), (NSWorkspace.sessionDidBecomeActiveNotification, "session")]
            for (name, reason) in sleepEvents {
                workspaceObservers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                    self?.sleepReasons.insert(reason); self?.suspend()
                })
            }
            for (name, reason) in wakeEvents {
                workspaceObservers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                    self?.sleepReasons.remove(reason); self?.suspend()
                })
            }
        }
    }

    func setAxes(lx: Double, ly: Double, rx: Double, ry: Double, precision: Bool) {
        guard [lx, ly, rx, ry].allSatisfy({ $0.isFinite }) else { suspend(); return }
        axes = (max(-1, min(1, lx)), max(-1, min(1, ly)), max(-1, min(1, rx)), max(-1, min(1, ry)))
        self.precision = precision
        if enabled && !systemSuspended && awaitingNeutral && hypot(lx, ly) <= deadZone && hypot(rx, ry) <= deadZone { awaitingNeutral = false }
    }

    func setEnabled(_ enabled: Bool) {
        guard self.enabled != enabled else { return }
        suspend()
        self.enabled = enabled
    }

    func mouseButton(_ name: String, down: Bool) {
        guard ["left", "right", "middle"].contains(name) else { return }
        if !down {
            // A release must survive a foreground switch, loss of permission or
            // pause. Never gate it on the context used to start the drag.
            guard held.remove(name) != nil else { return }
            emit(.button(name, false, location(), clickCounts.removeValue(forKey: name) ?? 1))
            return
        }
        guard enabled, !systemSuspended, !awaitingNeutral, permitted(), !held.contains(name) else { return }
        let now = clock(), point = location()
        var count = 1
        if let previous = clicks[name], now - previous.time <= doubleClickInterval,
           hypot(point.x - previous.point.x, point.y - previous.point.y) <= 5 {
            count = previous.count >= 3 ? 1 : previous.count + 1
        }
        clicks[name] = (now, point, count); clickCounts[name] = count; held.insert(name)
        emit(.button(name, true, point, count))
    }

    func suspend() {
        for name in held.sorted() { emit(.button(name, false, location(), clickCounts[name] ?? 1)) }
        held.removeAll(); clickCounts.removeAll(); clicks.removeAll()
        axes = (0, 0, 0, 0); precision = false; scrollRemainder = .zero
        awaitingNeutral = true; lastTick = nil
    }

    func stop() {
        setEnabled(false); suspend()
        timer?.invalidate(); timer = nil
        workspaceObservers.forEach { NSWorkspace.shared.notificationCenter.removeObserver($0) }; workspaceObservers.removeAll()
    }

    func tick(delta: Double) {
        guard enabled, !systemSuspended, !awaitingNeutral else { return }
        guard permitted() else { suspend(); return }
        // Never replay a backlog after a stalled run loop, sleep or debugger.
        guard delta.isFinite, delta > 0 else { return }
        let dt = min(delta, 1.0 / 30.0), scale = precision ? min(1, max(0.02, precisionScale)) : 1
        let cursor = ControllerMotion.vector(x: axes.lx, y: axes.ly, deadZone: deadZone)
        if cursor != .zero {
            let old = location(), speed = max(0, min(5000, cursorSpeed)) * scale * dt
            let next = ControllerMotion.constrained(CGPoint(x: old.x + cursor.dx * speed, y: old.y - cursor.dy * speed), displays: displays())
            if next != old {
                let drag = held.contains("left") ? "left" : held.contains("right") ? "right" : held.contains("middle") ? "middle" : nil
                emit(.move(next, CGVector(dx: next.x - old.x, dy: next.y - old.y), drag))
            }
        }
        let scroll = ControllerMotion.vector(x: axes.rx, y: axes.ry, deadZone: deadZone, exponent: 1.5)
        if scroll == .zero { scrollRemainder = .zero; return }
        let scrollFactor = max(0, min(5000, scrollSpeed)) * scale * dt * (invertScroll ? -1 : 1)
        scrollRemainder.dx += -scroll.dx * scrollFactor
        scrollRemainder.dy += scroll.dy * scrollFactor
        let horizontal = Int32(scrollRemainder.dx.rounded(.towardZero)), vertical = Int32(scrollRemainder.dy.rounded(.towardZero))
        scrollRemainder.dx -= Double(horizontal); scrollRemainder.dy -= Double(vertical)
        if horizontal != 0 || vertical != 0 { emit(.scroll(vertical, horizontal)) }
    }

    static func activeDisplays() -> [CGRect] {
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else { return [CGDisplayBounds(CGMainDisplayID())] }
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetActiveDisplayList(count, &ids, &count) == .success else { return [CGDisplayBounds(CGMainDisplayID())] }
        return ids.prefix(Int(count)).map { CGDisplayBounds($0) }
    }

    private static func post(_ output: ControllerMouseOutput) {
        let source = CGEventSource(stateID: .privateState)
        let event: CGEvent?
        switch output {
        case let .move(point, delta, drag):
            let type: CGEventType = drag == "left" ? .leftMouseDragged : drag == "right" ? .rightMouseDragged : drag == "middle" ? .otherMouseDragged : .mouseMoved
            let button: CGMouseButton = drag == "right" ? .right : drag == "middle" ? .center : .left
            event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: button)
            event?.setIntegerValueField(.mouseEventDeltaX, value: Int64(delta.dx.rounded()))
            event?.setIntegerValueField(.mouseEventDeltaY, value: Int64(delta.dy.rounded()))
        case let .button(name, down, point, count):
            let type: CGEventType = name == "left" ? (down ? .leftMouseDown : .leftMouseUp) : name == "right" ? (down ? .rightMouseDown : .rightMouseUp) : (down ? .otherMouseDown : .otherMouseUp)
            let button: CGMouseButton = name == "right" ? .right : name == "middle" ? .center : .left
            event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: button)
            event?.setIntegerValueField(.mouseEventClickState, value: Int64(count))
        case let .scroll(vertical, horizontal):
            event = CGEvent(scrollWheelEvent2Source: source, units: .pixel, wheelCount: 2, wheel1: vertical, wheel2: horizontal, wheel3: 0)
            event?.setIntegerValueField(.scrollWheelEventIsContinuous, value: 1)
        }
        event?.flags = []
        event?.post(tap: .cghidEventTap)
    }
}

func controllerEngineSelfTest() {
    var checks = 0
    func expect(_ condition: Bool, _ name: String) { checks += 1; if !condition { fatalError("Controller test: \(name)") } }
    expect(ControllerMotion.vector(x: 0.1, y: -0.1, deadZone: 0.18) == .zero, "radial dead zone")
    let diagonal = ControllerMotion.vector(x: 1, y: 1, deadZone: 0.18)
    expect(abs(hypot(diagonal.dx, diagonal.dy) - 1) < 0.00001, "diagonal speed normalized")
    expect(ControllerMotion.vector(x: .nan, y: 1, deadZone: 0.18) == .zero, "invalid axis rejected")
    let screens = [CGRect(x: 0, y: 0, width: 1920, height: 1080), CGRect(x: -1280, y: -200, width: 1280, height: 1024)]
    expect(ControllerMotion.constrained(CGPoint(x: -400, y: -100), displays: screens) == CGPoint(x: -400, y: -100), "negative monitor coordinates")
    expect(ControllerMotion.constrained(CGPoint(x: 2000, y: 400), displays: screens) == CGPoint(x: 1919, y: 400), "outer edge clipped")
    var outputs: [ControllerMouseOutput] = [], pointer = CGPoint(x: 100, y: 100), time = 0.0, allowed = true
    let engine = ControllerEngine(startTimer: false, output: { output in outputs.append(output); if case let .move(point, _, _) = output { pointer = point } }, location: { pointer }, displays: { screens }, permitted: { allowed }, clock: { time }, doubleClickInterval: 0.5)
    engine.setEnabled(true)
    engine.setAxes(lx: 1, ly: 0, rx: 0, ry: 0, precision: false); engine.tick(delta: 1.0 / 60.0)
    expect(outputs.isEmpty && engine.awaitingNeutral, "reconnect requires neutral")
    engine.setAxes(lx: 0, ly: 0, rx: 0, ry: 0, precision: false)
    engine.mouseButton("left", down: true)
    engine.setAxes(lx: 0, ly: 1, rx: 0, ry: 0, precision: false); engine.tick(delta: 1.0 / 60.0)
    if case let .move(_, delta, drag) = outputs.last { expect(delta.dy < 0 && drag == "left", "up axis becomes upward drag") } else { expect(false, "drag output exists") }
    allowed = false; engine.mouseButton("left", down: false)
    if case let .button(name, down, _, _) = outputs.last { expect(name == "left" && !down, "release bypasses lost permission") } else { expect(false, "release output exists") }
    allowed = true; outputs.removeAll(); engine.suspend()
    engine.setAxes(lx: 0, ly: 0, rx: 0, ry: 0, precision: false)
    engine.mouseButton("left", down: true); engine.mouseButton("left", down: false); time = 0.1
    engine.mouseButton("left", down: true)
    if case let .button(_, _, _, count) = outputs.last { expect(count == 2, "double click count") }
    engine.setEnabled(false)
    if case let .button(_, down, _, _) = outputs.last { expect(!down, "pause releases held click") }
    let disabledCount = outputs.count; engine.tick(delta: 1); engine.mouseButton("right", down: true)
    expect(outputs.count == disabledCount, "disabled controller emits nothing")
    engine.setEnabled(true); engine.setAxes(lx: 0, ly: 0, rx: 0, ry: 0, precision: false)
    outputs.removeAll(); let before = pointer
    engine.setAxes(lx: 1, ly: 0, rx: 0, ry: 0, precision: true); engine.tick(delta: 10)
    expect(pointer.x - before.x < 10, "precision speed and stalled timer bounded")
    engine.setAxes(lx: 0, ly: 0, rx: 0, ry: 1, precision: false); engine.tick(delta: 1.0 / 60.0)
    if case let .scroll(vertical, horizontal) = outputs.last { expect(vertical > 0 && horizontal == 0, "up stick scrolls upward") } else { expect(false, "scroll output exists") }
    outputs.removeAll(); engine.suspend(); engine.tick(delta: 1)
    expect(outputs.isEmpty, "suspend clears stale movement")
    engine.stop()
    print("PASS \(checks) controller motion/lifecycle checks")
}
