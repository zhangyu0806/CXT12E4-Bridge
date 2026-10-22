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
    eventtap={checkKeyboardModifiers=function()return e.flags end,checkMouseButtons=function()return e.buttons or{}end,
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
            e.posted=e.posted or{};e.posted[#e.posted+1]={kind=kind,position=copy(position),tag=self:getProperty(42),stamp=self:timestamp(),at=e.time}
            if options.deferOwnMove and kind=='move' then e.queuedMove=self
            elseif options.deferOwnDown and kind=='down' then e.queuedDown=self
            else for _,tap in ipairs(e.taps)do if tap.active and tap.kinds[kind]then tap.fn(self)end end end
            if kind=='move'then e.hoverStarted=e.time end
            if kind=='down'then e.dragStart=copy(position);e.resizeArmed=not options.requiredHoverTime or(e.hoverStarted and e.time-e.hoverStarted>=options.requiredHoverTime)end
            if kind=='drag'and e.focused and e.resizeArmed then
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
      function chooser:choices(items)self.items=items;return self end
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

test('S reaches claude while Z X C keep their existing private channels',function()
 local e=environment();local calls={}
 e.wm.workspaceLayouts={apply=function(name)calls[#calls+1]=name;return true end,cancel=function()end}
 assert(e.keys.s and e.keys.z and e.keys.f17 and e.keys.f18)
 e.keys.s();e.keys.z();e.keys.f17();e.keys.f18()
 assert(table.concat(calls,',')=='claude,focus,terminal,office');e:close()
end)
test('named workspaceClaude action also works without an initially focused window',function()
 local e=environment();local selected
 e.wm.workspaceLayouts={apply=function(name)selected=name;return true end,cancel=function()end}
 assert(e.wm.dispatch('workspaceClaude'));assert(selected=='claude');e:close()
end)
test('Space lists Claude and preserves the explicit single-window span action',function()
 local e=environment();local w=window(e);local selected={}
 e.wm.workspaceLayouts={apply=function(name)selected[#selected+1]=name;return true end,cancel=function()end,
 choices=function()return{{text='Codex + Claude',preset='claude'}}end}
 e.keys.space();local span,claude
 for _,item in ipairs(e.chooser.items)do
  if item.action=='span'then span=item end
  if item.preset=='claude'then claude=item end
 end
 assert(span and claude);assert(not span.subText:find('S ·',1,true),'menu still advertises old S binding')
 e.chooser.callback(span);e:advance(.7)
 assert(#selected==0 and w:frame().h>2000,'menu span was redirected to workspaceClaude')
 e.keys.space();e.chooser.callback(claude);assert(selected[1]=='claude');e:close()
end)
print(string.format('RESULT %d routing/menu checks passed, %d failed',passed,failed))
assert(failed==0)
