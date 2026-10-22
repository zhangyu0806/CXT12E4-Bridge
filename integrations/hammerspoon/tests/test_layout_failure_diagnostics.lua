-- Isolated Lua contract tests: no real windows, applications, keys or settings.
local modulePath = os.getenv('LAYOUT_TEST_SOURCE') or 'workspace_layouts.lua'
local clock, timers, apps, screens, focus, manager, module, alerts, launched, pending
local counts, failures, asynchronous, failAt, failReason, delayedApps
local function copy(t)
    if type(t) ~= "table" then return t end
    local result = {}; for k,v in pairs(t) do result[k]=copy(v) end; return result
end
local function eq(a,b,msg) assert(a==b,(msg or "mismatch") .. ": " .. tostring(a) .. " ~= " .. tostring(b)) end
local function frameEq(a,b,msg)
    for _, key in ipairs({"x","y","w","h"}) do eq(a[key],b[key],(msg or "frame").."."..key) end
end
local function timer(delay,fn,repeating)
    local item={at=clock+delay,fn=fn,repeating=repeating,delay=delay}
    function item:stop() self.stopped=true end
    timers[#timers+1]=item; return item
end
local function advance(seconds)
    local untilTime=clock+seconds
    for _=1,10000 do
        local selected
        for _, item in ipairs(timers) do
            if not item.stopped and item.at<=untilTime and (not selected or item.at<selected.at) then selected=item end
        end
        if not selected then clock=untilTime; return end
        clock=selected.at
        if selected.repeating then selected.at=clock+selected.delay else selected.stopped=true end
        selected.fn()
    end
    error("timer loop")
end
local function screen(uuid,x,y,w,h)
    local s={uuid=uuid,full={x=x,y=y,w=w,h=h},usable={x=x,y=y,w=w,h=h}}
    function s:getUUID() return self.uuid end
    function s:frame() return copy(self.usable) end
    function s:fullFrame() return copy(self.full) end
    return s
end
local function app(bundle,pid)
    local a={bundle=bundle,process=pid,windows={}}
    function a:bundleID() return self.bundle end
    function a:pid() return self.process end
    function a:focusedWindow() return self.focused end
    function a:mainWindow() return self.main end
    function a:allWindows() return self.windows end
    function a:findMenuItem(path)
        eq(path[1],"File");eq(path[2],"New Window")
        return self.menuDisabled and {enabled=false} or {enabled=true}
    end
    function a:selectMenuItem(path)
        eq(path[1],"File");eq(path[2],"New Window")
        counts.newWindow=(counts.newWindow or 0)+1
        if self.menuFailure then return false end
        if self.onNewWindow then self.onNewWindow(self) end
        return true
    end
    apps[bundle]=apps[bundle] or {}; table.insert(apps[bundle],a)
    return a
end
local function window(a,id,frame)
    local w={identifier=id,process=a.process,f=frame or{x=50,y=50,w=800,h=600},standard=true,visible=true,minimized=false,fullscreen=false,hidden=false}
    function w:id() return self.identifier end
    function w:pid() return self.process end
    function w:isStandard() return self.standard end
    function w:isVisible() return self.visible end
    function w:isMinimized() return self.minimized end
    function w:isFullScreen() return self.fullscreen end
    function w:frame() return copy(self.f) end
    function w:raise() counts.raised[#counts.raised+1]=self:id();return self end
    function w:focus() focus=self;counts.focus=counts.focus+1;return self end
    a.windows[#a.windows+1]=w; a.main=a.main or w
    return w
end
local function getWindow(bundle) return apps[bundle][1].main end
local function complete(callback,ok,detail)
    if asynchronous then
        local item=timer(.2,function() if pending then pending=nil; callback(ok,detail) end end)
        pending={callback=callback,timer=item}
    else callback(ok,detail) end
end
local function reset()
    clock,timers,apps,screens,focus,alerts,launched,pending=0,{},{},{},nil,{},{},nil
    counts={apply=0,restore=0,cancel=0,capture=0,raised={},focus=0}; failures={}; asynchronous=false; failAt=nil; failReason=nil; delayedApps={}
    local top=screen("REPLACE_WITH_LEFT_TOP_UUID",0,0,1920,1080)
    top.usable.y=30; top.usable.h=1050
    local bottom=screen("REPLACE_WITH_LEFT_BOTTOM_UUID",0,1080,1920,1080)
    local right=screen("REPLACE_WITH_RIGHT_UUID",1920,240,1080,1920)
    local sidecar=screen("TEST_UNUSED_SCREEN_UUID",3000,1206,1373,954)
    screens={sidecar,right,bottom,top} -- Intentionally unlike role or screen order.
    for index,bundle in ipairs({"com.openai.codex","com.apple.Safari","notion.id","com.mitchellh.ghostty"}) do window(app(bundle,100+index),10+index) end
    hs={configdir="/nonexistent-workspace-layouts-test-config",
        timer={secondsSinceEpoch=function()return clock end,doEvery=function(d,f)return timer(d,f,true)end},
        screen={allScreens=function()return screens end},
        alert={show=function(s)alerts[#alerts+1]=s end},
        window={focusedWindow=function()return focus end},
        json={decode=function()error("unexpected config")end},
        application={
            applicationsForBundleID=function(bundle)return apps[bundle] or{}end,
            pathForBundleID=function(bundle)return delayedApps[bundle]~="not-installed" and "/Applications/Test.app" or nil end,
            launchOrFocusByBundleID=function(bundle)
                launched[bundle]=(launched[bundle] or 0)+1
                if type(delayedApps[bundle])=="function" then delayedApps[bundle]() end
                return true
            end,
        },
    }
    manager={usableFrame=function(s)return s:frame()end,windowPresentationReady=function(w)return not w.presentationMismatch,"请先按 Win+D 收回桌面或退出任务总览，再应用布局"end}
    function manager.prepareWorkspacePresentation(items,state)
        for _,item in ipairs(items) do
            if not item.before.minimized and not item.before.hidden then
                local ready,reason=manager.windowPresentationReady(item.win)
                if not ready then return false,reason end
            end
        end
        return true
    end
    function manager.workspaceSpanFrame(uuids)
        local selected={}
        for _,uuid in ipairs(uuids) do for _,s in ipairs(screens)do if s.uuid==uuid then selected[#selected+1]=s end end end
        if #selected~=2 or selected[1].full.x~=selected[2].full.x then return nil,"unaligned-span" end
        local a,b=selected[1]:frame(),selected[2]:frame()
        return{x=a.x,y=math.min(a.y,b.y),w=a.w,h=math.max(a.y+a.h,b.y+b.h)-math.min(a.y,b.y)}
    end
    function manager.captureWorkspaceWindow(w)
        counts.capture=counts.capture+1
        return{id=w:id(),pid=w:pid(),frame=w:frame(),minimized=w.minimized,fullscreen=w.fullscreen,hidden=w.hidden,saved=copy(w.saved),activeSpan=copy(w.activeSpan)}
    end
    function manager.applyWorkspaceWindow(w,spec,callback)
        counts.apply=counts.apply+1
        counts.lastSpec=copy(spec)
        if not spec.span then assert(spec.screenUUID and spec.unit,"ordinary placement must include stable screen UUID and unit") end
        w.minimized,w.fullscreen,w.hidden=false,false,false
        w.f=copy(spec.frame); w.activeSpan=spec.span and copy(spec.spanScreens) or nil
        if counts.apply==failAt then complete(callback,false,failReason or "rejected") else complete(callback,true) end
    end
    function manager.restoreWorkspaceWindow(w,snapshot,callback)
        counts.restore=counts.restore+1
        w.f=copy(snapshot.frame); w.minimized=snapshot.minimized; w.fullscreen=snapshot.fullscreen;w.hidden=snapshot.hidden;w.saved=copy(snapshot.saved);w.activeSpan=copy(snapshot.activeSpan)
        complete(callback,true)
    end
    function manager.cancelWorkspaceOperation(reason)
        counts.cancel=counts.cancel+1
        if pending then local item=pending;pending=nil;item.timer:stop();item.callback(false,reason)end
    end
    module=dofile(modulePath); assert(module.start(manager))
end
local total=0
local function test(name,fn)
    reset(); fn(); total=total+1; print("PASS "..name)
end

test("third Safari placement failure reverses the successful Ghostty bottom placement",function()
    asynchronous=true;failAt=3;failReason="Safari resize rejected"
    local ghost=getWindow("com.mitchellh.ghostty");local original=ghost:frame()
    assert(module.apply("terminal"));advance(.21)
    frameEq(ghost:frame(),{x=0,y=1080,w=1920,h=1080})
    advance(2)
    frameEq(ghost:frame(),original);eq(counts.restore,3)
    assert(module.lastAction.detail:find("Safari resize rejected",1,true))
    local d=module.lastAction.failedPlacement
    assert(d.bundleID=="com.apple.Safari" and d.index==3 and d.windowID==getWindow("com.apple.Safari"):id())
    assert(d.pid==getWindow("com.apple.Safari"):pid())
    assert(d.target.w==1080 and d.actual.w==1080 and d.reason=="Safari resize rejected")
end)
test("real user cancellation after Ghostty placement does not restore it",function()
    asynchronous=true;assert(module.apply("terminal"));advance(.21)
    local ghost=getWindow("com.mitchellh.ghostty")
    frameEq(ghost:frame(),{x=0,y=1080,w=1920,h=1080})
    module.cancel("user-interrupted");advance(2)
    frameEq(ghost:frame(),{x=0,y=1080,w=1920,h=1080});eq(counts.restore,0)
end)
test("unavailable AX application while reporting failure cannot hide original cause or skip rollback",function()
    local safari=getWindow("com.apple.Safari")
    safari.application=function()error("NSRunningApplication temporarily unavailable")end
    failAt=3;failReason="sticky-edge";assert(module.apply("terminal"))
    assert(module.lastAction.status=="failed" and counts.restore==3)
    assert(module.lastAction.detail:find("com.apple.Safari：sticky-edge",1,true))
end)
print(string.format("PASS %d rollback diagnostic tests",total))
