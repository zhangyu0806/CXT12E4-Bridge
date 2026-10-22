#!/usr/bin/python3
"""Local-only status and tmux adapter. Never writes Codex databases or rollout logs."""
import contextlib
import fcntl
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import sys
import time

ROOT = Path(os.environ.get('CXT_STATE_DIR', str(Path.home() / 'Library/Application Support/CXT12E4Bridge')))
CODEX = Path(os.environ.get('CODEX_HOME', str(Path.home() / '.codex')))
TMUX = os.environ.get('CXT_TMUX_PATH') or next((str(p) for p in [Path.home()/'.local/bin/tmux',Path('/opt/homebrew/bin/tmux'),Path('/usr/local/bin/tmux')] if p.exists()), 'tmux')
STATE_CODES = {'idle':1,'thinking':2,'busy':2,'running':2,'completed':3,'waiting':4,'permission':4,'error':5}
MARKER = re.compile(rb'"type"\s*:\s*"event_msg"\s*,\s*"payload"\s*:\s*\{\s*"type"\s*:\s*"(task_started|task_complete|turn_aborted)"')


def atomic_json(path, value):
    path.parent.mkdir(parents=True,exist_ok=True)
    tmp=path.with_name(path.name+f'.{os.getpid()}.tmp')
    tmp.write_text(json.dumps(value,ensure_ascii=False));tmp.replace(path)


def call_tmux(*args):
    p=subprocess.run([TMUX,*args],capture_output=True,text=True,timeout=2)
    if p.returncode:
        # An absent server means an empty inventory, never a successful mutation.
        if args and args[0] in ('list-panes','list-clients') and ('no server running' in p.stderr or ('error connecting to' in p.stderr and 'No such file or directory' in p.stderr)):
            return ''
        raise RuntimeError('tmux: '+p.stderr.strip()[:200])
    return p.stdout.strip()


def rollout_state(path):
    """Find a complete lifecycle boundary backwards, then interpret only that turn."""
    try:
        with open(path,'rb') as f:
            end=f.seek(0,2);start=end;buf=b'';match=None
            while start>0:
                size=min(65536,start);start-=size;f.seek(start);buf=f.read(size)+buf
                hits=list(MARKER.finditer(buf))
                if hits:match=hits[-1];break
            if match is None:return 1,''
            line_start=buf.rfind(b'\n',0,match.start())+1
            state=1;revision='';pending=set()
            for line in buf[line_start:].splitlines():
                try:obj=json.loads(line)
                except (ValueError,UnicodeDecodeError):continue
                p=obj.get('payload',{});kind=p.get('type')
                if obj.get('type')=='event_msg':
                    if kind=='task_started':state=2;pending.clear();revision=''
                    elif kind=='task_complete':state=3;pending.clear();revision=f'{path}:{start+match.start()}'
                    elif kind=='turn_aborted':
                        # User interruption ends the turn normally for lighting.
                        # Keep unknown/failed abort reasons visible as errors.
                        state=1 if p.get('reason')=='interrupted' else 5
                        pending.clear();revision=''
                elif obj.get('type')=='response_item' and state in (2,4):
                    name=p.get('name','').split('.')[-1]
                    if kind in ('function_call','custom_tool_call') and name in ('request_user_input','request_plugin_install'):
                        pending.add(p.get('call_id'))
                    elif kind in ('function_call_output','custom_tool_call_output'):
                        pending.discard(p.get('call_id'))
            return (4 if pending else state),revision
    except OSError:return 1,''


def catalog():
    databases=sorted(CODEX.glob('state_*.sqlite'),key=lambda p:int(re.search(r'state_(\d+)',p.name)[1]),reverse=True)
    if not databases:return []
    with sqlite3.connect(databases[0].as_uri()+'?mode=ro',uri=True,timeout=1) as db:
        db.row_factory=sqlite3.Row
        cols={r[1] for r in db.execute('PRAGMA table_info(threads)')}
        order='recency_at_ms' if 'recency_at_ms' in cols else 'updated_at'
        clause=" AND (thread_source IS NULL OR thread_source <> 'subagent')" if 'thread_source' in cols else ''
        rows=db.execute(f"SELECT id,title,rollout_path FROM threads WHERE archived=0{clause} ORDER BY {order} DESC, id DESC LIMIT 6")
        return [dict(r) for r in rows]


def panes():
    fields=['session_name','session_id','session_created','pid','pane_id','window_id','pane_current_command','pane_left','pane_top','pane_width','pane_height','window_layout']
    result=[]
    for line in call_tmux('list-panes','-a','-F','\t'.join('#{'+f+'}' for f in fields)).splitlines():
        vals=line.split('\t')
        if len(vals)==len(fields):result.append(dict(zip(fields,vals)))
    return result


def clients():
    fields=['client_name','client_tty','client_activity','session_name','session_id','window_id','pane_id','client_pid']
    result=[]
    for line in call_tmux('list-clients','-F','\t'.join('#{'+f+'}' for f in fields)).splitlines():
        vals=line.split('\t')
        if len(vals)==len(fields):result.append(dict(zip(fields,vals)))
    return sorted(result,key=lambda r:int(r['client_activity'] or 0),reverse=True)


def session_identity(session):
    return ':'.join(session[k] for k in ['session_id','session_created','pid'])


def same_pane_generation(record, pane):
    # Session names can change during automatic renumbering. Keep the server,
    # session generation and pane checks so a stale file cannot light a new pane.
    return all(str(record.get(k))==pane[k] for k in ['session_id','session_created','pid','pane_id'])


def session_slots(inventory, cache=None):
    cache={} if cache is None else cache
    unique={session_identity(p):p for p in inventory}
    assigned=[None]*6;reserved=set();automatic={}
    numbered={i:[] for i in range(6)}
    for identity,p in unique.items():
        match=re.match(r'^0([1-6])(?:\D|$)',p['session_name'])
        if match:numbered[int(match[1])-1].append(p)
        else:automatic[identity]=p
    for i,sessions in numbered.items():
        if sessions:reserved.add(i)
        if len(sessions)==1:assigned[i]=sessions[0]
    # Retain live automatic assignments when other sessions disappear or rename.
    # Explicit 01..06 slots take precedence; duplicates reserve an empty slot.
    previous=cache.get('terminalSlots',{});retained={}
    ordered=sorted(automatic.items(),key=lambda item:(int(item[1]['session_created']),int(item[1]['session_id'][1:]),item[0]))
    for identity,p in ordered:
        i=previous.get(identity)
        if type(i) is int and 0<=i<6 and i not in reserved and assigned[i] is None:
            assigned[i]=p;retained[identity]=i
    for identity,p in ordered:
        if identity in retained:continue
        free=next((i for i in range(6) if i not in reserved and assigned[i] is None),None)
        if free is None:break
        assigned[free]=p;retained[identity]=free
    cache['terminalSlots']=retained
    result=[]
    for s in assigned:
        if s is None:result.append(None);continue
        states=[];revision=0
        for p in inventory:
            if session_identity(p)!=session_identity(s):continue
            file=ROOT/'terminal-status'/f"pane-{p['pane_id'][1:]}.json"
            try:r=json.loads(file.read_text())
            except (OSError,ValueError):continue
            if not same_pane_generation(r,p):continue
            states.append(STATE_CODES.get(r.get('state'),1))
            if r.get('state')=='completed':revision=max(revision,int(r.get('revision',0)))
        state=next((v for v in [5,4,2,3,1] if v in states),1)
        result.append({'id':session_identity(s),'name':s['session_name'],'state':state,'revision':revision})
    return result


def status(cache):
    rows=catalog();tasks=[]
    seen=cache.setdefault('taskSeen',{});acked=cache.setdefault('taskAck',{})
    for r in rows:
        state,revision=rollout_state(r.pop('rollout_path'))
        tid=r['id']
        if tid not in seen and state==3:acked[tid]=revision # historical completions start white
        seen[tid]=True
        if state==3 and acked.get(tid)==revision:state=1
        tasks.append({**r,'state':state,'revision':revision})
    try:
        terminals=session_slots(panes(),cache);cache['lastTerminals']=terminals;cache['tmuxError']=''
    except (OSError,RuntimeError,subprocess.TimeoutExpired) as e:
        terminals=cache.get('lastTerminals',[None]*6);cache['tmuxError']=str(e)
    seen_t=cache.setdefault('terminalSeen',{});ack_t=cache.setdefault('terminalAck',{})
    for t in terminals:
        if t is None:continue
        if t['id'] not in seen_t:ack_t[t['id']]=time.time_ns()
        seen_t[t['id']]=True
        if t['state']==3 and int(t['revision'])<=int(ack_t.get(t['id'],0)):t['state']=1
    cache['tasks']=tasks
    return {'tasks':tasks,'terminals':terminals,'tmuxError':cache.get('tmuxError','')}


def active_client(tty=None, allow_recent=False):
    rows=clients()
    count=len(rows)
    if tty:
        rows=[r for r in rows if r['client_tty']==tty]
    if allow_recent and not tty and rows:return {**rows[0],'_attached_count':count}
    # Never guess between multiple attached clients with indistinguishable focus.
    if len(rows)!=1:raise RuntimeError('当前终端无法唯一对应 tmux client；请只保留一个客户端，或设置 CXT_TMUX_TTY')
    return {**rows[0],'_attached_count':count}


def tmux_action(action, cache, tty=None, allow_recent=False):
    tty=tty or os.environ.get('CXT_TMUX_TTY')
    if action.startswith(('session:','session-id:')):
        inventory=panes()
        if action.startswith('session-id:'):
            wanted=action.split(':',1)[1]
            # The chooser uses full generation IDs; bare IDs remain a CLI convenience.
            if not re.fullmatch(r'\$\d+(?::\d+:\d+)?',wanted):raise RuntimeError('无效的 session 身份')
            p=next((p for p in inventory if (session_identity(p) if ':' in wanted else p['session_id'])==wanted),None)
            target={'id':session_identity(p),'name':p['session_name']} if p else None
        else:
            try:slot=int(action.split(':',1)[1])
            except ValueError:raise RuntimeError('无效的 session 灯位') from None
            ss=session_slots(inventory,cache)
            target=ss[slot-1] if 1<=slot<=6 else None
        if target is None:raise RuntimeError('对应 session 已退出或灯位编号重复')
        c=active_client(tty,allow_recent=allow_recent)
        # Names can change during renumbering. The live session ID is unambiguous.
        session=target['id'].split(':',1)[0]
        cutoff=time.time_ns()
        call_tmux('switch-client','-c',c['client_name'],'-t',session)
        cache.setdefault('terminalAck',{})[target['id']]=cutoff
        return {'ok':True,'focusTerminal':True,'clientPID':c.get('client_pid'),'clientTTY':c.get('client_tty'),'attachedClients':c['_attached_count']}
    c=active_client(tty)
    if action in ('previous-session','next-session'):
        call_tmux('switch-client','-c',c['client_name'],'-p' if action=='previous-session' else '-n')
        return {'ok':True}
    pane=c['pane_id'];window=c['window_id'];session=c['session_id']
    inventory=[p for p in panes() if p['window_id']==window]
    current=next((p for p in inventory if p['pane_id']==pane),None)
    if current is None:raise RuntimeError('当前 pane 已变更')
    commands={
        'split-right':['split-window','-h','-t',pane,'-c','#{pane_current_path}'],
        'split-down':['split-window','-v','-t',pane,'-c','#{pane_current_path}'],
        'zoom':['resize-pane','-Z','-t',pane], 'rotate':['rotate-window','-t',window],
        'windows':['choose-tree','-w','-t',pane], 'copy':['copy-mode','-t',pane],
        'last-pane':['last-pane','-t',window],
        'width':['select-layout','-t',window,'even-horizontal'],
        'height':['select-layout','-t',window,'even-vertical'],
        'previous-window':['previous-window','-t',session], 'next-window':['next-window','-t',session],
        'last-window':['last-window','-t',session],
    }
    for name,direction in [('left','L'),('right','R'),('up','U'),('down','D')]:
        commands['resize-'+name]=['resize-pane','-t',pane,'-'+direction,'5']
        commands['pane-'+name]=['select-pane','-t',pane,'-'+direction]
    if action=='horizontal':
        direction='R' if any(int(p['pane_left'])>int(current['pane_left']) for p in inventory) else 'L'
        cmd=['select-pane','-t',pane,'-'+direction]
    elif action=='vertical':
        direction='D' if any(int(p['pane_top'])>int(current['pane_top']) for p in inventory) else 'U'
        cmd=['select-pane','-t',pane,'-'+direction]
    elif action=='layout':
        if len(inventory)<2:raise RuntimeError('只有一个 pane，无法切换均分布局')
        horizontal=len({p['pane_top'] for p in inventory})==1
        cmd=['select-layout','-t',window,'even-vertical' if horizontal else 'even-horizontal']
    elif action=='stash':
        if Path(current['pane_current_command']).name not in ('opencode','claude','grok'):raise RuntimeError('当前 pane 不是支持暂存输入的 Agent')
        cmd=['send-keys','-t',pane,'C-s']
    elif action=='bottom':
        if call_tmux('display-message','-p','-t',pane,'#{pane_in_mode}')=='1':
            cmd=['send-keys','-X','-t',pane,'cancel']
        else:return {'ok':True}
    else:cmd=commands.get(action)
    if not cmd:raise RuntimeError('未知 tmux 操作')
    call_tmux(*cmd)
    return {'ok':True}


def report_agent(state,pane):
    if not re.fullmatch(r'%\d+',pane or ''):return
    ROOT.mkdir(parents=True,exist_ok=True)
    with (ROOT/'agent.lock').open('a') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        inventory=panes();meta=next((p for p in inventory if p['pane_id']==pane),None)
        if meta is None:return
        path=ROOT/'terminal-status'/f'pane-{pane[1:]}.json'
        if state in ('off','delete'):
            path.unlink(missing_ok=True);return
        try:previous=json.loads(path.read_text())
        except (OSError,ValueError):previous={}
        old=previous.get('state') if same_pane_generation(previous,meta) else None
        if state=='idle-if-active':
            if old not in ('thinking','waiting','permission'):return
            state='idle'
        if state=='thinking-if-waiting':
            if old not in ('waiting','permission'):return
            state='thinking'
        if state not in STATE_CODES:raise ValueError('Invalid agent state')
        atomic_json(path,{**{k:meta[k] for k in ['session_name','session_id','session_created','pid','pane_id']},'state':state,'revision':time.time_ns()})


def main():
    if len(sys.argv)>1 and sys.argv[1]=='agent':
        report_agent(sys.argv[2],os.environ.get('TMUX_PANE',''));return
    ROOT.mkdir(parents=True,exist_ok=True)
    # Swift starts a fresh helper for each bounded operation. Serialize cache updates.
    with (ROOT/'helper.lock').open('a') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        cachepath=ROOT/'status-cache.json'
        try:cache=json.loads(cachepath.read_text())
        except (OSError,ValueError):cache={}
        command=sys.argv[1] if len(sys.argv)>1 else 'status'
        if command=='status':result=status(cache)
        elif command=='reset':cache={'terminalSlots':cache.get('terminalSlots',{})};result={'ok':True}
        elif command=='ack-task':
            cache.setdefault('taskAck',{})[sys.argv[2]]=sys.argv[3];result={'ok':True}
        elif command=='tmux':result=tmux_action(sys.argv[2],cache,allow_recent='--allow-recent' in sys.argv[3:])
        else:raise ValueError('Unknown command')
        atomic_json(cachepath,cache);print(json.dumps(result,ensure_ascii=False))

if __name__=='__main__':
    try:main()
    except Exception as e:
        print(json.dumps({'error':str(e)},ensure_ascii=False));sys.exit(1)
