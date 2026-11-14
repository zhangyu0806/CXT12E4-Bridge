-- Pure Lua regression; no desktop interaction.
local mock = dofile("tests/support/mock_desktop.lua")
local environment, equalFrame = mock.environment, mock.equalFrame

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
