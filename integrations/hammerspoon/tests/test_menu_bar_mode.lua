-- All OS APIs are mocked. No preferences or windows are changed.
local source = "menu_bar_mode.lua"
local passed = 0
local function environment()
    local e = { now = 0, hidden = false, writes = 0, notifications = 0, timers = {}, windows = {}, settings = {} }
    hs = {
        configdir = "/unused", printf = function() end,
        execute = function(command)
            if command:find("menu%-bar%-inset") then return tostring(e.inset or (e.hidden and 0 or 30)), true end
            if command:find("defaults read", 1, true) then return "0", true end
            if command:find("defaults write", 1, true) then
                e.writes = e.writes + 1; e.hidden = command:find("true", 1, true) ~= nil; return "", true
            end
            if command:find("defaults delete", 1, true) then e.writes = e.writes + 1; return "", true end
            error("unexpected command " .. command)
        end,
        settings = {
            get = function(key) return e.settings[key] end,
            set = function(key, value) e.settings[key] = value end,
            clear = function(key) e.settings[key] = nil end,
        },
        distributednotifications = { post = function() e.notifications = e.notifications + 1 end },
        window = { get = function(id) return e.windows[id] end },
        timer = {
            secondsSinceEpoch = function() return e.now end,
            doAfter = function(interval, fn)
                local t
                t=hs.timer.doEvery(interval, function() t:stop(); fn() end)
                return t
            end,
            doEvery = function(interval, fn)
                local timer = { interval = interval, next = e.now + interval, fn = fn, active = true }
                function timer:stop() self.active = false end
                e.timers[#e.timers + 1] = timer; return timer
            end,
        },
    }
    function e.advance(duration)
        local untilTime = e.now + duration
        while e.now < untilTime - .00001 do
            e.now = math.min(untilTime, e.now + .025)
            local count = #e.timers
            for i = 1, count do
                local t = e.timers[i]
                if t.active and t.next <= e.now + .00001 then t.next = t.next + t.interval; t.fn() end
            end
        end
    end
    function e.window(id, pid)
        local w = { number = id, process = pid or 100, axRole = "AXWindow" }
        function w:id() return self.number end
        function w:pid() return self.process end
        function w:role() if self.raise then error("transient AX failure") end; return self.axRole end
        e.windows[id] = w; return w
    end
    e.module = assert(loadfile(source))()
    return e
end
local function test(name, fn)
    fn(); passed = passed + 1; print("PASS " .. name)
end

test("registration survives nil AX role and thrown AX read", function()
    local e = environment(); local w = e.window(1); assert(e.module.enter(w)); e.advance(.2)
    w.axRole = nil; assert(e.module.contains(w)); assert(e.module.owner() == w); assert(#e.module.members() == 1)
    w.raise = true; e.advance(1.5); assert(e.module.contains(w)); assert(e.hidden)
    w.raise = false; w.axRole = "AXWindow"; e.advance(1); assert(e.module.contains(w))
end)
test("fresh wrapper with matching PID recovers stale AX reference", function()
    local e = environment(); local w = e.window(1); e.module.enter(w); e.advance(.2)
    w.axRole = nil; local fresh = e.window(1); e.advance(.5)
    assert(e.module.contains(w)); assert(e.module.owner() == fresh)
end)
test("confirmed closed window expires and restores original preference", function()
    local e = environment(); local w = e.window(1); e.module.enter(w); e.advance(.2)
    w.axRole = nil; e.windows[1] = nil; e.advance(1.5); assert(e.module.contains(w))
    e.advance(1.5); assert(not e.module.contains(w)); assert(not e.hidden)
end)
test("PID reuse is rejected and does not discard other member", function()
    local e = environment(); local a, b = e.window(1), e.window(2)
    e.module.enter(a); e.module.enter(b); e.advance(.2)
    a.axRole = nil; local reused = e.window(1, 200); e.advance(3)
    assert(not e.module.contains(a)); assert(not e.module.contains(reused)); assert(e.module.contains(b)); assert(e.hidden)
end)
test("pending enter tolerates transient AX failure", function()
    local e = environment(); local w = e.window(1); local ready, failed = 0, 0
    w.axRole = nil
    e.module.enter(w, function() ready = ready + 1 end, function() failed = failed + 1 end)
    e.advance(.8); assert(e.module.contains(w)); assert(ready == 0 and failed == 0)
    w.axRole = "AXWindow"; e.advance(.2); assert(ready == 1 and failed == 0)
end)
test("failed repeat entry retains an already registered member", function()
    local e = environment(); local w = e.window(1); e.module.enter(w); e.advance(.2)
    e.module.enter(w, function() error("callback failure") end); e.advance(.2)
    assert(e.module.contains(w)); assert(e.hidden)
end)
test("reload keeps all members and backup with zero menu writes", function()
    local e = environment(); local a, b = e.window(1), e.window(2)
    e.module.enter(a); e.module.enter(b); e.advance(.2)
    a.axRole = nil
    local writes, notifications = e.writes, e.notifications
    local old = e.module; local state = assert(old.detachForReload())
    e.module = assert(loadfile(source))(state)
    assert(e.module.contains(a) and e.module.contains(b)); assert(#e.module.members() == 2)
    assert(e.writes == writes and e.notifications == notifications)
    old.stop(); assert(e.writes == writes)
    a.axRole = "AXWindow"; e.advance(1)
    e.module.leave(a); assert(e.hidden); e.module.leave(b); assert(not e.hidden)
end)
test("reload refuses pending enter without detaching it", function()
    local e = environment(); local w = e.window(1); e.module.enter(w)
    local state, reason = e.module.detachForReload(); assert(state == nil and reason)
    assert(e.module.contains(w)); e.advance(.2); assert(e.module.detachForReload())
end)
test("reload refuses incomplete preference restoration", function()
    local e = environment(); local w = e.window(1); e.module.enter(w); e.advance(.2)
    e.inset = 0; e.module.leave(w)
    local state, reason = e.module.detachForReload(); assert(state == nil and reason)
    e.inset = nil; e.advance(.2); assert(e.module.detachForReload())
end)
test("already hidden menu is verified asynchronously without 150 ms wait", function()
    local e=environment(); local w=e.window(1); local ready=0
    e.module.enter(w,function()ready=ready+1 end)
    assert(ready==0, "ready must not run inside enter")
    e.advance(.025); assert(ready==1)
    e.advance(.5); assert(ready==1)
end)
test("slow menu transition still waits for its actual inset", function()
    local e=environment(); local w=e.window(1); local ready=0; e.inset=30
    e.module.enter(w,function()ready=ready+1 end); e.advance(.3); assert(ready==0)
    e.inset=0; e.advance(.2); assert(ready==1)
end)
test("cancel before first verification never fires ready", function()
    local e=environment(); local w=e.window(1); local ready=0
    e.module.enter(w,function()ready=ready+1 end); e.module.leave(w); e.advance(.5)
    assert(ready==0 and not e.hidden and not e.module.contains(w))
end)
print(string.format("%d menu mode checks passed",passed))
