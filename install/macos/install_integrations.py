#!/usr/bin/python3
"""Merge macOS integrations with timestamped backups; preserve unrelated settings."""
import datetime
import json
import os
from pathlib import Path
import plistlib
import shutil
import sys

HOME_DIR=Path.home()
ROOT=Path(__file__).resolve().parents[2]
SUPPORT=HOME_DIR/'Library/Application Support/CXT12E4Bridge'
BACKUP=SUPPORT/'backups'/datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
CHANGES=[]

def save(path,data):
    if path.exists():
        if path.read_bytes()==data:return
        backup=BACKUP/path.relative_to(HOME_DIR);backup.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(path,backup)
    path.parent.mkdir(parents=True,exist_ok=True)
    tmp=path.with_name(path.name+'.cxt-tmp');tmp.write_bytes(data);tmp.replace(path)
    CHANGES.append(str(path))

def save_json(path,data):save(path,(json.dumps(data,ensure_ascii=False,indent=2)+'\n').encode())

def remove_cxt_hooks(hooks):
    # Refresh our commands on upgrades without dropping other commands that
    # happen to share a hook group with them.
    for event, groups in list(hooks.items()):
        if not isinstance(groups, list):continue
        cleaned=[]
        for group in groups:
            if not isinstance(group, dict) or not isinstance(group.get('hooks'), list):
                cleaned.append(group);continue
            updated=dict(group)
            updated['hooks']=[hook for hook in group['hooks']
                              if not (isinstance(hook,dict) and 'cxt12e4-' in str(hook.get('command','')))]
            if updated['hooks']:cleaned.append(updated)
        hooks[event]=cleaned

# Scope every private trigger to this board; preserve the user's other device rules.
kpath=HOME_DIR/'.config/karabiner/karabiner.json'
if not kpath.exists():raise RuntimeError('Install and enable Karabiner-Elements before installing the Bridge')
k=json.loads(kpath.read_text())
endpoint=str(SUPPORT/'input.sock')
def message(key,down):return {'send_user_command':{'endpoint':endpoint,'payload':{'cxt':1,'key':key,'down':down}}}
condition={'type':'device_if','identifiers':[{'vendor_id':0x5754,'product_id':0xc401}]}
manipulators=[]
for key in range(13):
    code='right_control' if key==0 else 'f'+str(key+12)
    manipulators.append({'type':'basic','from':{'key_code':code,'modifiers':{'optional':['any']}},
                         'conditions':[condition],'to':[message(key,True)],'to_after_key_up':[message(key,False)]})
rule={'description':'CXT12E4 Bridge: private input to macOS receiver','manipulators':manipulators}
for profile in k.get('profiles',[]):
    if not profile.get('selected'):continue
    devices=profile.setdefault('devices',[])
    # Both the boot keyboard and composite mouse/keyboard collection are reported by Karabiner.
    devices[:]=[d for d in devices if not (d.get('identifiers',{}).get('vendor_id')==0x5754 and d.get('identifiers',{}).get('product_id')==0xc401)]
    for keyboard,pointing in [(True,False),(True,True),(False,True)]:
        devices.append({'identifiers':{'vendor_id':0x5754,'product_id':0xc401,'is_keyboard':keyboard,'is_pointing_device':pointing},'ignore':False})
    rules=profile.setdefault('complex_modifications',{}).setdefault('rules',[])
    rules[:]=[r for r in rules if not r.get('description','').startswith('CXT12E4 Bridge:')]
    rules.insert(0,rule)
save_json(kpath,k)
save_json(HOME_DIR/'.config/karabiner/assets/complex_modifications/cxt12e4-bridge.json',{'title':'CXT12E4 Bridge macOS input','rules':[rule]})

# Install the portable reporter without GNU date, flock, or a WSL-mounted path.
helper=SUPPORT/'bridge_helper.py';save(helper,(ROOT/'src/Cxt12E4Mac/bridge_helper.py').read_bytes())
reporter=HOME_DIR/'.local/bin/cxt12e4-agent-status'
save(reporter,b'#!/bin/sh\nexec /usr/bin/python3 "$HOME/Library/Application Support/CXT12E4Bridge/bridge_helper.py" agent "${1:-idle}"\n');reporter.chmod(0o755)
idle=HOME_DIR/'.local/bin/cxt12e4-claude-idle'
save(idle,b'#!/bin/sh\nexec "$HOME/.local/bin/cxt12e4-agent-status" idle-if-active\n');idle.chmod(0o755)

claude=HOME_DIR/'.claude/settings.json'
if claude.parent.exists():
    settings=json.loads(claude.read_text()) if claude.exists() else {}
    template=json.loads((ROOT/'integrations/terminal-status/claude-settings.json').read_text())
    remove_cxt_hooks(settings.setdefault('hooks',{}))
    for event,groups in template['hooks'].items():
        existing=settings.setdefault('hooks',{}).setdefault(event,[])
        existing.extend(groups)
    # Permission approval resumes blue on the next tool event.
    for event in ['PreToolUse','PostToolUse']:
        groups=settings['hooks'].setdefault(event,[])
        if not any('cxt12e4-' in str(g) for g in groups):groups.append({'hooks':[{'type':'command','command':'"$HOME/.local/bin/cxt12e4-agent-status" thinking-if-waiting'}]})
    save_json(claude,settings)

opencode=HOME_DIR/'.config/opencode'
if opencode.exists():
    save(opencode/'plugins/cxt12e4-terminal-lights.js',(ROOT/'integrations/terminal-status/cxt12e4-terminal-lights.js').read_bytes())
    tui=opencode/'tui.json'
    if tui.exists():
        data=json.loads(tui.read_text());data.setdefault('keybinds',{})['prompt_stash']='ctrl+s';save_json(tui,data)
    elif (opencode/'tui.jsonc').exists():
        print('OpenCode tui.jsonc preserved; set prompt_stash=ctrl+s in that file')
    else:save_json(tui,{'keybinds':{'prompt_stash':'ctrl+s'}})

grok=HOME_DIR/'.grok/hooks'
if grok.parent.exists():
    hooks=json.loads((ROOT/'integrations/terminal-status/grok-hooks.json').read_text())
    save_json(grok/'cxt12e4-macos.json',hooks)

# The same app bundle owns TCC permissions at every login. Only crash exits restart.
launch=HOME_DIR/'Library/LaunchAgents/com.zhangyu.cxt12e4.bridge.plist'
plist={'Label':'com.zhangyu.cxt12e4.bridge','ProgramArguments':['/Applications/CXT12E4 Bridge.app/Contents/MacOS/CXT12E4Bridge'],
       'RunAtLoad':True,'KeepAlive':{'SuccessfulExit':False},'ThrottleInterval':5,'ProcessType':'Interactive',
       'StandardOutPath':str(SUPPORT/'launch.log'),'StandardErrorPath':str(SUPPORT/'launch.error.log')}
save(launch,plistlib.dumps(plist))
SUPPORT.mkdir(parents=True,exist_ok=True)
(SUPPORT/'install-record.json').write_text(json.dumps({'backup':str(BACKUP),'changed':CHANGES},ensure_ascii=False,indent=2))
print('Backups:',BACKUP)
print('Configured',len(CHANGES),'files')
