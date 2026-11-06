-- Isolated navigation behavior: numbering order is deliberately different from
-- physical left/right adjacency. No live Hammerspoon, windows or keyboard input.
local source=os.getenv('SCREEN_NAVIGATION_TEST_SOURCE') or 'screen_navigation.lua'
local function copy(value) local out={} for k,v in pairs(value) do out[k]=v end return out end
local function sameFrame(actual,expected)
  for _,key in ipairs({'x','y','w','h'}) do
    assert(math.abs(actual[key]-expected[key])<.001,key..': '..tostring(actual[key])..' ~= '..tostring(expected[key]))
  end
end
local function environment(options)
  options=options or {}
  local e={screens={},order=options.order or {1,2,3,4},online=options.online or {1,2,3,4},
    current=options.current or 1,calls={},writes={},alerts={},ended=0,cancelled=0,keys={},time=100}
  local frames={{x=0,y=30,w=1920,h=1050},{x=0,y=1080,w=1920,h=1013},
    {x=1920,y=240,w=1080,h=1920},{x=3000,y=542,w=2330,h=1618}}
  for i,bounds in ipairs(frames) do
    local s={number=i,bounds=copy(bounds)}
    function s:getUUID() return 'screen-'..self.number end
    function s:name() return 'display-'..self.number end
    function s:id() return self.number end
    function s:frame() return copy(self.bounds) end
    function s:fullFrame() return copy(self.bounds) end
    function s:toEast() e.geometric=(e.geometric or 0)+1;return e.screens[3] end
    function s:toWest() e.geometric=(e.geometric or 0)+1;return nil end
    function s:toNorth() e.geometric=(e.geometric or 0)+1;return self.number==2 and e.screens[1] or nil end
    function s:toSouth() e.geometric=(e.geometric or 0)+1;return self.number==1 and e.screens[2] or nil end
    e.screens[i]=s
  end
  local win={frameValue=copy(options.frame or {x=560,y=255,w=800,h=600})}
  function win:application()
    if options.noApplication then return nil end
    return {bundleID=function()return 'test.app'end}
  end
  function win:isStandard() return not options.nonstandard end
  function win:isFullScreen() return options.fullscreen or false end
  function win:screen() return e.screens[e.current] end
  function win:frame() return copy(self.frameValue) end
  function win:setSize(size) e.writes[#e.writes+1]='size';self.frameValue.w=size.w;self.frameValue.h=size.h end
  function win:setFrame(value) e.writes[#e.writes+1]='frame';self.frameValue=copy(value) end
  function win:focus() e.focused=self;e.focusCount=(e.focusCount or 0)+1 end
  e.win,e.focused=win,win
  local manager={}
  function manager.status()
    local rows={};for index,id in ipairs(e.order) do rows[#rows+1]={uuid='screen-'..id,index=index} end
    return {screens=rows}
  end
  function manager.usableFrame(screen) return copy(screen.bounds) end
  function manager.endFullSpanMode(w) assert(w==win);e.ended=e.ended+1 end
  function manager.dispatch(action,w)
    e.calls[#e.calls+1]={action=action,win=w}
    if options.dispatchFailure then return false,'mock dispatch failure' end
    local index=tonumber(action:match('^screen(%d+)$'));assert(index,'must use numbered-screen dispatch')
    e.history=e.history or copy(w:frame())
    e.current=assert(e.order[index])
    -- Model manager placement independently; the navigation layer must apply
    -- its size-preserving target only after the dispatch succeeds.
    win.frameValue=copy(e.screens[e.current].bounds)
    return true
  end
  function e:pending()
    manager._pendingSpan={controller={cancel=function()
      self.cancelled=self.cancelled+1;manager._pendingSpan=nil
    end}}
  end
  hs={accessibilityState=function()return not options.noPermission end,
    alert={show=function(message)e.alerts[#e.alerts+1]=message end},printf=function()end,
    timer={secondsSinceEpoch=function()return e.time end},
    window={focusedWindow=function()return e.focused end},
    screen={allScreens=function()
      local result={};for _,id in ipairs(e.online) do result[#result+1]=e.screens[id] end;return result
    end},
    hotkey={bind=function(_,key,callback)
      local h={callback=callback,deleted=false};function h:delete()self.deleted=true end
      e.keys[key]=h;return h
    end}}
  e.manager=manager;e.nav=assert(loadfile(source))();e.nav.start(manager)
  function e:close() self.nav.stop() end
  return e
end
local passed,failed=0,0
local function test(name,fn)
  local ok,reason=pcall(fn)
  if ok then passed=passed+1;print('PASS '..name)
  else failed=failed+1;print('FAIL '..name..': '..tostring(reason)) end
end
local function move(e,direction,id)
  local ok,reason=e.nav.move(direction,e.win);assert(ok,tostring(reason))
  assert(e.current==id,'expected display '..id..', got '..e.current)
end
local function unchanged(e,frame)
  assert(e.ended==0 and e.cancelled==0 and #e.calls==0 and #e.writes==0,'rejected request changed layout state')
  sameFrame(e.win:frame(),frame)
end

test('right traverses all four numbered displays and wraps, despite vertical first pair',function()
  local e=environment()
  for _,id in ipairs({2,3,4,1}) do move(e,'right',id) end
  assert(not e.geometric,'horizontal navigation used physical adjacency');e:close()
end)
test('left traverses all four numbered displays in reverse and wraps',function()
  local e=environment()
  for _,id in ipairs({4,3,2,1}) do move(e,'left',id) end
  assert(not e.geometric);e:close()
end)
test('two stacked displays alternate with either horizontal arrow',function()
  local e=environment({order={1,2},online={1,2}})
  move(e,'right',2);move(e,'right',1);move(e,'left',2);move(e,'left',1);e:close()
end)
test('one display leaves the window and active span untouched',function()
  local e=environment({order={1},online={1}});e:pending();local before=e.win:frame()
  assert(not e.nav.move('right',e.win));unchanged(e,before)
  assert(not e.nav.move('left',e.win));unchanged(e,before);e:close()
end)
test('a removed display is skipped on the very next request',function()
  local e=environment();move(e,'right',2)
  e.order={1,2,4};e.online={1,2,4}
  move(e,'right',4);assert(e.calls[#e.calls].action=='screen3');move(e,'right',1);e:close()
end)
test('changed numbering is read on every request',function()
  local e=environment();move(e,'right',2)
  e.order={2,4,1,3}
  move(e,'right',4);move(e,'left',2);move(e,'left',3);e:close()
end)
test('ordinary windows retain pixel dimensions and relative position',function()
  local e=environment();local original=e.win:frame();move(e,'right',2)
  sameFrame(e.win:frame(),{x=560,y=1286.5,w=800,h=600})
  assert(e.calls[1].action=='screen2' and e.calls[1].win==e.win)
  sameFrame(e.history,original);assert(e.ended==1 and #e.writes==2);e:close()
end)
test('an ordinary oversized window is clamped to the destination usable bounds',function()
  local e=environment({current=2,frame={x=100,y=1200,w=1500,h=900}});move(e,'right',3)
  local f=e.win:frame();assert(f.w==1080 and f.h==900 and f.x==1920)
  assert(f.y>=240 and f.y+f.h<=2160);e:close()
end)
test('a maximized window stays maximized on each destination',function()
  local e=environment({frame={x=0,y=30,w=1920,h=1050}})
  move(e,'right',2);sameFrame(e.win:frame(),e.screens[2]:frame())
  move(e,'right',3);sameFrame(e.win:frame(),e.screens[3]:frame());e:close()
end)
test('up and down still use physical adjacency',function()
  local e=environment();move(e,'down',2);move(e,'up',1)
  assert(e.geometric==2);e:close()
end)
test('no physical neighbor does not cancel an active span or destroy history',function()
  local e=environment();e:pending();local before=e.win:frame()
  assert(not e.nav.move('up',e.win));unchanged(e,before);e:close()
end)
test('a source absent from current numbering is rejected before mutation',function()
  local e=environment({order={2,3,4}});e:pending();local before=e.win:frame()
  assert(not e.nav.move('right',e.win));unchanged(e,before);e:close()
end)
test('a destination which disconnected before resolution is rejected before mutation',function()
  local e=environment({online={1,3,4}});e:pending();local before=e.win:frame()
  assert(not e.nav.move('right',e.win));unchanged(e,before);e:close()
end)
test('native fullscreen is rejected before any move or span cancellation',function()
  local e=environment({fullscreen=true});e:pending();local before=e.win:frame()
  assert(not e.nav.move('right',e.win));unchanged(e,before);e:close()
end)
test('a failed numbered move never adds size, frame or focus writes',function()
  local e=environment({dispatchFailure=true});local ok,reason=e.nav.move('right',e.win)
  assert(not ok and reason=='mock dispatch failure')
  assert(#e.calls==1 and e.calls[1].action=='screen2' and #e.writes==0 and not e.focusCount);e:close()
end)
test('a valid move cancels pending drag once and reuses the numbered restore path',function()
  local e=environment();e:pending();move(e,'right',2)
  assert(e.cancelled==1 and e.ended==1 and #e.calls==1)
  assert(e.calls[1].action=='screen2' and e.history);e:close()
end)
test('H and L bindings move the focused window to previous and next displays',function()
  local e=environment();assert(e.keys.h and e.keys.j and e.keys.k and e.keys.l)
  e.keys.l.callback();assert(e.current==2)
  e.keys.h.callback();assert(e.current==1)
  local old=e.keys.l;e.nav.start(e.manager);assert(old.deleted)
  e:close();assert(e.keys.l.deleted and #e.nav._hotkeys==0)
end)
test('permission and nonstandard-window failures have no layout side effects',function()
  for _,options in ipairs({{noPermission=true},{nonstandard=true},{noApplication=true}}) do
    local e=environment(options);e:pending();local before=e.win:frame()
    assert(not e.nav.move('right',e.win));unchanged(e,before);e:close()
  end
end)


test('optional init binds Hyper+L exactly once and preserves the F19 portrait entry',function()
  local file=assert(io.open('init.example.lua','r'));local original=file:read('*a');file:close()
  for _,enabled in ipairs({false,true}) do
    local text=original
    if enabled then
      local count;text,count=text:gsub('local enableScreenNavigation = false','local enableScreenNavigation = true',1)
      assert(count==1,'documented enable switch missing')
    end
    local bindings,all,stopped,previous={}, {}, {},0
    local h={configdir='/mock',json={decode=function()return {roles={leftTop='mock-screen'}}end},
      fs={attributes=function()return 'file'end},hotkey={}}
    function h.hotkey.bind(_,key,callback)
      assert(not bindings[key],'duplicate active hotkey '..key)
      local hotkey={callback=callback,deleted=false}
      function hotkey:delete()self.deleted=true;bindings[key]=nil end
      bindings[key]=hotkey;all[#all+1]=hotkey;return hotkey
    end
    h.shutdownCallback=function()previous=previous+1 end
    local manager,layouts={},{}
    function manager.start() manager.portrait=h.hotkey.bind({},'f19',function()end) end
    function manager.stop()stopped.manager=true;manager.portrait:delete()end
    function manager.dispatch()end
    function layouts.start(m)assert(m==manager);return true end
    function layouts.stop()stopped.layouts=true end
    local env=setmetatable({hs=h,io={open=function()
      return {read=function()return '{}'end,close=function()end}
    end}},{__index=_G})
    local navigation,loaded
    env.require=function(name)
      if name=='window_manager' then return manager end
      if name=='workspace_layouts' then return layouts end
      assert(name=='screen_navigation',name)
      loaded=true;navigation=assert(loadfile(source,'t',env))();return navigation
    end
    assert(load(text,'@init.example.lua','t',env))()
    assert(bindings.l and bindings.f19 and bindings.x and bindings.c)
    if enabled then assert(loaded and #navigation._hotkeys==4 and bindings.h and bindings.j and bindings.k)
    else assert(not loaded and not bindings.h and not bindings.j and not bindings.k) end
    h.shutdownCallback()
    assert(stopped.manager and stopped.layouts and previous==1 and next(bindings)==nil)
    for _,hotkey in ipairs(all) do assert(hotkey.deleted) end
  end
end)

print(string.format('%d screen navigation checks passed, %d failed',passed,failed))
assert(failed==0,'screen navigation regression failure')
