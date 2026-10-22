-- Pure Lua regression: every hs API below is a mock; no desktop interaction.
local source = os.getenv('WM_TEST_SOURCE') or 'window_manager.lua'
local function copy(t) local r={} for k,v in pairs(t) do r[k]=v end return r end
local function equalFrame(a,b)
  for _,k in ipairs({'x','y','w','h'}) do assert(a[k]==b[k],k..' differs: '..a[k]..' / '..b[k]) end
end
local function environment(options)
  options=options or {}
  local e={time=0,timers={},filters={},windows={},members={},calls={},alerts={},flags={},cancelled=0,mouseEvents={},taps={},screenWatchers={},mouseWarps={},keys={},topInset=options.topInset or 0}
  local function timer(delay,fn,period)
    local t={at=e.time+delay,fn=fn,period=period,active=true}
    function t:stop() self.active=false; return self end
    function t:running() return self.active end
    e.timers[#e.timers+1]=t;return t
  end
  function e:advance(delta)
    local untilTime=self.time+delta
    for _=1,10000 do
      local nextTimer
      for _,t in ipairs(self.timers) do
        if t.active and t.at<=untilTime and (not nextTimer or t.at<nextTimer.at) then nextTimer=t end
      end
      if not nextTimer then self.time=untilTime;return end
      self.time=nextTimer.at
      if nextTimer.period then nextTimer.at=self.time+nextTimer.period else nextTimer.active=false end
      nextTimer.fn()
    end
    error('timer runaway')
  end
  local screenFrames=options.screenFrames or {{x=0,y=0,w=1920,h=1080},{x=0,y=1080,w=1920,h=1080},{x=1920,y=240,w=1080,h=1920}}
  local screens={}
  for i,f in ipairs(screenFrames)do
    local s={number=i,bounds=f}
    function s:id()return self.number end
    function s:getUUID()return 'screen-'..self.number end
    function s:name()return self:getUUID() end
    function s:frame()return copy(self.bounds)end
    function s:fullFrame()return copy(self.bounds)end
    screens[i]=s
  end
  e.screens=screens
  local filter={windowFocused='focused',windowMoved='moved',windowTitleChanged='title'}
  function filter.new()
    local f={subscriptions={},enabled=true}
    function f:subscribe(events,fn)
      if type(events)=='string'then events={events}end
      for _,event in ipairs(events)do self.subscriptions[event]=fn end
      return self
    end
    function f:unsubscribeAll()self.subscriptions={};self.enabled=false end
    e.filters[#e.filters+1]=f;return f
  end
  function e:emit(win,event)
    for _,f in ipairs(self.filters)do
      local fn=f.enabled and f.subscriptions[event]
      if fn then fn(win,'MockApp',event)end
    end
  end
  local menu={}
  function menu.contains(win)return win and e.members[win:id()]~=nil or false end
  function menu.enter(win,ready)
    e.members[win:id()]=win
    timer(.01,function()if menu.contains(win)then ready()end end)
    return true
  end
  function menu.leave(win)if win then e.members[win:id()]=nil else e.members={}end;return true end
  function menu.stop()return menu.leave()end
  function menu.members()local result={}for _,w in pairs(e.members)do result[#result+1]=w end return result end
  function menu.restoringTopInset()return nil end
  function menu.currentTopInset()return options.topInset or 0 end
  local drag={}
  function drag.start(win,base,target,done)
    local call={win=win,done=done,finished=false,target=target}
    e.calls[#e.calls+1]=call
    win:setFrame(base)
    local function finish(ok,reason)
      if call.finished then return end
      call.finished=true
      if ok then win.frameValue=copy(target);win.frameValue.h=target.h-2;e:emit(win,'moved')end
      done(ok,win:frame(),reason)
    end
    call.timer=timer(options.dragDelay or .05,function()finish(not options.fail,'mock rejected resize')end)
    return {cancel=function(reason)
      e.cancelled=e.cancelled+1;call.timer:stop();finish(false,reason)
    end}
  end
  hs={
    timer={secondsSinceEpoch=function()return e.time end,absoluteTime=function()return math.floor(e.time*1e9)end,doAfter=function(d,fn)return timer(d,fn)end,
      doEvery=function(d,fn)return timer(d,fn,d)end},
    screen={allScreens=function()return e.screens end,primaryScreen=function()return e.screens[1]end,
      watcher={new=function(fn)
        local watcher={fn=fn,active=false}
        function watcher:start()self.active=true;return self end
        function watcher:stop()self.active=false end
        e.screenWatchers[#e.screenWatchers+1]=watcher;return watcher
      end}},
    spaces={screensHaveSeparateSpaces=function()return options.separateSpaces or false end},
    window={focusedWindow=function()return e.focused end,filter=filter,
      list=function()
        if e.cgUnavailable then return nil end
        local result={};for _,w in pairs(e.windows)do
          if not e.cgMissing then
            local f=e.cgFrame or w:frame()
            result[#result+1]={kCGWindowNumber=w:id(),kCGWindowOwnerPID=e.cgPID or w:pid(),
              kCGWindowIsOnscreen=true,kCGWindowAlpha=1,
              kCGWindowBounds={X=f.x,Y=f.y,Width=f.w,Height=f.h}}
          end
        end;return result
      end,
      allWindows=function()local result={}for _,w in pairs(e.windows)do result[#result+1]=w end return result end},
    application={applicationsForBundleID=function(bundle)
      return{{allWindows=function()local result={}for _,w in pairs(e.windows)do
        if w:application():bundleID()==bundle then result[#result+1]=w end
      end return result end}}
    end},
    eventtap={checkKeyboardModifiers=function()return e.flags end,checkMouseButtons=function()return{}end,
      new=function(events,fn)
        local tap={fn=fn,active=false,kinds={}}
        for _,kind in ipairs(events)do tap.kinds[kind]=true end
        function tap:start()self.active=true;return self end
        function tap:stop()self.active=false end
        e.taps[#e.taps+1]=tap;return tap
      end,
      event={properties={eventSourceUserData=42,mouseEventDeltaX=43,mouseEventDeltaY=44,eventSourceUnixProcessID=45},
        types={mouseMoved='move',leftMouseDown='down',leftMouseDragged='drag',leftMouseUp='up',
          rightMouseDown='rdown',rightMouseDragged='rdrag',rightMouseUp='rup',
          otherMouseDown='odown',otherMouseDragged='odrag',otherMouseUp='oup'},
        newMouseEvent=function(kind,point)
          local position=copy(point)
          local event={kind=kind,position=position,properties={}}
          function event:setProperty(key,value)self.properties[key]=value;return self end
          function event:getProperty(key)return self.properties[key]or 0 end
          function event:getType()return self.kind end
          function event:location()return copy(self.position)end
          function event:timestamp(value)if value then self.stamp=value;return self end;return self.stamp or 0 end
          function event:post()
            e.mouseEvents[#e.mouseEvents+1]=kind
            e.mouse=copy(position)
            e.posted=e.posted or{};e.posted[#e.posted+1]={kind=kind,position=copy(position),tag=self:getProperty(42)}
            if options.deferOwnDown and kind=='down' then e.queuedDown=self
            else for _,tap in ipairs(e.taps)do if tap.active and tap.kinds[kind]then tap.fn(self)end end end
            if kind=='down'then e.dragStart=copy(position)end
            if kind=='drag'and e.focused then
              local f=e.focused.frameValue
              if math.abs(position.x-(e.dragStart or position).x)>math.abs(position.y-(e.dragStart or position).y)then
                f.w=position.x+1-f.x
              else f.h=position.y+1-f.y end
            end
          end
          return event
        end}},
    mouse={absolutePosition=function(position)
      if position then e.mouse=copy(position);e.mouseWarps[#e.mouseWarps+1]=copy(position)end
      return copy(e.mouse or{x=400,y=300})
    end},
    chooser={new=function(callback)
      local chooser={callback=callback}
      for _,method in ipairs({'rows','width','searchSubText','placeholderText','choices','query','show'})do
        chooser[method]=function(self)return self end
      end
      function chooser:delete()end
      e.chooser=chooser;return chooser
    end},
    accessibilityState=function()return true end,
    hotkey={bind=function(_,key,fn)
      e.keys[key]=fn
      if key=='escape'then e.escape=fn end
      return{delete=function()if key=='escape'and e.escape==fn then e.escape=nil end end}
    end},
    alert={show=function(message)e.alerts[#e.alerts+1]=message end},
    printf=function()end,
  }
  package.loaded.window_span_drag=options.realDrag and dofile('window_span_drag.lua') or drag
  package.loaded.menu_bar_mode=menu
  _G.wm=nil
  e.wm=dofile(source)
  e.wm.start(true)
  function e:window(id,registered,frame)
    local w={number=id,frameValue=copy(frame or{x=0,y=0,w=1920,h=1080}),minimized=false}
    function w:id()return self.number end
    function w:pid()return self.pidValue or self.number+1000 end
    function w:role()return'AXWindow'end
    function w:frame()return copy(self.frameValue)end
    function w:screen()
      local f=self.frameValue
      for _,s in ipairs(e.screens)do local b=s.bounds
        if f.x>=b.x and f.x<b.x+b.w and f.y>=b.y and f.y<b.y+b.h then return s end
      end
      return e.screens[1]
    end
    function w:isFullScreen()return self.fullscreen or false end
    function w:setFullScreen(value)
      if options.fullscreenReject then return nil end
      if options.fullscreenDelay then timer(options.fullscreenDelay,function()self.fullscreen=value end)
      else self.fullscreen=value end
      return self
    end
    function w:unminimize()
      if options.unminimizeReject then return nil end
      self.minimized=false;return self
    end
    function w:isStandard()return true end
    function w:isMinimized()return self.minimized end
    function w:minimize()
      if options.minimizeReject then return nil end
      if options.minimizeDelay then timer(options.minimizeDelay,function()self.minimized=true end)
      else self.minimized=true end
      return self
    end
    function w:focus()e.focused=self end
    function w:raise()self.raised=true;return self end
    local application = {}
    function application:name()return 'MockApp'end
    function application:bundleID()return w.bundleID or 'com.openai.codex'end
    function application:isHidden()return w.hidden or false end
    function application:hide()if not options.hideReject then w.hidden=true end;return self end
    function application:unhide()if not options.unhideReject then w.hidden=false end;return self end
    function w:application()return application end
    function w:setSize(size)self.frameValue.w=size.w;self.frameValue.h=math.min(size.h,self:screen().bounds.h)end
    function w:setFrame(value)
      self.writes=(self.writes or 0)+1
      self.frameValue=copy(value)
      self.frameValue.h=math.min(self.frameValue.h,self:screen().bounds.h)
      if options.clampWidth then self.frameValue.w=math.min(self.frameValue.w,self:screen().bounds.w)end
      e:emit(self,'moved')
    end
    self.windows[id]=w
    if registered then
      self.members[id]=w
      self.wm._savedFrames[id]={pid=w:pid(),frame={x=100,y=100,w=800,h=600},
        screenUUID='screen-1',screenFrame=copy(screenFrames[1]),action='fullspan'}
    end
    return w
  end
  function e:sendMouse(kind,point)
    local event=hs.eventtap.event.newMouseEvent(kind,point or self.mouse or{x=400,y=300})
    if point then self.mouse=copy(point)end
    for _,tap in ipairs(self.taps)do if tap.active and tap.kinds[kind]then tap.fn(event)end end
  end
  function e:deliver(event)
    for _,tap in ipairs(self.taps)do if tap.active and tap.kinds[event:getType()]then tap.fn(event)end end
  end
  function e:acknowledgeDown()
    local event=self.queuedDown;self.queuedDown=nil;assert(event);self:deliver(event)
  end
  function e:changeScreens(list)
    self.screens=list
    for _,watcher in ipairs(self.screenWatchers)do if watcher.active then watcher.fn()end end
  end
  function e:close()self.wm.stop(true)end
  return e
end

local passed,failed=0,0
local function test(name,fn)
  local ok,reason=pcall(fn)
  if ok then passed=passed+1;print('PASS '..name)
  else failed=failed+1;print('FAIL '..name..': '..tostring(reason))end
end


local function callbackResult()
  local result={count=0}
  result.callback=function(ok,detail)result.count=result.count+1;result.ok=ok;result.detail=detail end
  return result
end
local pair={'screen-1','screen-2'}
local function applyPreset(e,w,spec)
  local r=callbackResult();assert(e.wm.applyWorkspaceWindow(w,spec,r.callback));e:advance(2)
  assert(r.count==1 and r.ok,tostring(r.detail));return r
end
local function restorePreset(e,w,snapshot)
  local r=callbackResult();assert(e.wm.restoreWorkspaceWindow(w,snapshot,r.callback));e:advance(3)
  assert(r.count==1 and r.ok,tostring(r.detail));return r
end
local function window(e,frame)
 local w=e:window(1,false,frame or{x=100,y=100,w=800,h=600});e.focused=w;return w
end
local function activeSpan(e,full)
  local w=window(e)
  applyPreset(e,w,{span=true,spanScreens=pair,fullBounds=full})
  return w
end

local function revealEnvironment()
 local e=environment();local a=e:window(41,false,{x=0,y=30,w=1920,h=1050})
 local b=e:window(42,false,{x=1920,y=240,w=1080,h=1920});b.bundleID='notion.id'
 e.cgByID={};e.activeSpaces={['screen-1']=1};e.closes=0;e.toggles=0
 hs.window.list=function()
  local result={};for _,w in pairs(e.windows)do
   local f=e.cgByID[w:id()] or w:frame()
   if f~='missing' then result[#result+1]={kCGWindowNumber=w:id(),kCGWindowOwnerPID=w:pid(),kCGWindowBounds={X=f.x,Y=f.y,Width=f.w,Height=f.h},kCGWindowIsOnscreen=true,kCGWindowAlpha=1}end
  end;return result
 end
 hs.spaces.activeSpaces=function()return e.activeSpaces end
 hs.spaces.closeMissionControl=function()e.closes=e.closes+1;if e.closeWorks then e.cgByID={}end end
 hs.spaces.toggleShowDesktop=function()e.toggles=e.toggles+1;if not e.toggleReject then e.cgByID={}end end
 local items={{win=a,id=a:id(),pid=a:pid(),before={fullscreen=a:isFullScreen()}},{win=b,id=b:id(),pid=b:pid(),before={fullscreen=b:isFullScreen()}}}
 function e:desktop()
  self.cgByID[a:id()]={x=-1908,y=42,w=1920,h=1050}
  self.cgByID[b:id()]={x=2976,y=362,w=1080,h=1920}
 end
 function e:prepare(state,markTouched)return self.wm.prepareWorkspacePresentation(items,state,markTouched)end
 return e,a,b,items
end
test('global preparation accepts background ordinary windows without moving or focusing',function()
 local e,a,b=revealEnvironment();e.focused=b;assert(e:prepare({})==true)
 assert(e.focused==b and not a.writes and e.toggles==0 and e.closes==0);e:close()
end)
test('explicit preset waits for released modifiers before revealing desktop',function()
 local e=revealEnvironment();e:desktop();e.flags={ctrl=true,alt=true};local s={}
 assert(e:prepare(s)==nil);e:advance(.5);assert(e:prepare(s)==nil and e.toggles==0 and e.closes==0)
 e.flags={};assert(e:prepare(s)==nil and e.closes==1);e:close()
end)
test('two stable applications at desktop edges cause one reveal and no mouse or frame writes',function()
 local e,a,b=revealEnvironment();e:desktop();local s={};assert(e:prepare(s)==nil)
 e:advance(.31);assert(e:prepare(s)==nil and e.toggles==0)
 e:advance(.16);assert(e:prepare(s)==nil and e.toggles==1)
 e:advance(.4);assert(e:prepare(s)==true and e.toggles==1)
 assert(not a.writes and not b.writes and #e.mouseEvents==0);e:close()
end)
test('a rejected desktop reveal never toggles again and times out without dragging',function()
 local e=revealEnvironment();e:desktop();e.toggleReject=true;local s={}
 for _=1,22 do e:prepare(s);e:advance(.4)end
 local ok=e:prepare(s);assert(ok==false and e.toggles==1 and #e.mouseEvents==0);e:close()
end)
test('mission control close is followed by readiness rather than a desktop toggle',function()
 local e=revealEnvironment();e.closeWorks=true;e.cgByID[41]={x=100,y=100,w=300,h=150};local s={}
 assert(e:prepare(s)==nil);e:advance(.35);assert(e:prepare(s)==true and e.closes==1 and e.toggles==0);e:close()
end)
test('thumbnail scaling never qualifies as Show Desktop',function()
 local e=revealEnvironment();e.cgByID[41]={x=-1908,y=42,w=300,h=150};e.cgByID[42]={x=2976,y=362,w=300,h=150};local s={}
 for _=1,6 do e:prepare(s);e:advance(.4)end
 assert(e.toggles==0);e:close()
end)
test('one displaced application cannot trigger a global desktop toggle',function()
 local e,a,b=revealEnvironment();e:desktop();b.pidValue=a:pid();local s={}
 local items={{win=a,id=a:id(),pid=a:pid(),before={fullscreen=a:isFullScreen()}},{win=b,id=b:id(),pid=b:pid(),before={fullscreen=b:isFullScreen()}}}
 for _=1,6 do e.wm.prepareWorkspacePresentation(items,s);e:advance(.4)end
 assert(e.toggles==0);e:close()
end)
test('a window substantially visible on another monitor is not a desktop edge',function()
 local e=revealEnvironment();e:desktop();e.cgByID[41]={x=0,y=1080,w=1920,h=1050};local s={}
 for _=1,6 do e:prepare(s);e:advance(.4)end
 assert(e.toggles==0);e:close()
end)
test('Space changes reset the stable desktop evidence interval',function()
 local e=revealEnvironment();e:desktop();local s={};e:prepare(s);e:advance(.31);e:prepare(s)
 e:advance(.16);e.activeSpaces['screen-1']=2;e:prepare(s);assert(e.toggles==0)
 e:advance(.16);e:prepare(s);assert(e.toggles==1);e:close()
end)
test('display changes reset the stable desktop evidence interval',function()
 local e=revealEnvironment();e:desktop();local s={};e:prepare(s);e:advance(.31);e:prepare(s)
 e:advance(.16);e.screens[3].bounds.y=241;e:prepare(s);assert(e.toggles==0)
 e:advance(.16);e:prepare(s);assert(e.toggles==1);e:close()
end)
test('hidden and minimized targets stay untouched until the existing operation restores them',function()
 local e,a,b=revealEnvironment();a.hidden=true;b.minimized=true;e.cgByID[41]='missing';e.cgByID[42]='missing'
 assert(e:prepare({})==true and a.hidden and b.minimized and not a.writes and not b.writes);e:close()
end)
test('missing compositor windows receive bounded exact focus requests before timeout',function()
 local e,a=revealEnvironment();e.cgByID[41]='missing';local requests=0;function a:focus()requests=requests+1 end
 local s={};for _=1,22 do e:prepare(s);e:advance(.4)end
 assert(e:prepare(s)==false and requests>=1 and requests<=3 and e.toggles==0 and #e.mouseEvents==0);e:close()
end)
test('reused window identity fails before any reveal or focus request',function()
 local e,a=revealEnvironment();a.pidValue=9876;assert(e:prepare({})==false and e.closes==0 and e.toggles==0);e:close()
end)
test('explicit preset preparation suppresses concurrent automatic span maintenance',function()
 local e=environment();local w=e:window(1,true);e.focused=w;e.wm.workspaceLayouts={_transaction={},cancel=function()end}
 e:emit(w,'moved');e:advance(1);assert(#e.calls==0 and not w.writes)
 e.wm.workspaceLayouts._transaction=nil;e:advance(1);assert(#e.calls==1);e:close()
end)


local function finishPreparation(e,state,seconds,markTouched)
 local ready,reason
 for _=1,math.ceil((seconds or 12)/.05) do
  ready,reason=e:prepare(state,markTouched)
  if ready~=nil then return ready,reason end
  e:advance(.05)
 end
 return ready,reason
end

test('holding shortcut modifiers for six seconds does not spend window readiness budget',function()
 local e,a,b=revealEnvironment();e.flags={cmd=true,shift=true,alt=true};local s={}
 for _=1,13 do assert(e:prepare(s)==nil);e:advance(.5)end
 assert(e.closes==0 and e.toggles==0 and not a.writes and not b.writes)
 e.flags={};local ok,reason=finishPreparation(e,s,3)
 assert(ok==true,tostring(reason));e:close()
end)

test('a temporary missing compositor entry can recover on a second exact focus request',function()
 local e,a,b=revealEnvironment();e.cgByID[a:id()]='missing';local requests=0
 function a:focus()
  requests=requests+1;e.focused=self
  if requests==2 then e.cgByID[a:id()]=nil end
 end
 local ok,reason=finishPreparation(e,{},12)
 assert(ok==true,tostring(reason));assert(requests==2 and e.toggles==0)
 assert(not a.writes and not b.writes and #e.mouseEvents==0);e:close()
end)

test('native-fullscreen target exits before requiring its missing compositor entry',function()
 local e,a,b,items=revealEnvironment();a.fullscreen=true;items[1].before.fullscreen=true
 e.cgByID[a:id()]='missing';local marked,exits=0,0
 function a:setFullScreen(value)
  assert(value==false,'preparation must only exit fullscreen')
  assert(marked==1,'original state must be recorded before exit')
  exits=exits+1
  hs.timer.doAfter(.8,function()self.fullscreen=false;e.cgByID[self:id()]=nil end)
  return self
 end
 local function touched(item)assert(item==items[1]);marked=marked+1 end
 local ok,reason=finishPreparation(e,{},12,touched)
 assert(ok==true,tostring(reason));assert(exits==1 and marked==1 and not a.fullscreen)
 assert(not a.writes and not b.writes and e.toggles==0 and #e.mouseEvents==0)
 assert(items[1].before.fullscreen==true,'undo snapshot changed during native transition');e:close()
end)

test('native-fullscreen exit waits for held modifiers before recording or changing state',function()
 local e,a,b,items=revealEnvironment();a.fullscreen=true;items[1].before.fullscreen=true;e.cgByID[a:id()]='missing'
 e.flags={ctrl=true,shift=true};local marked,exits=0,0
 function a:setFullScreen(value)exits=exits+1;self.fullscreen=value;e.cgByID[self:id()]=nil;return self end
 local s={};local function touched()marked=marked+1 end
 for _=1,12 do assert(e:prepare(s,touched)==nil);e:advance(.5)end
 assert(marked==0 and exits==0 and a.fullscreen)
 e.flags={};local ok,reason=finishPreparation(e,s,12,touched)
 assert(ok==true,tostring(reason));assert(marked==1 and exits==1);e:close()
end)

test('all target identities are validated before the first native-fullscreen change',function()
 local e,a,b,items=revealEnvironment();a.fullscreen=true;items[1].before.fullscreen=true
 b.pidValue=99;local marked,exits=0,0
 function a:setFullScreen(value)exits=exits+1;return self end
 local ok=e:prepare({},function()marked=marked+1 end)
 assert(ok==false and marked==0 and exits==0 and a.fullscreen)
 assert(e.closes==0 and e.toggles==0 and not a.writes and not b.writes);e:close()
end)

test('all target standard-window checks precede native-fullscreen mutations',function()
 local e,a,b,items=revealEnvironment();a.fullscreen=true;items[1].before.fullscreen=true
 function b:isStandard()return false end
 local marked,exits=0,0;function a:setFullScreen(value)exits=exits+1;return self end
 local ok=e:prepare({},function()marked=marked+1 end)
 assert(ok==false and marked==0 and exits==0 and a.fullscreen)
 assert(e.closes==0 and e.toggles==0);e:close()
end)

test('fullscreen refusal is bounded to at most three native transition requests',function()
 local e,a,b,items=revealEnvironment();a.fullscreen=true;items[1].before.fullscreen=true
 e.cgByID[a:id()]='missing';local marked,exits=0,0
 function a:setFullScreen(value)exits=exits+1;return nil end
 local ok,reason=finishPreparation(e,{},20,function()marked=marked+1 end)
 assert(ok==false,tostring(reason));assert(marked==1 and exits>=1 and exits<=3 and a.fullscreen)
 assert(not a.writes and not b.writes and #e.mouseEvents==0 and e.toggles==0);e:close()
end)

test('identity replacement during fullscreen transition never changes the replacement',function()
 local e,a,b,items=revealEnvironment();a.fullscreen=true;items[1].before.fullscreen=true
 e.cgByID[a:id()]='missing';local exits=0
 function a:setFullScreen(value)exits=exits+1;return self end
 local s={};assert(e:prepare(s,function()end)==nil);assert(exits==1)
 a.pidValue=9999;e:advance(.2);local ok=e:prepare(s,function()error('replacement marked touched')end)
 assert(ok==false and exits==1 and not a.writes and not b.writes);e:close()
end)


test('permanent held shortcut ends with input guidance and no window mutation',function()
 local e,a,b=revealEnvironment();e.flags={ctrl=true,shift=true};local s={};local ok,reason
 for _=1,33 do ok,reason=e:prepare(s);if ok~=nil then break end;e:advance(.5)end
 assert(ok==false and reason:find('松开',1,true),tostring(reason))
 assert(e.closes==0 and e.toggles==0 and not a.writes and not b.writes);e:close()
end)

test('held modifiers mid-transition pause the readiness budget without resetting retry bounds',function()
 local e,a,b=revealEnvironment();e.cgByID[a:id()]='missing';local requests=0
 function a:focus()requests=requests+1;e.focused=self;return self end
 local s={};for _=1,7 do assert(e:prepare(s)==nil);e:advance(.5)end
 e.flags={cmd=true};for _=1,12 do assert(e:prepare(s)==nil);e:advance(.5)end
 e.flags={};e.cgByID[a:id()]=nil
 local ok,reason=finishPreparation(e,s,3);assert(ok==true,tostring(reason))
 assert(requests<=3 and not a.writes and not b.writes);e:close()
end)

test('rejected mutation recording prevents fullscreen exit and focus',function()
 local e,a,b,items=revealEnvironment();a.fullscreen=true;items[1].before.fullscreen=true
 local exits,focuses=0,0
 function a:setFullScreen()exits=exits+1 end;function a:focus()focuses=focuses+1 end
 local ok=e:prepare({},function()return false end)
 assert(ok==false and exits==0 and focuses==0 and a.fullscreen);e:close()
end)

test('two native-fullscreen targets leave their Spaces sequentially with stable identities',function()
 local e,a,b,items=revealEnvironment();a.fullscreen=true;b.fullscreen=true
 items[1].before.fullscreen=true;items[2].before.fullscreen=true
 e.cgByID[a:id()]='missing';e.cgByID[b:id()]='missing'
 local calls,marks={},{}
 for _,w in ipairs({a,b})do
  function w:setFullScreen(value)
   assert(value==false);assert(marks[self:id()]==1)
   if self==b then assert(not a.fullscreen,'overlapping native transitions')end
   calls[self:id()]=(calls[self:id()] or 0)+1
   hs.timer.doAfter(.7,function()self.fullscreen=false;e.cgByID[self:id()]=nil end)
   return self
  end
 end
 local ok,reason=finishPreparation(e,{},12,function(item)marks[item.id]=(marks[item.id] or 0)+1 end)
 assert(ok==true,tostring(reason));assert(calls[a:id()]==1 and calls[b:id()]==1)
 assert(not a.writes and not b.writes and #e.mouseEvents==0);e:close()
end)

test('timeout diagnostics identify the exact app window and reason',function()
 local e,a,b=revealEnvironment();e.cgByID[b:id()]='missing'
 local s={};local ok,reason=finishPreparation(e,s,12)
 assert(ok==false and reason:find('MockApp',1,true),tostring(reason))
 assert(s.waiting.windowID==b:id() and s.waiting.pid==b:pid())
 assert(s.waiting.bundleID=='notion.id' and s.waiting.reason=='cg-window-missing')
 assert(e.wm.lastPresentationFailure.windowID==b:id());e:close()
end)



test('sequential native restores reveal the exact second window after the first hides its Space',function()
 local e,a,b=revealEnvironment();local first=e.wm.captureWorkspaceWindow(a);local second=e.wm.captureWorkspaceWindow(b)
 first.fullscreen=true;first.frame=a:screen():fullFrame();second.fullscreen=true;second.frame=b:screen():fullFrame()
 local bFocus=0
 function a:setFullScreen(value)
  self.fullscreen=value
  if value then self.frameValue=self:screen():fullFrame();e.cgByID[b:id()]='missing' end
  return self
 end
 function b:focus()
  bFocus=bFocus+1;e.focused=self;e.cgByID[self:id()]=nil;e.cgByID[a:id()]='missing';return self
 end
 local r1=callbackResult();assert(e.wm.restoreWorkspaceWindow(a,first,r1.callback));e:advance(2)
 assert(r1.count==1 and r1.ok,tostring(r1.detail));assert(a:isFullScreen() and e.cgByID[b:id()]=='missing')
 local r2=callbackResult();assert(e.wm.restoreWorkspaceWindow(b,second,r2.callback));e:advance(3)
 assert(r2.count==1 and r2.ok,tostring(r2.detail));assert(a:isFullScreen() and b:isFullScreen())
 assert(bFocus==1 and e.toggles==0 and #e.mouseEvents==0);e:close()
end)

test('native restore of a permanently missing compositor window times out without frame writes',function()
 local e,a,b=revealEnvironment();local snapshot=e.wm.captureWorkspaceWindow(b);snapshot.fullscreen=true
 local original=b:frame();e.cgByID[b:id()]='missing';local focuses,nativeCalls=0,0
 function b:focus()focuses=focuses+1;return self end
 function b:setFullScreen(value)nativeCalls=nativeCalls+1;self.fullscreen=value;return self end
 local r=callbackResult();assert(e.wm.restoreWorkspaceWindow(b,snapshot,r.callback));e:advance(10)
 assert(r.count==1 and not r.ok,tostring(r.detail));assert(focuses>=1 and focuses<=3)
 assert(not b.writes and nativeCalls==0 and #e.mouseEvents==0 and e.toggles==0)
 equalFrame(b:frame(),original);e:advance(3);assert(r.count==1 and not b.writes);e:close()
end)

test('ordinary snapshot restore remains strict when the compositor target is absent',function()
 local e,a,b=revealEnvironment();local snapshot=e.wm.captureWorkspaceWindow(b);assert(not snapshot.fullscreen)
 e.cgByID[b:id()]='missing';local focuses=0;function b:focus()focuses=focuses+1;return self end
 local r=callbackResult();assert(e.wm.restoreWorkspaceWindow(b,snapshot,r.callback));e:advance(.5)
 assert(r.count==1 and not r.ok and focuses==0 and not b.writes)
 assert(e.closes==0 and e.toggles==0);e:close()
end)

test('new placement remains strict when its compositor target is absent',function()
 local e,a,b=revealEnvironment();e.cgByID[b:id()]='missing';local focuses=0
 function b:focus()focuses=focuses+1;return self end
 local r=callbackResult();assert(e.wm.applyWorkspaceWindow(b,{screenUUID='screen-3'},r.callback));e:advance(.5)
 assert(r.count==1 and not r.ok and focuses==0 and not b.writes)
 assert(e.closes==0 and e.toggles==0);e:close()
end)

print(string.format('RESULT %d presentation checks passed, %d failed',passed,failed))
assert(failed==0,'presentation regressions')
