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


test("failed presentation after native-fullscreen exit restores the original snapshot",function()
 local w=getWindow('com.apple.Safari');w.fullscreen=true
 w.saved={action='fullspan',frame={x=77,y=88,w=900,h=500}};w.activeSpan={'old-a','old-b'}
 local original=copy(w.f)
 function manager.prepareWorkspacePresentation(items,state,markTouched)
  assert(type(markTouched)=='function')
  if not state.mutated then
   state.mutated=true;markTouched(items[2]);w.fullscreen=false;return nil
  end
  return false,'native transition failure'
 end
 assert(module.apply('office'));eq(counts.apply,0);eq(w.fullscreen,false)
 advance(.2);eq(module.lastAction.status,'failed');eq(counts.apply,0);eq(counts.restore,1)
 eq(w.fullscreen,true);frameEq(w.f,original);eq(w.saved.frame.x,77);eq(w.activeSpan[1],'old-a')
 assert(module.lastAction.detail:find('已恢复',1,true));eq(module._transaction,nil)
end)

test("presentation-marked item restores once when later placement of the same item fails",function()
 local w=getWindow('com.apple.Safari');w.fullscreen=true;local original=copy(w.f)
 function manager.prepareWorkspacePresentation(items,state,markTouched)
  markTouched(items[2]);w.fullscreen=false;return true
 end
 failAt=2
 module.apply('office');eq(module.lastAction.status,'failed');eq(counts.apply,2)
 eq(counts.restore,2);eq(w.fullscreen,true);frameEq(w.f,original);eq(module._transaction,nil)
end)

test("successful prepared native-fullscreen window retains original state for undo",function()
 local w=getWindow('com.apple.Safari');w.fullscreen=true;local original=copy(w.f)
 function manager.prepareWorkspacePresentation(items,state,markTouched)
  markTouched(items[2]);w.fullscreen=false;return true
 end
 module.apply('office');eq(module.lastAction.status,'completed');eq(w.fullscreen,false)
 eq(module._undo.items[2].before.fullscreen,true)
 module.undo();eq(counts.restore,3);eq(w.fullscreen,true);frameEq(w.f,original)
end)

test("later target snapshot identity failure cannot enter mutating presentation preparation",function()
 local w=getWindow('com.openai.codex');w.fullscreen=true
 local capture=manager.captureWorkspaceWindow
 manager.captureWorkspaceWindow=function(win)
  local snapshot=capture(win)
  if win==getWindow('notion.id')then snapshot.pid=999 end
  return snapshot
 end
 manager.prepareWorkspacePresentation=function()error('must not prepare before all target identity validation')end
 module.apply('office');eq(module.lastAction.status,'failed');eq(counts.apply,0);eq(counts.restore,0)
 eq(w.fullscreen,true);eq(module._transaction,nil)
end)

test("user cancellation after native preparation preserves manual state and prevents delayed placement",function()
 local w=getWindow('com.apple.Safari');w.fullscreen=true
 function manager.prepareWorkspacePresentation(items,state,markTouched)
  if not state.mutated then state.mutated=true;markTouched(items[2]);w.fullscreen=false end
  return nil
 end
 module.apply('office');eq(w.fullscreen,false);module.cancel('user-interrupted');advance(2)
 eq(counts.apply,0);eq(counts.restore,0);eq(w.fullscreen,false);eq(module._transaction,nil)
end)


test("rejected unchanged native transition needs no destructive restore",function()
 local w=getWindow('com.apple.Safari');w.fullscreen=true
 local original=copy(w.f)
 function manager.prepareWorkspacePresentation(items,state,markTouched)
  markTouched(items[2]);return false,'native transition rejected'
 end
 module.apply('office');eq(module.lastAction.status,'failed');eq(counts.apply,0);eq(counts.restore,0)
 eq(w.fullscreen,true);frameEq(w.f,original);eq(module._transaction,nil)
 assert(not module.lastAction.detail:find('部分窗口无法恢复',1,true))
end)

test("changed metadata prevents treating rejected fullscreen transition as unchanged",function()
 local w=getWindow('com.apple.Safari');w.fullscreen=true;w.saved={action='fullspan',frame={x=8,y=9,w=900,h=800}}
 function manager.prepareWorkspacePresentation(items,state,markTouched)
  markTouched(items[2]);w.saved.frame.x=44;return false,'native transition rejected'
 end
 module.apply('office');eq(module.lastAction.status,'failed');eq(counts.restore,1)
 eq(w.saved.frame.x,8);eq(w.fullscreen,true)
end)


test("undo restores ordinary windows before returning any target to native fullscreen",function()
 local fullscreen=getWindow('com.apple.Safari');fullscreen.fullscreen=true
 local original={}
 for _,bundle in ipairs({'com.openai.codex','com.apple.Safari','notion.id'})do original[bundle]=getWindow(bundle):frame()end
 function manager.prepareWorkspacePresentation(items,state,markTouched)
  markTouched(items[2]);fullscreen.fullscreen=false;return true
 end
 module.apply('office');eq(module.lastAction.status,'completed');eq(fullscreen.fullscreen,false)
 local restore=manager.restoreWorkspaceWindow;local enteredNative=false;local order={}
 manager.restoreWorkspaceWindow=function(win,snapshot,callback)
  order[#order+1]=win:id()
  if enteredNative and not snapshot.fullscreen then callback(false,'ordinary window disappeared into inactive Space');return end
  if snapshot.fullscreen then enteredNative=true end
  return restore(win,snapshot,callback)
 end
 assert(module.undo());eq(module.lastAction.status,'completed');eq(module._undo,nil)
 eq(#order,3);eq(order[1],getWindow('com.openai.codex'):id());eq(order[2],getWindow('notion.id'):id());eq(order[3],fullscreen:id())
 eq(fullscreen.fullscreen,true)
 for bundle,frame in pairs(original)do frameEq(getWindow(bundle):frame(),frame)end
end)


test("undo rejects closed AX object that retains cached id and pid before restoring survivors",function()
 module.apply('office');eq(module.lastAction.status,'completed')
 local closed=getWindow('com.apple.Safari');local id,pid=closed:id(),closed:pid()
 closed.standard=false;closed.f={x=0,y=0,w=0,h=0}
 local remaining={getWindow('com.openai.codex'):frame(),getWindow('notion.id'):frame()}
 local called,ok,reason=pcall(module.undo)
 eq(called,true);eq(ok,false);eq(reason,'stale-window')
 eq(closed:id(),id);eq(closed:pid(),pid);eq(counts.restore,0);eq(module._transaction,nil);eq(module._undo,nil)
 frameEq(getWindow('com.openai.codex'):frame(),remaining[1]);frameEq(getWindow('notion.id'):frame(),remaining[2])
end)

test("undo handles snapshot AX exception without throwing or moving another window",function()
 module.apply('office');eq(module.lastAction.status,'completed')
 local initialUndo=module._undo;local capture=manager.captureWorkspaceWindow
 local frames={};for _,bundle in ipairs({'com.openai.codex','com.apple.Safari','notion.id'})do frames[bundle]=getWindow(bundle):frame()end
 manager.captureWorkspaceWindow=function(win)
  if win==getWindow('notion.id')then error('AX object became invalid while capturing')end
  return capture(win)
 end
 local called,ok,reason=pcall(module.undo)
 eq(called,true);eq(ok,false);eq(reason,'capture-failed');eq(counts.restore,0);eq(module._transaction,nil)
 for bundle,frame in pairs(frames)do frameEq(getWindow(bundle):frame(),frame)end
 -- Transient AX read failure is retryable; a successful second call must still
 -- restore from the original undo snapshot rather than a partially captured one.
 eq(module._undo,initialUndo);manager.captureWorkspaceWindow=capture
 assert(module.undo());eq(counts.restore,3);eq(module._undo,nil)
end)

test("undo treats an exception reading standard-window status as stale without mutation",function()
 module.apply('office');eq(module.lastAction.status,'completed')
 local w=getWindow('notion.id');function w:isStandard()error('AX object disconnected')end
 local called,ok,reason=pcall(module.undo)
 eq(called,true);eq(ok,false);eq(reason,'stale-window');eq(counts.restore,0);eq(module._transaction,nil)
end)

print(string.format('PASS %d preparation rollback checks',total))
