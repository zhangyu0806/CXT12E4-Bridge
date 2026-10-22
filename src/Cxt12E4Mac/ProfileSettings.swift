import AppKit
import UniformTypeIdentifiers

private final class SettingsDocumentView: NSView {
    override var isFlipped: Bool { true }
}

// This window edits configuration only; choosing a layer never focuses an app
// or executes the action being configured.
final class ProfileSettings: NSObject, NSWindowDelegate {
    let store: ProfileStore
    var onClose: () -> Void = {}
    private var window: NSWindow?
    private let devicePicker = NSPopUpButton()
    private let layerPicker = NSPopUpButton()
    private let status = NSTextField(wrappingLabelWithString: "")
    private let mappings = NSStackView()
    private let parameterStack = NSStackView()
    private let enabled = NSButton(checkboxWithTitle: "启用 Xbox 桌面控制", target: nil, action: nil)
    private let inverted = NSButton(checkboxWithTitle: "反转两个滚动方向", target: nil, action: nil)
    private let deadZone = NSTextField()
    private let cursorSpeed = NSTextField()
    private let scrollSpeed = NSTextField()
    private let precisionScale = NSTextField()
    private let paused = NSTextView()
    private var selectedDevice = "xbox"
    private var selectedLayer = 0
    private var parameterDirty = false
    private var rendering = false
    private var mappingControls: [ObjectIdentifier: String] = [:]
    var isVisible: Bool { window?.isVisible == true }

    init(store: ProfileStore) { self.store = store; super.init() }

    func show(device: String = "xbox", layer: Int = 0) {
        if window == nil { buildWindow() }
        // Retain any unsaved parameter edit if the open window is requested again.
        if window?.isVisible != true || !parameterDirty {
            selectedDevice = LayerProfiles.devices.contains(device) ? device : "xbox"
            selectedLayer = (0...7).contains(layer) ? layer : 0
            devicePicker.selectItem(withTag: selectedDevice == "keyboard" ? 0 : 1)
            layerPicker.selectItem(withTag: selectedLayer)
            renderMappings(); renderParameters()
        }
        activate()
    }

    func activate() {
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    // Bridge panels retain the previous application's layer for editing.
    // Route their controls locally instead of sending an action to that app.
    @discardableResult func handleController(_ button: String, down: Bool) -> Bool {
        guard let window = window, window.isKeyWindow, NSApp.isActive else { return false }
        // The pointer engine owns mouse down/up, including cross-window drags.
        if ["x", "y", "r3"].contains(button) { return false }
        guard down else { return true }
        switch button {
        case "a", "b", "menu": window.performClose(nil)
        case "up", "down", "left", "right":
            Self.moveSelection(button, responder: window.firstResponder)
        default: break
        }
        return true
    }

    static func moveSelection(_ button: String, responder: NSResponder?) {
        if let picker = responder as? NSPopUpButton, picker.isEnabled, picker.numberOfItems > 0 {
            let delta = button == "up" || button == "left" ? -1 : 1
            let next = max(0, min(picker.numberOfItems - 1, picker.indexOfSelectedItem + delta))
            guard next != picker.indexOfSelectedItem else { return }
            picker.selectItem(at: next)
            if let action = picker.action { picker.sendAction(action, to: picker.target) }
        } else if let editor = responder as? NSTextView {
            switch button {
            case "up": editor.moveUp(nil)
            case "down": editor.moveDown(nil)
            case "left": editor.moveLeft(nil)
            case "right": editor.moveRight(nil)
            default: break
            }
        }
    }

    private func label(_ text: String, size: CGFloat = 13, secondary: Bool = false) -> NSTextField {
        let result = NSTextField(wrappingLabelWithString: text)
        result.font = .systemFont(ofSize: size)
        if secondary { result.textColor = .secondaryLabelColor }
        return result
    }

    private func button(_ title: String, _ action: Selector) -> NSButton {
        NSButton(title: title, target: self, action: action)
    }

    private func stack(_ views: [NSView], orientation: NSUserInterfaceLayoutOrientation = .vertical, spacing: CGFloat = 10) -> NSStackView {
        let value = NSStackView(views: views)
        value.orientation = orientation; value.alignment = orientation == .vertical ? .leading : .centerY; value.spacing = spacing
        return value
    }

    private func scrollable(_ content: NSStackView) -> NSScrollView {
        let scroll = NSScrollView(); scroll.hasVerticalScroller = true; scroll.drawsBackground = false
        scroll.borderType = .noBorder
        let document = SettingsDocumentView(); scroll.documentView = document
        content.translatesAutoresizingMaskIntoConstraints = false
        document.addSubview(content)
        document.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            document.widthAnchor.constraint(equalTo: scroll.contentView.widthAnchor),
            content.leadingAnchor.constraint(equalTo: document.leadingAnchor),
            content.trailingAnchor.constraint(equalTo: document.trailingAnchor, constant: -10),
            content.topAnchor.constraint(equalTo: document.topAnchor),
            content.bottomAnchor.constraint(equalTo: document.bottomAnchor, constant: -12)
        ])
        return scroll
    }

    private func buildWindow() {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1060, height: 780), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Bridge · 按层设置"; window.minSize = NSSize(width: 980, height: 680)
        window.isReleasedWhenClosed = false; window.delegate = self; window.center(); self.window = window
        guard let host = window.contentView else { return }

        let title = label("两种设备，同一套应用层", size: 22)
        title.font = .systemFont(ofSize: 22, weight: .semibold)
        let subtitle = label("分别设置 12E4 和 Xbox。映射修改会立即保存；这里选择的层只用于编辑，不会切换当前应用。", secondary: true)
        devicePicker.addItem(withTitle: "12E4 键盘"); devicePicker.lastItem?.tag = 0
        devicePicker.addItem(withTitle: "Xbox 手柄"); devicePicker.lastItem?.tag = 1
        devicePicker.target = self; devicePicker.action = #selector(selectionChanged(_:))
        for number in 1...8 {
            let layer = number == 8 ? 0 : number
            layerPicker.addItem(withTitle: "L\(number)  \(layerNames[layer])"); layerPicker.lastItem?.tag = layer
        }
        layerPicker.target = self; layerPicker.action = #selector(selectionChanged(_:))
        let selectors = stack([label("设备"), devicePicker, label("应用层"), layerPicker], orientation: .horizontal, spacing: 12)
        devicePicker.widthAnchor.constraint(equalToConstant: 170).isActive = true
        layerPicker.widthAnchor.constraint(equalToConstant: 270).isActive = true

        mappings.orientation = .vertical; mappings.alignment = .leading; mappings.spacing = 8
        parameterStack.orientation = .vertical; parameterStack.alignment = .leading; parameterStack.spacing = 12
        let mappingScroll = scrollable(mappings), parametersScroll = scrollable(parameterStack)
        let divider = NSBox(); divider.boxType = .separator
        let body = stack([mappingScroll, divider, parametersScroll], orientation: .horizontal, spacing: 16)
        body.alignment = .top
        body.distribution = .fill
        parametersScroll.widthAnchor.constraint(equalToConstant: 260).isActive = true
        divider.widthAnchor.constraint(equalToConstant: 1).isActive = true
        NSLayoutConstraint.activate([mappingScroll.widthAnchor.constraint(equalTo: body.widthAnchor, constant: -293), mappingScroll.heightAnchor.constraint(equalTo: body.heightAnchor), parametersScroll.heightAnchor.constraint(equalTo: body.heightAnchor), divider.heightAnchor.constraint(equalTo: body.heightAnchor)])

        status.font = .systemFont(ofSize: 12); status.textColor = .secondaryLabelColor
        status.stringValue = store.loadError ?? "配置保存在本机；12E4 与 Xbox 的映射彼此独立。"
        let fixed = label("固定操作：12E4 长按仍切换任务 / 已有 tmux 会话，K12＋其他键仍启动应用。Xbox LT 精细移动、RT 组合键。设置窗口内 X / Y 为鼠标；A 完成，B / Menu 关闭（均校验并保存参数）；方向键调整当前控件。", size: 12, secondary: true)
        let reset = button("恢复当前设备的当前层", #selector(resetLayer(_:)))
        let export = button("导出配置…", #selector(exportProfile(_:)))
        let importButton = button("导入配置…", #selector(importProfile(_:)))
        let done = button("完成", #selector(closeWindow(_:))); done.keyEquivalent = "\r"
        let spacer = NSView(); spacer.setContentHuggingPriority(.defaultLow, for: .horizontal)
        let buttons = stack([reset, importButton, export, spacer, done], orientation: .horizontal, spacing: 10)

        let outer = stack([title, subtitle, selectors, body, status, fixed, buttons], spacing: 12)
        outer.translatesAutoresizingMaskIntoConstraints = false; host.addSubview(outer)
        NSLayoutConstraint.activate([
            outer.leadingAnchor.constraint(equalTo: host.leadingAnchor, constant: 20), outer.trailingAnchor.constraint(equalTo: host.trailingAnchor, constant: -20),
            outer.topAnchor.constraint(equalTo: host.topAnchor, constant: 20), outer.bottomAnchor.constraint(equalTo: host.bottomAnchor, constant: -16)
        ])
        for view in [subtitle, selectors, body, status, fixed, buttons] { view.widthAnchor.constraint(equalTo: outer.widthAnchor).isActive = true }
        body.setContentHuggingPriority(.defaultLow, for: .vertical)
        body.setContentCompressionResistancePriority(.defaultLow, for: .vertical)
        body.heightAnchor.constraint(greaterThanOrEqualToConstant: 300).isActive = true
    }

    private func clear(_ content: NSStackView) {
        for view in content.arrangedSubviews { content.removeArrangedSubview(view); view.removeFromSuperview() }
    }

    private func renderMappings() {
        clear(mappings); mappingControls.removeAll()
        let title = label("L\(selectedLayer == 0 ? 8 : selectedLayer) · \(layerNames[selectedLayer])", size: 16)
        title.font = .systemFont(ofSize: 16, weight: .semibold); mappings.addArrangedSubview(title)
        if selectedDevice == "xbox" {
            mappings.addArrangedSubview(label("鼠标按键按下即生效；按住可拖拽。RT 组合单独设置。", size: 12, secondary: true))
        }
        for control in LayerProfiles.controls(selectedDevice) {
            let field = label(control.title)
            field.widthAnchor.constraint(equalToConstant: selectedDevice == "xbox" ? 155 : 130).isActive = true
            let picker = NSPopUpButton()
            for choice in LayerProfiles.choices(selectedDevice, control.id, selectedLayer) {
                picker.addItem(withTitle: choice.title); picker.lastItem?.representedObject = choice.id
            }
            let selected = store.action(selectedDevice, control.id, selectedLayer)
            picker.select(picker.itemArray.first { ($0.representedObject as? String) == selected })
            picker.target = self; picker.action = #selector(bindingChanged(_:))
            picker.setContentHuggingPriority(.defaultLow, for: .horizontal)
            picker.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
            picker.toolTip = "\(control.title)：\(LayerProfiles.title(selected, selectedDevice, control.id, selectedLayer))"
            mappingControls[ObjectIdentifier(picker)] = control.id
            let row = stack([field, picker], orientation: .horizontal, spacing: 10)
            row.distribution = .fill
            mappings.addArrangedSubview(row); row.widthAnchor.constraint(equalTo: mappings.widthAnchor).isActive = true
        }
    }

    private func renderParameters() {
        rendering = true; defer { rendering = false }
        clear(parameterStack)
        let title = label(selectedDevice == "xbox" ? "手柄通用参数" : "12E4 操作约定", size: 16)
        title.font = .systemFont(ofSize: 16, weight: .semibold); parameterStack.addArrangedSubview(title)
        if selectedDevice == "keyboard" {
            parameterStack.addArrangedSubview(label("短按和旋钮可按层修改。\n\n长按保留 Windows 的全局任务 / 会话顺序；K12 组合必须两键都松开才执行。\n\n按住实体键时请勿同时转动旋钮；重叠输入会取消，全部松开后恢复。\n\n切换前台应用或窗口会取消尚未完成的按压，避免动作落在别处。", secondary: true))
            return
        }
        let doc = store.document
        enabled.state = doc.xboxEnabled ? .on : .off; inverted.state = doc.invertScroll ? .on : .off
        enabled.target = self; enabled.action = #selector(parameterChanged(_:))
        inverted.target = self; inverted.action = #selector(parameterChanged(_:))
        parameterStack.addArrangedSubview(enabled)
        for (field, name, value, hint) in [
            (deadZone, "摇杆死区", doc.deadZone, "0.05–0.40；增大可抑制漂移"),
            (cursorSpeed, "鼠标速度", doc.cursorSpeed, "200–4000 点 / 秒"),
            (scrollSpeed, "滚动速度", doc.scrollSpeed, "50–2000 像素 / 秒"),
            (precisionScale, "LT 精细倍率", doc.precisionScale, "0.10–0.70；越小越慢")
        ] {
            field.stringValue = value.rounded() == value ? String(Int(value)) : String(value)
            field.target = self; field.action = #selector(parameterChanged(_:)); field.delegate = self
            let row = stack([label(name), field], orientation: .horizontal)
            field.widthAnchor.constraint(equalToConstant: 85).isActive = true
            parameterStack.addArrangedSubview(row); parameterStack.addArrangedSubview(label(hint, size: 11, secondary: true))
        }
        parameterStack.addArrangedSubview(inverted)
        parameterStack.addArrangedSubview(label("这些应用中自动暂停", size: 13))
        parameterStack.addArrangedSubview(label("每行一个应用 Bundle ID。进入游戏前，也可长按 Menu 暂停。", size: 11, secondary: true))
        paused.isRichText = false; paused.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        paused.isAutomaticQuoteSubstitutionEnabled = false; paused.isAutomaticDashSubstitutionEnabled = false
        paused.string = doc.pausedApplications.joined(separator: "\n"); paused.delegate = self
        let pausedScroll = NSScrollView(); pausedScroll.hasVerticalScroller = true; pausedScroll.borderType = .bezelBorder
        pausedScroll.documentView = paused
        paused.minSize = NSSize(width: 0, height: 100); paused.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        paused.isVerticallyResizable = true; paused.isHorizontallyResizable = false
        paused.autoresizingMask = [.width]; paused.textContainer?.widthTracksTextView = true
        parameterStack.addArrangedSubview(pausedScroll)
        pausedScroll.widthAnchor.constraint(equalTo: parameterStack.widthAnchor).isActive = true
        pausedScroll.heightAnchor.constraint(equalToConstant: 112).isActive = true
        parameterStack.addArrangedSubview(stack([button("保存手柄参数", #selector(saveParameters(_:))), button("撤销修改", #selector(revertParameters(_:)))], orientation: .horizontal, spacing: 8))
        parameterStack.addArrangedSubview(label("修改后点保存。断连、休眠和暂停会释放拖拽；恢复时须松开按钮并让摇杆回中。", size: 11, secondary: true))
        parameterDirty = false
    }

    private func report(_ message: String, error: Bool = false) {
        status.stringValue = message; status.textColor = error ? .systemRed : .secondaryLabelColor
    }

    @objc private func selectionChanged(_ sender: Any?) {
        if parameterDirty && !persistParameters() {
            devicePicker.selectItem(withTag: selectedDevice == "keyboard" ? 0 : 1); layerPicker.selectItem(withTag: selectedLayer)
            return
        }
        selectedDevice = devicePicker.selectedTag() == 0 ? "keyboard" : "xbox"
        selectedLayer = layerPicker.selectedTag(); renderMappings(); renderParameters()
    }

    @objc private func bindingChanged(_ sender: NSPopUpButton) {
        guard let control = mappingControls[ObjectIdentifier(sender)], let action = sender.selectedItem?.representedObject as? String else { return }
        do { try store.set(selectedDevice, control, selectedLayer, action); report("已保存：\(sender.selectedItem?.title ?? action)") }
        catch { renderMappings(); report("未保存，已恢复原映射：\(error.localizedDescription)", error: true) }
    }

    @objc private func parameterChanged(_ sender: Any?) {
        guard !rendering else { return }; parameterDirty = true; report("手柄参数尚未保存。点击“保存手柄参数”应用。")
    }

    private func persistParameters() -> Bool {
        guard parameterDirty else { return true }
        func numeric(_ field: NSTextField) -> Double? { Double(field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)) }
        guard let dead = numeric(deadZone), let cursor = numeric(cursorSpeed), let scroll = numeric(scrollSpeed), let precision = numeric(precisionScale) else {
            report("未保存：请为摇杆参数填写有效数字。", error: true); return false
        }
        var next = store.document
        next.xboxEnabled = enabled.state == .on; next.invertScroll = inverted.state == .on
        next.deadZone = dead; next.cursorSpeed = cursor; next.scrollSpeed = scroll; next.precisionScale = precision
        next.pausedApplications = Array(NSOrderedSet(array: paused.string.components(separatedBy: .newlines).map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty })) as? [String] ?? []
        do { try store.replace(next); parameterDirty = false; report("手柄参数已保存并应用到全部应用层。"); return true }
        catch { report("未保存：\(error.localizedDescription)", error: true); return false }
    }

    @objc private func saveParameters(_ sender: Any?) { _ = persistParameters() }

    @objc private func revertParameters(_ sender: Any?) { renderParameters(); report("已撤销尚未保存的手柄参数修改。") }

    @objc private func resetLayer(_ sender: Any?) {
        do { try store.reset(selectedDevice, selectedLayer); renderMappings(); report("已恢复 \(selectedDevice == "keyboard" ? "12E4" : "Xbox") L\(selectedLayer == 0 ? 8 : selectedLayer) 默认映射；其他层保持原配置。") }
        catch { report("恢复失败：\(error.localizedDescription)", error: true) }
    }

    @objc private func importProfile(_ sender: Any?) {
        guard let window = window, persistParameters() else { return }
        let panel = NSOpenPanel(); panel.allowedContentTypes = [.json]; panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false; panel.prompt = "导入全部配置"
        panel.message = "导入会替换两种设备全部层和手柄参数；当前配置会自动备份。"
        panel.beginSheetModal(for: window) { [weak self] response in
            guard let self = self, response == .OK, let url = panel.url else { return }
            do {
                let next = try JSONDecoder().decode(BridgeProfileDocument.self, from: Data(contentsOf: url))
                try self.store.replace(next); self.renderMappings(); self.renderParameters()
                self.report("全部配置已导入；导入前配置已备份。")
            } catch { self.report("未导入，原配置保持不变：\(error.localizedDescription)", error: true) }
        }
    }

    @objc private func exportProfile(_ sender: Any?) {
        guard let window = window, persistParameters() else { return }
        let panel = NSSavePanel(); panel.allowedContentTypes = [.json]; panel.nameFieldStringValue = "CXT12E4-Bridge-profiles.json"
        panel.beginSheetModal(for: window) { [weak self] response in
            guard let self = self, response == .OK, let url = panel.url else { return }
            do {
                let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
                try encoder.encode(self.store.document).write(to: url, options: .atomic); self.report("配置已导出：\(url.lastPathComponent)")
            } catch { self.report("导出失败：\(error.localizedDescription)", error: true) }
        }
    }

    @objc private func closeWindow(_ sender: Any?) { window?.performClose(nil) }
    func windowShouldClose(_ sender: NSWindow) -> Bool { persistParameters() }
    func windowWillClose(_ notification: Notification) { onClose() }
}

extension ProfileSettings: NSTextFieldDelegate, NSTextViewDelegate {
    func controlTextDidChange(_ notification: Notification) { parameterChanged(nil) }
    func textDidChange(_ notification: Notification) { parameterChanged(nil) }
}
