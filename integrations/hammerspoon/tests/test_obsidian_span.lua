-- Isolated policy regressions. The app model clamps AX writes to one display,
-- then accepts a drag only up to its independent, Dock-constrained bottom edge.
-- No live hs APIs, application launches, pointer events or keyboard events.
local source = os.getenv('WM_TEST_SOURCE') or 'window_manager.lua'
local function copy(value) local result={} for k,v in pairs(value) do result[k]=v end return result end
local function equalFrame(actual, expected)
  for _,key in ipairs({'x','y','w','h'}) do
    assert(actual[key]==expected[key], key..': '..tostring(actual[key])..' ~= '..expected[key])
  end
end

local function environment(options)
  options=options or {}
  local e={time=0,timers={},members={},calls={},alerts={},filters={},screens={}}
  local function timer(delay, callback, interval)
    local t={at=e.time+delay,callback=callback,interval=interval,active=true}
    function t:stop() self.active=false end
    function t:running() return self.active end
    e.timers[#e.timers+1]=t; return t
  end
  function e:advance(delta)
    local deadline=self.time+delta
    for _=1,10000 do
      local nextTimer
      for _,t in ipairs(self.timers) do
        if t.active and t.at<=deadline and (not nextTimer or t.at<nextTimer.at) then nextTimer=t end
      end
      if not nextTimer then self.time=deadline; return end
      self.time=nextTimer.at
      if nextTimer.interval then nextTimer.at=self.time+nextTimer.interval else nextTimer.active=false end
      nextTimer.callback()
    end
    error('timer runaway')
  end
  local physical={{x=0,y=0,w=1920,h=1080},{x=0,y=1080,w=1920,h=1080},
    {x=1920,y=240,w=1080,h=1920}}
  physical[2].x=options.bottomX or 0
  for i,frame in ipairs(physical) do
    local screen={number=i,bounds=copy(frame),usable=copy(frame)}
    if i==1 then screen.usable.y=30; screen.usable.h=1050 end
    if i==2 then screen.usable.h=1080-(options.dock==false and 0 or 71) end
    function screen:id() return self.number end
    function screen:getUUID() return 'screen-'..self.number end
    function screen:name() return self:getUUID() end
    function screen:frame() return copy(self.usable) end
    function screen:fullFrame() return copy(self.bounds) end
    e.screens[i]=screen
  end
  local filter={windowFocused='focused',windowMoved='moved',windowTitleChanged='title'}
  function filter.new()
    local f={}
    function f:subscribe(_, callback) self.callback=callback; return self end
    function f:unsubscribeAll() self.callback=nil end
    e.filters[#e.filters+1]=f; return f
  end
  local function moved(win)
    for _,f in ipairs(e.filters) do if f.callback then f.callback(win,'MockApp','moved') end end
  end
  local menu={}
  function menu.contains(win) return win and e.members[win:id()]~=nil or false end
  function menu.enter(win, ready) e.members[win:id()]=win; timer(.01,ready); return true end
  function menu.leave(win) if win then e.members[win:id()]=nil else e.members={} end; return true end
  function menu.stop() return menu.leave() end
  function menu.members() local list={} for _,w in pairs(e.members) do list[#list+1]=w end return list end
  function menu.restoringTopInset() return nil end
  function menu.currentTopInset() return next(e.members) and 0 or 30 end
  local function inert() return {start=function(self)return self end,stop=function()end,delete=function()end} end
  hs={
    timer={secondsSinceEpoch=function()return e.time end,doAfter=timer,
      doEvery=function(delay,fn)return timer(delay,fn,delay)end},
    screen={allScreens=function()return {table.unpack(e.screens)}end,primaryScreen=function()return e.screens[1]end,
      watcher={new=inert}},
    spaces={screensHaveSeparateSpaces=function()return false end},
    window={filter=filter,focusedWindow=function()return e.win end,
      allWindows=function()return e.win and {e.win} or {} end,
      list=function()
        if not e.win then return {} end
        local w,f=e.win,e.win:frame()
        return {{kCGWindowNumber=w:id(),kCGWindowOwnerPID=w:pid(),kCGWindowIsOnscreen=true,
          kCGWindowAlpha=1,kCGWindowBounds={X=f.x,Y=f.y,Width=f.w,Height=f.h}}}
      end},
    eventtap={checkKeyboardModifiers=function()return {} end,checkMouseButtons=function()return {} end,
      new=inert,event={properties={eventSourceUserData=42},types={mouseMoved=1,leftMouseDown=2,
        leftMouseDragged=3,leftMouseUp=4,rightMouseDown=5,rightMouseDragged=6,rightMouseUp=7,
        otherMouseDown=8,otherMouseDragged=9,otherMouseUp=10}}},
    mouse={absolutePosition=function()return {x=400,y=300}end},
    accessibilityState=function()return true end,hotkey={bind=inert},printf=function()end,
    alert={show=function(message)e.alerts[#e.alerts+1]=message end},
  }
  package.loaded.menu_bar_mode=menu
  package.loaded.window_span_drag={start=function(win,base,target,done)
    local call={target=copy(target)}; e.calls[#e.calls+1]=call
    win:setFrame(base)
    local pending=timer(.05,function()
      local actual=copy(target)
      if options.actualHeight then actual.h=options.actualHeight
      elseif win:application():bundleID()=='md.obsidian' then
        local bottom=e.screens[2]:frame()
        actual.h=math.min(target.y+target.h,bottom.y+bottom.h)-target.y-1
      else actual.h=target.h-1 end
      if options.actualWidth then actual.w=options.actualWidth end
      win.frameValue=actual; moved(win)
      -- The same strict acceptance boundary as window_span_drag; a one-screen
      -- or wrong-width result must still fail, even with the Dock accommodation.
      local ok=math.abs(actual.x-target.x)<=4 and math.abs(actual.y-target.y)<=4
        and math.abs(actual.w-target.w)<=32 and math.abs(actual.h-target.h)<=32
      call.accepted=ok; done(ok,win:frame(),ok and nil or '应用未接受跨屏拖动')
    end)
    return {cancel=function(reason)pending:stop();done(false,win:frame(),reason)end}
  end}
  _G.wm=nil; e.wm=dofile(source); e.wm.start(true)
  local w={frameValue=copy(options.frame or {x=100,y=100,w=800,h=600})}
  function w:id() return 1 end
  function w:pid() return 1001 end
  function w:role() return 'AXWindow' end
  function w:frame() return copy(self.frameValue) end
  function w:screen()
    for _,screen in ipairs(e.screens) do local f,b=self.frameValue,screen.bounds
      if f.x>=b.x and f.x<b.x+b.w and f.y>=b.y and f.y<b.y+b.h then return screen end
    end
    return e.screens[1]
  end
  function w:isStandard() return true end
  function w:isFullScreen() return false end
  function w:isMinimized() return false end
  function w:raise() return self end
  function w:focus() return self end
  local app={name=function()return 'MockApp'end,
    bundleID=function()return options.bundle or 'md.obsidian'end,isHidden=function()return false end}
  function w:application() return app end
  function w:setSize(size) self.frameValue.w=size.w; self.frameValue.h=math.min(size.h,self:screen().bounds.h) end
  function w:setFrame(frame)
    self.frameValue=copy(frame); self.frameValue.h=math.min(frame.h,self:screen().bounds.h); moved(self)
  end
  e.win=w
  function e:close() self.wm.stop(true) end
  return e,w
end

local passed,failed=0,0
local function test(name,fn)
  local ok,err=pcall(fn)
  if ok then passed=passed+1;print('PASS '..name)
  else failed=failed+1;print('FAIL '..name..': '..tostring(err)) end
end
local function pressA(e,w)
  assert(e.wm.dispatch('a',w)); e:advance(1)
  assert(e.wm.lastAction.succeeded,tostring(e.wm.lastAction.detail))
end
local function workspace(e,w,spec,snapshot)
  local result={count=0}
  local function done(ok,detail) result.count=result.count+1;result.ok=ok;result.detail=detail end
  if snapshot then assert(e.wm.restoreWorkspaceWindow(w,snapshot,done))
  else assert(e.wm.applyWorkspaceWindow(w,spec,done)) end
  e:advance(2); assert(result.count==1,tostring(result.count)..' callbacks'); return result
end
local pair={'screen-1','screen-2'}
local fullSpec={span=true,spanScreens=pair,fullBounds=true}

test('A spans Obsidian above the Dock and keeps the physical top edge',function()
  local e,w=environment(); pressA(e,w)
  equalFrame(e.calls[1].target,{x=0,y=0,w=1920,h=2089})
  equalFrame(w:frame(),{x=0,y=0,w=1920,h=2088})
  assert(e.calls[1].accepted and e.members[w:id()] and #e.calls==1)
  equalFrame(e.wm._savedFrames[w:id()].spanTarget,e.calls[1].target);e:close()
end)
test('A without a bottom Dock still targets the complete physical span',function()
  local e,w=environment({dock=false});pressA(e,w)
  equalFrame(e.calls[1].target,{x=0,y=0,w=1920,h=2160});assert(w:frame().h==2159);e:close()
end)
test('a permitted 1-4 pixel horizontal offset still uses the lowest display Dock',function()
  for offset=-1,-4,-1 do
    local e,w=environment({bottomX=offset})
    local physical,reason=e.wm.workspaceSpanFrame(pair,true)
    assert(physical,tostring(reason))
    equalFrame(physical,{x=0,y=0,w=1920+offset,h=2160})
    pressA(e,w)
    equalFrame(e.calls[1].target,{x=0,y=0,w=1920+offset,h=2089})
    assert(w:frame().h==2088)
    pressA(e,w)
    local result=workspace(e,w,fullSpec);assert(result.ok,tostring(result.detail))
    equalFrame(e.wm.lastWorkspaceOperation.target,{x=0,y=0,w=1920+offset,h=2089})
    e:close()
  end
end)
test('other applications retain the complete physical span above the same Dock',function()
  for _,bundle in ipairs({'com.openai.codex','com.anthropic.claudefordesktop','md.obsidian.extra'}) do
    local e,w=environment({bundle=bundle});pressA(e,w)
    equalFrame(e.calls[1].target,{x=0,y=0,w=1920,h=2160});e:close()
  end
end)
test('a second A restores the exact original window instead of retrying the span',function()
  local e,w=environment();local original=w:frame();pressA(e,w);pressA(e,w)
  equalFrame(w:frame(),original);assert(#e.calls==1 and not e.members[w:id()])
  assert(not e.wm._savedFrames[w:id()]);e:close()
end)
test('a single-screen Obsidian resize is rejected and rolled back',function()
  local e,w=environment({actualHeight=1080});local original=w:frame()
  assert(e.wm.dispatch('a',w));e:advance(1)
  assert(e.wm.lastAction.succeeded==false and e.calls[1].accepted==false)
  equalFrame(w:frame(),original);assert(not e.members[w:id()]);e:close()
end)
test('a wrong-width resize is rejected despite reaching the usable bottom',function()
  local e,w=environment({actualWidth=1600});local original=w:frame()
  assert(e.wm.dispatch('a',w));e:advance(1)
  assert(e.wm.lastAction.succeeded==false and e.calls[1].accepted==false)
  equalFrame(w:frame(),original);e:close()
end)
test('fixed fullspan layout verifies the same adjusted target as A',function()
  local e,w=environment();local result=workspace(e,w,fullSpec)
  assert(result.ok,tostring(result.detail))
  equalFrame(e.wm.lastWorkspaceOperation.target,{x=0,y=0,w=1920,h=2089})
  assert(w:frame().h==2088 and e.wm.lastWorkspaceOperation.succeeded);e:close()
end)
test('a fixed layout does not report a one-screen resize as success',function()
  local e,w=environment({actualHeight=1080});local result=workspace(e,w,fullSpec)
  assert(not result.ok and not e.wm.lastWorkspaceOperation.succeeded)
  assert(e.wm.lastWorkspaceOperation.target.h==2089);e:close()
end)
test('undo after fixed fullspan restores the ordinary window and its history',function()
  local e,w=environment();local snapshot=e.wm.captureWorkspaceWindow(w)
  assert(workspace(e,w,fullSpec).ok)
  local result=workspace(e,w,nil,snapshot);assert(result.ok,tostring(result.detail))
  equalFrame(w:frame(),snapshot.frame);assert(not e.members[w:id()] and not e.wm._savedFrames[w:id()]);e:close()
end)
test('undo to a previous active Obsidian span uses the Dock target again',function()
  local e,w=environment();pressA(e,w);local snapshot=e.wm.captureWorkspaceWindow(w)
  assert(snapshot.activeSpan)
  assert(workspace(e,w,{screenUUID='screen-3'}).ok)
  local result=workspace(e,w,nil,snapshot);assert(result.ok,tostring(result.detail))
  assert(w:frame().h==2088 and e.members[w:id()] and e.calls[#e.calls].target.h==2089)
  pressA(e,w);equalFrame(w:frame(),{x=100,y=100,w=800,h=600});e:close()
end)
test('maintenance repairs an app clamp with the same Dock-constrained target',function()
  local e,w=environment();pressA(e,w)
  w.frameValue={x=0,y=0,w=1920,h=1080};e:advance(2)
  assert(#e.calls==2 and e.calls[2].target.h==2089 and w:frame().h==2088)
  e:advance(2);assert(#e.calls==2,'stable span repeatedly resized');e:close()
end)
test('A on the right portrait display still fills only that physical display',function()
  local e,w=environment({frame={x=2000,y=400,w=800,h=600}});pressA(e,w)
  equalFrame(w:frame(),{x=1920,y=240,w=1080,h=1920});assert(#e.calls==0);e:close()
end)

print(string.format('%d checks passed, %d failed',passed,failed))
assert(failed==0,'Obsidian span regression failure')
