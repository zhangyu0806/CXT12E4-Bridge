-- Hide the system menu bar while any registered window spans both displays.
-- Use preferences + a distributed notification; System Events may block on TCC.
local M = {}
-- loadfile(path)(state) transfers an active instance without restoring or
-- rewriting the menu preference. A normal require passes a module name.
local inherited = ...
if type(inherited) ~= "table" or inherited.version ~= 1 then inherited = nil end
local key = "window_manager.menu_bar_mode.backup.v1"
local notification = "AppleInterfaceMenuBarHidingChangedNotification"
local insetCommand = "'" .. (hs.configdir .. "/helpers/menu-bar-inset"):gsub("'", "'\\''") .. "' 2>&1"
local backup = inherited and inherited.backup
local members = inherited and inherited.members or {}
local restoreTimer, watchdog, generation = nil, nil, 0

local function log(message)
    hs.printf("[menu_bar_mode] %s", tostring(message))
end

local function execute(command)
    local called, output, success = pcall(hs.execute, command)
    if not called then return false, tostring(output) end
    return success == true, output or ""
end

local function readPreference()
    local ok, output = execute("/usr/bin/defaults read NSGlobalDomain _HIHideMenuBar 2>&1")
    if ok then
        local value = output:match("^%s*(.-)%s*$")
        if value == "1" or value == "0" then
            return { existed = true, value = value == "1" }
        end
    elseif output:find("does not exist", 1, true)
        or output:find("Could not find key '_HIHideMenuBar'", 1, true) then
        return { existed = false, value = false }
    end
    return nil, "无法读取菜单栏原设置：" .. output
end

local function setPreference(hidden)
    local command = hidden
        and "/usr/bin/defaults write NSGlobalDomain _HIHideMenuBar -bool true"
        or "/usr/bin/defaults write NSGlobalDomain _HIHideMenuBar -bool false"
    local ok, output = execute(command)
    if not ok then return false, "无法设置菜单栏：" .. output end
    local posted, err = pcall(hs.distributednotifications.post, notification)
    if not posted then return false, "无法通知菜单栏设置变化：" .. tostring(err) end
    return true
end

local function topInset()
    local ok, output = execute(insetCommand)
    if not ok then error("无法读取菜单栏实际高度：" .. output) end
    local value = output:match("^%s*(.-)%s*$")
    local inset = tonumber(value)
    if not value:match("^%d+%.?%d*$") or not inset or inset == math.huge then
        error("菜单栏高度返回值无效：" .. output)
    end
    return inset
end

local function identity(win)
    local ok, id, pid = pcall(function()
        return win and win:id(), win and win:pid()
    end)
    if not ok or not id or not pid then return nil end
    return tostring(pid) .. ":" .. tostring(id), id, pid
end

local function alive(member)
    local function matches(win)
        local ok, exists = pcall(function()
            return win and win:id() == member.id and win:pid() == member.pid
                and win:role() == "AXWindow"
        end)
        return ok and exists or false
    end
    if matches(member.win) then return true end
    -- AX can briefly lose the old wrapper while an application switches tasks.
    local ok, fresh = pcall(hs.window.get, member.id)
    if ok and matches(fresh) then member.win = fresh; return true end
    return false
end

local function expired(member)
    if alive(member) then
        member.missingSince, member.missingChecks = nil, nil
        return false, true
    end
    local now = hs.timer.secondsSinceEpoch()
    member.missingSince = member.missingSince or now
    member.missingChecks = (member.missingChecks or 0) + 1
    -- A single failed AX read is not proof that a registered window closed.
    return member.missingChecks >= 3 and now - member.missingSince >= 2, false
end

local function findMember(win)
    local memberKey = identity(win)
    if memberKey and members[memberKey] then return memberKey, members[memberKey] end
    -- A closed AX window may no longer supply its PID, but its stored object
    -- still lets a cancellation release only that window's membership.
    for candidateKey, member in pairs(members) do
        if member.win == win then return candidateKey, member end
    end
end

local function cancelRestoration()
    generation = generation + 1
    if restoreTimer then restoreTimer:stop(); restoreTimer = nil end
end

local function stopMemberTimer(member)
    if member.timer then member.timer:stop(); member.timer = nil end
end

local function callback(fn, ...)
    if not fn then return true end
    local ok, err = pcall(fn, ...)
    if not ok then log(err) end
    return ok, err
end

function M.owner()
    for _, member in pairs(members) do
        return member.win
    end
    return nil
end

function M.members()
    local result = {}
    for _, member in pairs(members) do
        result[#result + 1] = member.win
    end
    return result
end

function M.contains(win)
    local _, member = findMember(win)
    return member ~= nil
end

function M.isActive()
    return M.owner() ~= nil
end

function M.restoringTopInset()
    if next(members) == nil and backup then return backup.topInset end
    return nil
end

function M.currentTopInset()
    return topInset()
end

local function restorePreference()
    cancelRestoration()
    if watchdog then watchdog:stop(); watchdog = nil end
    if not backup then return true end
    local token, restoring = generation, backup
    -- Deleting a previously absent key alone leaves the running UI hidden.
    -- First restore its effective false value, then delete after UI recovery.
    local ok, err = setPreference(restoring.value)
    if not ok then log(err); return false end
    local started = hs.timer.secondsSinceEpoch()
    local function verify()
        if token ~= generation then return end
        if math.abs(topInset() - restoring.topInset) > 1 then
            if hs.timer.secondsSinceEpoch() - started > 5 then
                if restoreTimer then restoreTimer:stop(); restoreTimer = nil end
                log("菜单栏恢复尚未生效，保留恢复记录供下次重试")
            end
            return
        end
        if restoreTimer then restoreTimer:stop(); restoreTimer = nil end
        if not restoring.existed then
            local deleted, output = execute("/usr/bin/defaults delete NSGlobalDomain _HIHideMenuBar 2>&1")
            if not deleted then log("无法清理临时菜单栏设置：" .. output); return end
            -- No notification here: the effective visible state is restored.
        end
        local cleared, clearResult = pcall(hs.settings.clear, key)
        if not cleared or clearResult == false then
            log("无法清理菜单栏恢复记录：" .. tostring(clearResult))
            return
        end
        backup = nil
    end
    local function tick()
        local checked, checkError = pcall(verify)
        if not checked then
            if restoreTimer then restoreTimer:stop(); restoreTimer = nil end
            log(checkError)
        end
    end
    restoreTimer = hs.timer.doEvery(.15, tick)
    tick()
    return true
end

function M.leave(win)
    if win ~= nil then
        local memberKey, member = findMember(win)
        if not member then return true end
        stopMemberTimer(member)
        members[memberKey] = nil
    else
        for _, member in pairs(members) do stopMemberTimer(member) end
        members = {}
    end
    if next(members) == nil then return restorePreference() end
    return true
end

local function startWatchdog()
    if watchdog then return end
    watchdog = hs.timer.doEvery(.5, function()
        local closed = {}
        for _, member in pairs(members) do
            if expired(member) then closed[#closed + 1] = member end
        end
        for _, member in ipairs(closed) do
            if members[member.key] == member then
                if member.pending then member.fail("原窗口已关闭")
                else M.leave(member.win) end
            end
        end
    end)
end

function M.enter(win, ready, failed)
    local memberKey, id, pid = identity(win)
    if not memberKey then
        callback(failed, "原窗口已关闭")
        return false
    end
    -- A new enter during restoration inherits the first backup and cancels
    -- its delayed deletion. Never capture our own temporary true/false value.
    if not backup then
        local original, readError = readPreference()
        if not original then callback(failed, readError); return false end
        local measured, inset = pcall(topInset)
        if not measured then callback(failed, tostring(inset)); return false end
        original.topInset = inset
        local stored, storeError = pcall(hs.settings.set, key, original)
        if not stored then callback(failed, tostring(storeError)); return false end
        backup = original
    end
    cancelRestoration()
    local previous = members[memberKey]
    if previous then stopMemberTimer(previous) end
    local member = { key = memberKey, win = win, id = id, pid = pid, pending = true }
    members[memberKey] = member
    local function fail(reason)
        if members[memberKey] ~= member then return end
        stopMemberTimer(member)
        members[memberKey] = previous and not previous.pending and previous or nil
        if next(members) == nil then restorePreference() end
        callback(failed, reason)
    end
    member.fail = fail
    local ok, err = setPreference(true)
    if not ok then fail(err); return false end
    local started = hs.timer.secondsSinceEpoch()
    local function verify()
        if members[memberKey] ~= member then return end
        local closed, readable = expired(member)
        if closed then return fail("原窗口已关闭") end
        if not readable then return end
        if math.abs(topInset()) <= 1 then
            stopMemberTimer(member)
            member.pending = false
            local called, callbackError = callback(ready)
            if not called then fail("跨屏准备失败：" .. tostring(callbackError)) end
        elseif hs.timer.secondsSinceEpoch() - started > 5 then
            fail("菜单栏未能隐藏，请重试")
        end
    end
    member.timer = hs.timer.doEvery(.15, function()
        local checked, checkError = pcall(verify)
        if not checked then fail(tostring(checkError)) end
    end)
    startWatchdog()
    return true
end

function M.stop()
    -- The effective original preference is written synchronously. If shutdown
    -- ends the verification timer, the persisted backup survives for startup.
    return M.leave()
end

function M.detachForReload()
    if restoreTimer then return nil, "菜单栏正在恢复，请稍后重载" end
    for _, member in pairs(members) do
        if member.pending then return nil, "跨屏正在准备，请稍后重载" end
    end
    cancelRestoration()
    if watchdog then watchdog:stop(); watchdog = nil end
    local state = { version = 1, members = members, backup = backup }
    members, backup = {}, nil
    return state
end

if inherited then
    if next(members) ~= nil then startWatchdog() end
else
    local loaded, saved = pcall(hs.settings.get, key)
    if loaded and type(saved) == "table" and type(saved.existed) == "boolean"
        and type(saved.value) == "boolean" and type(saved.topInset) == "number" then
        backup = saved
        M.leave()
    elseif not loaded then
        log(saved)
    end
end

return M
