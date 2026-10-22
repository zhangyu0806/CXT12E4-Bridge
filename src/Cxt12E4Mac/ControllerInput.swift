import AppKit
import GameController

// Pure transition state shared by callback and polling paths. Events release
// regular buttons before modifiers and press modifiers before regular buttons.
struct ControllerInputState {
    private(set) var awaitingNeutral = true
    private(set) var pressed: Set<String> = []
    static let buttonOrder = ["lt", "rt", "a", "b", "x", "y", "up", "down", "left", "right", "lb", "rb", "l3", "r3", "menu"]

    mutating func update(buttons: Set<String>, lx: Double, ly: Double, rx: Double, ry: Double, lt: Double, rt: Double, neutralDeadZone: Double = 0.18) -> [(String, Bool)]? {
        var next = buttons
        if lt >= (pressed.contains("lt") ? 0.40 : 0.55) { next.insert("lt") }
        if rt >= (pressed.contains("rt") ? 0.40 : 0.55) { next.insert("rt") }
        if awaitingNeutral {
            let deadZone = min(0.4, max(0.05, neutralDeadZone))
            guard next.isEmpty, hypot(lx, ly) <= deadZone, hypot(rx, ry) <= deadZone, lt < 0.10, rt < 0.10 else { return nil }
            awaitingNeutral = false
        }
        let releases = Self.buttonOrder.reversed().filter { pressed.contains($0) && !next.contains($0) }.map { ($0, false) }
        let presses = Self.buttonOrder.filter { next.contains($0) && !pressed.contains($0) }.map { ($0, true) }
        pressed = next
        return releases + presses
    }

    mutating func reset() -> [(String, Bool)] {
        let releases = Self.buttonOrder.reversed().filter { pressed.contains($0) }.map { ($0, false) }
        pressed.removeAll(); awaitingNeutral = true
        return releases
    }
}

// The controller is an independent input source. It does not reuse the 12E4
// firmware protocol or replace the system's Xbox, View or Share gestures.
final class ControllerInput {
    var onButton: ((String, Bool) -> Void)?
    var onAxes: ((Double, Double, Double, Double, Double, Double, Bool) -> Void)?
    var onConnection: ((Bool, String) -> Void)?
    var neutralDeadZone = 0.18
    var awaitingNeutral: Bool { state.awaitingNeutral }
    private var controller: GCController?
    private var observers: [NSObjectProtocol] = []
    private var workspaceObservers: [NSObjectProtocol] = []
    private var sampleTimer: Timer?
    private var state = ControllerInputState()
    private var started = false
    private var sleepReasons: Set<String> = []
    private var sleeping: Bool { !sleepReasons.isEmpty }

    // All callbacks run on the main queue, including disconnect releases.
    func start() {
        precondition(Thread.isMainThread)
        guard !started else { return }
        started = true
        GCController.shouldMonitorBackgroundEvents = true
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: .GCControllerDidConnect, object: nil, queue: .main) { [weak self] _ in self?.selectController() })
        observers.append(center.addObserver(forName: .GCControllerDidDisconnect, object: nil, queue: .main) { [weak self] note in
            guard let self = self else { return }
            if let disconnected = note.object as? GCController, disconnected === self.controller { self.detach() }
            self.selectController()
        })
        let workspace = NSWorkspace.shared.notificationCenter
        let sleepEvents: [(Notification.Name, String)] = [(NSWorkspace.willSleepNotification, "system"), (NSWorkspace.screensDidSleepNotification, "display"), (NSWorkspace.sessionDidResignActiveNotification, "session")]
        let wakeEvents: [(Notification.Name, String)] = [(NSWorkspace.didWakeNotification, "system"), (NSWorkspace.screensDidWakeNotification, "display"), (NSWorkspace.sessionDidBecomeActiveNotification, "session")]
        for (name, reason) in sleepEvents {
            workspaceObservers.append(workspace.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                guard let self = self else { return }; self.sleepReasons.insert(reason); self.detach()
            })
        }
        for (name, reason) in wakeEvents {
            workspaceObservers.append(workspace.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                guard let self = self else { return }; self.sleepReasons.remove(reason); self.selectController()
            })
        }
        // Value-change callbacks capture quick taps; sampling maintains analog
        // state and detects a neutral controller after connection or wake.
        let timer = Timer(timeInterval: 1.0 / 60.0, repeats: true) { [weak self] _ in self?.sample() }
        RunLoop.main.add(timer, forMode: .common)
        sampleTimer = timer
        selectController()
    }

    func stop() {
        precondition(Thread.isMainThread)
        started = false
        sampleTimer?.invalidate(); sampleTimer = nil
        observers.forEach { NotificationCenter.default.removeObserver($0) }; observers.removeAll()
        workspaceObservers.forEach { NSWorkspace.shared.notificationCenter.removeObserver($0) }; workspaceObservers.removeAll()
        detach()
    }

    private func selectController() {
        guard started, !sleeping, controller == nil else { return }
        // A connected game console controller belonging to somebody else should
        // not suddenly become a desktop pointer when Bridge starts.
        guard let selected = GCController.controllers().first(where: { $0.extendedGamepad is GCXboxGamepad }) else { return }
        controller = selected
        selected.handlerQueue = .main
        _ = state.reset()
        selected.extendedGamepad?.valueChangedHandler = { [weak self, weak selected] _, _ in
            guard let self = self, let selected = selected, self.controller === selected else { return }
            self.sample()
        }
        onConnection?(true, selected.vendorName ?? "Xbox Wireless Controller")
        sample()
    }

    private func detach() {
        let old = controller
        old?.extendedGamepad?.valueChangedHandler = nil
        controller = nil
        let releases = state.reset()
        onAxes?(0, 0, 0, 0, 0, 0, false)
        // Tell the action router to cancel pending taps before sending releases.
        // An unplugged Menu button must not open a panel on its synthetic key-up.
        if let old = old { onConnection?(false, old.vendorName ?? "Xbox Wireless Controller") }
        // Release regular controls before modifiers so a chord cannot turn into
        // an unmodified release action when the device disappears.
        for (key, down) in releases { onButton?(key, down) }
    }

    private func sample() {
        guard started, !sleeping, let pad = controller?.extendedGamepad else { return }
        let lx = Double(pad.leftThumbstick.xAxis.value), ly = Double(pad.leftThumbstick.yAxis.value)
        let rx = Double(pad.rightThumbstick.xAxis.value), ry = Double(pad.rightThumbstick.yAxis.value)
        let lt = Double(pad.leftTrigger.value), rt = Double(pad.rightTrigger.value)
        let buttons: [(String, GCControllerButtonInput?)] = [
            ("a", pad.buttonA), ("b", pad.buttonB), ("x", pad.buttonX), ("y", pad.buttonY),
            ("up", pad.dpad.up), ("down", pad.dpad.down), ("left", pad.dpad.left), ("right", pad.dpad.right),
            ("lb", pad.leftShoulder), ("rb", pad.rightShoulder),
            ("l3", pad.leftThumbstickButton), ("r3", pad.rightThumbstickButton), ("menu", pad.buttonMenu)
        ]
        let next = Set(buttons.filter { $0.1?.isPressed == true }.map { $0.0 })
        // Trigger hysteresis prevents a light squeeze from rapidly toggling a
        // modifier as its analog value fluctuates near the threshold.
        guard let changes = state.update(buttons: next, lx: lx, ly: ly, rx: rx, ry: ry, lt: lt, rt: rt, neutralDeadZone: neutralDeadZone) else { return }
        onAxes?(lx, ly, rx, ry, lt, rt, state.pressed.isEmpty)
        for (key, down) in changes { onButton?(key, down) }
    }
}

func controllerInputSelfTest() {
    var state = ControllerInputState(), checks = 0
    func expect(_ condition: Bool, _ name: String) { checks += 1; if !condition { fatalError("Controller input test: \(name)") } }
    func update(_ buttons: Set<String> = [], lx: Double = 0, rt: Double = 0) -> [String]? {
        state.update(buttons: buttons, lx: lx, ly: 0, rx: 0, ry: 0, lt: 0, rt: rt)?.map { "\($0.0):\($0.1)" }
    }
    expect(update(["a"]) == nil, "held button blocks startup")
    expect(update(lx: 0.8) == nil, "deflected stick blocks startup")
    expect(update(rt: 0.2) == nil, "partly held trigger blocks startup")
    expect(update() == [], "neutral arms input")
    expect(update(["a"], rt: 0.6) == ["rt:true", "a:true"], "modifier presses before button")
    expect(update(["a"], rt: 0.45) == [], "trigger hysteresis holds modifier")
    expect(update(["a"], rt: 0.39) == ["rt:false"], "trigger releases below lower threshold")
    expect(update(["a"], rt: 0.45) == [], "trigger does not bounce back on")
    expect(update(["a"], rt: 0.6) == ["rt:true"], "trigger requires upper threshold")
    expect(update() == ["a:false", "rt:false"], "button releases before modifier")
    expect(update() == [], "polling does not duplicate edge events")
    _ = update(["menu"], rt: 0.8)
    expect(state.reset().map { "\($0.0):\($0.1)" } == ["menu:false", "rt:false"], "disconnect releases all controls")
    expect(state.awaitingNeutral && state.pressed.isEmpty, "disconnect resets neutral gate")
    expect(update(["menu"]) == nil, "held button cannot revive after reconnect")
    print("PASS \(checks) controller input-state checks")
}
