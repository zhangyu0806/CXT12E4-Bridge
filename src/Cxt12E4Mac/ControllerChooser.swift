import AppKit

private final class ControllerChooserPanel: NSPanel {
    var controllerKey: ((String) -> Void)?
    override var canBecomeKey: Bool { true }
    override func keyDown(with event: NSEvent) {
        let keys: [UInt16: String] = [126: "up", 125: "down", 123: "lb", 124: "rb", 36: "a", 76: "a", 53: "b"]
        if let key = keys[event.keyCode] { controllerKey?(key) } else { super.keyDown(with: event) }
    }
}

// This is a snapshot selector: selecting a row returns its original identity,
// never an index into a newer task/session inventory. It does not launch apps or
// create sessions. The owner restores the previous app and runs the action.
final class ControllerChooser: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSWindowDelegate {
    var onSelect: ((String, [String: Any]) -> Void) = { _, _ in }
    var onClose: (Bool) -> Void = { _ in }
    var isVisible: Bool { panel?.isVisible == true }
    private var panel: ControllerChooserPanel?
    private let table = NSTableView()
    private let groups = NSSegmentedControl(labels: ["Codex 任务", "tmux 会话"], trackingMode: .selectOne, target: nil, action: nil)
    private let empty = NSTextField(wrappingLabelWithString: "")
    private var records: [[[String: Any]]] = [[], []]
    private var selectedGroup = 0
    private var selectionInFlight = false

    func show(tasks: [[String: Any]], terminals: [[String: Any]?], layer: Int) {
        precondition(Thread.isMainThread)
        records = [tasks.filter { $0["id"] as? String != nil }, terminals.compactMap { $0 }.filter { $0["id"] as? String != nil }]
        selectedGroup = layer == 2 ? 1 : 0
        selectionInFlight = false
        if panel == nil { build() }
        groups.selectedSegment = selectedGroup
        reload()
        panel?.center()
        panel?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        panel?.makeFirstResponder(panel)
    }

    @discardableResult func handle(_ button: String, down: Bool) -> Bool {
        guard isVisible, panel?.isKeyWindow == true, NSApp.isActive else { return false }
        guard down, !selectionInFlight else { return true }
        switch button {
        case "a": confirm()
        case "b", "menu": close()
        case "up": move(-1)
        case "down": move(1)
        case "lb", "left": selectedGroup = 0; groups.selectedSegment = 0; reload()
        case "rb", "right": selectedGroup = 1; groups.selectedSegment = 1; reload()
        default: break
        }
        return true
    }

    func close(restoreFocus: Bool = true) {
        guard isVisible else { return }
        panel?.orderOut(nil)
        selectionInFlight = false
        onClose(restoreFocus)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool { close(); return false }
    func numberOfRows(in tableView: NSTableView) -> Int { records[selectedGroup].count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard records[selectedGroup].indices.contains(row) else { return nil }
        let record = records[selectedGroup][row], state = record["state"] as? Int ?? 1
        let cell = NSTableCellView()
        let dot = NSView(); dot.wantsLayer = true; dot.layer?.cornerRadius = 5
        dot.layer?.backgroundColor = Self.stateStyle(state).0.cgColor
        dot.translatesAutoresizingMaskIntoConstraints = false
        let title = NSTextField(labelWithString: (record[selectedGroup == 0 ? "title" : "name"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? (selectedGroup == 0 ? "未命名任务" : "未命名会话"))
        title.font = .systemFont(ofSize: 14, weight: .medium); title.lineBreakMode = .byTruncatingTail
        title.translatesAutoresizingMaskIntoConstraints = false
        let detail = NSTextField(labelWithString: Self.stateStyle(state).1)
        detail.font = .systemFont(ofSize: 11); detail.textColor = .secondaryLabelColor
        detail.translatesAutoresizingMaskIntoConstraints = false
        cell.addSubview(dot); cell.addSubview(title); cell.addSubview(detail)
        NSLayoutConstraint.activate([
            dot.widthAnchor.constraint(equalToConstant: 10), dot.heightAnchor.constraint(equalToConstant: 10),
            dot.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 12), dot.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
            title.leadingAnchor.constraint(equalTo: dot.trailingAnchor, constant: 12), title.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -12), title.topAnchor.constraint(equalTo: cell.topAnchor, constant: 9),
            detail.leadingAnchor.constraint(equalTo: title.leadingAnchor), detail.topAnchor.constraint(equalTo: title.bottomAnchor, constant: 3)
        ])
        cell.textField = title
        cell.setAccessibilityLabel("\(title.stringValue)，\(detail.stringValue)")
        return cell
    }

    private static func stateStyle(_ state: Int) -> (NSColor, String) {
        switch state {
        case 2: return (.systemBlue, "运行中")
        case 3: return (.systemGreen, "新完成")
        case 4: return (.systemYellow, "等待输入")
        case 5: return (.systemRed, "出错")
        default: return (.lightGray, "空闲 / 已查看")
        }
    }

    private func build() {
        let window = ControllerChooserPanel(contentRect: NSRect(x: 0, y: 0, width: 560, height: 430), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "Bridge · 任务与会话"
        window.isReleasedWhenClosed = false; window.delegate = self
        window.level = .floating
        window.controllerKey = { [weak self] key in _ = self?.handle(key, down: true) }
        guard let content = window.contentView else { return }
        groups.target = self; groups.action = #selector(groupChanged(_:))
        groups.translatesAutoresizingMaskIntoConstraints = false
        groups.segmentStyle = .rounded
        groups.setWidth(160, forSegment: 0); groups.setWidth(160, forSegment: 1)
        content.addSubview(groups)
        let scroll = NSScrollView(); scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder; scroll.translatesAutoresizingMaskIntoConstraints = false
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("record")); column.width = 510
        table.addTableColumn(column); table.headerView = nil; table.rowHeight = 53
        table.dataSource = self; table.delegate = self; table.allowsEmptySelection = false
        table.usesAlternatingRowBackgroundColors = true
        table.target = self; table.doubleAction = #selector(confirmFromMouse(_:))
        scroll.documentView = table
        content.addSubview(scroll)
        empty.alignment = .center; empty.textColor = .secondaryLabelColor
        empty.translatesAutoresizingMaskIntoConstraints = false; content.addSubview(empty)
        let hint = NSTextField(labelWithString: "↑ ↓ 选择    A 确认    B 返回    LB / RB 切组")
        hint.font = .systemFont(ofSize: 12); hint.textColor = .secondaryLabelColor
        hint.translatesAutoresizingMaskIntoConstraints = false; content.addSubview(hint)
        NSLayoutConstraint.activate([
            groups.centerXAnchor.constraint(equalTo: content.centerXAnchor), groups.topAnchor.constraint(equalTo: content.topAnchor, constant: 18),
            scroll.topAnchor.constraint(equalTo: groups.bottomAnchor, constant: 18), scroll.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 18), scroll.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -18), scroll.bottomAnchor.constraint(equalTo: hint.topAnchor, constant: -16),
            hint.centerXAnchor.constraint(equalTo: content.centerXAnchor), hint.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -17),
            empty.centerXAnchor.constraint(equalTo: scroll.centerXAnchor), empty.centerYAnchor.constraint(equalTo: scroll.centerYAnchor), empty.widthAnchor.constraint(equalTo: scroll.widthAnchor, constant: -48)
        ])
        panel = window
    }

    private func reload() {
        table.reloadData()
        let hasRows = !records[selectedGroup].isEmpty
        empty.isHidden = hasRows
        empty.stringValue = selectedGroup == 0 ? "暂无可打开的 Codex 任务" : "暂无已有的 tmux 会话\n这里不会自动新建 session"
        if hasRows { table.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false); table.scrollRowToVisible(0) }
    }
    private func move(_ delta: Int) {
        let count = records[selectedGroup].count
        guard count > 0 else { return }
        let selected = table.selectedRow < 0 ? 0 : table.selectedRow
        let next = (selected + delta + count) % count
        table.selectRowIndexes(IndexSet(integer: next), byExtendingSelection: false); table.scrollRowToVisible(next)
    }
    private func confirm() {
        guard !selectionInFlight, records[selectedGroup].indices.contains(table.selectedRow) else { return }
        selectionInFlight = true
        let type = selectedGroup == 0 ? "task" : "terminal", record = records[selectedGroup][table.selectedRow]
        onSelect(type, record)
    }
    @objc private func groupChanged(_ sender: NSSegmentedControl) { selectedGroup = max(0, min(1, sender.selectedSegment)); selectionInFlight = false; reload() }
    @objc private func confirmFromMouse(_ sender: Any?) { confirm() }
}
