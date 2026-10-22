-- T90 window controls. Karabiner bridges Command+Shift+physical Win to these internal mods.
-- Screen numbers are calculated on each action: left to right, then top to bottom.
local M = { _hotkeys = {}, _chooser = nil }
local mods = { "ctrl", "alt", "cmd", "shift" }
local previousManager = rawget(_G, "wm")
local saved = previousManager and previousManager._savedFrames or {}
M._savedFrames = saved
local tolerance = 4
local navigationTTL = 3
local dragEventTag = 0x574D5350 -- WM span synthetic mouse events
M._spanMaintenance = {}
local spanDrag = require("window_span_drag")
local menuMode = require("menu_bar_mode")

local function rect(r)
    return { x = r.x, y = r.y, w = r.w, h = r.h }
end

local presentationMessage = "请先按 Win+D 收回桌面或退出任务总览，再应用布局"
function M.windowPresentationReady(win)
    local diagnostic = {time=hs.timer.secondsSinceEpoch()}
    local ok, ready, detail = pcall(function()
        local id, pid, frame = win:id(), win:pid(), win:frame()
        diagnostic.windowID, diagnostic.pid, diagnostic.ax = id, pid, rect(frame)
        local app = win:application()
        if app then
            diagnostic.bundleID = app:bundleID()
            if app.name then diagnostic.appName = app:name() end
        end
        if not hs.window.list then return false, "cg-unavailable" end
        local list = hs.window.list(true)
        if type(list) ~= "table" then return false, "cg-unavailable" end
        for _, item in ipairs(list) do
            if item.kCGWindowNumber == id then
                diagnostic.cgPID = item.kCGWindowOwnerPID
                if item.kCGWindowOwnerPID ~= pid then return false, "pid-mismatch" end
                local bounds = item.kCGWindowBounds
                if type(bounds) ~= "table" then return false, "bounds-unavailable" end
                diagnostic.cg = {x=bounds.X,y=bounds.Y,w=bounds.Width,h=bounds.Height}
                for ax, cg in pairs({x="X",y="Y",w="Width",h="Height"}) do
                    if type(bounds[cg]) ~= "number" or math.abs(bounds[cg]-frame[ax]) > 4 then
                        return false, "presentation-mismatch"
                    end
                end
                if item.kCGWindowIsOnscreen == false or item.kCGWindowAlpha == 0 then
                    return false, "not-presented"
                end
                return true
            end
        end
        return false, "cg-window-missing"
    end)
    if ok and ready then return true end
    diagnostic.reason = ok and detail or tostring(ready)
    M.lastPresentationFailure = diagnostic
    return false, presentationMessage, diagnostic
end

-- Menu bar animation finishes after the preference changes. Layouts must use
-- its restored bounds immediately, including synchronous direction moves.
local function usableFrame(screen)
    local frame = rect(screen:frame())
    if screen:id() == hs.screen.primaryScreen():id() then
        local inset = menuMode.restoringTopInset() or menuMode.currentTopInset()
        local bottom = frame.y + frame.h
        frame.y = screen:fullFrame().y + inset
        frame.h = bottom - frame.y
    end
    return frame
end
M.usableFrame = usableFrame

local function notice(message)
    hs.alert.show(message, 2)
    return false, message
end

local function screens()
    local result = hs.screen.allScreens()
    table.sort(result, function(a, b)
        local af, bf = a:fullFrame(), b:fullFrame()
        if af.x ~= bf.x then return af.x < bf.x end
        if af.y ~= bf.y then return af.y < bf.y end
        return a:id() < b:id()
    end)
    return result
end

local function topology()
    local snapshot, parts = {}, {}
    for _, screen in ipairs(screens()) do
        local f = rect(screen:fullFrame())
        local key = screen:getUUID() or tostring(screen:id())
        snapshot[#snapshot + 1] = { uuid = key, frame = f }
        parts[#parts + 1] = table.concat({key, f.x, f.y, f.w, f.h}, ":")
    end
    return table.concat(parts, "|"), snapshot
end

local function spanScreens(frame)
    local result = {}
    for _, screen in ipairs(screens()) do
        local f = screen:fullFrame()
        if math.min(f.x+f.w,frame.x+frame.w) > math.max(f.x,frame.x)
            and math.min(f.y+f.h,frame.y+frame.h) > math.max(f.y,frame.y) then
            result[#result+1] = {uuid=screen:getUUID(),frame=rect(f)}
        end
    end
    return result
end

local function unchangedSpanScreens(members)
    if not members or #members == 0 then return nil end
    local result, current = {}, {}
    for _, screen in ipairs(screens()) do current[screen:getUUID()] = screen end
    for _, member in ipairs(members) do
        local screen = current[member.uuid]
        if not screen then return nil end
        local f, old = screen:fullFrame(), member.frame
        if f.x ~= old.x or f.y ~= old.y or f.w ~= old.w or f.h ~= old.h then return nil end
        result[#result+1] = screen
    end
    return result
end

local function invalidateNavigation(reason)
    if M._navigationToken then M.lastNavigationInvalidation = reason end
    M._navigationToken = nil
end

local function identity(win)
    if not win then return nil end
    -- Read AX identity directly; launching a helper can temporarily invalidate
    -- NSRunningApplication's process lookup within the same run-loop turn.
    local id, pid = win:id(), win:pid()
    if not id or not pid or pid <= 0 then return nil end
    return id, pid
end

function M.endFullSpanMode(win)
    invalidateNavigation("explicit layout exit")
    local id, pid = identity(win)
    if id then
        M._spanMaintenance[id] = nil
        local entry = saved[id]
        if entry and entry.pid == pid then entry.maintenanceDisabled = true end
    end
    if menuMode.contains(win) then return menuMode.leave(win) end
    return true
end

local function validate(win, allowFullscreen)
    if not hs.accessibilityState() then
        return notice("请在系统设置 → 隐私与安全性 → 辅助功能中开启 Hammerspoon")
    end
    if not win or not identity(win) then return notice("请先点选要调整的应用窗口") end
    if win:isFullScreen() and not allowFullscreen then return notice("请先退出 macOS 原生全屏，再使用窗口布局快捷键") end
    if not win:isStandard() then return notice("当前窗口不是可调整的普通窗口") end
    if not win:screen() then return notice("未找到当前窗口所在的显示器") end
    return true
end

local function getSaved(win)
    local id, pid = identity(win)
    local entry = id and saved[id]
    if entry and entry.pid ~= pid then saved[id] = nil; entry = nil end
    return entry
end

local function remember(win)
    local entry = getSaved(win)
    if entry then return entry end
    local id, pid = identity(win)
    entry = { pid = pid, frame = rect(win:frame()), screenUUID = win:screen():getUUID(),
        screenFrame = usableFrame(win:screen()) }
    saved[id] = entry
    return entry
end

local function clamp(frame, bounds)
    local result = rect(frame)
    result.w, result.h = math.min(result.w, bounds.w), math.min(result.h, bounds.h)
    result.x = math.max(bounds.x, math.min(result.x, bounds.x + bounds.w - result.w))
    result.y = math.max(bounds.y, math.min(result.y, bounds.y + bounds.h - result.h))
    return result
end

local function visibleScreenArea(frame)
    -- Integrate the union, rather than counting overlapping/mirrored displays twice.
    local bounds, edges = {}, {frame.x, frame.x + frame.w}
    for _, screen in ipairs(screens()) do
        local f = screen:fullFrame()
        local left, right = math.max(frame.x,f.x), math.min(frame.x+frame.w,f.x+f.w)
        if right > left then
            bounds[#bounds+1] = f
            edges[#edges+1], edges[#edges+2] = left, right
        end
    end
    table.sort(edges)
    local area = 0
    for i=1,#edges-1 do
        local left, right, intervals = edges[i], edges[i+1], {}
        for _, f in ipairs(bounds) do
            if f.x <= left and f.x+f.w >= right then
                local top, bottom = math.max(frame.y,f.y), math.min(frame.y+frame.h,f.y+f.h)
                if bottom > top then intervals[#intervals+1] = {top,bottom} end
            end
        end
        table.sort(intervals,function(a,b)return a[1]<b[1]end)
        local height, last = 0, -math.huge
        for _, interval in ipairs(intervals) do
            height = height + math.max(0, interval[2] - math.max(last,interval[1]))
            last = math.max(last,interval[2])
        end
        area = area + (right-left)*height
    end
    return area
end

local function coveredByScreens(frame)
    return visibleScreenArea(frame) >= frame.w * frame.h - tolerance * (frame.w + frame.h)
end

-- Show Desktop has no read-only state API. Require stable, independent windows
-- pushed to the outer edges at their original size before toggling it once.
local function desktopEvidence(items)
    local cg, windows, seen, pids, signatures = {}, {}, {}, {}, {}
    for _, item in ipairs(hs.window.list(true) or {}) do cg[item.kCGWindowNumber] = item end
    for _, item in ipairs(items) do windows[#windows+1] = item.win end
    for _, win in ipairs(hs.window.allWindows()) do windows[#windows+1] = win end
    for _, win in ipairs(windows) do
        local id, pid = identity(win)
        local app = win:application()
        local row = id and cg[id]
        if id and not seen[id] and row and row.kCGWindowOwnerPID == pid
            and row.kCGWindowAlpha ~= 0 and row.kCGWindowIsOnscreen ~= false
            and win:isStandard() and not win:isMinimized() and not win:isFullScreen()
            and app and not app:isHidden() then
            seen[id] = true
            local b, ax = row.kCGWindowBounds, win:frame()
            if b and type(b.X)=="number" and type(b.Y)=="number"
                and type(b.Width)=="number" and type(b.Height)=="number" then
                local actual = {x=b.X,y=b.Y,w=b.Width,h=b.Height}
                local area = ax.w * ax.h
                local narrow = true
                for _, screen in ipairs(screens()) do
                    local s = screen:fullFrame()
                    local w = math.min(actual.x+actual.w,s.x+s.w)-math.max(actual.x,s.x)
                    local h = math.min(actual.y+actual.h,s.y+s.h)-math.max(actual.y,s.y)
                    if w > 64 and h > 64 then narrow = false end
                end
                if area > 0 and math.abs(actual.w-ax.w)<=4 and math.abs(actual.h-ax.h)<=4
                    and math.max(math.abs(actual.x-ax.x),math.abs(actual.y-ax.y))>64
                    and visibleScreenArea(ax)>=area*.5 and visibleScreenArea(actual)<=area*.05 and narrow then
                    pids[pid] = true
                    signatures[#signatures+1] = table.concat({id,pid,b.X,b.Y,b.Width,b.Height,ax.x,ax.y,ax.w,ax.h}, ":")
                end
            end
        end
    end
    local count = 0
    for _ in pairs(pids) do count=count+1 end
    if count < 2 then return nil end
    local active = hs.spaces.activeSpaces and hs.spaces.activeSpaces()
    if type(active) ~= "table" then return nil end
    for screen, space in pairs(active) do signatures[#signatures+1] = tostring(screen).."="..tostring(space) end
    table.sort(signatures)
    local signature = topology()
    return signature.."|"..table.concat(signatures, "|")
end

-- Explicit preset requests may reveal the desktop. Automatic maintenance never
-- calls this helper and continues to pause when the window isn't presented.
function M.prepareWorkspacePresentation(items, state, markTouched)
    local now = hs.timer.secondsSinceEpoch()
    local flags, buttons = hs.eventtap.checkKeyboardModifiers(), hs.eventtap.checkMouseButtons()
    local held = flags.cmd or flags.ctrl or flags.alt or flags.shift or flags.fn or next(buttons)
    local delta = state.lastTick and math.max(0, now-state.lastTick) or 0
    state.lastTick = now
    if held then
        state.inputStarted = state.inputStarted or now
        state.wasWaitingInput, state.status = true, "waiting-release"
        state.input = {cmd=flags.cmd,ctrl=flags.ctrl,alt=flags.alt,shift=flags.shift,fn=flags.fn,
            mouse=next(buttons) ~= nil}
        if now-state.inputStarted > 15 then return false, "请松开快捷键和鼠标后再按一次，已取消本次布局" end
        return nil
    end
    state.started = state.started or now
    state.elapsed = (state.elapsed or 0) + (state.wasWaitingInput and 0 or delta)
    state.wasWaitingInput, state.inputStarted, state.input = false, nil, nil
    local function diagnostic(item, reason)
        local app = item.win:application()
        return {time=now,windowID=item.id,pid=item.pid,reason=reason,
            bundleID=app and app:bundleID(),appName=app and app.name and app:name()}
    end
    local function timeout()
        local d = state.waiting or {}
        local name = d.appName or d.bundleID or "目标应用"
        local reasons = { ["native-fullscreen"]="退出原生全屏尚未完成",
            ["cg-window-missing"]="窗口仍在其他桌面或尚未显示",
            ["presentation-mismatch"]="窗口动画或任务总览尚未结束",
            ["not-presented"]="窗口尚未显示" }
        return false, name .. "：" .. (reasons[d.reason] or "窗口信息暂不可用") .. "，请稍后重试"
    end
    if not hs.accessibilityState() then return false, "请开启 Hammerspoon 辅助功能权限" end
    -- Check every captured identity before any fullscreen mutation. Preparation
    -- changes belong to the same transaction and must be reversible on failure.
    for _, item in ipairs(items) do
        local id, pid = identity(item.win)
        if id ~= item.id or pid ~= item.pid then return false, "原窗口已关闭，布局未开始" end
        if not item.win:isStandard() then return false, "目标窗口无法调整，请先关闭应用的对话框" end
    end
    state.native = state.native or {}
    for _, item in ipairs(items) do
        local key = tostring(item.pid) .. ":" .. tostring(item.id)
        local native = state.native[key]
        if item.win:isFullScreen() then
            state.status, state.waiting = "exiting-fullscreen", diagnostic(item, "native-fullscreen")
            if state.elapsed > 8 then return timeout() end
            if not native then
                -- Only a transaction that records the original fullscreen state
                -- may change it during preflight. No blind native-space mutation.
                if not markTouched or markTouched(item) == false then
                    return false, "无法记录全屏窗口的恢复位置，布局未开始"
                end
                native = {attempts=0}
                state.native[key] = native
            end
            native.exitedAt = nil
            if native.attempts < 3 and (not native.lastAt or now-native.lastAt >= 1) then
                native.attempts, native.lastAt = native.attempts+1, now
                local focused, result = pcall(function() return item.win:focus() end)
                native.focusAccepted = focused and result ~= nil and result ~= false
                local id, pid = identity(item.win)
                if id ~= item.id or pid ~= item.pid then return false, "原窗口已关闭，布局未开始" end
                local exited, result = pcall(function() return item.win:setFullScreen(false) end)
                native.exitAccepted = exited and result ~= nil and result ~= false
                native.error = not exited and tostring(result) or nil
            end
            return nil -- Exit one native Space at a time; never overlap animations.
        elseif native then
            native.exitedAt = native.exitedAt or now
            if now-native.exitedAt < .5 then
                state.status, state.waiting = "settling-fullscreen", diagnostic(item, "native-fullscreen")
                if state.elapsed > 8 then return timeout() end
                return nil
            end
        end
    end
    local waiting
    for _, item in ipairs(items) do
        local app = item.win:application()
        if not item.win:isMinimized() and not (app and app:isHidden()) then
            local ready, _, detail = M.windowPresentationReady(item.win)
            if not ready and not waiting then waiting, state.waiting = item.win, detail end
        end
    end
    if not waiting then state.status, state.waiting = "ready", nil; return true end
    state.status = "revealing-window"
    if state.elapsed > 8 then return timeout() end
    if not state.closedMissionControl then
        state.closedMissionControl, state.overlayAt = true, now
        if hs.spaces.closeMissionControl then hs.spaces.closeMissionControl() end
        return nil
    end
    if now-state.overlayAt < .3 then return nil end
    if not state.desktopToggled then
        local evidence = desktopEvidence(items)
        if evidence then
            if state.evidence ~= evidence then state.evidence, state.evidenceAt = evidence, now; return nil end
            if now-state.evidenceAt < .15 then return nil end
            state.desktopToggled, state.overlayAt = true, now
            hs.spaces.toggleShowDesktop()
            return nil
        end
        state.evidence, state.evidenceAt = nil, nil
    end
    -- A transient AX/Space activation failure must not consume the entire
    -- request. Retry only this captured window, at most three times.
    if not state.desktopToggled then
        state.focused = state.focused or {}
        local key = tostring(waiting:pid()) .. ":" .. tostring(waiting:id())
        local attempt = state.focused[key] or {count=0}
        state.focused[key] = attempt
        if attempt.count < 3 and (not attempt.lastAt or now-attempt.lastAt >= .6) then
            attempt.count, attempt.lastAt = attempt.count+1, now
            local ok, result = pcall(function() return waiting:focus() end)
            attempt.accepted = ok and result ~= nil and result ~= false
        end
    end
    return nil
end

local function restore(win)
    local entry = getSaved(win)
    if not entry then return notice("这个窗口尚无已保存的位置") end
    local destination
    for _, screen in ipairs(screens()) do
        if screen:getUUID() == entry.screenUUID then destination = screen; break end
    end
    local originalScreenPresent = destination ~= nil
    destination = destination or win:screen() or hs.screen.primaryScreen()
    if not destination then return notice("未找到可用显示器") end
    M.endFullSpanMode(win)
    local bounds, originalBounds = usableFrame(destination), entry.screenFrame
    local unchanged = originalScreenPresent and bounds.x == originalBounds.x
        and bounds.y == originalBounds.y and bounds.w == originalBounds.w and bounds.h == originalBounds.h
    -- Preserve a deliberately spanning original frame when the topology is unchanged.
    local target = unchanged and coveredByScreens(entry.frame) and entry.frame or clamp(entry.frame, bounds)
    if entry.action == "span" or entry.action == "fullspan" then
        win:setSize({ w = target.w, h = target.h })
    end
    win:setFrame(target, 0)
    saved[win:id()] = nil
    return true, rect(win:frame())
end

local function fits(actual, target)
    return math.abs(actual.x - target.x) <= tolerance
        and math.abs(actual.y - target.y) <= tolerance
        and math.abs(actual.w - target.w) <= 32
        and math.abs(actual.h - target.h) <= 32
end

local function apply(win, frame, action, dragBase, origin, options)
    options = options or {}
    local automatic = options.automatic == true
    local preserveMembership = options.repair == true
    if action == "span" or action == "fullspan" then
        local ready, reason = M.windowPresentationReady(win)
        if not ready then return false, reason end
    end
    local previousFrame = origin and origin.frame or rect(win:frame())
    local existingEntry = getSaved(win)
    if origin then existingEntry = origin.entry end
    local entry = remember(win)
    win:setFrame(frame, 0)
    local actual = rect(win:frame())
    -- AppKit can silently clamp window height to one display even when
    -- separate Spaces are disabled. Allow one terminal row/column of rounding,
    -- but never report a single-screen result as a successful spanning layout.
    local spanning = action == "span" or action == "fullspan"
    if spanning and (math.abs(actual.x - frame.x) > tolerance
        or math.abs(actual.y - frame.y) > tolerance
        or math.abs(actual.w - frame.w) > 32
        or math.abs(actual.h - frame.h) > 32) then
        if dragBase then
            local pending = { win = win, record = M.lastAction, hadSaved = existingEntry ~= nil,
                action = action, quiet = automatic, automatic = automatic, repair = preserveMembership }
            local id, pid = identity(win)
            M._pendingSpan = pending
            local started, controller = pcall(spanDrag.start, win, dragBase, frame, function(ok, result, reason)
                if M._pendingSpan == pending then M._pendingSpan = nil end
                pending.record.pending = nil
                pending.record.succeeded = ok
                pending.record.detail = reason
                if ok then
                    entry.action = action
                    entry.maintenanceDisabled = nil
                    entry.spanScreens, entry.spanTarget = spanScreens(frame), rect(frame)
                    M.lastSpanFailure = nil
                    hs.printf("[window_manager] window %s spanned by dragging: %dx%d",
                        tostring(id), result.w, result.h)
                    return
                end
                local currentID, currentPID = identity(win)
                local restored
                if currentID == id and currentPID == pid and reason ~= "user-interrupted" then
                    win:setSize({ w = previousFrame.w, h = previousFrame.h })
                    win:setFrame(previousFrame, 0)
                    restored = rect(win:frame())
                end
                if not existingEntry and saved[id] == entry then saved[id] = nil end
                if action == "fullspan" and (not preserveMembership or reason == "跨屏操作已取消") then
                    M.endFullSpanMode(win)
                end
                if reason == "user-interrupted" then
                    if pending.automatic and pending.repair then
                        -- Yield the pointer without mistaking a cancelled repair
                        -- for a request to leave the user's existing span layout.
                        -- Real window edits are detected by the mouse gesture guard.
                        M._spanPointerQuietUntil = hs.timer.secondsSinceEpoch() + .7
                    else
                        M.endFullSpanMode(win)
                        entry.maintenanceDisabled = true
                    end
                end
                if action == "span" and reason == "跨屏操作已取消" then
                    entry.action = "cancelled"
                    M._spanMaintenance[id] = nil
                end
                M.lastSpanFailure = { requested = rect(frame), actual = result and rect(result),
                    restored = restored, reason = reason }
                local restoredExactly = restored and math.abs(restored.h - previousFrame.h) <= 32
                    and math.abs(restored.w - previousFrame.w) <= 32
                if not pending.quiet then
                    notice((reason or "跨屏未完成") .. (restoredExactly and "；已恢复原位置"
                        or "；应用未恢复原跨屏尺寸，请重试布局快捷键"))
                end
            end, M.windowPresentationReady)
            if not started then
                M._pendingSpan = nil
                if action == "fullspan" and not preserveMembership then M.endFullSpanMode(win) end
                win:setFrame(previousFrame, 0)
                if not existingEntry then saved[id] = nil end
                hs.printf("[window_manager] Cannot start spanning: %s", tostring(controller))
                if automatic then return false, "焦点跨屏恢复未能启动" end
                return notice("跨屏拖动未能启动，已恢复原位置")
            end
            pending.controller = controller
            return true, "pending"
        end
        win:setFrame(previousFrame, 0)
        if action == "fullspan" and not preserveMembership then M.endFullSpanMode(win) end
        if not existingEntry then saved[win:id()] = nil end
        local app = win:application()
        M.lastSpanFailure = { app = app and app:name() or tostring(win:pid()),
            requested = rect(frame), actual = actual, restored = rect(win:frame()) }
        hs.printf("[window_manager] %s rejected span: requested %dx%d, actual %dx%d",
            M.lastSpanFailure.app, frame.w, frame.h, actual.w, actual.h)
        return notice("当前应用将窗口尺寸限制在单屏，无法直接铺满两屏；已恢复原位置")
    end
    entry.action = action
    if spanning then
        entry.maintenanceDisabled = nil
        entry.spanScreens, entry.spanTarget = spanScreens(frame), rect(frame)
    end
    if spanning then M.lastSpanFailure = nil end
    return true, actual
end

local function near(a, b) return math.abs(a - b) <= tolerance end

local function pairFrame(pair, fullBounds, allowHorizontal)
    local a, b = pair[1]:fullFrame(), pair[2]:fullFrame()
    local vertical = near(a.x, b.x) and near(a.w, b.w)
        and (near(a.y + a.h, b.y) or near(b.y + b.h, a.y))
    local horizontal = near(a.y, b.y) and near(a.h, b.h)
        and (near(a.x + a.w, b.x) or near(b.x + b.w, a.x))
    if not vertical and not (allowHorizontal and horizontal) then
        return nil, "跨屏需要两屏边缘相邻且对齐；请先调整系统显示器排列"
    end
    local af = fullBounds and pair[1]:fullFrame() or usableFrame(pair[1])
    local bf = fullBounds and pair[2]:fullFrame() or usableFrame(pair[2])
    if vertical then
        -- Share usable horizontal bounds so a side Dock stays uncovered.
        local x, y = math.max(af.x, bf.x), math.min(af.y, bf.y)
        return { x = x, y = y,
            w = math.min(af.x + af.w, bf.x + bf.w) - x,
            h = math.max(af.y + af.h, bf.y + bf.h) - y }, nil,
            { x = x, y = y, w = math.min(af.x + af.w, bf.x + bf.w) - x,
                h = (af.y < bf.y and af.y + af.h or bf.y + bf.h) - y }
    end
    -- Share usable vertical bounds so the menu bar and bottom Dock stay uncovered.
    local x, y = math.min(af.x, bf.x), math.max(af.y, bf.y)
    return { x = x, y = y,
        w = math.max(af.x + af.w, bf.x + bf.w) - x,
        h = math.min(af.y + af.h, bf.y + bf.h) - y }, nil,
        { x = x, y = y, w = (af.x < bf.x and af.x + af.w or bf.x + bf.w) - x,
            h = math.min(af.y + af.h, bf.y + bf.h) - y }
end

local function spanFrame(fullBounds)
    if hs.spaces.screensHaveSeparateSpaces() then
        return nil, "跨屏需在系统设置 → 桌面与程序坞关闭『显示器具有单独的空间』，并注销后重新登录；单屏布局仍可用"
    end
    local list = screens()
    if #list < 2 then return nil, "跨屏需要连接至少两块显示器" end
    local pair = list
    if #list > 2 then
        pair = {}
        local leftX = list[1]:fullFrame().x
        for _, screen in ipairs(list) do
            if near(screen:fullFrame().x, leftX) then table.insert(pair, screen) end
        end
        if #pair ~= 2 then return nil, "请先在显示器排列中将左侧两屏上下对齐" end
    end
    return pairFrame(pair, fullBounds, #list == 2)
end

local function savedSpanFrame(win, action)
    local entry = getSaved(win)
    local pair = entry and unchangedSpanScreens(entry.spanScreens)
    if pair and #pair == 2 and not hs.spaces.screensHaveSeparateSpaces() then
        return pairFrame(pair, action == "fullspan", true)
    end
    return spanFrame(action == "fullspan")
end

local function fullSpan(win, force, intendedScreen)
    local entry = getSaved(win)
    local registered = menuMode.contains(win)
    local wasFullspan = registered or (entry and entry.action == "fullspan")
    local frame, reason, base = savedSpanFrame(win, "fullspan")
    local currentScreen = intendedScreen or win:screen()
    local current = currentScreen:fullFrame()
    -- A remains useful after unplugging displays or changing Spaces settings.
    -- Without a valid spanning region, it is an ordinary single-screen maximize.
    if not frame then
        local target = usableFrame(currentScreen)
        if not force and entry and entry.action == "maximize" and fits(win:frame(), target) then
            return restore(win)
        end
        M.endFullSpanMode(win)
        return apply(win, target, "maximize")
    end
    -- A on the right-hand portrait display fills that display on this desktop.
    -- It must not create a native fullscreen Space or disturb the left window.
    if current.x >= frame.x + frame.w or current.x + current.w <= frame.x then
        if not force and wasFullspan and fits(win:frame(), current) then return restore(win) end
        local baseFrame = rect(current)
        baseFrame.h = math.min(win:frame().h, current.h)
        M.endFullSpanMode(win)
        return apply(win, current, "fullspan", baseFrame)
    end
    -- A only toggles off a window that is still at its intended size. A
    -- registered window clamped by activation/navigation needs repair instead.
    if not force and wasFullspan and fits(win:frame(), frame) then return restore(win) end
    M._spanMaintenance[win:id()] = nil
    if registered then
        return apply(win, frame, "fullspan", base, nil, { repair = true })
    end
    local origin = { frame = rect(win:frame()), entry = getSaved(win) }
    local remembered = remember(win)
    local id = win:id()
    local record = M.lastAction
    local pending = { win = win, record = record, hadSaved = origin.entry ~= nil, action = "fullspan" }
    local function cancel(reason)
        if M._pendingSpan ~= pending then return end
        M._pendingSpan = nil
        menuMode.leave(win)
        if not origin.entry and saved[id] == remembered then saved[id] = nil end
        record.pending, record.succeeded, record.detail = nil, false, reason
        if not pending.quiet then notice(reason or "全屏铺满已取消") end
    end
    pending.controller = { cancel = cancel }
    M._pendingSpan = pending
    local entered = menuMode.enter(win, function()
        if M._pendingSpan ~= pending then return end
        M._pendingSpan = nil
        local ok, result, detail = pcall(apply, win, frame, "fullspan", base, origin)
        if not ok then
            M._pendingSpan = pending
            cancel("全屏铺满失败：" .. tostring(result))
        elseif detail ~= "pending" then
            record.pending, record.succeeded, record.detail = nil, result == true, detail
        end
    end, cancel)
    if not entered then return false, record.detail end
    return true, "pending"
end

-- Native fullscreen uses a shared Space on this spanning setup. A converts it
-- back to a normal window before filling its display(s), preserving the others.
local function exitNativeAndFill(win)
    local id, pid = identity(win)
    local intendedScreen = win:screen()
    local initialFocus = hs.window.focusedWindow()
    local initialFocusID = initialFocus and initialFocus:id()
    local record = M.lastAction
    local pending = { win = win, record = record, hadSaved = getSaved(win) ~= nil, action = "fullspan" }
    local timer, started = nil, hs.timer.secondsSinceEpoch()
    local function cancel(reason)
        if timer then timer:stop(); timer = nil end
        if M._pendingSpan ~= pending then return end
        M._pendingSpan = nil
        record.pending, record.succeeded, record.detail = nil, false, reason
        if not pending.quiet then notice(reason) end
    end
    pending.controller = { cancel = cancel }
    M._pendingSpan = pending
    local called, err = pcall(function() win:setFullScreen(false) end)
    if not called then cancel("无法退出系统全屏：" .. tostring(err)); return false, record.detail end
    timer = hs.timer.doEvery(.1, function()
        local ok, reason = pcall(function()
            local currentID, currentPID = identity(win)
            if currentID ~= id or currentPID ~= pid then return cancel("原窗口已关闭") end
            local elapsed = hs.timer.secondsSinceEpoch() - started
            if not win:isFullScreen() and elapsed >= 1 then
                local focused = hs.window.focusedWindow()
                if focused and focused:isStandard() and focused:id() ~= id
                    and focused:id() ~= initialFocusID then
                    return cancel("焦点已切换，已取消铺满窗口")
                end
                timer:stop(); timer = nil
                M._pendingSpan = nil
                if not validate(win) then
                    record.pending, record.succeeded = nil, false
                    return
                end
                local success, detail = fullSpan(win, true, intendedScreen)
                if detail ~= "pending" then
                    record.pending, record.succeeded, record.detail = nil, success == true, detail
                end
            elseif elapsed >= 6 then cancel("应用未退出系统全屏，请退出后再按 A") end
        end)
        if not ok then
            if M._pendingSpan == nil then M._pendingSpan = pending end
            cancel("退出系统全屏失败：" .. tostring(reason))
        end
    end)
    return true, "pending"
end

-- Workspace operations use the same span repair path, but never toggle a
-- correctly placed window back to its old size on a repeated preset request.
local function deepCopy(value)
    if type(value) ~= "table" then return value end
    local out = {}
    for key, child in pairs(value) do out[key] = deepCopy(child) end
    return out
end

function M.workspaceSpanFrame(uuids, fullBounds)
    if type(uuids) ~= "table" or #uuids ~= 2 or uuids[1] == uuids[2] then
        return nil, "跨屏布局需要两块不同的指定显示器"
    end
    if hs.spaces.screensHaveSeparateSpaces() then return nil, "当前独立空间设置不支持跨屏" end
    local byID, pair = {}, {}
    for _, screen in ipairs(screens()) do byID[screen:getUUID()] = screen end
    for _, uuid in ipairs(uuids) do
        if not byID[uuid] then return nil, "固定布局所需显示器未连接" end
        pair[#pair+1] = byID[uuid]
    end
    return pairFrame(pair, fullBounds, true)
end

function M.captureWorkspaceWindow(win)
    local id, pid = identity(win)
    if not id or not win:isStandard() then error("无法保存普通窗口的位置") end
    local entry = getSaved(win)
    local activeSpan = entry and not entry.maintenanceDisabled
        and (entry.action == "span" or (entry.action == "fullspan" and menuMode.contains(win)))
    local app = win:application()
    return {id=id, pid=pid, frame=rect(win:frame()), minimized=win:isMinimized(),
        fullscreen=win:isFullScreen(), hidden=app and app:isHidden() or false,
        screenUUID=win:screen():getUUID(), saved=deepCopy(entry),
        activeSpan=activeSpan == true, menuMembership=menuMode.contains(win)}
end

function M.cancelWorkspaceOperation(reason)
    if M._workspaceOperation then M._workspaceOperation.cancel(reason or "user-interrupted") end
end

local function workspaceOperation(win, spec, snapshot, callback)
    if M._workspaceOperation then callback(false, "workspace-busy"); return false end
    local id, pid = identity(win)
    if not id or (snapshot and (id ~= snapshot.id or pid ~= snapshot.pid)) then
        callback(false, "原窗口已关闭或进程已改变"); return false
    end
    local bundleOK, bundleID = pcall(function()
        local app = win:application()
        return app and app:bundleID()
    end)
    local op = {win=win, id=id, pid=pid, started=hs.timer.secondsSinceEpoch(), phase="prepare",
        record={action="workspace",time=hs.timer.secondsSinceEpoch(),windowID=id,pid=pid,
            bundleID=bundleOK and bundleID or nil,restoring=snapshot ~= nil,retries=0}}
    M._workspaceOperation = op
    local function finish(ok, reason)
        if op.finished then return end
        op.finished = true
        if op.timer then op.timer:stop() end
        if M._workspaceOperation == op then M._workspaceOperation = nil end
        local pending = M._pendingSpan
        if pending and pending.record == op.record then
            pending.quiet = true
            pending.controller.cancel(reason or "workspace-finished")
        end
        if not ok and op.menuEntered then menuMode.leave(win) end
        op.record.pending, op.record.succeeded, op.record.detail = nil, ok, reason
        op.record.finishedAt = hs.timer.secondsSinceEpoch()
        local readOK, actual = pcall(function() return rect(win:frame()) end)
        if readOK then op.record.actual = actual end
        M.lastWorkspaceOperation = deepCopy(op.record)
        if not ok and not snapshot then M.lastWorkspaceFailure = deepCopy(op.record) end
        callback(ok, reason)
    end
    op.cancel = function(reason) finish(false, reason) end
    local function screenFor(uuid)
        for _, screen in ipairs(screens()) do if screen:getUUID() == uuid then return screen end end
    end
    local function targetFrame()
        if spec.span then return M.workspaceSpanFrame(spec.spanScreens, spec.fullBounds) end
        if spec.screenUUID then
            local screen = screenFor(spec.screenUUID)
            if not screen then return nil, "固定布局显示器已断开" end
            local f, u = usableFrame(screen), spec.unit or {0,0,1,1}
            return {x=f.x+f.w*u[1],y=f.y+f.h*u[2],w=f.w*u[3],h=f.h*u[4]}
        end
        return spec.frame and rect(spec.frame)
    end
    local function place()
        if op.finished then return end
        local f, reason, base = targetFrame()
        if not f or not coveredByScreens(f) then return finish(false, reason or "布局目标已不在连接的显示器上") end
        op.target, op.phase = f, "verify"
        op.record.target = rect(f)
        M.lastAction = op.record
        win:raise()
        -- Shrinking a previously dragged Electron span needs a size write first.
        if not spec.span then win:setSize({w=f.w,h=f.h}) end
        local ok, detail = apply(win, f, spec.span and (spec.fullBounds and "fullspan" or "span") or "workspace",
            base, nil, {repair=spec.fullBounds, automatic=true})
        if not ok then return finish(false, detail) end
        op.record.pending = detail == "pending" or nil
        if detail ~= "pending" then op.record.succeeded = true end
        op.placedAt = hs.timer.secondsSinceEpoch()
    end
    local function step()
        if op.finished then return end
        local currentID, currentPID = identity(win)
        if currentID ~= id or currentPID ~= pid then return finish(false, "原窗口已关闭或进程已改变") end
        local now = hs.timer.secondsSinceEpoch()
        if now-op.started > 8 then return finish(false, "窗口布局超时") end
        if op.phase == "prepare" then
            if not hs.accessibilityState() or not win:isStandard() then return finish(false, "窗口无法调整") end
            -- Check the entire target before changing any window or menu state.
            local f, reason = targetFrame()
            if not f or not coveredByScreens(f) then return finish(false, reason or "布局目标不可用") end
            local app = win:application()
            if not win:isMinimized() and not (app and app:isHidden()) then
                local presented, presentationReason = M.windowPresentationReady(win)
                if not presented then
                    if snapshot and snapshot.fullscreen and not win:isFullScreen() then
                        -- Restoring an earlier native window can switch Spaces.
                        -- Reveal this exact next target before restoring it too.
                        op.presentationState = op.presentationState or {}
                        local ready, reason = M.prepareWorkspacePresentation(
                            {{win=win,id=id,pid=pid}}, op.presentationState)
                        if ready == nil then return end
                        if not ready then return finish(false, reason) end
                    else return finish(false, presentationReason) end
                end
            end
            if app and app:isHidden() then app:unhide() end
            M.endFullSpanMode(win)
            if win:isMinimized() then win:unminimize() end
            op.nativeExit = win:isFullScreen()
            if op.nativeExit then win:setFullScreen(false) end
            op.phase, op.preparedAt = "ready", now
        elseif op.phase == "ready" then
            local app = win:application()
            if (app and app:isHidden()) or win:isMinimized() or win:isFullScreen()
                or now-op.preparedAt < (op.nativeExit and 1 or .12) then return end
            local presented, presentationReason = M.windowPresentationReady(win)
            if not presented then
                if now-op.preparedAt >= 2 then return finish(false, presentationReason) end
                return
            end
            if spec.span and spec.fullBounds then
                op.phase = "menu"
                op.menuEntered = true
                if not menuMode.enter(win, place, function(reason) finish(false, reason) end) then
                    return finish(false, "菜单栏未能切换")
                end
            else place() end
        elseif op.phase == "verify" then
            if op.record.pending or now-(op.lastWriteAt or op.placedAt) < .15 then return end
            if op.record.succeeded == false then return finish(false, op.record.detail or "跨屏未完成") end
            local actual = rect(win:frame())
            op.record.actual = actual
            if not fits(actual, op.target) then
                if now-op.placedAt > 1.2 then return finish(false, "应用未接受固定窗口尺寸") end
                -- AX resize can stick at display edges even after position or
                -- height changes succeed. Retry ordinary placements using Hammerspoon's
                -- built-in edge/terminal workaround instead of changing the
                -- global setFrame behavior used by deliberate spanning windows.
                if not spec.span and op.record.retries < 1
                    and now-(op.lastWriteAt or op.placedAt) >= .3 then
                    local target, reason = targetFrame()
                    if not target or not coveredByScreens(target) then
                        return finish(false, reason or "布局目标已不在连接的显示器上")
                    end
                    for _, key in ipairs({"x", "y", "w", "h"}) do
                        if target[key] ~= op.target[key] then return finish(false, "显示器排列已变化，已停止布局") end
                    end
                    local presented = M.windowPresentationReady(win)
                    if not presented or next(hs.eventtap.checkMouseButtons()) then return end
                    op.record.retries = op.record.retries + 1
                    op.lastWriteAt = now
                    if win.setFrameWithWorkarounds then win:setFrameWithWorkarounds(op.target, 0)
                    else
                        win:setSize({w=op.target.w,h=op.target.h})
                        win:setFrame(op.target, 0)
                        win:setSize({w=op.target.w,h=op.target.h})
                    end
                end
                return
            end
            if snapshot then
                -- Undo restores the previous R history too, rather than making
                -- the preset frame the next origin of an unrelated window action.
                saved[id] = deepCopy(snapshot.saved)
                if snapshot.activeSpan and not spec.span and saved[id] then saved[id].maintenanceDisabled = true end
                if snapshot.fullscreen then win:setFullScreen(true) end
                if snapshot.minimized then win:minimize() end
                local app = win:application()
                if snapshot.hidden and app then app:hide() end
                op.phase, op.restoredAt = "restore-state", now
            else finish(true, rect(win:frame())) end
        elseif op.phase == "restore-state" then
            if snapshot.fullscreen and not win:isFullScreen() then return end
            if snapshot.minimized and not win:isMinimized() then return end
            local app = win:application()
            if snapshot.hidden and app and not app:isHidden() then return end
            if now-op.restoredAt < .12 then return end
            finish(true, rect(win:frame()))
        end
    end
    op.timer = hs.timer.doEvery(.05, function()
        local ok, err = pcall(step)
        if not ok then finish(false, tostring(err)) end
    end)
    return true
end

function M.applyWorkspaceWindow(win, spec, callback)
    return workspaceOperation(win, spec, nil, callback)
end

function M.restoreWorkspaceWindow(win, snapshot, callback)
    local spec = {frame=deepCopy(snapshot.frame)}
    local entry = snapshot.saved
    if snapshot.activeSpan and entry and entry.spanScreens and unchangedSpanScreens(entry.spanScreens) then
        local uuids = {}
        for _, screen in ipairs(entry.spanScreens) do uuids[#uuids+1] = screen.uuid end
        if #uuids == 2 then
            spec = {span=true, fullBounds=snapshot.menuMembership, spanScreens=uuids}
        end
    end
    if not spec.span and snapshot.fullscreen then
        -- A native-fullscreen snapshot includes menu/Dock pixels which a normal
        -- window cannot occupy. Restore to the saved screen's usable area first,
        -- then workspaceOperation re-enters fullscreen after frame verification.
        local destination
        for _, screen in ipairs(screens()) do
            if screen:getUUID() == snapshot.screenUUID then destination = screen; break end
        end
        destination = destination or win:screen() or hs.screen.primaryScreen()
        spec.frame = clamp(spec.frame, usableFrame(destination))
    elseif not spec.span and not coveredByScreens(spec.frame) then
        local destination = win:screen() or hs.screen.primaryScreen()
        spec.frame = clamp(spec.frame, usableFrame(destination))
    end
    return workspaceOperation(win, spec, snapshot, callback)
end

local units = {
    left = { 0, 0, .5, 1 }, right = { .5, 0, .5, 1 },
    up = { 0, 0, 1, .5 }, down = { 0, .5, 1, .5 },
    topLeft = { 0, 0, .5, .5 }, topRight = { .5, 0, .5, .5 },
    bottomLeft = { 0, .5, .5, .5 }, bottomRight = { .5, .5, .5, .5 },
    center = { .1, .1, .8, .8 },
}

local function portraitScreen()
    local config = M.workspaceLayouts and M.workspaceLayouts.config
    local uuid = config and config.roles and config.roles.right or "REPLACE_WITH_RIGHT_UUID"
    for _, screen in ipairs(screens()) do
        if screen:getUUID() == uuid then return screen end
    end
end

local function showLayouts(win)
    if not M._chooser then
        M._chooser = hs.chooser.new(function(choice)
            local target, expected = M._choiceWindow, M._choiceIdentity
            M._choiceWindow, M._choiceIdentity = nil, nil
            if not choice then return end
            if choice.preset and M.workspaceLayouts then return M.workspaceLayouts.apply(choice.preset) end
            if choice.workspaceUndo and M.workspaceLayouts then return M.workspaceLayouts.undo() end
            local id, pid = identity(target)
            if not expected or id ~= expected.id or pid ~= expected.pid then
                notice("原来的窗口已关闭，请重新选择窗口")
                return
            end
            M.dispatch(choice.action, target)
        end):rows(12):width(48):searchSubText(true)
            :placeholderText("固定布局 / 当前窗口布局（Esc 取消）")
    end
    local choices = {
        { text = "全屏铺满 / 恢复", subText = "A · 左侧铺满上下两屏并隐藏菜单栏；右侧铺满竖屏", action = "fullspan" },
        { text = "单屏最大化 / 恢复", subText = "铺满当前显示器", action = "maximize" },
        { text = "移到右侧竖屏并铺满", subText = "L · 当前窗口铺满固定右屏", action = "portraitMaximize" },
        { text = "跨两屏 / 恢复", subText = "两屏时跨两屏；三屏时跨左侧上下两屏", action = "span" },
        { text = "左半屏", subText = "←", action = "left" },
        { text = "右半屏", subText = "→", action = "right" },
        { text = "上半屏", subText = "↑", action = "up" },
        { text = "下半屏", subText = "↓", action = "down" },
        { text = "左上四分之一", action = "topLeft" },
        { text = "右上四分之一", action = "topRight" },
        { text = "左下四分之一", action = "bottomLeft" },
        { text = "右下四分之一", action = "bottomRight" },
        { text = "居中，占屏幕 80%", action = "center" },
        { text = "恢复原位置", subText = "R · 恢复本次调整之前的位置", action = "restore" },
    }
    for index, screen in ipairs(screens()) do
        table.insert(choices, { text = "移到屏幕 " .. index .. " · " .. screen:name(),
            subText = "屏号按左到右、同列上到下排列", action = "screen" .. index })
    end
    if M.workspaceLayouts then
        local combined = M.workspaceLayouts.choices()
        table.insert(combined, {text="撤销上次整套布局", subText="恢复上次调整的窗口位置、最小化与跨屏状态", workspaceUndo=true})
        for _, choice in ipairs(choices) do table.insert(combined, choice) end
        choices = combined
    end
    local id, pid = identity(win)
    M._choiceWindow, M._choiceIdentity = win, { id = id, pid = pid }
    M._chooser:choices(choices):query(""):show()
    return true
end

local aliases = { a = "fullspan", s = "workspaceClaude", z = "workspaceFocus", f17 = "workspaceTerminal",
    f18 = "workspaceOffice", space = "layouts", r = "restore", l = "portraitMaximize", f19 = "portraitMaximize",
    ["1"] = "screen1", ["2"] = "screen2", ["3"] = "screen3", ["4"] = "screen4" }

local function dispatch(action, optionalWindow)
    action = aliases[action] or action
    local win = optionalWindow or hs.window.focusedWindow()
    if action == "fullspan" and hs.accessibilityState() and win and identity(win) and win:isFullScreen() then
        return exitNativeAndFill(win)
    end
    if action == "layouts" then return showLayouts(win) end
    if not validate(win, action == "portraitMaximize") then return false end
    if action == "portraitMaximize" then
        local destination = portraitScreen()
        if not destination then return notice("右侧竖屏未连接，窗口保持原位") end
        local record = M.lastAction
        remember(win) -- Preserve R's origin before the operation's initial size write.
        local started = M.applyWorkspaceWindow(win, {screenUUID=destination:getUUID()}, function(ok, detail)
            record.pending, record.succeeded, record.detail = nil, ok, type(detail)=="string" and detail or nil
            M.lastAction = record
            if not ok and detail ~= "explicit-layout" and detail ~= "user-interrupted" then
                notice(detail or "窗口未能移到右侧竖屏")
            end
        end)
        return started, started and "pending" or record.detail
    end
    if action == "restore" then return restore(win) end
    if action == "fullspan" then return fullSpan(win) end
    if action == "maximizeOnly" then
        local bounds, current = usableFrame(win:screen()), rect(win:frame())
        local entry = remember(win)
        if not fits(current, bounds) then
            local previousAction = entry.action
            if previousAction == "span" or previousAction == "fullspan" then
                local target = savedSpanFrame(win, previousAction)
                if entry.maintenanceDisabled or not target or not fits(current, target) then
                    previousAction = nil
                end
            end
            entry.maximizeRestore = { frame = current, screenUUID = win:screen():getUUID(),
                screenFrame = rect(bounds), action = previousAction, topology = topology(), spanScreens=entry.spanScreens }
        elseif entry.action ~= "maximizeOnly" then
            -- A new maximize on an already maximized window has no smaller
            -- immediate predecessor. Do not reuse an unrelated old cycle.
            entry.maximizeRestore = nil
        end
        M.endFullSpanMode(win)
        return apply(win, bounds, action)
    end
    if action == "restoreOrMinimize" then
        local entry = getSaved(win)
        local origin = entry and entry.maximizeRestore
        if entry and entry.action == "maximizeOnly" and origin then
            local previousAction = origin.action
            entry.maximizeRestore = nil
            local sameTopology = origin.topology == topology() or unchangedSpanScreens(origin.spanScreens) ~= nil
            if sameTopology and previousAction == "fullspan" then return fullSpan(win, true) end
            if sameTopology and previousAction == "span" then
                local target, reason, base = savedSpanFrame(win, "span")
                if not target then return notice(reason) end
                M.endFullSpanMode(win)
                return apply(win, target, "span", base)
            end
            local destination
            for _, screen in ipairs(screens()) do
                if screen:getUUID() == origin.screenUUID then destination = screen; break end
            end
            destination = destination or win:screen() or hs.screen.primaryScreen()
            if not destination then return notice("未找到可用显示器") end
            local target = clamp(origin.frame, usableFrame(destination))
            M.endFullSpanMode(win)
            win:setSize({w=target.w,h=target.h})
            return apply(win, target, "restoredFromMaximize")
        end
        if entry and (entry.action == "maximize" or entry.action == "fullspan"
            or entry.action == "span") then return restore(win) end
        action = "minimize"
    end
    if action == "minimize" then
        M.endFullSpanMode(win)
        -- minimize() returns the window and always uses the system animation;
        -- AXMinimized may still be false until that animation completes.
        local requested = win:minimize()
        if requested then
            local entry = getSaved(win)
            if entry then entry.action = "minimize" end
        end
        return requested ~= nil and requested ~= false, requested and "minimize-requested" or "minimize-rejected"
    end
    if action == "maximize" or action == "span" then
        local entry = getSaved(win)
        if action == "maximize" then
            if entry and entry.action == action then return restore(win) end
            local target = usableFrame(win:screen())
            M.endFullSpanMode(win)
            return apply(win, target, action)
        end
        local frame, reason, dragBase = spanFrame()
        if not frame then return notice(reason) end
        if entry and entry.action == action and fits(win:frame(), frame) then return restore(win) end
        M.endFullSpanMode(win)
        return apply(win, frame, action, dragBase, nil, { repair = entry and entry.action == action })
    end
    local unit = units[action]
    if unit then
        -- Read the target before changing the old layout's maintenance state.
        local screen = win:screen()
        M.endFullSpanMode(win)
        local f = usableFrame(screen)
        return apply(win, { x = f.x + f.w * unit[1], y = f.y + f.h * unit[2],
            w = f.w * unit[3], h = f.h * unit[4] }, action)
    end
    local index = tonumber(tostring(action):match("^screen(%d+)$"))
    if index then
        local destination = screens()[index]
        if not destination then return notice("当前没有屏幕 " .. index) end
        M.endFullSpanMode(win)
        local source = usableFrame(win:screen())
        local bounds, frame = usableFrame(destination), win:frame()
        local target = {
            x = bounds.x + (frame.x - source.x) / source.w * bounds.w,
            y = bounds.y + (frame.y - source.y) / source.h * bounds.h,
            w = frame.w / source.w * bounds.w,
            h = frame.h / source.h * bounds.h,
        }
        remember(win).action = action
        win:setFrame(clamp(target, bounds), 0)
        return true, rect(win:frame())
    end
    return notice("未知窗口操作：" .. tostring(action))
end

function M.dispatch(action, optionalWindow)
    local requested = aliases[action] or action
    local preset = ({workspaceFocus="focus", workspaceTerminal="terminal", workspaceOffice="office",
        workspaceClaude="claude"})[requested]
    if preset then
        if M.workspaceLayouts then return M.workspaceLayouts.apply(preset) end
        return notice("固定布局尚未加载")
    end
    -- Missing destinations must not cancel an otherwise valid in-flight layout.
    local index = tonumber(tostring(requested):match("^screen(%d+)$"))
    if index and not screens()[index] then return notice("当前没有屏幕 " .. index) end
    if requested == "portraitMaximize" and not portraitScreen() then
        return notice("右侧竖屏未连接，窗口保持原位")
    end
    if requested == "span" then
        local target, reason = spanFrame()
        if not target then return notice(reason) end
    end
    if requested == "span" or requested == "fullspan" then
        local win = optionalWindow or hs.window.focusedWindow()
        if win and identity(win) then
            local ready, reason = M.windowPresentationReady(win)
            if not ready then return notice(reason) end
        end
    end
    if requested ~= "layouts" then
        if M.workspaceLayouts then M.workspaceLayouts.cancel("explicit-layout") end
        M.cancelWorkspaceOperation("explicit-layout")
        invalidateNavigation("explicit layout action")
    end
    -- Opening the chooser is not a layout change. Its eventual selection is
    -- dispatched normally, and cancelling the chooser leaves layout intent intact.
    if M._pendingSpan and requested ~= "layouts" then
        local pending = M._pendingSpan
        local target = optionalWindow or hs.window.focusedWindow()
        local targetID, targetPID = identity(target)
        local pendingID, pendingPID = identity(pending.win)
        if targetID == pendingID and targetPID == pendingPID and requested == pending.action
            and (requested == "fullspan" or (requested == "span" and pending.automatic)) then
            -- Repeated fullspan or span during automatic repair joins the same operation
            -- instead of cancelling it and accidentally restoring the old frame.
            pending.record.pending = true
            return true, "pending"
        end
        pending.quiet = true
        pending.controller.cancel("新的窗口操作取消了跨屏拖动")
        if targetID == pendingID and targetPID == pendingPID
            and (requested == (pending.action or "span") or (requested == "restore" and not pending.hadSaved)) then
            M.lastAction = { action = action, time = hs.timer.secondsSinceEpoch(), succeeded = true }
            return true, rect(target:frame())
        end
    end
    M.lastAction = { action = action, time = hs.timer.secondsSinceEpoch() }
    local ok, result, detail = pcall(dispatch, action, optionalWindow)
    if not ok then
        hs.printf("[window_manager] %s", tostring(result))
        return notice("窗口操作未完成，请重试；详细信息见 Hammerspoon Console")
    end
    if detail == "pending" then M.lastAction.pending = true
    else M.lastAction.succeeded = result == true end
    M.lastAction.detail = type(detail) == "string" and detail or nil
    return result, detail
end

-- AppKit can clamp a tall window after focus, task navigation, or a delayed
-- resize. Events cover all three; a foreground-only poll covers missed AX
-- notifications. Never focus or drag a background window to maintain it.
local function maintainedAction(win)
    local entry = win and getSaved(win)
    if not entry or entry.maintenanceDisabled then return nil end
    if entry.action == "span" then return "span" end
    if entry.action == "fullspan" and menuMode.contains(win) then return "fullspan" end
end

-- Only an intact, deliberately registered Codex span can authorize navigation
-- recovery. A caller must invoke this synchronously before opening the task.
local function codexWindow(win)
    local app = win and win:application()
    return app and app:bundleID() == "com.openai.codex"
end

local function navigationCandidate(win)
    if not win or not codexWindow(win) or not win:isStandard()
        or win:isFullScreen() or win:isMinimized() then return nil end
    local action = maintainedAction(win)
    if not action then return nil end
    local target = savedSpanFrame(win, action)
    if not target or not fits(win:frame(), target) then return nil end
    local id, pid = identity(win)
    if not id then return nil end
    return { win = win, id = id, pid = pid, action = action, frame = rect(target),
        originalFrame = rect(win:frame()), topology = topology(),
        expiresAt = hs.timer.secondsSinceEpoch() + navigationTTL }
end

local function navigationStatus(token, retained)
    return { armed = true, windowID = token.id, pid = token.pid,
        expiresAt = token.expiresAt, retained = retained == true }
end

function M.prepareTaskNavigation()
    local ok, result = pcall(function()
        if not hs.accessibilityState() then return { armed = false, reason = "accessibility" } end
        if M._userGesture then return { armed = false, reason = "mouse-interaction" } end
        local candidates, seen = {}, {}
        local front = hs.window.focusedWindow()
        if codexWindow(front) then
            local candidate = navigationCandidate(front)
            if candidate then candidates[1] = candidate end
        else
            for _, app in ipairs(hs.application.applicationsForBundleID("com.openai.codex")) do
                for _, win in ipairs(app:allWindows()) do
                    local candidate = navigationCandidate(win)
                    if candidate then
                        local key = tostring(candidate.pid) .. ":" .. tostring(candidate.id)
                        if not seen[key] then candidates[#candidates + 1] = candidate; seen[key] = true end
                    end
                end
            end
        end
        if #candidates == 1 then
            M._navigationToken = candidates[1]
            M._spanMaintenance[candidates[1].id] = nil
            return navigationStatus(candidates[1])
        end
        if #candidates > 1 then
            invalidateNavigation("ambiguous Codex windows")
            return { armed = false, reason = "ambiguous" }
        end
        -- Rapid successive task keys can arrive while the first repair is in
        -- flight. Its unexpired proof remains valid until an explicit user action.
        local token = M._navigationToken
        if token and hs.timer.secondsSinceEpoch() <= token.expiresAt and token.topology == topology() then
            local id, pid = identity(token.win)
            if id == token.id and pid == token.pid and maintainedAction(token.win) == token.action
                and (not codexWindow(front) or identity(front) == id) then
                token.expiresAt = hs.timer.secondsSinceEpoch() + navigationTTL
                M._spanMaintenance[token.id] = nil
                return navigationStatus(token, true)
            end
        end
        invalidateNavigation("no intact Codex span")
        return { armed = false, reason = "no-intact-span" }
    end)
    if ok then return result end
    invalidateNavigation("navigation inspection failed")
    return { armed = false, reason = tostring(result) }
end

local function navigationClamp(win, action, actual)
    local token = M._navigationToken
    if not token then return false end
    local id, pid = identity(win)
    if hs.timer.secondsSinceEpoch() > token.expiresAt or token.topology ~= topology() then
        invalidateNavigation("navigation token expired or topology changed")
        return false
    end
    if id ~= token.id or pid ~= token.pid or action ~= token.action then return false end
    for _, screen in ipairs(screens()) do
        local full = screen:fullFrame()
        local within = full.x >= token.frame.x - tolerance
            and full.x + full.w <= token.frame.x + token.frame.w + tolerance
            and full.y + full.h > token.frame.y and full.y < token.frame.y + token.frame.h
        local withinHorizontal = full.y >= token.frame.y - 32
            and full.y + full.h <= token.frame.y + token.frame.h + 32
            and full.x + full.w > token.frame.x and full.x < token.frame.x + token.frame.w
        if within or withinHorizontal then
            local bounds = action == "fullspan" and full or usableFrame(screen)
            if fits(actual, bounds) then return true end
        end
    end
    return false
end

local function intersectionArea(a, b)
    return math.max(0, math.min(a.x+a.w,b.x+b.w)-math.max(a.x,b.x))
        * math.max(0, math.min(a.y+a.h,b.y+b.h)-math.max(a.y,b.y))
end

local function reconcileTopology()
    local signature, snapshot = topology()
    if signature == M._topology then return end
    M._topology, M._screenSnapshot = signature, snapshot
    invalidateNavigation("display topology changed")
    M._userGesture = nil
    if M._pendingSpan then
        M._pendingSpan.quiet = true
        M._pendingSpan.controller.cancel("显示器排列发生变化，请重试")
    end
    local registered = {}
    for _, win in ipairs(menuMode.members()) do registered[win:id()] = win end
    for id, entry in pairs(saved) do
        if (entry.action == "span" or entry.action == "fullspan")
            and not unchangedSpanScreens(entry.spanScreens) then
            entry.maintenanceDisabled = true
            M._spanMaintenance[id] = nil
            if registered[id] then menuMode.leave(registered[id]) end
        end
    end
    local list = screens()
    local record = { time = hs.timer.secondsSinceEpoch(), relocated = {}, errors = {} }
    M.lastTopologyChange = record
    if #list == 0 then return end
    for _, win in ipairs(hs.window.allWindows()) do
        local ok, err = pcall(function()
            if not win:isStandard() or win:isFullScreen() then return end
            local f, best, bestArea = win:frame(), nil, -1
            for _, screen in ipairs(list) do
                local b = screen:fullFrame()
                local overlap = intersectionArea(f, b)
                if overlap > bestArea then best, bestArea = screen, overlap end
            end
            -- Leave ordinary half-screen and valid spanning windows untouched.
            if coveredByScreens(f) then return end
            local destination = bestArea > 0 and best or hs.screen.primaryScreen() or list[1]
            local target = clamp(f, usableFrame(destination))
            win:setSize({w=target.w,h=target.h})
            win:setFrame(target, 0)
            record.relocated[#record.relocated + 1] = win:id()
        end)
        if not ok then record.errors[#record.errors + 1] = tostring(err) end
    end
end

local function installMouseGuard()
    local types, properties = hs.eventtap.event.types, hs.eventtap.event.properties
    local down = { [types.leftMouseDown]=true, [types.rightMouseDown]=true, [types.otherMouseDown]=true }
    local dragged = { [types.leftMouseDragged]=true, [types.rightMouseDragged]=true, [types.otherMouseDragged]=true }
    local up = { [types.leftMouseUp]=true, [types.rightMouseUp]=true, [types.otherMouseUp]=true }
    local events = { types.mouseMoved }
    for kind in pairs(down) do events[#events+1] = kind end
    for kind in pairs(dragged) do events[#events+1] = kind end
    for kind in pairs(up) do events[#events+1] = kind end
    local function handleMouse(event)
        if event:getProperty(properties.eventSourceUserData) == dragEventTag then
            local pending = M._pendingSpan
            if pending and pending.controller.observeOwnEvent then pending.controller.observeOwnEvent(event) end
            return false
        end
        local kind = event:getType()
        if M._spanPointerQuietUntil and (kind == types.mouseMoved or down[kind] or dragged[kind]) then
            M._spanPointerQuietUntil = hs.timer.secondsSinceEpoch() + .7
        end
        local pending = M._pendingSpan
        local controller = pending and pending.controller
        if kind == types.mouseMoved and controller and controller.isStationaryFeedback
            and controller.isStationaryFeedback(event) then return false end
        local ownsPointer = controller and controller.isDragging and controller.isDragging()
        local active = pending or M._workspaceOperation
            or (M.workspaceLayouts and M.workspaceLayouts._transaction)
        if ((down[kind] or dragged[kind]) and active) or (kind == types.mouseMoved and ownsPointer) then
            -- Capture before cancel: callbacks synchronously clear pending state
            -- and post mouse-up, which would overwrite the triggering evidence.
            local point = event:location()
            M.lastMouseInterruption = {time=hs.timer.secondsSinceEpoch(), type=kind,
                tag=event:getProperty(properties.eventSourceUserData),
                sourcePID=event:getProperty(properties.eventSourceUnixProcessID),
                sourceStateID=event:getProperty(properties.eventSourceStateID),
                location={x=point.x,y=point.y}, deltaX=event:getProperty(properties.mouseEventDeltaX),
                deltaY=event:getProperty(properties.mouseEventDeltaY),
                eventTimestamp=event.timestamp and event:timestamp() or nil,
                pendingAction=pending and pending.action,
                windowID=pending and pending.win and pending.win:id(),
                controller=controller and controller.diagnostics and controller.diagnostics() or nil}
        end
        if down[kind] or dragged[kind] then
            if M.workspaceLayouts then M.workspaceLayouts.cancel("user-interrupted") end
            M.cancelWorkspaceOperation("user-interrupted")
        end
        pending = M._pendingSpan
        if pending and kind == types.mouseMoved and pending.controller.mouseMoved then
            pending.controller.mouseMoved()
        end
        ownsPointer = pending and pending.controller.isDragging and pending.controller.isDragging()
        if pending and (down[kind] or dragged[kind] or (kind == types.mouseMoved and ownsPointer)) then
            -- Pointer motion alone says nothing about the intended window layout.
            -- Keep the short-lived navigation proof for a later quiet retry; a
            -- real button gesture still invalidates it immediately below.
            if not (kind == types.mouseMoved and pending.automatic and pending.repair) then
                invalidateNavigation("real mouse interrupted span")
            end
            pending.quiet = true
            pending.controller.cancel("user-interrupted")
        end
        if down[kind] or dragged[kind] then
            invalidateNavigation("real mouse interaction")
            local win = hs.window.focusedWindow()
            local id, pid = identity(win)
            if win and maintainedAction(win) and (not M._userGesture or M._userGesture.id ~= id) then
                M._userGesture = {win=win,id=id,pid=pid,frame=rect(win:frame())}
            end
        elseif up[kind] and M._userGesture then
            local gesture = M._userGesture
            if M._gestureTimer then M._gestureTimer:stop() end
            M._gestureTimer = hs.timer.doAfter(.1, function()
                M._gestureTimer = nil
                if M._userGesture ~= gesture then return end
                M._userGesture = nil
                local id, pid = identity(gesture.win)
                if id == gesture.id and pid == gesture.pid and not fits(gesture.win:frame(), gesture.frame) then
                    M.endFullSpanMode(gesture.win)
                end
            end)
        end
        return false
    end
    M._mouseGuard = hs.eventtap.new(events, function(event)
        local ok, err = pcall(handleMouse, event)
        if not ok then hs.printf("[window_manager] Mouse guard failed: %s", tostring(err)) end
        return false
    end):start()
end

local function maintainSpan(win)
    if not win or M._pendingSpan or M._workspaceOperation or M._userGesture then return end
    if M._spanPointerQuietUntil then
        if hs.timer.secondsSinceEpoch() < M._spanPointerQuietUntil then return end
        M._spanPointerQuietUntil = nil
    end
    if M.workspaceLayouts and M.workspaceLayouts._transaction then return end
    local action = maintainedAction(win)
    if not action then return end
    local id, pid = identity(win)
    local focusedID, focusedPID = identity(hs.window.focusedWindow())
    if id ~= focusedID or pid ~= focusedPID or win:isFullScreen() or win:isMinimized() then return end
    -- S keeps the menu bar visible; A uses the complete physical display bounds.
    local target, _, base = savedSpanFrame(win, action)
    if not target or not base then return end
    local now = hs.timer.secondsSinceEpoch()
    local state = M._spanMaintenance[id]
    if not state or state.pid ~= pid then
        state = { pid = pid, attempts = 0, nextAttempt = 0 }
        M._spanMaintenance[id] = state
    end
    local actual = win:frame()
    if fits(actual, target) then
        state.attempts, state.stableSince = 0, state.stableSince or now
        return
    end
    state.stableSince = nil
    -- Only repair a recognisable single-screen clamp, not a deliberate move.
    local verticalClamp = near(actual.x, target.x) and actual.y >= target.y - tolerance
        and actual.y - target.y <= 32 and math.abs(actual.w - target.w) <= 32
        and math.abs(actual.h - base.h) <= 32 and target.h - actual.h > 32
    local horizontalClamp = near(actual.y, target.y) and actual.x >= target.x - tolerance
        and actual.x - target.x <= 32 and math.abs(actual.h - target.h) <= 32
        and math.abs(actual.w - base.w) <= 32 and target.w - actual.w > 32
    if not verticalClamp and not horizontalClamp and not navigationClamp(win, action, actual) then return end
    if state.attempts >= 3 or now < state.nextAttempt then return end
    if not M.windowPresentationReady(win) then return end
    local flags = hs.eventtap.checkKeyboardModifiers()
    if flags.cmd or flags.alt or flags.ctrl or flags.shift or flags.fn
        or next(hs.eventtap.checkMouseButtons()) then return end
    state.attempts, state.nextAttempt = state.attempts + 1, now + .7
    local record = { action = action == "fullspan" and "maintainFullspan" or "maintainSpan",
        time = now, windowID = id, attempt = state.attempts }
    M.lastAction = record
    local ok, result, detail = pcall(apply, win, target, action, base, nil,
        { automatic = true, repair = true })
    if not ok then
        record.succeeded, record.detail = false, tostring(result)
        hs.printf("[window_manager] Span maintenance failed: %s", tostring(result))
    elseif detail == "pending" then record.pending = true
    else record.succeeded = result == true end
end

local function maintainSafely(win)
    local ok, err = pcall(function() maintainSpan(win or hs.window.focusedWindow()) end)
    if not ok then hs.printf("[window_manager] Span check failed: %s", tostring(err)) end
end

local function maintainSpanOnChange(win, _, event)
    if M._pendingSpan or not maintainedAction(win) then return end
    local id, pid = identity(win)
    local focusedID, focusedPID = identity(hs.window.focusedWindow())
    -- Background events must not cancel the foreground window's debounce.
    if id ~= focusedID or pid ~= focusedPID then return end
    local state = M._spanMaintenance[id]
    if state and (event == hs.window.filter.windowFocused or event == hs.window.filter.windowTitleChanged) then
        -- A different task navigation is a new repair opportunity. Our own frame
        -- writes emit moved events, which must never reset the retry limit.
        state.attempts = 0
    end
    if M._focusTimer then M._focusTimer:stop() end
    M._focusTimer = hs.timer.doAfter(.12, function()
        M._focusTimer = nil
        maintainSafely(win)
    end)
end

function M.stop(preserveMenuMode)
    if M.workspaceLayouts then M.workspaceLayouts.cancel("stopped") end
    M.cancelWorkspaceOperation("stopped")
    invalidateNavigation("window configuration stopped")
    M._userGesture = nil
    M._spanPointerQuietUntil = nil
    if M._mouseGuard then M._mouseGuard:stop(); M._mouseGuard = nil end
    if M._gestureTimer then M._gestureTimer:stop(); M._gestureTimer = nil end
    if M._screenWatcher then M._screenWatcher:stop(); M._screenWatcher = nil end
    if M._topologyTimer then M._topologyTimer:stop(); M._topologyTimer = nil end
    if M._maintenanceTimer then M._maintenanceTimer:stop(); M._maintenanceTimer = nil end
    if M._focusTimer then M._focusTimer:stop(); M._focusTimer = nil end
    if M._focusFilter then M._focusFilter:unsubscribeAll(); M._focusFilter = nil end
    if M._pendingSpan then
        M._pendingSpan.quiet = true
        M._pendingSpan.controller.cancel("窗口配置已停止")
    end
    if not preserveMenuMode then menuMode.stop() end
    for _, hotkey in ipairs(M._hotkeys) do hotkey:delete() end
    M._hotkeys = {}
    if M._chooser then M._chooser:delete(); M._chooser = nil end
    M._choiceWindow, M._choiceIdentity = nil, nil
    return M
end

function M.start(preserveMenuMode)
    M.stop(preserveMenuMode)
    M._topology, M._screenSnapshot = topology()
    -- Migrate current-session registrations loaded by the previous configuration.
    for _, entry in pairs(saved) do
        if not entry.spanScreens and not entry.maintenanceDisabled
            and (entry.action == "span" or entry.action == "fullspan") then
            local target = spanFrame(entry.action == "fullspan")
            if target then entry.spanScreens, entry.spanTarget = spanScreens(target), rect(target) end
        end
    end
    installMouseGuard()
    M._screenWatcher = hs.screen.watcher.new(function()
        if M._topologyTimer then M._topologyTimer:stop() end
        M._topologyTimer = hs.timer.doAfter(.2, function()
            M._topologyTimer = nil
            local ok, err = pcall(reconcileTopology)
            if not ok then hs.printf("[window_manager] Topology recovery failed: %s", tostring(err)) end
        end)
    end):start()
    for _, key in ipairs({ "a", "s", "z", "f17", "f18", "space", "r", "f19", "1", "2", "3", "4", "left", "right", "up", "down" }) do
        local action = key
        local hotkey = hs.hotkey.bind(mods, key, function() M.dispatch(action) end)
        if hotkey then table.insert(M._hotkeys, hotkey)
        else hs.printf("[window_manager] Could not bind %s", key) end
    end
    -- Plain physical Win+arrows use separate private keys so the original
    -- Command+Shift+Win layouts and Win+Shift screen moves keep their meanings.
    for key, action in pairs({ u = "maximizeOnly", m = "restoreOrMinimize", n = "left", o = "right" }) do
        local requested = action
        local hotkey = hs.hotkey.bind(mods, key, function() M.dispatch(requested) end)
        if hotkey then table.insert(M._hotkeys, hotkey)
        else hs.printf("[window_manager] Could not bind %s", key) end
    end
    M._focusFilter = hs.window.filter.new(true)
        :subscribe({ hs.window.filter.windowFocused, hs.window.filter.windowMoved,
            hs.window.filter.windowTitleChanged }, maintainSpanOnChange)
    M._maintenanceTimer = hs.timer.doEvery(.4, function()
        maintainSafely()
    end)
    return M
end

function M.status()
    local result = { accessibility = hs.accessibilityState(), hotkeyCount = #M._hotkeys,
        modifiers = mods, screens = {}, lastAction = M.lastAction, lastSpanFailure = M.lastSpanFailure,
        lastTopologyChange = M.lastTopologyChange, navigation = M._navigationToken and navigationStatus(M._navigationToken) }
    for index, screen in ipairs(screens()) do
        table.insert(result.screens, { index = index, id = screen:id(), uuid = screen:getUUID(), name = screen:name(),
            frame = rect(screen:frame()), fullFrame = rect(screen:fullFrame()) })
    end
    local _, reason = spanFrame()
    result.spanAvailable = reason == nil
    result.spanUnavailableReason = reason
    return result
end

return M
