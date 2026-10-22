-- Some apps reject tall AX frames but allow their bottom border to be dragged
-- across vertically stacked displays. Keep this interaction asynchronous.
local M = {}
local dragEventTag = 0x574D5350 -- shared with window_manager's real-mouse guard

function M.start(win, base, target, done, presented)
    local id, pid = win:id(), win:pid()
    local event, types = hs.eventtap.event, hs.eventtap.event.types
    local horizontal = target.w - base.w > 32 and math.abs(target.h - base.h) <= 32
    local axis = horizontal and "x" or "y"
    local state = { phase = "waiting", since = hs.timer.secondsSinceEpoch(), dragEvents = 0 }
    local startedAt = state.since
    local initialFocus = hs.window.focusedWindow()
    local initialFocusID = initialFocus and initialFocus:id()
    local controller = {}

    local function alive()
        return win:id() == id and win:pid() == pid
    end

    local function post(kind, point)
        local item = event.newMouseEvent(kind, point, {})
            :setProperty(event.properties.eventSourceUserData, dragEventTag)
        item:timestamp(hs.timer.absoluteTime())
        if kind == types.mouseMoved then
            state.positionAcknowledgedAt, state.positionTimestamp = nil, item:timestamp()
        elseif kind == types.leftMouseDown then
            state.downAcknowledged, state.downTimestamp = false, item:timestamp()
        elseif kind == types.leftMouseUp then state.downAcknowledged = false end
        state.lastPost = {type=kind, point={x=point.x,y=point.y},
            time=hs.timer.secondsSinceEpoch(), phase=state.phase,
            tag=item:getProperty(event.properties.eventSourceUserData),
            sourcePID=item:getProperty(event.properties.eventSourceUnixProcessID),
            sourceStateID=item:getProperty(event.properties.eventSourceStateID),
            timestamp=item.timestamp and item:timestamp() or nil}
        item:post()
    end

    function controller.diagnostics()
        local last = state.lastPost
        local snapshot = last and {type=last.type, point={x=last.point.x,y=last.point.y},
            time=last.time, phase=last.phase, tag=last.tag, sourcePID=last.sourcePID,
            sourceStateID=last.sourceStateID, timestamp=last.timestamp} or nil
        return {phase=state.phase, down=state.down == true, downAcknowledged=state.downAcknowledged == true,
            downTimestamp=state.downTimestamp, acknowledgedAt=state.acknowledgedAt,
            positionTimestamp=state.positionTimestamp, positionAcknowledgedAt=state.positionAcknowledgedAt,
            hoverStartedAt=state.hoverStartedAt, lastPointerMotionAt=state.lastPointerMotionAt,
            rehoverCount=state.rehoverCount or 0, restorationUnsafe=state.restorationUnsafe == true,
            finished=state.finished == true,
            dragEvents=state.dragEvents, startedAt=startedAt, lastPost=snapshot}
    end

    function controller.isStationaryFeedback(item)
        local last = state.lastPost
        if state.finished or not state.down or not last or item:getType() ~= types.mouseMoved then return false end
        local age = hs.timer.secondsSinceEpoch() - last.time
        if age < 0 or age > .15 then return false end
        local point = item:location()
        -- A same-position, zero-delta report cannot express a window adjustment.
        -- No tolerance: even one point of real movement still yields control.
        return point.x == last.point.x and point.y == last.point.y
            and item:getProperty(event.properties.mouseEventDeltaX) == 0
            and item:getProperty(event.properties.mouseEventDeltaY) == 0
    end

    local function finish(ok, reason)
        if state.finished then return end
        local finalState = controller.diagnostics()
        state.finished = true
        if state.timer then state.timer:stop() end
        if state.watcher then state.watcher:stop() end
        if state.escape then state.escape:delete() end
        -- Always release a button we pressed, including when callbacks fail.
        if state.down then
            local releasePoint = reason == "user-interrupted" and hs.mouse.absolutePosition() or state.point
            pcall(post, types.leftMouseUp, releasePoint)
            state.down = false
        end
        if state.mouse and not state.restorationUnsafe and reason ~= "user-interrupted" then
            pcall(hs.mouse.absolutePosition, state.mouse)
        end
        local valid, exists = pcall(alive)
        local actual = valid and exists and win:frame() or nil
        M.lastRun = { ok = ok, duration = hs.timer.secondsSinceEpoch() - startedAt,
            dragEvents = state.dragEvents, reason = reason, phase = finalState.phase,
            cancelledPhase = not ok and finalState.phase or nil, lastPost = finalState.lastPost,
            hoverStartedAt=finalState.hoverStartedAt, positionAcknowledgedAt=finalState.positionAcknowledgedAt,
            lastPointerMotionAt=finalState.lastPointerMotionAt, rehoverCount=finalState.rehoverCount,
            restorationUnsafe=finalState.restorationUnsafe }
        done(ok, actual, reason)
    end

    function controller.cancel(reason)
        finish(false, reason or "跨屏操作已取消")
    end

    function controller.observeOwnEvent(item)
        if state.finished then return end
        if state.phase == "hovering" and state.positionTimestamp
            and item:getType() == types.mouseMoved and item:timestamp() == state.positionTimestamp
            and not state.positionAcknowledgedAt then
            state.positionAcknowledgedAt = hs.timer.secondsSinceEpoch()
        end
        if not state.down then return end
        if item:getType() == types.leftMouseDown and item:timestamp() == state.downTimestamp
            and not state.downAcknowledged then
            state.downAcknowledged, state.acknowledgedAt = true, hs.timer.secondsSinceEpoch()
        end
    end

    function controller.isDragging()
        -- post() queues an event; mouse events already in transit can arrive
        -- before our own down reaches the tap. They cannot interrupt a drag
        -- which has not begun. state.down independently guarantees release.
        return state.down == true and state.downAcknowledged == true and not state.finished
    end

    function controller.mouseMoved()
        if state.finished or state.down then return end
        if state.hoverStartedAt then
            -- A warp can return as an untagged mouseMoved. Drain it while no
            -- button is held; never replace the saved pointer with that echo.
            state.lastPointerMotionAt = hs.timer.secondsSinceEpoch()
            state.restorationUnsafe = true
            if state.phase ~= "verifying" then return end
        end
        -- Before mouse-down this is ordinary pointer movement, not a window
        -- edit. After mouse-up the user owns the pointer again: do not warp it
        -- back merely because AX frame verification is still outstanding.
        if state.phase == "verifying" then state.mouse = nil
        elseif state.mouse then state.mouse = hs.mouse.absolutePosition() end
    end

    local function modifiersDown()
        local flags = hs.eventtap.checkKeyboardModifiers()
        return flags.cmd or flags.alt or flags.ctrl or flags.shift or flags.fn
    end

    local function step()
        if not alive() then return finish(false, "原窗口已关闭") end
        if win:isFullScreen() then return finish(false, "窗口进入了系统全屏") end
        local now = hs.timer.secondsSinceEpoch()
        if state.hoverStartedAt and not state.down and state.phase ~= "verifying"
            and now-state.hoverStartedAt >= 1 then
            return finish(false, "跨屏鼠标定位未能稳定，请重试")
        end
        if state.phase == "waiting" then
            local focused = hs.window.focusedWindow()
            if focused and focused:id() ~= id and focused:id() ~= initialFocusID then
                return finish(false, "焦点已切换，跨屏操作已取消")
            end
            if modifiersDown() or next(hs.eventtap.checkMouseButtons()) then
                if now - state.since > 4 then
                    finish(false, "请松开快捷键和鼠标后再按一次")
                end
                return
            end
            if presented then
                local ready, reason = presented(win)
                if not ready then
                    if now-startedAt >= 1 then finish(false, reason) end
                    return
                end
            end
            if not state.hoverStartedAt then state.mouse = hs.mouse.absolutePosition() end
            if not focused or focused:id() ~= id then win:focus() end
            -- An explicit size write also makes shrinking a previously dragged
            -- Electron window reliable before positioning it on the top screen.
            local current = win:frame()
            local needsBase = math.abs(current.x - base.x) > 4 or math.abs(current.y - base.y) > 4
                or math.abs(current.w - base.w) > 4 or math.abs(current.h - base.h) > 4
            if needsBase then
                win:setSize({ w = base.w, h = base.h })
                win:setFrame(base, 0)
            end
            state.settleDelay = needsBase and .05 or 0
            state.phase, state.since = "settling", now
            return
        end

        -- Mouse-up ends our interaction. A new shortcut or focus change must
        -- not turn an already completed resize into a failure and AX rollback.
        -- Escape still cancels through its independent hotkey callback.
        if state.phase == "verifying" then
            if now - state.since < .05 then return end
            local f = win:frame()
            local ok = math.abs(f.x - target.x) <= 4 and math.abs(f.y - target.y) <= 4
                and math.abs(f.w - target.w) <= 32 and math.abs(f.h - target.h) <= 32
            -- Do not write target again: an AX write can undo the live resize.
            if ok then finish(true)
            elseif now - state.since >= .2 then finish(false, "应用未接受跨屏拖动") end
            return
        end

        local focused = hs.window.focusedWindow()
        if not focused or focused:id() ~= id then
            return finish(false, "焦点已切换，跨屏操作已取消")
        end
        -- Once mouse-down has started the short resize, finish its unmodified
        -- mouse events even if A is pressed again. Focus changes above and the
        -- independent Escape callback still cancel and release the button.
        if modifiersDown() and not state.down then
            if state.phase == "settling" or state.phase == "hovering" then
                -- No button has been pressed yet; let the user's shortcut
                -- finish and resume the same fast drag after release.
                state.phase, state.since = "waiting", now
                state.positionTimestamp, state.positionAcknowledgedAt = nil, nil
                return
            end
            return finish(false, "按键打断了跨屏操作，请重试")
        end

        if state.phase == "settling" then
            if now - state.since < state.settleDelay then return end
            if presented then
                local ready, reason = presented(win)
                if not ready then return finish(false, reason) end
            end
            local f = win:frame()
            if math.abs(f.x - base.x) > 4 or math.abs(f.y - base.y) > 4
                or math.abs(f.w - base.w) > 32 or math.abs(f.h - base.h) > 32 then
                return finish(false, "应用未能铺满起始屏幕")
            end
            state.point = horizontal and { x = f.x + f.w - 1, y = f.y + f.h / 2 }
                or { x = f.x + f.w / 2, y = f.y + f.h - 1 }
            state.endPosition = horizontal and target.x + target.w - 1 or target.y + target.h - 1
            -- An auto-hidden Dock intercepts the last row at the physical
            -- bottom edge. Stay two points farther inside that edge; keep the
            -- full usable target for verification and normal Dock layouts.
            for _, screen in ipairs(hs.screen.allScreens()) do
                local full = screen:fullFrame()
                if horizontal then
                    if state.point.y >= full.y and state.point.y < full.y + full.h
                        and math.abs(target.x + target.w - full.x - full.w) <= 1 then
                        state.endPosition = math.min(state.endPosition, full.x + full.w - 3)
                    end
                elseif state.point.x >= full.x and state.point.x < full.x + full.w
                    and math.abs(target.y + target.h - full.y - full.h) <= 1 then
                    state.endPosition = math.min(state.endPosition, full.y + full.h - 3)
                end
            end
            state.hoverStartedAt = state.hoverStartedAt or now
            state.phase, state.since = "hovering", now
            post(types.mouseMoved, state.point)
        elseif state.phase == "hovering" then
            if not state.positionAcknowledgedAt then return end
            local quietSince = math.max(state.positionAcknowledgedAt, state.lastPointerMotionAt or 0)
            if now-quietSince < .1 then return end
            -- Recheck everything after the asynchronous hover. Posting down at
            -- a different pointer position would cause a second, unguarded warp.
            if next(hs.eventtap.checkMouseButtons()) then return finish(false, "user-interrupted") end
            if presented then
                local ready, reason = presented(win)
                if not ready then return finish(false, reason) end
            end
            local f = win:frame()
            if math.abs(f.x-base.x)>4 or math.abs(f.y-base.y)>4
                or math.abs(f.w-base.w)>32 or math.abs(f.h-base.h)>32 then
                return finish(false, "起始窗口位置已变化，跨屏操作已取消")
            end
            local point = hs.mouse.absolutePosition()
            if math.abs(point.x-state.point.x)>1 or math.abs(point.y-state.point.y)>1 then
                state.restorationUnsafe = true
                if (state.rehoverCount or 0) >= 2 then
                    return finish(false, "跨屏鼠标定位未能稳定，请重试")
                end
                state.rehoverCount = (state.rehoverCount or 0) + 1
                state.since = now
                post(types.mouseMoved, state.point)
                return
            end
            state.down = true
            post(types.leftMouseDown, state.point)
            state.phase, state.since = "priming", now
        elseif state.phase == "priming" then
            if not state.downAcknowledged then
                if now - state.since >= 1 then finish(false, "跨屏鼠标按下未能确认，请重试") end
                return
            end
            if now - state.acknowledgedAt < .02 then return end
            -- AppKit needs a small initial movement to enter its resize loop;
            -- the following event can then jump directly to the final edge.
            state.point[axis] = state.point[axis] + 4
            state.dragEvents = state.dragEvents + 1
            post(types.leftMouseDragged, state.point)
            state.phase, state.since = "dragging", now
        elseif state.phase == "dragging" then
            if now - state.since < .02 then return end
            -- One drag event goes straight to the final edge; no interpolated
            -- animation. Release on a separate tick so the app handles resize.
            state.point[axis] = state.endPosition
            state.dragEvents = state.dragEvents + 1
            post(types.leftMouseDragged, state.point)
            state.phase, state.since = "releasing", now
        elseif state.phase == "releasing" then
            if now - state.since < .02 then return end
            post(types.leftMouseUp, state.point)
            state.down = false
            state.phase, state.since = "verifying", now
        end
    end

    state.watcher = hs.screen.watcher.new(function()
        finish(false, "显示器排列发生变化，请重试")
    end):start()
    state.escape = hs.hotkey.bind({}, "escape", function()
        finish(false, "跨屏操作已取消")
    end)
    state.timer = hs.timer.doEvery(.02, function()
        local ok, err = pcall(step)
        if not ok then
            hs.printf("[window_span_drag] %s", tostring(err))
            finish(false, "跨屏拖动出错，请重试")
        end
    end)
    return controller
end

return M
