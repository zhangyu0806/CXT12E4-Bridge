-- Physical T90 Win+Shift+arrows are bridged to private Hyper+H/J/K/L.
-- Reuse window_manager's numbered-screen action so its restore cache survives.
-- Left/right cycle through that same numbering; up/down follow screen geometry.
local M = { _hotkeys = {} }
local steps = { left = -1, right = 1 }
local methods = { up = "toNorth", down = "toSouth" }
local labels = { up = "上方", down = "下方" }

local function rect(f)
    return { x = f.x, y = f.y, w = f.w, h = f.h }
end

local function fail(message)
    hs.alert.show(message, 1.5)
    return false, message
end

local function relativeOffset(position, origin, available)
    if available <= 4 then return .5 end
    return math.max(0, math.min(1, (position - origin) / available))
end

local function move(direction, optionalWindow)
    local win = optionalWindow or hs.window.focusedWindow()
    if not hs.accessibilityState() then return fail("请开启 Hammerspoon 的辅助功能权限") end
    if not win or not win:application() or not win:isStandard() then
        return fail("请先点选要移动的普通应用窗口")
    end
    if win:isFullScreen() then return fail("请先退出 macOS 原生全屏，再移动窗口") end
    if not steps[direction] and not methods[direction] then return fail("未知移屏方向") end
    local source = win:screen()
    if not source then return fail("未找到当前窗口所在的显示器") end
    local frame = win:frame()
    local ordered = M.manager.status().screens
    local destination, index
    if steps[direction] then
        if #ordered < 2 then return fail("当前只有一块显示器，无需移屏") end
        local current
        for position, screen in ipairs(ordered) do
            if screen.uuid == source:getUUID() then current = position; break end
        end
        if not current then return fail("显示器排列发生变化，请重试") end
        local target = ordered[(current - 1 + steps[direction]) % #ordered + 1]
        for _, screen in ipairs(hs.screen.allScreens()) do
            if screen:getUUID() == target.uuid then destination = screen; break end
        end
        index = target.index
    else
        destination = source[methods[direction]](source, frame, true)
        if not destination then return fail(labels[direction] .. "没有其他显示器") end
        for _, screen in ipairs(ordered) do
            if screen.uuid == destination:getUUID() then index = screen.index; break end
        end
    end
    -- Invalid destinations leave any active span and its recovery state intact.
    if not destination or not index then return fail("显示器排列发生变化，请重试") end
    if M.manager._pendingSpan then
        local pending = M.manager._pendingSpan
        pending.quiet = true
        pending.controller.cancel("移屏操作取消了跨屏拖动")
    end
    -- Cancelling an in-flight resize may restore a different frame or screen.
    source = win:screen()
    if not source then return fail("未找到当前窗口所在的显示器") end
    frame = win:frame()

    M.manager.endFullSpanMode(win)
    local old, bounds = M.manager.usableFrame(source), M.manager.usableFrame(destination)
    local fillsScreen = frame.x <= old.x + 4 and frame.y <= old.y + 4
        and frame.x + frame.w >= old.x + old.w - 4
        and frame.y + frame.h >= old.y + old.h - 4
    local target = rect(bounds)
    if not fillsScreen then
        target.w, target.h = math.min(frame.w, bounds.w), math.min(frame.h, bounds.h)
        target.x = bounds.x + relativeOffset(frame.x, old.x, old.w - frame.w) * (bounds.w - target.w)
        target.y = bounds.y + relativeOffset(frame.y, old.y, old.h - frame.h) * (bounds.h - target.h)
    end

    local ok, reason = M.manager.dispatch("screen" .. index, win)
    if not ok then return false, reason end
    -- The existing numbered move scales proportionally; these moves keep
    -- ordinary windows' absolute size, while a maximized window stays maximized.
    win:setSize({ w = target.w, h = target.h })
    win:setFrame(target, 0)
    win:focus()
    local actualScreen = win:screen()
    if not actualScreen or actualScreen:getUUID() ~= destination:getUUID() then
        return fail("应用未能移到目标显示器，请重试")
    end
    M.lastMove.from = source:name()
    M.lastMove.to = destination:name()
    M.lastMove.frame = rect(win:frame())
    return true
end

function M.move(direction, optionalWindow)
    M.lastMove = { direction = direction, time = hs.timer.secondsSinceEpoch() }
    local ok, result, reason = pcall(move, direction, optionalWindow)
    if not ok then
        hs.printf("[screen_navigation] %s", tostring(result))
        result, reason = fail("移屏未完成，请重试")
    end
    M.lastMove.succeeded = result == true
    M.lastMove.reason = reason
    return result, reason
end

function M.stop()
    for _, hotkey in ipairs(M._hotkeys) do hotkey:delete() end
    M._hotkeys = {}
end

function M.start(manager)
    M.stop()
    M.manager = manager
    for key, direction in pairs({ h = "left", j = "down", k = "up", l = "right" }) do
        local action = direction
        local hotkey = hs.hotkey.bind({ "ctrl", "alt", "cmd", "shift" }, key,
            function() M.move(action) end)
        if hotkey then table.insert(M._hotkeys, hotkey) end
    end
    return M
end

return M
