import AppKit

// Manages only Bridge's own question hints. No inspection or control of Codex UI.
final class QuestionManager: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSWindowDelegate {
    private var panel: NSPanel?
    private let table = NSTableView()
    private let detail = NSTextView()
    private let status = NSTextField(wrappingLabelWithString: "")
    private let action = NSButton(title: "忽略此提问", target: nil, action: nil)
    private let refresh = NSButton(title: "刷新", target: nil, action: nil)
    private let queue = DispatchQueue(label: "cxt.questions", qos: .userInitiated)
    private var rows: [[String: Any]] = []
    private var busy = false
    var onClose: () -> Void = {}
    var isVisible: Bool { panel?.isVisible == true }

    func show() {
        if panel == nil { build() }
        NSApp.activate(ignoringOtherApps: true); panel?.makeKeyAndOrderFront(nil)
        load()
    }
    func close() { panel?.close() }
    func windowWillClose(_ notification: Notification) { onClose() }
    func numberOfRows(in tableView: NSTableView) -> Int { rows.count }
    func tableViewSelectionDidChange(_ notification: Notification) { selectionChanged() }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard rows.indices.contains(row) else { return nil }
        let record = rows[row], column = tableColumn?.identifier.rawValue
        let text = column == "task" ? "键 \(record["key"] as? Int ?? 0) · \(record["taskTitle"] as? String ?? "")" :
            column == "state" ? (record["ignored"] as? Bool == true ? "已忽略" : "等待输入") : record["title"] as? String ?? ""
        let field = NSTextField(labelWithString: text.replacingOccurrences(of: "\n", with: " "))
        field.lineBreakMode = .byTruncatingTail; field.toolTip = text
        return field
    }
    private func identity(_ row: [String: Any]) -> String {
        ["threadId", "turnId", "callId"].map { row[$0] as? String ?? "" }.joined(separator: "\n") + "\n\(row["index"] as? Int ?? -1)"
    }
    private func selectionChanged() {
        guard rows.indices.contains(table.selectedRow) else { detail.string = "选择一条提问查看完整内容。"; action.isEnabled = false; return }
        let row = rows[table.selectedRow]
        detail.string = "键 \(row["key"] as? Int ?? 0) · \(row["taskTitle"] as? String ?? "")\n\n\(row["title"] as? String ?? "")"
        action.title = row["ignored"] as? Bool == true ? "恢复等待提示" : "忽略此提问"
        action.isEnabled = !busy
    }
    private func load(message: String = "") {
        guard !busy else { return }
        let selected = rows.indices.contains(table.selectedRow) ? identity(rows[table.selectedRow]) : nil
        busy = true; action.isEnabled = false; refresh.isEnabled = false; status.stringValue = "正在读取提问…"
        queue.async {
            let result = helper(["questions"])
            DispatchQueue.main.async {
                self.busy = false; self.refresh.isEnabled = true
                self.rows = result["questions"] as? [[String: Any]] ?? []; self.table.reloadData()
                if let selected = selected, let index = self.rows.firstIndex(where: { self.identity($0) == selected }) {
                    self.table.selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false)
                } else { self.table.deselectAll(nil) }
                self.status.stringValue = result["error"] as? String ?? (message.isEmpty ? (self.rows.isEmpty ? "当前六个任务没有可管理的异步提问。权限请求和同步等待不能在这里忽略。" : "选择具体问题后操作；已回答或已进入新一轮的问题会自动退出列表。") : message)
                self.selectionChanged()
            }
        }
    }
    @objc private func reload(_ sender: Any?) { load() }
    @objc private func toggle(_ sender: Any?) {
        guard !busy, rows.indices.contains(table.selectedRow) else { return }
        let row = rows[table.selectedRow], restore = row["ignored"] as? Bool == true
        guard let thread = row["threadId"] as? String, let turn = row["turnId"] as? String,
              let call = row["callId"] as? String, let index = row["index"] as? Int else { return }
        busy = true; action.isEnabled = false; refresh.isEnabled = false
        queue.async {
            let result = helper(["question-ignore", thread, turn, call, String(index), restore ? "restore" : "ignore"])
            DispatchQueue.main.async {
                self.busy = false
                self.load(message: result["error"] as? String ?? (result["ok"] as? Bool == true ? (restore ? "已恢复这道题的等待提示。" : "已忽略这道题。若还有其他问题，黄灯会继续保留。") : "操作未完成，请刷新后重试。"))
            }
        }
    }
    private func build() {
        let window = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 820, height: 540), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.title = "Bridge · 忽略此提问"; window.isReleasedWhenClosed = false; window.delegate = self
        window.minSize = NSSize(width: 700, height: 460); window.center()
        guard let content = window.contentView else { return }
        let hint = NSTextField(wrappingLabelWithString: "在 ChatGPT 点过“跳过”后，可在这里忽略对应提问。仅调整本机 Bridge 灯光，不会回答问题或继续任务；可随时恢复。")
        let scroll = NSScrollView(); scroll.hasVerticalScroller = true; scroll.borderType = .bezelBorder
        for (id, title, width) in [("task", "任务", 250.0), ("question", "具体提问", 390.0), ("state", "提示状态", 100.0)] {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id)); column.title = title; column.width = width; table.addTableColumn(column)
        }
        table.dataSource = self; table.delegate = self; table.rowHeight = 30
        table.allowsMultipleSelection = false; table.allowsEmptySelection = true; table.usesAlternatingRowBackgroundColors = true
        scroll.documentView = table
        let textScroll = NSScrollView(); textScroll.hasVerticalScroller = true; textScroll.borderType = .bezelBorder
        detail.isEditable = false; detail.isRichText = false; detail.font = .systemFont(ofSize: 13)
        detail.isHorizontallyResizable = false; detail.autoresizingMask = [.width]; detail.textContainer?.widthTracksTextView = true
        textScroll.documentView = detail
        action.target = self; action.action = #selector(toggle(_:)); action.bezelStyle = .rounded; action.isEnabled = false
        refresh.target = self; refresh.action = #selector(reload(_:)); refresh.bezelStyle = .rounded
        status.textColor = .secondaryLabelColor; status.font = .systemFont(ofSize: 12)
        for view in [hint, scroll, textScroll, status, action, refresh] as [NSView] { view.translatesAutoresizingMaskIntoConstraints = false; content.addSubview(view) }
        NSLayoutConstraint.activate([
            hint.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 18), hint.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -18), hint.topAnchor.constraint(equalTo: content.topAnchor, constant: 16),
            scroll.leadingAnchor.constraint(equalTo: hint.leadingAnchor), scroll.trailingAnchor.constraint(equalTo: hint.trailingAnchor), scroll.topAnchor.constraint(equalTo: hint.bottomAnchor, constant: 14), scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 150),
            textScroll.leadingAnchor.constraint(equalTo: hint.leadingAnchor), textScroll.trailingAnchor.constraint(equalTo: hint.trailingAnchor), textScroll.topAnchor.constraint(equalTo: scroll.bottomAnchor, constant: 12), textScroll.heightAnchor.constraint(equalToConstant: 140),
            status.leadingAnchor.constraint(equalTo: hint.leadingAnchor), status.trailingAnchor.constraint(equalTo: hint.trailingAnchor), status.topAnchor.constraint(equalTo: textScroll.bottomAnchor, constant: 10),
            action.trailingAnchor.constraint(equalTo: hint.trailingAnchor), action.topAnchor.constraint(equalTo: status.bottomAnchor, constant: 12), action.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -16),
            refresh.leadingAnchor.constraint(equalTo: hint.leadingAnchor), refresh.centerYAnchor.constraint(equalTo: action.centerYAnchor)
        ])
        panel = window
    }
}
