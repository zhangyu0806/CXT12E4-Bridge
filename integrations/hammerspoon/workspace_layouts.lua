-- Fixed multi-application layouts. The hotkey owner calls apply(); this module
-- deliberately owns no key bindings, so the existing window chooser stays usable.
local M = { _active = false, _transaction = nil, _undo = nil }
local acquireTimeout = 8
local defaults = {
    defaultPreset = "focus",
    roles = {
        leftTop = "REPLACE_WITH_LEFT_TOP_UUID",
        leftBottom = "REPLACE_WITH_LEFT_BOTTOM_UUID",
        right = "REPLACE_WITH_RIGHT_UUID",
    },
    apps = { codex = "com.openai.codex", browser = "com.apple.Safari",
        notes = "notion.id", terminal = "com.mitchellh.ghostty",
        claude = "com.anthropic.claudefordesktop" },
    presets = {
        { id = "office", title = "三屏办公", description = "左上 Codex · 左下浏览器 · 右侧 Notion",
          placements = { { app = "codex", role = "leftTop" },
              { app = "browser", role = "leftBottom" }, { app = "notes", role = "right" } } },
        { id = "terminal", title = "终端开发", description = "左上 Codex · 左下 Ghostty · 右侧浏览器",
          placements = { { app = "codex", role = "leftTop" },
              { app = "terminal", role = "leftBottom" }, { app = "browser", role = "right" } } },
        { id = "focus", title = "跨屏专注", description = "Codex 跨左侧上下两屏 · 右侧 Notion",
          placements = { { app = "codex", roles = { "leftTop", "leftBottom" } },
              { app = "notes", role = "right" } } },
        { id = "claude", title = "Codex + Claude", description = "左上 Codex · 左下 Claude 桌面版 · 右侧 Notion",
          placements = { { app = "codex", role = "leftTop" },
              { app = "claude", role = "leftBottom" }, { app = "notes", role = "right" } } },
    },
}

local function copy(value)
    if type(value) ~= "table" then return value end
    local result = {}
    for key, item in pairs(value) do result[key] = copy(item) end
    return result
end
local function now() return hs.timer.secondsSinceEpoch() end
local function rect(f) return { x=f.x, y=f.y, w=f.w, h=f.h } end
local function alert(message) hs.alert.show(message, 2.5) end
local function stopTimer(tx)
    if tx and tx.timer then tx.timer:stop(); tx.timer = nil end
end
local function stopReopenTasks(tx)
    for _, request in pairs(tx.reopens or {}) do
        local task = request.task
        request.task = nil
        if task then pcall(function()
            if task:isRunning() then
                request.record.taskStoppedAt = now()
                task:terminate()
            end
        end) end
    end
end
local function live(tx) return M._active and M._transaction == tx end
local function identity(win)
    if not win then return nil end
    local ok, id, pid = pcall(function() return win:id(), win:pid() end)
    if not ok or not id or not pid or pid <= 0 then return nil end
    return id, pid
end
local function sameWindow(item)
    local id, pid = identity(item.win)
    if id ~= item.id or pid ~= item.pid then return false end
    -- AX can retain a closed window's id/pid while frame becomes zero and its
    -- standard-window attribute disappears. Cached identity alone is not live.
    local ok, standard = pcall(function() return item.win:isStandard() end)
    return ok and standard == true
end
local function screenMap()
    local result = {}
    for _, screen in ipairs(hs.screen.allScreens()) do result[screen:getUUID()] = screen end
    return result
end
local function topologyFor(plan)
    local map, parts, used = screenMap(), {}, {}
    for _, item in ipairs(plan) do
        for _, uuid in ipairs(item.uuids) do
            if not used[uuid] then
                local screen = map[uuid]
                if not screen then return nil end
                local f = screen:fullFrame()
                parts[#parts+1] = table.concat({uuid, f.x, f.y, f.w, f.h}, ":")
                used[uuid] = true
            end
        end
    end
    table.sort(parts)
    return table.concat(parts, "|")
end
local function presetByID(id)
    for _, preset in ipairs(M.config.presets or {}) do
        if type(preset) == "table" and preset.id == id then return preset end
    end
end
local function defaultID()
    return M.config.defaultPreset
end

local function buildPlan(preset)
    if type(preset.placements) ~= "table" or #preset.placements == 0 then
        return nil, "固定布局没有配置窗口"
    end
    local map, plan, bundles = screenMap(), {}, {}
    for _, placement in ipairs(preset.placements) do
        if type(placement) ~= "table" then return nil, "固定布局的窗口配置无效" end
        local bundle = placement.bundleID or M.config.apps[placement.app]
        if type(bundle) ~= "string" or bundle == "" or bundles[bundle] then
            return nil, "固定布局的应用标识无效或重复"
        end
        bundles[bundle] = true
        local roles = placement.roles or { placement.role }
        if type(roles) ~= "table" or #roles == 0 or #roles > 2 then return nil, "固定布局的显示器角色无效" end
        local uuids, selected = {}, {}
        for _, role in ipairs(roles) do
            local uuid = M.config.roles[role]
            if not uuid or not map[uuid] then return nil, "缺少显示器：" .. tostring(role) .. "；窗口保持原位" end
            if selected[uuid] then return nil, "跨屏布局不能重复使用同一显示器" end
            uuids[#uuids+1], selected[uuid] = uuid, true
        end
        local spec, reason
        if #uuids == 2 then
            local frame
            frame, reason = M._manager.workspaceSpanFrame(uuids, false)
            if not frame then return nil, reason or "左侧两屏无法跨屏" end
            spec = { span = true, spanScreens = uuids, frame = rect(frame) }
        else
            local frame = M._manager.usableFrame(map[uuids[1]])
            if frame.w <= 0 or frame.h <= 0 then return nil, "显示器可用区域无效" end
            spec = { frame = rect(frame), screenUUID=uuids[1], unit={0,0,1,1} }
        end
        plan[#plan+1] = { bundleID=bundle, uuids=uuids, spec=spec }
    end
    return plan
end

local function appCandidates(bundle)
    local apps = {}
    if hs.application.applicationsForBundleID then
        apps = hs.application.applicationsForBundleID(bundle) or {}
    else
        local app = hs.application.get(bundle)
        if app then apps = {app} end
    end
    local exact = {}
    for _, app in ipairs(apps) do
        if app:bundleID() == bundle then exact[#exact+1] = app end
    end
    return exact
end
local function standard(win, app)
    local id, pid = identity(win)
    return id and pid == app:pid() and win:isStandard()
end
local function chooseWindow(bundle)
    local apps = appCandidates(bundle)
    if #apps == 0 then return nil, "waiting" end
    local focused = hs.window.focusedWindow()
    -- A dialog takes precedence over its parent; do not move a document whose
    -- application currently asks the user to finish a modal interaction.
    for _, app in ipairs(apps) do
        local appFocused = app:focusedWindow()
        if appFocused and not appFocused:isStandard() then return nil, "请先关闭应用的对话框：" .. bundle end
    end
    for _, app in ipairs(apps) do
        if standard(focused, app) then return focused end
    end
    if #apps == 1 then
        local app = apps[1]
        local appFocused = app:focusedWindow()
        if standard(appFocused, app) then return appFocused end
        local main = app:mainWindow()
        if standard(main, app) then return main end
    end
    local candidates, known = {}, {}
    for _, app in ipairs(apps) do
        for _, win in ipairs(app:allWindows() or {}) do
            if standard(win, app) and (win:isVisible() or win:isMinimized() or win:isFullScreen()) then
                local id, pid = identity(win)
                local key = tostring(pid) .. ":" .. tostring(id)
                if not known[key] then candidates[#candidates+1], known[key] = win, true end
            end
        end
    end
    if #candidates == 1 then return candidates[1] end
    if #candidates > 1 then return nil, "应用有多个候选窗口，请先点选目标窗口：" .. bundle end
    return nil, "waiting"
end

local function ensureGhosttyWindow(tx, bundle)
    -- Creating a terminal window is safe only when this exact Ghostty process
    -- has no windows at all. Never synthesize Cmd+N or create other app documents.
    if bundle ~= "com.mitchellh.ghostty" or tx.createdWindow[bundle] then return true end
    local apps = appCandidates(bundle)
    if #apps == 0 then return true end
    if #apps ~= 1 then return false, "检测到多个 Ghostty 进程，未新建窗口" end
    local app, observed = apps[1], {}
    local all = app:allWindows()
    if type(all) ~= "table" then return false, "无法确认 Ghostty 是否已有窗口，未新建窗口" end
    for _, win in ipairs(all) do observed[#observed+1] = win end
    local main, focused = app:mainWindow(), app:focusedWindow()
    if main then observed[#observed+1] = main end
    if focused then observed[#observed+1] = focused end
    for _, win in ipairs(observed) do
        -- Include hidden, minimized and other-Space windows in this guard.
        if win:isStandard() then tx.noWindow[bundle] = nil; return true end
        return false, "Ghostty 有尚未关闭的对话框，未新建窗口"
    end
    local previous, pid = tx.noWindow[bundle], app:pid()
    if not previous or previous.pid ~= pid then
        tx.noWindow[bundle] = {pid=pid, since=now()}
        return true
    end
    if now() - previous.since < .5 then return true end
    tx.createdWindow[bundle] = true
    local path = {"File", "New Window"}
    local ok, menu = pcall(function() return app:findMenuItem(path) end)
    if not ok or not menu or menu.enabled ~= true then
        return false, "Ghostty 的 File → New Window 菜单当前不可用，请先打开一个终端窗口"
    end
    local selected, result = pcall(function() return app:selectMenuItem(path) end)
    if not selected or not result then
        return false, "Ghostty 未能打开新窗口；其他窗口保持原位"
    end
    return true
end

local function ensureNotionWindow(tx, bundle)
    if bundle ~= "notion.id" then return true end
    local apps = appCandidates(bundle)
    if #apps == 0 then return true end
    if #apps ~= 1 then return false, "检测到多个 Notion 进程，未重新打开窗口" end
    local app, observed = apps[1], {}
    local all = app:allWindows()
    if type(all) ~= "table" then return false, "无法确认 Notion 是否已有窗口，未重新打开窗口" end
    for _, win in ipairs(all) do observed[#observed+1] = win end
    local main, focused = app:mainWindow(), app:focusedWindow()
    if main then observed[#observed+1] = main end
    if focused then observed[#observed+1] = focused end
    if #observed > 0 then
        tx.noWindow[bundle] = nil
        for _, win in ipairs(observed) do
            if not win:isStandard() then return false, "Notion 有尚未关闭的对话框，未重新打开窗口" end
        end
        return true -- Includes hidden/minimized/other-Space standard windows.
    end
    local pid, request = app:pid(), tx.reopens[bundle]
    if type(pid) ~= "number" or pid <= 0 then return false, "无法确认 Notion 进程，未重新打开窗口" end
    if request then
        if request.pid ~= pid then return false, "Notion 进程已变化，已停止唤回窗口" end
        if request.error then return false, request.error end
        return true
    end
    -- A newly launched process may still be creating its first window. Require
    -- the same empty process for half a second before sending one reopen event.
    local previous = tx.noWindow[bundle]
    if not previous or previous.pid ~= pid then
        tx.noWindow[bundle] = {pid=pid, since=now()}
        return true
    end
    if now()-previous.since < .5 then return true end
    local record = {bundleID=bundle,pid=pid,status="running",startedAt=now()}
    request = {pid=pid,record=record}
    tx.reopens[bundle] = request
    tx.record.reopens = tx.record.reopens or {}
    tx.record.reopens[bundle] = record
    local ok, task = pcall(function()
        return hs.task.new("/usr/bin/open", function(code, _, stderr)
            if not live(tx) or tx.reopens[bundle] ~= request then return end
            request.task = nil
            record.exitCode, record.finishedAt = code, now()
            if stderr and stderr ~= "" then record.stderr = stderr:sub(1,512) end
            if record.status == "window-ready" then return end
            record.status = code == 0 and "requested" or "failed"
            if code ~= 0 then request.error = "Notion 窗口唤回失败（open 退出码 " .. tostring(code) .. "）" end
        end, {"-b",bundle})
    end)
    if ok and task then
        request.task = task
        ok, task = pcall(function() return task:start() end)
    end
    if not ok or not task then
        request.task, record.status = nil, "failed"
        request.error = "未能启动 Notion 窗口唤回；其他窗口保持原位"
        return false, request.error
    end
    return true
end

local function clearQueued(reason)
    if not M._queuedPreset then return end
    M.lastQueuedCancellation = {preset=M._queuedPreset, reason=reason, time=now()}
    M._queuedPreset = nil
    if M._transaction then M._transaction.record.queuedPreset = nil end
end

local function finish(tx, success, detail)
    if M._transaction ~= tx then return end
    stopTimer(tx)
    M._transaction = nil
    stopReopenTasks(tx)
    tx.record.status = success and "completed" or "failed"
    tx.record.detail, tx.record.finishedAt = detail, now()
    if success then
        if tx.mode == "undo" then M._undo = nil
        elseif not tx.idempotent then M._undo = { items = tx.items, preset=tx.preset.id, createdAt=now() } end
        if tx.mode == "apply" then
            -- Geometry can already match while another app covers the layout.
            -- Reveal every selected window and leave its primary app focused.
            local function activate(item, method)
                if not item or not sameWindow(item) then return end
                local ok, reason = pcall(function() item.win[method](item.win) end)
                if not ok then tx.record.activationError = tostring(reason) end
            end
            for index = #tx.items, 1, -1 do activate(tx.items[index], "raise") end
            activate(tx.items[1], "focus")
        end
        alert(tx.mode == "undo" and "已恢复布局前的窗口位置" or ("已应用：" .. tx.preset.title))
    elseif detail then alert(detail) end
    -- A later hotkey remains the user's next requested layout. The current
    -- transaction finishes (including any rollback) before that one starts.
    local queued = M._queuedPreset
    M._queuedPreset = nil
    tx.record.queuedPreset = nil
    if queued and M._active then
        tx.record.followedBy = queued
        M.apply(queued)
    end
end
local function userCancellation(reason)
    reason = tostring(reason or "")
    return reason:find("user%-interrupted") or reason:find("explicit%-layout")
        or reason:find("用户")
end
local function sameValue(a, b)
    if type(a) ~= type(b) then return false end
    if type(a) ~= "table" then return a == b end
    for key, value in pairs(a) do if not sameValue(value, b[key]) then return false end end
    for key in pairs(b) do if a[key] == nil then return false end end
    return true
end
local rollback
local function fail(tx, reason)
    if not live(tx) then return end
    stopTimer(tx)
    if userCancellation(reason) then
        clearQueued(reason)
        M._undo = nil -- A partial layout plus user edits is no longer a reliable undo transaction.
        return finish(tx, false, "布局已停止，窗口保留当前位置")
    end
    if #tx.touched == 0 then return finish(tx, false, reason) end
    rollback(tx, reason)
end
rollback = function(tx, reason)
    if not live(tx) then return end
    tx.phase, tx.record.phase, tx.record.status = "rollback", "rollback", "restoring"
    local index, failures = #tx.touched, {}
    local function nextItem()
        if not live(tx) then return end
        if index == 0 then
            if #failures > 0 then M._undo = nil end
            return finish(tx, false, reason .. (#failures == 0 and "；已恢复已调整的窗口" or "；部分窗口无法恢复"))
        end
        local item = tx.touched[index]
        index = index - 1
        if not sameWindow(item) then failures[#failures+1] = "window-closed"; return nextItem() end
        -- A rejected native fullscreen exit may leave the original window
        -- completely unchanged on its inactive Space. Do not "restore" it by
        -- trying to reveal or resize it again.
        if item.preparedNative and not item.placementStarted then
            local ok, current = pcall(M._manager.captureWorkspaceWindow, item.win)
            if ok and current and sameValue(current, item.before) then return nextItem() end
        end
        local called = false
        local function done(ok, detail)
            if called or not live(tx) then return end
            called = true
            if userCancellation(detail) then return fail(tx, detail) end
            if not ok then failures[#failures+1] = detail or "restore-failed" end
            nextItem()
        end
        local ok, err = pcall(M._manager.restoreWorkspaceWindow, item.win, item.before, done)
        if not ok then done(false, tostring(err)) end
    end
    nextItem()
end

local function markTouched(tx, item)
    if not live(tx) or not sameWindow(item) then return false end
    for _, existing in ipairs(tx.touched) do if existing == item then return true end end
    tx.touched[#tx.touched+1] = item
    return true
end

local function moveNext(tx)
    if not live(tx) then return end
    if tx.mode ~= "undo" and topologyFor(tx.plan) ~= tx.topology then return fail(tx, "显示器排列已变化，已停止布局") end
    tx.index = tx.index + 1
    local item = tx.items[tx.index]
    if not item then return finish(tx, true) end
    if not sameWindow(item) then return fail(tx, "原窗口已关闭，已停止布局") end
    local spec
    if tx.mode ~= "undo" then
        local plan, reason = buildPlan(tx.preset)
        if not plan then return fail(tx, reason) end
        spec = plan[tx.index].spec
    end
    if not markTouched(tx, item) then return fail(tx, "原窗口已关闭，已停止布局") end
    item.placementStarted = true
    local called = false
    local function done(ok, detail)
        if called or not live(tx) then return end
        called = true
        if not ok then
            local plan = tx.plan and tx.plan[tx.index]
            local failure = {index=tx.index,windowID=item.id,pid=item.pid,
                bundleID=plan and plan.bundleID,reason=tostring(detail or "应用未接受窗口布局"),
                target=spec and spec.frame and rect(spec.frame)}
            local readOK, actual = pcall(function() return rect(item.win:frame()) end)
            if readOK then failure.actual = actual end
            tx.record.failedPlacement = failure
            local nameOK, name = pcall(function() return item.win:application():name() end)
            if not nameOK or not name then name = failure.bundleID end
            return fail(tx, (name and name .. "：" or "") .. failure.reason)
        end
        moveNext(tx)
    end
    local ok, err
    if tx.mode == "undo" then
        ok, err = pcall(M._manager.restoreWorkspaceWindow, item.win, item.target, done)
    else
        ok, err = pcall(M._manager.applyWorkspaceWindow, item.win, spec, done)
    end
    if not ok then done(false, tostring(err)) end
end

local function preparePresentation(tx)
    for _, item in ipairs(tx.items) do
        if not sameWindow(item) then return fail(tx, "原窗口已关闭，已停止布局") end
    end
    local ready, reason = M._manager.prepareWorkspacePresentation(tx.items, tx.presentationState,
        function(item)
            if not markTouched(tx, item) then return false end
            item.preparedNative = true
            return true
        end)
    tx.record.presentation = copy(tx.presentationState)
    if not live(tx) or ready == nil then return end
    if not ready then return fail(tx, reason or "无法唤回布局窗口，窗口保持原位") end
    stopTimer(tx)
    tx.phase, tx.record.phase, tx.index = "moving", "moving", 0
    -- Repeating an already fulfilled shortcut must not erase the original undo
    -- snapshot. It must still reveal/focus the layout after presentation is ready.
    local items, prior = tx.items, M._undo
    local alreadyPlaced = #tx.touched == 0 and prior and prior.preset == tx.preset.id and #prior.items == #items
    for index, item in ipairs(items) do
        local previous, spec = prior and prior.items[index], tx.plan[index].spec
        -- A Space/reveal transition can resize an Electron window. Inspect its
        -- current state without replacing the original snapshot used by undo.
        local snapshot = item.before
        if prior then snapshot = M._manager.captureWorkspaceWindow(item.win) end
        if not snapshot or snapshot.id ~= item.id or snapshot.pid ~= item.pid then
            return fail(tx, "窗口身份已变化，窗口保持原位")
        end
        local f, wanted = snapshot.frame, spec.frame
        if not previous or previous.id ~= item.id or previous.pid ~= item.pid
            or snapshot.minimized or snapshot.fullscreen or snapshot.hidden or snapshot.menuMembership
            or math.abs(f.x-wanted.x) > 4 or math.abs(f.y-wanted.y) > 4
            or math.abs(f.w-wanted.w) > 32 or math.abs(f.h-wanted.h) > 32
            or (spec.span and not snapshot.activeSpan) then alreadyPlaced = false end
    end
    if alreadyPlaced then tx.idempotent = true; return finish(tx, true, "already-applied") end
    tx.record.status = "arranging"
    moveNext(tx)
end

local function acquire(tx)
    if not live(tx) then return end
    if topologyFor(tx.plan) ~= tx.topology then return fail(tx, "显示器排列已变化，窗口保持原位") end
    if tx.items then return preparePresentation(tx) end
    local windows, waiting = {}, nil
    for index, item in ipairs(tx.plan) do
        local request = tx.reopens[item.bundleID]
        if request then
            local current = appCandidates(item.bundleID)
            if #current ~= 1 or current[1]:pid() ~= request.pid then
                return fail(tx, "Notion 进程已变化，已停止唤回窗口")
            end
        end
        local win, reason = chooseWindow(item.bundleID)
        if not win then
            if reason ~= "waiting" then return fail(tx, reason) end
            tx.record.waitingBundle = item.bundleID
            if #appCandidates(item.bundleID) == 0 and not tx.launched[item.bundleID] then
                tx.launched[item.bundleID] = true
                if not hs.application.launchOrFocusByBundleID(item.bundleID) then
                    return fail(tx, "无法打开应用：" .. item.bundleID .. "；窗口保持原位")
                end
            end
            local ready, detail = ensureGhosttyWindow(tx, item.bundleID)
            if not ready then return fail(tx, detail) end
            ready, detail = ensureNotionWindow(tx, item.bundleID)
            if not ready then return fail(tx, detail) end
            waiting = item.bundleID
        else
            windows[index] = win
            if item.bundleID == "notion.id" then tx.noWindow[item.bundleID] = nil end
            if request then
                request.record.status, request.record.windowID = "window-ready", win:id()
                request.record.readyAt = request.record.readyAt or now()
            end
        end
    end
    if waiting then
        if now() >= tx.deadline then return fail(tx, "等待应用窗口超时：" .. waiting .. "；其他窗口保持原位") end
        return
    end
    tx.record.waitingBundle = nil
    -- Every target is ready before the first unminimize/fullscreen exit/move.
    local items = {}
    for index, win in ipairs(windows) do
        local id, pid = identity(win)
        local snapshot = M._manager.captureWorkspaceWindow(win)
        if not snapshot or snapshot.id ~= id or snapshot.pid ~= pid then
            return fail(tx, "窗口身份已变化，窗口保持原位")
        end
        items[index] = { win=win, id=id, pid=pid, before=snapshot }
    end
    -- Keep these snapshots fixed while Show Desktop/Space transitions settle.
    -- Reacquiring would erase the original undo state or choose another window.
    tx.items, tx.presentationState, tx.phase = items, {}, "presenting"
    tx.record.status, tx.record.phase = "preparing", "presenting"
    preparePresentation(tx)
end

function M.apply(id)
    if not M._active then return false, "固定布局尚未启动" end
    id = id or defaultID()
    local preset = presetByID(id)
    if not preset then alert("未找到固定布局：" .. tostring(id)); return false, "unknown-preset" end
    M.lastRequest = {preset=id, time=now()}
    if M._transaction then
        local tx = M._transaction
        if tx.mode == "apply" and tx.preset.id == id then
            clearQueued("latest-request-is-active")
            tx.record.repeatedAt = now()
            M.lastRequest.status = "already-pending"
            return true, "already-pending"
        end
        M._queuedPreset = id
        tx.record.queuedPreset, tx.record.queuedAt = id, now()
        M.lastRequest.status = "queued"
        M.lastRequest.activePreset = tx.record.preset
        return true, "queued"
    end
    M.lastRequest.status = "starting"
    local plan, reason = buildPlan(preset)
    if not plan then alert(reason); return false, reason end
    -- Installed-app check precedes any launch as well as every window mutation.
    for _, item in ipairs(plan) do
        if #appCandidates(item.bundleID) == 0 and hs.application.pathForBundleID
            and not hs.application.pathForBundleID(item.bundleID) then
            reason = "未安装应用：" .. item.bundleID .. "；窗口保持原位"
            alert(reason); return false, reason
        end
    end
    local tx = { mode="apply", preset=preset, plan=plan, topology=topologyFor(plan),
        launched={}, createdWindow={}, noWindow={}, reopens={}, touched={}, deadline=now()+acquireTimeout,
        phase="acquiring", record={preset=id, phase="acquiring", status="waiting", startedAt=now()} }
    M._transaction, M.lastAction = tx, tx.record
    local function safeAcquire()
        local ok, err = pcall(acquire, tx)
        if not ok then fail(tx, "准备窗口失败：" .. tostring(err)) end
    end
    tx.timer = hs.timer.doEvery(.1, safeAcquire)
    safeAcquire()
    return true, "pending"
end

function M.undo()
    if not M._active then return false, "固定布局尚未启动" end
    if M._transaction then alert("正在调整窗口，请稍候"); return false, "busy" end
    if not M._undo then alert("暂无可恢复的固定布局"); return false, "no-snapshot" end
    local items = {}
    for _, previous in ipairs(M._undo.items) do
        if not sameWindow(previous) then
            alert("布局中的窗口已关闭，未调整其他窗口")
            M._undo = nil
            return false, "stale-window"
        end
        local captured, before = pcall(M._manager.captureWorkspaceWindow, previous.win)
        if not captured or not before then
            alert("暂时无法读取窗口位置，未调整其他窗口；请稍后重试")
            return false, "capture-failed"
        end
        items[#items+1] = {win=previous.win,id=previous.id,pid=previous.pid,before=before,target=previous.before}
    end
    -- Entering native fullscreen switches a shared desktop Space. Restore
    -- ordinary windows first, while their CG presentation is still available.
    local ordered = {}
    for _, item in ipairs(items) do if not item.target.fullscreen then ordered[#ordered+1] = item end end
    for _, item in ipairs(items) do if item.target.fullscreen then ordered[#ordered+1] = item end end
    items = ordered
    local tx = { mode="undo", items=items, touched={}, index=0,
        record={status="restoring",startedAt=now(),preset=M._undo.preset} }
    M._transaction, M.lastAction = tx, tx.record
    moveNext(tx)
    return true, "pending"
end

function M.cancel(reason)
    clearQueued(reason or "user-interrupted")
    local tx = M._transaction
    if not tx then return false end
    stopTimer(tx)
    M._transaction = nil -- Ignore a synchronous cancellation callback from WM.
    stopReopenTasks(tx)
    M._manager.cancelWorkspaceOperation(reason or "user-interrupted")
    tx.record.status, tx.record.detail, tx.record.finishedAt = "cancelled", reason or "user-interrupted", now()
    if #tx.touched > 0 then M._undo = nil end
    return true
end

function M.setDefault(id)
    if not presetByID(id) then return false, "unknown-preset" end
    M.config.defaultPreset = id
    return true
end
function M.choices()
    local choices = {}
    for _, preset in ipairs(M.config.presets) do
        choices[#choices+1] = {text=preset.title,subText=preset.description,preset=preset.id,
            current=preset.id == defaultID()}
    end
    return choices
end
function M.selectMenu()
    if not M._chooser then
        M._chooser = hs.chooser.new(function(choice)
            if not choice then return end
            if choice.undo then M.undo(); return end
            M.apply(choice.preset)
        end):rows(5):width(48):searchSubText(true):placeholderText("选择固定布局（Esc 取消）")
    end
    local choices = M.choices()
    choices[#choices+1] = {text="恢复布局前的窗口位置",subText="恢复上一次固定布局涉及的窗口",undo=true}
    M._chooser:choices(choices):query(""):show()
    return true
end

function M.stop()
    M.cancel("stopped")
    M._active = false
    if M._chooser then M._chooser:hide(); M._chooser = nil end
end
function M.start(manager, config)
    M.stop()
    M._manager = manager
    local path = hs.configdir .. "/workspace_layouts.json"
    if not config then
        local file = io.open(path, "r")
        if file then
            local data = file:read("*a"); file:close()
            local ok, decoded = pcall(hs.json.decode, data)
            if not ok or type(decoded) ~= "table" then
                alert("固定布局配置无效：" .. path)
                return false, "invalid-config"
            end
            config = decoded
        end
    end
    M.config = copy(config or defaults)
    if type(M.config.roles) ~= "table" or type(M.config.apps) ~= "table"
        or type(M.config.presets) ~= "table" or not presetByID(M.config.defaultPreset) then
        alert("固定布局配置缺少显示器、应用或默认方案")
        return false, "invalid-config"
    end
    M._active = true
    return true
end
M.defaultConfig = function() return copy(defaults) end
return M
