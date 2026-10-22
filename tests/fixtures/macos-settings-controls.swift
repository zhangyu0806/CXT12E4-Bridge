import AppKit

// Test AppKit's real control behavior without showing windows, activating the
// app, or sending synthetic keyboard/mouse events to the user's desktop.
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)

final class SettingsActionProbe: NSObject {
    var calls = 0
    @objc func changed(_ sender: Any?) { calls += 1 }
}

var checks = 0
func check(_ condition: Bool, _ label: String) {
    checks += 1
    if !condition { fatalError(label) }
}

let probe = SettingsActionProbe()
let picker = NSPopUpButton()
picker.addItems(withTitles: ["one", "two", "three"])
picker.target = probe
picker.action = #selector(SettingsActionProbe.changed(_:))

ProfileSettings.moveSelection("down", responder: picker)
check(picker.indexOfSelectedItem == 1 && probe.calls == 1, "down selects and saves exactly once")
ProfileSettings.moveSelection("right", responder: picker)
check(picker.indexOfSelectedItem == 2 && probe.calls == 2, "right selects and saves")
ProfileSettings.moveSelection("down", responder: picker)
check(picker.indexOfSelectedItem == 2 && probe.calls == 2, "end does not resave")
ProfileSettings.moveSelection("up", responder: picker)
check(picker.indexOfSelectedItem == 1 && probe.calls == 3, "up selects and saves")
ProfileSettings.moveSelection("left", responder: picker)
check(picker.indexOfSelectedItem == 0 && probe.calls == 4, "left selects and saves")
ProfileSettings.moveSelection("left", responder: picker)
check(picker.indexOfSelectedItem == 0 && probe.calls == 4, "start does not resave")
picker.isEnabled = false
ProfileSettings.moveSelection("right", responder: picker)
check(picker.indexOfSelectedItem == 0 && probe.calls == 4, "disabled popup stays untouched")

let editor = NSTextView(frame: NSRect(x: 0, y: 0, width: 300, height: 100))
editor.string = "abcd"
editor.setSelectedRange(NSRange(location: 2, length: 0))
ProfileSettings.moveSelection("left", responder: editor)
check(editor.selectedRange() == NSRange(location: 1, length: 0), "text left moves insertion point")
ProfileSettings.moveSelection("right", responder: editor)
check(editor.selectedRange() == NSRange(location: 2, length: 0), "text right moves insertion point")
ProfileSettings.moveSelection("up", responder: nil)
ProfileSettings.moveSelection("down", responder: NSButton())
check(editor.string == "abcd" && probe.calls == 4, "unsupported responders do not change content")

let chooser = ControllerChooser()
check(!chooser.handle("a", down: true), "closed chooser cannot confirm")
let profileURL = FileManager.default.temporaryDirectory.appendingPathComponent("unused-cxt-profile-test-" + UUID().uuidString)
let settings = ProfileSettings(store: ProfileStore(url: profileURL))
check(!settings.handleController("a", down: true), "closed settings cannot consume input")

print("PASS \(checks) AppKit settings/chooser control checks")
