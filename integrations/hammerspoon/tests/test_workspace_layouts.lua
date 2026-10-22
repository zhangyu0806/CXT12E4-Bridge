-- Isolated Lua contract tests: no real windows, applications, keys or settings.
local modulePath = os.getenv('LAYOUT_TEST_SOURCE') or 'workspace_layouts.lua'
local clock, timers, apps, screens, focus, manager, module, alerts, launched, pending
local counts, failures, asynchronous, failAt, failReason, delayedApps, taskOptions, tasks
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
    taskOptions,tasks={},{}
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
        task={new=function(path,callback,arguments)
            eq(path,"/usr/bin/open");eq(#arguments,2);eq(arguments[1],"-b");eq(arguments[2],"notion.id")
            counts.taskCreated=(counts.taskCreated or 0)+1
            if taskOptions.newFailure then return nil end
            local task={callback=callback,arguments=arguments,running=false}
            function task:isRunning()return self.running end
            function task:start()
                counts.open=(counts.open or 0)+1
                if taskOptions.startFailure then return false end
                self.running=true
                if taskOptions.onStart then taskOptions.onStart(self) end
                if not taskOptions.hang then
                    timer(taskOptions.delay or .05,function()
                        if self.running then self.running=false;self.callback(taskOptions.code or 0,"",taskOptions.stderr or "")end
                    end)
                end
                return self
            end
            function task:terminate()
                self.running=false;counts.terminated=(counts.terminated or 0)+1
                self.callback(15,"","terminated") -- Cancellation callback may run synchronously.
            end
            tasks[#tasks+1]=task
            return task
        end},
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

test("office resolves UUIDs independent of screen enumeration and leaves Sidecar unassigned",function()
    assert(module.apply("office"));eq(counts.apply,3);eq(module.lastAction.status,"completed")
    frameEq(getWindow("com.openai.codex").f,{x=0,y=30,w=1920,h=1050})
    frameEq(getWindow("com.apple.Safari").f,{x=0,y=1080,w=1920,h=1080})
    frameEq(getWindow("notion.id").f,{x=1920,y=240,w=1080,h=1920})
    frameEq(getWindow("com.mitchellh.ghostty").f,{x=50,y=50,w=800,h=600})
end)
test("missing role is preflighted before launch or any window movement",function()
    table.remove(screens,3);apps["notion.id"]=nil
    eq(module.apply("office"),false);eq(counts.apply,0);eq(next(launched),nil)
end)
test("focus passes only the explicit left pair to forced span placement",function()
    assert(module.apply("focus"));eq(counts.apply,2)
    local w=getWindow("com.openai.codex")
    frameEq(w.f,{x=0,y=30,w=1920,h=2130});eq(#w.activeSpan,2)
    eq(w.activeSpan[1],module.config.roles.leftTop);eq(w.activeSpan[2],module.config.roles.leftBottom)
end)
test("invalid span adjacency aborts all placements",function()
    screens[3].full.x=200
    eq(module.apply("focus"),false);eq(counts.apply,0)
end)
test("all windows become ready before any target unminimizes or moves",function()
    local codex=getWindow("com.openai.codex");codex.minimized=true
    apps["notion.id"][1].main=nil;apps["notion.id"][1].windows={}
    module.apply("office");advance(.5);eq(counts.apply,0);eq(codex.minimized,true)
    window(apps["notion.id"][1],30);advance(.1);eq(counts.apply,3)
end)
test("missing apps launch once in parallel and timeout leaves ready apps unmoved",function()
    apps["com.apple.Safari"],apps["notion.id"]=nil,nil
    assert(module.apply("office"));eq(launched["com.apple.Safari"],1);eq(launched["notion.id"],1)
    advance(8.2);eq(counts.apply,0);eq(module.lastAction.status,"failed")
    eq(launched["com.apple.Safari"],1);eq(launched["notion.id"],1)
end)
test("delayed launch waits for real standard window and moves all only after readiness",function()
    apps["notion.id"]=nil
    delayedApps["notion.id"]=function()timer(.5,function()window(app("notion.id",300),40)end)end
    module.apply("office");advance(.4);eq(counts.apply,0);advance(.3);eq(counts.apply,3);eq(launched["notion.id"],1)
end)
test("uninstalled application aborts before even other missing apps launch",function()
    apps["com.apple.Safari"],apps["notion.id"]=nil,nil;delayedApps["notion.id"]="not-installed"
    eq(module.apply("office"),false);eq(next(launched),nil);eq(counts.apply,0)
end)
test("ambiguous standard windows without a main target abort before moving another app",function()
    local a=apps["notion.id"][1];window(a,50);a.main=nil
    module.apply("office");eq(counts.apply,0);eq(module.lastAction.status,"failed")
end)
test("modal application dialog aborts without moving its document",function()
    local a=apps["notion.id"][1];a.focused=window(a,51);a.focused.standard=false
    focus=a.main -- AX may still report the document while the application reports a dialog.
    module.apply("office");eq(counts.apply,0);eq(module.lastAction.status,"failed")
end)
test("an initial AX exception releases transaction and stops the acquisition timer",function()
    apps["notion.id"][1].main.isStandard=function()error("AX disconnected")end
    module.apply("office");eq(counts.apply,0);eq(module._transaction,nil);eq(module.lastAction.status,"failed")
    advance(10);eq(counts.apply,0)
end)
test("only one exact-bundle primary window moves",function()
    local a=apps["com.apple.Safari"][1];local other=window(a,55)
    module.apply("office");eq(counts.apply,3);frameEq(other.f,{x=50,y=50,w=800,h=600})
end)
test("explicit focused standard window wins over the same application's main window",function()
    local a=apps["com.apple.Safari"][1];local first=a.main;focus=window(a,56)
    local chosen=focus
    module.apply("office");frameEq(chosen.f,{x=0,y=1080,w=1920,h=1080});frameEq(first.f,{x=50,y=50,w=800,h=600})
end)
test("foreign bundle result is never moved as a configured app",function()
    apps["notion.id"][1].bundle="notion.id.fake"
    module.apply("office");advance(8.2);eq(counts.apply,0)
end)
test("repeated pending coalesces and different preset executes once afterward",function()
    asynchronous=true;module.apply("office");local ok,reason=module.apply("office")
    eq(ok,true);eq(reason,"already-pending");ok,reason=module.apply("terminal")
    eq(ok,true);eq(reason,"queued");eq(counts.apply,1)
    advance(2);eq(counts.apply,6);eq(module.lastAction.status,"completed");eq(module.lastAction.preset,"terminal")
end)
test("repeating an already completed preset preserves its original undo snapshot",function()
    module.apply("office");local previous=module._undo;module.apply("office")
    eq(counts.apply,3);eq(module._undo,previous);eq(module.lastAction.detail,"already-applied")
    module.undo();frameEq(getWindow("com.openai.codex").f,{x=50,y=50,w=800,h=600})
end)
test("repeat after a manual move reapplies and captures the user's immediate preceding position",function()
    module.apply("office");getWindow("com.openai.codex").f.x=100
    module.apply("office");eq(counts.apply,6);module.undo();eq(getWindow("com.openai.codex").f.x,100)
end)
test("display role changes during transaction rollback the touched windows",function()
    asynchronous=true;local original=getWindow("com.openai.codex"):frame()
    module.apply("office");table.remove(screens,3);advance(1)
    eq(counts.apply,1);eq(counts.restore,1);eq(module.lastAction.status,"failed");frameEq(getWindow("com.openai.codex").f,original)
end)
test("unrelated Sidecar changes do not cancel an intact role layout",function()
    asynchronous=true;module.apply("office");table.remove(screens,1);advance(1)
    eq(counts.apply,3);eq(counts.restore,0);eq(module.lastAction.status,"completed")
end)
test("failed second placement rolls back itself and the first window",function()
    failAt=2;module.apply("office");eq(counts.apply,2);eq(counts.restore,2)
    frameEq(getWindow("com.openai.codex").f,{x=50,y=50,w=800,h=600})
    frameEq(getWindow("com.apple.Safari").f,{x=50,y=50,w=800,h=600});eq(module._undo,nil)
end)
test("physical user interruption stops remaining placements without undoing user edits",function()
    failAt=2;failReason="user-interrupted";module.apply("office")
    eq(counts.apply,2);eq(counts.restore,0);eq(module._undo,nil);eq(module.lastAction.status,"failed")
end)
test("undo restores frames, native-fullscreen, minimization and independent span intent",function()
    local w=getWindow("com.openai.codex");w.minimized=true;w.fullscreen=true;w.hidden=true
    w.saved={action="fullspan",frame={x=5,y=10,w=700,h=600}};w.activeSpan={"old-a","old-b"}
    module.apply("office");eq(w.minimized,false);eq(w.fullscreen,false)
    w.saved.frame.x=900 -- Prove the undo capture does not alias live saved metadata.
    module.undo();eq(counts.restore,3);eq(w.minimized,true);eq(w.fullscreen,true);eq(w.hidden,true)
    eq(w.saved.frame.x,5);eq(w.activeSpan[1],"old-a");eq(module._undo,nil)
    eq(module.undo(),false);eq(counts.restore,3)
end)
test("undo validates all PID identities before changing any surviving window",function()
    module.apply("office");getWindow("notion.id").process=999
    eq(module.undo(),false);eq(counts.restore,0);eq(module._undo,nil)
end)
test("failed no-mutation request preserves the previous complete undo snapshot",function()
    module.apply("office");local old=module._undo;table.remove(screens,3)
    eq(module.apply("terminal"),false);eq(module._undo,old)
end)
test("successful later layout replaces undo with its own immediately preceding state",function()
    module.apply("office");module.apply("terminal");module.undo()
    frameEq(getWindow("com.apple.Safari").f,{x=0,y=1080,w=1920,h=1080})
    frameEq(getWindow("com.mitchellh.ghostty").f,{x=50,y=50,w=800,h=600})
end)
test("stop cancels owned pending operation and ignores late callback",function()
    asynchronous=true;module.apply("office");local old=pending.callback
    module.stop();old(true);advance(1);eq(counts.apply,1);eq(counts.cancel,1);eq(module._transaction,nil)
    eq(module.apply("office"),false)
end)
test("manual explicit layout cancellation stops pending workspace and invalidates partial undo",function()
    asynchronous=true;module.apply("office");module.cancel("explicit-layout");advance(1)
    eq(counts.apply,1);eq(counts.restore,0);eq(module._undo,nil);eq(module.lastAction.status,"cancelled")
end)
test("config allows a different browser without changing other application bindings",function()
    local config=module.defaultConfig();config.apps.browser="company.browser"
    local chosen=window(app("company.browser",701),90);module.start(manager,config)
    module.apply("office");frameEq(chosen.f,{x=0,y=1080,w=1920,h=1080})
    frameEq(getWindow("com.apple.Safari").f,{x=50,y=50,w=800,h=600})
end)
test("default matches the user's Z focus preset",function()
    eq(module.config.defaultPreset,"focus");module.apply();eq(module.lastAction.preset,"focus");eq(counts.apply,2)
end)
test("running windowless Ghostty opens one ordinary terminal window after a stable half second",function()
    local a=apps["com.mitchellh.ghostty"][1];a.windows={};a.main=nil
    a.onNewWindow=function()timer(.1,function()window(a,170)end)end
    module.apply("terminal");advance(.49);eq(counts.newWindow,nil);eq(counts.apply,0)
    advance(.5);eq(counts.newWindow,1);eq(counts.apply,3);eq(module.lastAction.status,"completed")
    eq(launched["com.mitchellh.ghostty"],nil)
end)
test("just-launched windowless Ghostty also creates at most one window through its menu",function()
    apps["com.mitchellh.ghostty"]=nil
    delayedApps["com.mitchellh.ghostty"]=function()
        local a=app("com.mitchellh.ghostty",901)
        a.onNewWindow=function()window(a,171)end
    end
    module.apply("terminal");advance(.49);eq(counts.newWindow,nil);advance(.4)
    eq(counts.newWindow,1);eq(launched["com.mitchellh.ghostty"],1);eq(counts.apply,3)
end)
test("Ghostty window appearing naturally during startup grace avoids a duplicate",function()
    local a=apps["com.mitchellh.ghostty"][1];a.windows={};a.main=nil
    timer(.3,function()window(a,172)end)
    module.apply("terminal");advance(1);eq(counts.newWindow,nil);eq(counts.apply,3)
end)
test("successful menu request that yields no window is never repeated before timeout",function()
    local a=apps["com.mitchellh.ghostty"][1];a.windows={};a.main=nil
    module.apply("terminal");advance(8.2);eq(counts.newWindow,1);eq(counts.apply,0)
    eq(module.lastAction.status,"failed")
end)
test("disabled Ghostty New Window command fails without blind key injection",function()
    local a=apps["com.mitchellh.ghostty"][1];a.windows={};a.main=nil;a.menuDisabled=true
    module.apply("terminal");advance(1);eq(counts.newWindow,nil);eq(counts.apply,0)
    eq(module.lastAction.status,"failed");assert(module.lastAction.detail:find("New Window",1,true))
end)
test("failed Ghostty menu selection reports failure once and leaves other windows in place",function()
    local a=apps["com.mitchellh.ghostty"][1];a.windows={};a.main=nil;a.menuFailure=true
    module.apply("terminal");advance(8.2);eq(counts.newWindow,1);eq(counts.apply,0);eq(module.lastAction.status,"failed")
end)
test("hidden and other-Space standard Ghostty windows forbid creating a duplicate",function()
    local a=apps["com.mitchellh.ghostty"][1];a.main.visible=false;a.main.hidden=true;a.main=nil
    module.apply("terminal");advance(8.2);eq(counts.newWindow,nil);eq(counts.apply,0)
end)
test("minimized Ghostty standard window is reused without opening another",function()
    local a=apps["com.mitchellh.ghostty"][1];a.main.visible=false;a.main.minimized=true;a.main=nil
    module.apply("terminal");advance(1);eq(counts.newWindow,nil);eq(counts.apply,3)
end)
test("unfocused Ghostty dialog blocks new window creation",function()
    local a=apps["com.mitchellh.ghostty"][1];a.main.standard=false;a.main.visible=false;a.main=nil
    module.apply("terminal");advance(1);eq(counts.newWindow,nil);eq(counts.apply,0);eq(module.lastAction.status,"failed")
end)
test("multiple exact Ghostty instances never receive a new-window request",function()
    local a=apps["com.mitchellh.ghostty"][1];a.windows={};a.main=nil;app("com.mitchellh.ghostty",906)
    module.apply("terminal");advance(1);eq(counts.newWindow,nil);eq(counts.apply,0);eq(module.lastAction.status,"failed")
end)
test("Notion reopen never uses a New Window menu command",function()
    local a=apps["notion.id"][1];a.windows={};a.main=nil
    module.apply("office");advance(8.2);eq(counts.newWindow,nil);eq(counts.apply,0);eq(counts.open,1)
end)

test("rapid Z X C retains only latest different preset without racing",function()
    asynchronous=true;module.apply("focus");module.apply("terminal");module.apply("office")
    eq(counts.apply,1);eq(module._transaction.record.queuedPreset,"office")
    advance(2);eq(counts.apply,5);eq(module.lastAction.preset,"office");eq(module._queuedPreset,nil)
end)
test("returning to current preset clears the obsolete queued choice",function()
    asynchronous=true;module.apply("focus");module.apply("office");module.apply("focus")
    eq(module._queuedPreset,nil);advance(2);eq(counts.apply,2);eq(module.lastAction.preset,"focus")
    eq(module.lastQueuedCancellation.reason,"latest-request-is-active")
end)
test("normal failed transaction rolls back before executing latest preset",function()
    asynchronous=true;failAt=2;module.apply("office");module.apply("focus")
    advance(3);eq(counts.restore,2);eq(counts.apply,4)
    eq(module.lastAction.preset,"focus");eq(module.lastAction.status,"completed")
end)
test("physical cancellation from operation clears queued preset",function()
    asynchronous=true;failAt=1;failReason="user-interrupted";module.apply("focus");module.apply("office")
    advance(2);eq(counts.apply,1);eq(module._queuedPreset,nil);eq(module.lastAction.status,"failed")
end)
for _,reason in ipairs({"user-interrupted","explicit-layout","stopped"}) do
 test("explicit cancel clears queue: "..reason,function()
    asynchronous=true;module.apply("focus");module.apply("office");module.cancel(reason)
    advance(2);eq(counts.apply,1);eq(module._queuedPreset,nil);eq(module.lastAction.status,"cancelled")
 end)
end
test("queued layout preserves undo of immediately preceding completed layout",function()
    asynchronous=true;module.apply("focus");module.apply("office");advance(2)
    eq(module._undo.preset,"office");module.undo();advance(2)
    frameEq(getWindow("com.openai.codex").f,{x=0,y=30,w=1920,h=2130})
    eq(module._undo,nil)
end)
test("repeating queued choice executes once and preserves completed undo snapshot",function()
    asynchronous=true;module.apply("focus");module.apply("office");module.apply("office");advance(2)
    eq(counts.apply,5);local old=module._undo;module.apply("office");eq(module._undo,old);eq(counts.apply,5)
end)

test("interruption message describes state without assigning user intent",function()
    asynchronous=true;failAt=1;failReason="user-interrupted";module.apply("focus");advance(2)
    eq(module.lastAction.detail,"布局已停止，窗口保留当前位置")
end)

test("unrecoverable presentation failure rejects all placements before first mutation",function()
    getWindow("notion.id").presentationMismatch=true
    module.apply("office");eq(counts.apply,0);eq(counts.restore,0)
    eq(module.lastAction.status,"failed");assert(module.lastAction.detail:find("Win+D",1,true))
end)

test("successful Z X C raise every target in reverse order and focus primary Codex",function()
    for _,preset in ipairs({"focus","terminal","office"}) do
        counts.raised={};local beforeFocus=counts.focus
        focus=getWindow("notion.id");module.apply(preset)
        local items=module._undo.items
        eq(#counts.raised,#items)
        for index=1,#items do eq(counts.raised[index],items[#items-index+1].id) end
        eq(focus,getWindow("com.openai.codex"));eq(counts.focus,beforeFocus+1)
    end
end)
test("already applied background layout still raises all targets and focuses Codex without replacing undo",function()
    module.apply("focus");local previous=module._undo;local beforeApply=counts.apply
    counts.raised={};focus=getWindow("notion.id");local beforeFocus=counts.focus
    module.apply("focus")
    eq(module.lastAction.detail,"already-applied");eq(counts.apply,beforeApply);eq(module._undo,previous)
    eq(#counts.raised,2);eq(counts.raised[1],getWindow("notion.id"):id())
    eq(counts.raised[2],getWindow("com.openai.codex"):id());eq(focus,getWindow("com.openai.codex"))
    eq(counts.focus,beforeFocus+1)
end)
test("presentation preparation receives every fixed snapshot before any mutation",function()
    local savedItems,savedState,calls
    calls=0
    manager.prepareWorkspacePresentation=function(items,state)
        calls=calls+1;eq(counts.capture,3);eq(counts.apply,0)
        if not savedItems then savedItems,savedState=items,state end
        eq(items,savedItems);eq(state,savedState);eq(#items,3)
        return calls>=4 and true or nil
    end
    local codex=getWindow("com.openai.codex");codex.minimized=true;codex.hidden=true
    module.apply("office");eq(module._transaction.phase,"presenting");advance(.21)
    eq(counts.apply,0);eq(codex.minimized,true);eq(codex.hidden,true);eq(counts.capture,3)
    advance(.2);eq(counts.apply,3);eq(counts.capture,3)
    eq(module._undo.items[1].before.minimized,true);eq(module._undo.items[1].before.hidden,true)
end)
test("presentation wait retains selected identity when another app window becomes main",function()
    local ready=false;manager.prepareWorkspacePresentation=function()return ready and true or nil end
    local a=apps["com.apple.Safari"][1];local original=a.main
    module.apply("office");local other=window(a,555);a.main=other;a.focused=other;focus=other
    ready=true;advance(.2)
    eq(module._undo.items[2].win,original);frameEq(original.f,{x=0,y=1080,w=1920,h=1080})
    frameEq(other.f,{x=50,y=50,w=800,h=600})
end)
test("closed target during presentation wait fails before any placement or final focus",function()
    manager.prepareWorkspacePresentation=function()return nil end
    module.apply("office");getWindow("notion.id").process=999;advance(.2)
    eq(module.lastAction.status,"failed");eq(counts.apply,0);eq(counts.focus,0);eq(#counts.raised,0)
end)
test("presentation failure preserves existing undo and does not focus or raise",function()
    module.apply("focus");local previous=module._undo;local beforeFocus=counts.focus;counts.raised={}
    manager.prepareWorkspacePresentation=function()return false,"cannot-present" end
    module.apply("office");eq(module.lastAction.status,"failed");eq(module.lastAction.detail,"cannot-present")
    eq(module._undo,previous);eq(counts.focus,beforeFocus);eq(#counts.raised,0)
end)
test("presentation preparation waits even when geometry is already applied",function()
    module.apply("focus");local previous=module._undo;local ready=false;local beforeFocus=counts.focus
    manager.prepareWorkspacePresentation=function()return ready and true or nil end
    module.apply("focus");advance(.5);eq(module.lastAction.status,"preparing");eq(counts.focus,beforeFocus)
    ready=true;advance(.2);eq(module.lastAction.detail,"already-applied");eq(module._undo,previous)
    eq(counts.focus,beforeFocus+1)
end)
test("presentation focus that clamps a span uses current geometry but preserves before snapshot",function()
    module.apply("focus");local codex=getWindow("com.openai.codex");local beforeApply=counts.apply
    local before=codex:frame();local ready=false
    manager.prepareWorkspacePresentation=function()
        if not ready then return nil end
        codex.f.h=1050
        return true
    end
    module.apply("focus");ready=true;advance(.2)
    eq(counts.apply,beforeApply+2);eq(module.lastAction.detail,nil)
    frameEq(codex.f,before);frameEq(module._undo.items[1].before.frame,before)
end)
test("failed current-state recapture cannot treat stale before geometry as applied",function()
    module.apply("focus");local beforeApply=counts.apply;local previous=module._undo
    local capture=manager.captureWorkspaceWindow;local ready=false
    manager.prepareWorkspacePresentation=function()return ready and true or nil end
    module.apply("focus");manager.captureWorkspaceWindow=function()return nil end
    ready=true;advance(.2)
    eq(module.lastAction.status,"failed");eq(counts.apply,beforeApply);eq(module._undo,previous)
    manager.captureWorkspaceWindow=capture
end)
for _,reason in ipairs({"user-interrupted","explicit-layout","stopped"}) do
 test("cancellation during presentation wait stops later activation and queued layout: "..reason,function()
    manager.prepareWorkspacePresentation=function()return nil end
    module.apply("focus");module.apply("office");module.cancel(reason);advance(3)
    eq(counts.apply,0);eq(counts.focus,0);eq(#counts.raised,0);eq(module._queuedPreset,nil)
    eq(module._transaction,nil);eq(module.lastAction.status,"cancelled")
 end)
end
test("last requested preset still runs after asynchronous presentation preparation",function()
    local ready=false;manager.prepareWorkspacePresentation=function()return ready and true or nil end
    module.apply("focus");module.apply("terminal");module.apply("office")
    eq(counts.apply,0);ready=true;advance(.2)
    eq(counts.apply,5);eq(module.lastAction.preset,"office");eq(module.lastAction.status,"completed")
    eq(module._undo.preset,"office");eq(focus,getWindow("com.openai.codex"))
end)
test("undo and failed layout never trigger the successful layout reveal",function()
    module.apply("office");local beforeFocus=counts.focus;counts.raised={};module.undo()
    eq(counts.focus,beforeFocus);eq(#counts.raised,0)
    failAt=counts.apply+1;module.apply("focus")
    eq(module.lastAction.status,"failed");eq(counts.focus,beforeFocus);eq(#counts.raised,0)
end)
test("raise exception is recorded without abandoning queue or valid geometry undo",function()
    getWindow("notion.id").raise=function()error("AX raise failed")end
    module.apply("office")
    eq(module.lastAction.status,"completed");assert(module.lastAction.activationError:find("AX raise failed",1,true))
    assert(module._undo);eq(module._transaction,nil);eq(focus,getWindow("com.openai.codex"))
end)

local function windowlessNotion()
    local a=apps["notion.id"][1];a.windows={};a.main=nil;a.focused=nil;return a
end
test("running windowless Notion reopens once after stable grace and completes original Z",function()
    local a=windowlessNotion();local pid=a:pid()
    taskOptions.onStart=function()timer(.4,function()window(a,123)end)end
    module.apply("focus");eq(module.lastAction.waitingBundle,"notion.id");eq(module.lastAction.phase,"acquiring")
    advance(.25);eq(counts.open,nil);eq(select(2,module.apply("focus")),"already-pending")
    advance(.24);eq(counts.open,nil);eq(counts.apply,0);eq(counts.capture,0)
    advance(.21);eq(counts.open,1);eq(module.lastAction.waitingBundle,"notion.id")
    local record=module.lastAction.reopens["notion.id"]
    eq(record.pid,pid);eq(record.status,"requested");eq(record.exitCode,0)
    module.apply("focus");advance(.5)
    eq(counts.open,1);eq(counts.apply,2);eq(module.lastAction.status,"completed")
    eq(record.status,"window-ready");eq(record.windowID,123);eq(a:pid(),pid)
    eq(module.lastAction.waitingBundle,nil);eq(module.lastAction.phase,"moving")
    eq(launched["notion.id"],nil);eq(counts.newWindow,nil)
end)
test("new Notion launch gets its startup grace and natural window avoids reopen",function()
    apps["notion.id"]=nil
    delayedApps["notion.id"]=function()
        local a=app("notion.id",700);timer(.3,function()window(a,123)end)
    end
    module.apply("focus");advance(.6)
    eq(launched["notion.id"],1);eq(counts.open,nil);eq(counts.apply,2)
end)
test("newly launched Notion still windowless after grace receives only one reopen",function()
    apps["notion.id"]=nil
    delayedApps["notion.id"]=function()app("notion.id",700)end
    taskOptions.onStart=function()timer(.2,function()window(apps["notion.id"][1],123)end)end
    module.apply("focus");advance(.49);eq(counts.open,nil)
    advance(.6);eq(counts.open,1);eq(launched["notion.id"],1);eq(counts.apply,2)
end)
test("a temporary Notion window resets the continuous-empty grace",function()
    local a=windowlessNotion();local browser=apps["com.apple.Safari"][1];browser.windows={};browser.main=nil
    module.apply("office");advance(.2);window(a,123);advance(.15)
    a.windows={};a.main=nil;advance(.3);eq(counts.open,nil)
    advance(.4);eq(counts.open,1);eq(counts.apply,0)
end)
for _,source in ipairs({"all","main","focused"})do
 test("any nonstandard Notion "..source.." window forbids reopening",function()
    local a=windowlessNotion();local dialog=window(a,333);dialog.standard=false;dialog.visible=false
    a.main=nil;a.focused=nil
    if source~="all"then a.windows={};a[source=="main"and"main"or"focused"]=dialog end
    module.apply("focus");advance(1)
    eq(counts.open,nil);eq(counts.apply,0);eq(module.lastAction.status,"failed")
 end)
end
test("hidden other-Space standard Notion window prevents duplicate reopen",function()
    local a=windowlessNotion();local w=window(a,444);w.hidden=true;w.visible=false;a.main=nil
    module.apply("focus");advance(8.2);eq(counts.open,nil);eq(counts.apply,0)
end)
test("minimized Notion window is reused rather than reopened",function()
    local a=windowlessNotion();local w=window(a,444);w.visible=false;w.minimized=true;a.main=nil
    module.apply("focus");eq(counts.apply,2);eq(counts.open,nil)
end)
test("unknown Notion allWindows enumeration never counts as empty",function()
    local a=windowlessNotion();a.allWindows=function()return nil end
    module.apply("focus");advance(1);eq(counts.open,nil);eq(module.lastAction.status,"failed")
    assert(module.lastAction.detail:find("无法确认 Notion",1,true))
end)
test("multiple exact Notion processes and invalid PID never reopen",function()
    local a=windowlessNotion();app("notion.id",999)
    module.apply("focus");eq(counts.open,nil);eq(module.lastAction.status,"failed")
    apps["notion.id"]={a};a.process=0;module.apply("focus");advance(1)
    eq(counts.open,nil);eq(module.lastAction.status,"failed")
end)
for _,failure in ipairs({"newFailure","startFailure"})do
 test("Notion task "..failure.." fails clearly before layout writes",function()
    windowlessNotion();taskOptions[failure]=true;module.apply("focus");advance(1)
    eq(module.lastAction.status,"failed");eq(counts.apply,0)
    assert(module.lastAction.detail:find("未能启动 Notion",1,true))
    eq(module.lastAction.reopens["notion.id"].status,"failed");eq(counts.taskCreated,1)
 end)
end
test("nonzero reopen exit records error and stops without repeated attempts",function()
    windowlessNotion();taskOptions.code=1;taskOptions.stderr="Notion reopen failed"
    module.apply("focus");advance(2)
    local record=module.lastAction.reopens["notion.id"]
    eq(module.lastAction.status,"failed");assert(module.lastAction.detail:find("退出码 1",1,true))
    eq(record.exitCode,1);eq(record.stderr,"Notion reopen failed");eq(record.status,"failed")
    eq(counts.open,1);eq(counts.apply,0)
end)
test("successful open without a window remains waiting then times out once",function()
    windowlessNotion();module.apply("focus");advance(1)
    eq(module.lastAction.waitingBundle,"notion.id");eq(module.lastAction.phase,"acquiring")
    eq(module.lastAction.reopens["notion.id"].status,"requested");eq(module.lastAction.status,"waiting")
    advance(8);eq(module.lastAction.status,"failed");eq(counts.open,1);eq(counts.apply,0)
    assert(module.lastAction.detail:find("notion.id",1,true));eq(module.lastAction.waitingBundle,"notion.id")
end)
test("cancel before grace never starts reopen later",function()
    windowlessNotion();module.apply("focus");advance(.2);module.cancel("user-interrupted");advance(2)
    eq(counts.open,nil);eq(counts.apply,0);eq(module.lastAction.status,"cancelled")
end)
for _,reason in ipairs({"user-interrupted","explicit-layout","stopped"})do
 test("cancel running reopen and ignore synchronous or late callbacks: "..reason,function()
    windowlessNotion();taskOptions.hang=true;module.apply("focus");advance(.7)
    local old=module.lastAction;local record=old.reopens["notion.id"];local cb=tasks[1].callback
    module.cancel(reason);eq(counts.terminated,1);eq(record.exitCode,nil);eq(old.status,"cancelled")
    module.apply("terminal");local current=module.lastAction;local applied=counts.apply
    cb(0,"","");cb(1,"","late failure");advance(1)
    eq(module.lastAction,current);eq(current.status,"completed");eq(record.exitCode,nil)
    eq(counts.open,1);eq(counts.apply,applied);eq(old.status,"cancelled")
 end)
end
test("Notion process replacement after reopen never selects replacement window",function()
    windowlessNotion();taskOptions.hang=true;module.apply("focus");advance(.7)
    apps["notion.id"]={};window(app("notion.id",999),555);advance(.2)
    eq(module.lastAction.status,"failed");assert(module.lastAction.detail:find("进程已变化",1,true))
    eq(counts.open,1);eq(counts.apply,0);eq(counts.terminated,1)
end)
test("window arriving before open exits is accepted and late callback cannot alter result",function()
    local a=windowlessNotion();taskOptions.hang=true
    taskOptions.onStart=function()timer(.05,function()window(a,123)end)end
    module.apply("focus");advance(1)
    local record=module.lastAction.reopens["notion.id"];eq(record.status,"window-ready")
    eq(module.lastAction.status,"completed");eq(counts.apply,2);eq(counts.terminated,1)
    tasks[1].callback(1,"","late failure");eq(record.status,"window-ready");eq(record.exitCode,nil)
end)
test("hanging reopen is bounded by acquisition timeout and its child task is stopped",function()
    windowlessNotion();taskOptions.hang=true;module.apply("focus");advance(8.2)
    eq(module.lastAction.status,"failed");eq(counts.open,1);eq(counts.terminated,1);eq(counts.apply,0)
end)
test("queued X still runs after reopened Notion lets Z complete",function()
    local a=windowlessNotion();taskOptions.onStart=function()timer(.2,function()window(a,123)end)end
    module.apply("focus");module.apply("terminal");advance(2)
    eq(counts.open,1);eq(counts.apply,5);eq(module.lastAction.preset,"terminal")
    eq(module.lastAction.status,"completed");eq(module._queuedPreset,nil)
end)
test("windowless Safari never receives the Notion reopen command",function()
    local a=apps["com.apple.Safari"][1];a.windows={};a.main=nil
    module.apply("office");advance(8.2);eq(counts.open,nil);eq(counts.newWindow,nil);eq(counts.apply,0)
end)

print(string.format("PASS %d workspace layout transaction tests",total))
