#!/usr/bin/python3
"""Local-only status and tmux adapter. Never writes Codex databases or rollout logs."""
import contextlib
from datetime import datetime
import fcntl
from functools import lru_cache
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


def reverse_lines(f):
    """Yield complete JSONL records with byte offsets, without loading a whole log."""
    position=f.seek(0,2);carry=b''
    while position:
        size=min(65536,position);position-=size;f.seek(position)
        data=f.read(size)+carry;parts=data.split(b'\n')
        carry=parts.pop(0) if position else b''
        offset=position+len(data)
        for line in reversed(parts):
            offset-=len(line)
            if line:yield offset,line
            offset-=1


@lru_cache(maxsize=1)
def system_boot_time():
    try:
        output=subprocess.run(['/usr/sbin/sysctl','-n','kern.boottime'],capture_output=True,text=True,timeout=1,check=True).stdout
        match=re.search(r'\bsec\s*=\s*(\d+)',output)
        return int(match[1]) if match else None
    except (OSError,subprocess.SubprocessError):return None


def event_time(value):
    if not isinstance(value,str):return None
    try:return datetime.fromisoformat(value.replace('Z','+00:00')).timestamp() if value.endswith('Z') or re.search(r'[+-]\d\d:\d\d$',value) else None
    except (ValueError,OverflowError):return None


def has_error(value):
    return bool(value.strip()) if isinstance(value,str) else bool(value)


def turn_id(obj):
    return next((obj[k] for k in ('turn_id','turnId') if isinstance(obj.get(k),str) and obj[k]),'')


def json_value(value):
    if not isinstance(value,str):return value
    try:return json.loads(value)
    except ValueError:return None


def question_replies(content):
    """Only a structured user answer can resolve an asynchronous question."""
    texts=[content] if isinstance(content,str) else [c.get('text','') for c in content if isinstance(c,dict) and c.get('type') in ('text','input_text')] if isinstance(content,list) else []
    replies=[]
    for text in texts:
        if not isinstance(text,str):continue
        for match in re.finditer(r'<send_user_message_question_reply>\s*(.*?)\s*</send_user_message_question_reply>',text,re.S):
            answers=json_value(match[1])
            if not isinstance(answers,list):continue
            for answer in answers:
                identity=json_value(answer.get('questionItemId')) if isinstance(answer,dict) else None
                if isinstance(identity,list) and len(identity)==3 and identity[0]=='request_user_input_async' and isinstance(identity[1],str) and type(identity[2]) is int:
                    replies.append((identity[1],identity[2]))
    return replies


def rollout_snapshot(path,boot_time=None,ignored_questions=(),include_questions=False):
    """Interpret structured events from the latest turn; item completion is not turn completion."""
    result={'state':1,'revision':'','turnId':'','terminal':False,'legacyRevisions':[],'questions':[]}
    try:
        records=[];local=False;started=None;activity=None;unknown_time=False
        with open(path,'rb') as f:
            try:
                meta=json.loads(f.readline())
                local=meta.get('type')=='session_meta' and meta.get('payload',{}).get('source') in ('cli','vscode','exec')
            except (ValueError,UnicodeDecodeError,AttributeError):pass
            for offset,line in reverse_lines(f):
                # This is only a cheap prefilter. JSON structure determines semantics.
                if not any(token in line for token in (b'event_msg',b'response_item')):continue
                try:obj=json.loads(line)
                except (ValueError,UnicodeDecodeError):continue
                if not isinstance(obj,dict):continue
                p=obj.get('payload',{})
                if not isinstance(p,dict):continue
                # Retain question titles only for the explicit question manager.
                # Never retain tool output, user prompts or answer text.
                item=p.get('item',{})
                payload={k:p[k] for k in ('type','reason','role','phase','channel','name','call_id','delivery') if k in p}
                payload['turn_id']=turn_id(p) or turn_id(obj)
                payload['error']=has_error(p.get('error'))
                payload['has_questions']=bool(p.get('questions'))
                if isinstance(item,dict):payload['item']={**{k:item[k] for k in ('type','phase','delivery') if k in item},'has_questions':bool(item.get('questions'))}
                if str(p.get('name','')).split('.')[-1]=='request_user_input_async':
                    arguments=json_value(p.get('arguments',p.get('input')))
                    questions=arguments.get('questions') if isinstance(arguments,dict) else None
                    payload['question_count']=len(questions) if isinstance(questions,list) and questions else 1
                    if include_questions:
                        payload['question_titles']=[q.get('title','') if isinstance(q,dict) else '' for q in questions] if isinstance(questions,list) else []
                if p.get('type') in ('function_call_output','custom_tool_call_output'):
                    output=json_value(p.get('output'))
                    payload['question_rejected']=isinstance(output,dict) and (output.get('accepted') is False or output.get('isError') is True or has_error(output.get('error')))
                if obj.get('type')=='response_item' and p.get('type')=='message' and p.get('role')=='user':
                    payload['question_replies']=question_replies(p.get('content'))
                elif obj.get('type')=='event_msg' and p.get('type')=='item_completed' and isinstance(item,dict) and item.get('type')=='UserMessage':
                    payload['question_replies']=question_replies(item.get('content'))
                records.append((offset,{'type':obj.get('type'),'timestamp':obj.get('timestamp'),'payload':payload}))
                if obj.get('type')=='event_msg' and p.get('type')=='task_started':break
        pending=set();async_pending={};titles={}
        for offset,obj in reversed(records):
            p=obj['payload'];kind=p.get('type');outer=obj.get('type')
            if outer=='event_msg' and kind=='task_started':
                result.update(state=2,revision='',turnId=p.get('turn_id',''),terminal=False,legacyRevisions=[])
                started=event_time(obj.get('timestamp'));activity=started;unknown_time=started is None
                pending.clear();async_pending.clear();titles.clear();continue
            turn=p.get('turn_id','')
            if turn and result['turnId'] and turn!=result['turnId']:continue
            if outer=='response_item' or (outer=='event_msg' and kind!='thread_settings_applied'):
                stamp=event_time(obj.get('timestamp'))
                if stamp is None:unknown_time=True
                else:activity=max(activity or stamp,stamp)
            if outer=='event_msg' and kind in ('task_complete','turn_aborted'):
                if kind=='turn_aborted':state=1 if p.get('reason')=='interrupted' else 5
                else:state=5 if p.get('error') else 3
                revision=f'{path}:turn:{result["turnId"]}' if result['turnId'] else f'{path}:{offset}'
                result.update(state=state,revision=revision if state==3 else '',terminal=True)
                if state==3:result['legacyRevisions'].append(f'{path}:{offset}')
                else:result['legacyRevisions']=[]
                # Successful turn completion does not answer an open question.
                # Failure or a deliberate stop does cancel the pending request.
                if state!=3:pending.clear();async_pending.clear()
                continue
            for call,index in p.get('question_replies',[]):
                if call in async_pending:
                    async_pending[call].discard(index)
                    if not async_pending[call]:async_pending.pop(call)
            if outer=='response_item' and kind in ('function_call_output','custom_tool_call_output'):
                pending.discard(p.get('call_id'))
                if p.get('question_rejected'):async_pending.pop(p.get('call_id'),None)
            item=p.get('item',{})
            final=(outer=='response_item' and kind=='message' and p.get('role')=='assistant' and p.get('delivery')!='async' and not p.get('has_questions') and
                   (p.get('phase')=='final_answer' or p.get('channel')=='final')) or (
                   outer=='event_msg' and kind=='item_completed' and isinstance(item,dict) and
                   item.get('type')=='AgentMessage' and item.get('phase')=='final_answer' and item.get('delivery')!='async' and not item.get('has_questions'))
            if final and not result['terminal']:
                revision=f'{path}:turn:{result["turnId"]}' if result['turnId'] else f'{path}:{offset}'
                result.update(state=3,revision=revision);result['legacyRevisions'].append(f'{path}:{offset}')
            elif outer=='response_item' and result['state'] in (2,4):
                name=str(p.get('name','')).split('.')[-1]
                if kind in ('function_call','custom_tool_call') and name in ('request_user_input','request_plugin_install'):
                    if p.get('call_id'):pending.add(p['call_id'])
                elif kind in ('function_call','custom_tool_call') and name=='request_user_input_async':
                    if p.get('call_id'):
                        async_pending[p['call_id']]=set(range(p.get('question_count',1)))
                        titles[p['call_id']]=p.get('question_titles',[])
        completed=result['state']==3
        ignored={tuple(q) for q in ignored_questions if valid_question_identity(q)}
        remaining=0
        for call,indices in async_pending.items():
            for index in sorted(indices):
                identity=(result['turnId'],call,index)
                suppressed=bool(result['turnId']) and identity in ignored
                if not suppressed:remaining+=1
                if include_questions and result['turnId']:
                    labels=titles.get(call,[])
                    title=labels[index] if index<len(labels) and isinstance(labels[index],str) else ''
                    result['questions'].append({'turnId':result['turnId'],'callId':call,'index':index,
                                               'title':title or f'提问 {index+1}','ignored':suppressed})
        if pending or remaining:
            result.update(state=4,revision='',legacyRevisions=[])
        # A local process cannot survive an OS reboot. Settings-only writes do not
        # restart its turn. Never use elapsed silence to infer success or failure.
        if local and not completed and result['state'] in (2,4) and started is not None and not unknown_time:
            boot=system_boot_time() if boot_time is None else boot_time
            if boot is not None and started<boot and activity<boot:
                result.update(state=1,revision='',terminal=True,questions=[])
    except OSError:pass
    return result


def rollout_state(path,legacy_revisions=None,ignored_questions=()):
    snapshot=rollout_snapshot(path,ignored_questions=ignored_questions)
    if legacy_revisions is not None:legacy_revisions.extend(snapshot['legacyRevisions'])
    return snapshot['state'],snapshot['revision']


def valid_question_identity(value):
    return (isinstance(value,(list,tuple)) and len(value)==3 and
            all(isinstance(x,str) and x for x in value[:2]) and type(value[2]) is int and value[2]>=0)


def ignored_questions(cache,thread):
    stored=cache.get('ignoredQuestions',{})
    values=stored.get(thread,[]) if isinstance(stored,dict) else []
    return [q for q in values if valid_question_identity(q)] if isinstance(values,list) else []


def question_list(cache):
    """Read fresh questions for fixed task identities; titles are returned, never cached."""
    assigned=task_slot_ids(cache);rows={r['id']:r for r in catalog(assigned)};result=[]
    for slot,thread in enumerate(assigned):
        row=rows.get(thread)
        if not row:continue
        snapshot=rollout_snapshot(row['rollout_path'],ignored_questions=ignored_questions(cache,thread),include_questions=True)
        for question in snapshot['questions']:
            result.append({**question,'threadId':thread,'taskTitle':row.get('title','') or '未命名任务','key':[1,2,3,5,6,7][slot]})
    return result


def set_question_ignored(cache,thread,turn,call,index,ignore):
    identity=[turn,call,index]
    if not valid_question_identity(identity):raise ValueError('无效的提问身份')
    # Re-read before applying a frozen selection. Never apply by title or slot.
    current=next((q for q in question_list(cache) if q['threadId']==thread and
                  [q['turnId'],q['callId'],q['index']]==identity),None)
    if current is None:raise RuntimeError('提问已结束或已更新，请刷新后重试')
    values=[q for q in ignored_questions(cache,thread) if q[0]==turn and list(q)!=identity]
    if ignore:values.append(identity)
    stored=cache.get('ignoredQuestions',{})
    if not isinstance(stored,dict):stored={}
    cache['ignoredQuestions']={**stored,thread:values}
    return {'ok':True}


def task_acknowledgements(cache,thread_id):
    value=cache.setdefault('taskAck',{}).get(thread_id,[])
    if isinstance(value,str):return [value] if value else []
    return [r for r in value if isinstance(r,str) and r] if isinstance(value,list) else []


def acknowledge_task(cache,thread_id,revision):
    if not thread_id or not revision:return False
    revisions=task_acknowledgements(cache,thread_id)
    if revision in revisions:return False
    # Keep distinct turns: a delayed older chooser must not replace a newer ack.
    cache['taskAck'][thread_id]=revisions+[revision]
    return True


def task_slot_ids(cache):
    """Keep legacy displayed positions on the first run of the stable-slot helper."""
    stored=cache.get('taskSlots')
    if not isinstance(stored,list):
        displayed=cache.get('tasks',[])
        stored=[r.get('id') if isinstance(r,dict) else None for r in displayed] if isinstance(displayed,list) else []
    result=[];seen=set()
    for identity in (stored+[None]*6)[:6]:
        if not isinstance(identity,str) or not identity or identity in seen:identity=None
        if identity:seen.add(identity)
        result.append(identity)
    return result


def reconcile_task_slots(rows,assigned,initialized):
    """Pure registry: existing IDs stay put; at most one new task replaces the oldest."""
    by_id={};duplicates=set()
    for row in rows:
        identity=row.get('id') if isinstance(row,dict) else None
        if not isinstance(identity,str) or not identity:continue
        if identity in by_id:duplicates.add(identity)
        else:by_id[identity]=dict(row)
    for identity in duplicates:by_id.pop(identity,None)
    def recency(row):
        value=row.get('recency_at_ms',0)
        return value if type(value) is int else 0
    candidates=sorted(by_id.values(),key=lambda r:(recency(r),r['id']),reverse=True)
    identities=task_slot_ids({'taskSlots':list(assigned)})
    slots=[by_id.get(identity) for identity in identities]
    occupied={r['id'] for r in slots if r is not None}
    for index,row in enumerate(slots):
        if row is not None:continue
        candidate=next((r for r in candidates if r['id'] not in occupied),None)
        if candidate is None:continue
        slots[index]=candidate;occupied.add(candidate['id'])
    if initialized and all(row is not None for row in slots):
        newest=next((r for r in candidates if r['id'] not in occupied),None)
        oldest=min(range(6),key=lambda i:recency(slots[i]))
        if newest is not None and recency(newest)>recency(slots[oldest]):slots[oldest]=newest
    return slots


def catalog(assigned_ids=()):
    databases=sorted(CODEX.glob('state_*.sqlite'),key=lambda p:int(re.search(r'state_(\d+)',p.name)[1]),reverse=True)
    if not databases:raise RuntimeError('Codex task catalog is unavailable')
    with sqlite3.connect(databases[0].as_uri()+'?mode=ro',uri=True,timeout=1) as db:
        db.row_factory=sqlite3.Row
        db.execute('BEGIN') # Both queries see one read-only catalog snapshot.
        cols={r[1] for r in db.execute('PRAGMA table_info(threads)')}
        recency='COALESCE(recency_at_ms,0)' if 'recency_at_ms' in cols else 'COALESCE(updated_at,0)*1000'
        clause=" AND (thread_source IS NULL OR thread_source <> 'subagent')" if 'thread_source' in cols else ''
        select=f"SELECT id,title,rollout_path,{recency} AS recency_at_ms FROM threads WHERE archived=0{clause}"
        rows=[dict(r) for r in db.execute(select+' ORDER BY recency_at_ms DESC, id DESC LIMIT 32')]
        # Assigned tasks may be older than the candidate window. Fetch them by ID
        # so falling out of LIMIT 32 cannot be mistaken for deletion/archiving.
        assigned=[identity for identity in task_slot_ids({'taskSlots':list(assigned_ids)}) if identity]
        if assigned:
            found={row['id'] for row in rows}
            for row in db.execute(select+' AND id IN ('+','.join('?' for _ in assigned)+')',assigned):
                if row['id'] not in found:rows.append(dict(row));found.add(row['id'])
        return rows


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
    assigned=task_slot_ids(cache)
    rows=catalog(assigned)
    slots=reconcile_task_slots(rows,assigned,'taskSlots' in cache or any(assigned))
    cache['taskSlots']=[row['id'] if row else None for row in slots]
    tasks=[]
    for r in slots:
        if r is None:
            # Keep interior holes so later physical keys do not shift. A missing
            # id excludes this slot from the chooser and prevents navigation.
            tasks.append({'state':0,'revision':''});continue
        legacy=[]
        state,revision=rollout_state(r['rollout_path'],legacy,ignored_questions(cache,r['id']))
        acknowledged=task_acknowledgements(cache,r['id'])
        if state==3 and revision and (revision in acknowledged or any(old in acknowledged for old in legacy)):
            acknowledge_task(cache,r['id'],revision)
            state=1
        tasks.append({'id':r['id'],'title':r.get('title',''),'state':state,'revision':revision})
    while tasks and 'id' not in tasks[-1]:tasks.pop()
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
        elif command=='reset':cache={'terminalSlots':cache.get('terminalSlots',{}),'taskAck':cache.get('taskAck',{}),'taskSlots':task_slot_ids(cache),'ignoredQuestions':cache.get('ignoredQuestions',{})};result={'ok':True}
        elif command=='questions':result={'questions':question_list(cache)}
        elif command=='question-ignore':
            if len(sys.argv)!=7 or sys.argv[6] not in ('ignore','restore'):raise ValueError('无效的提问操作')
            result=set_question_ignored(cache,sys.argv[2],sys.argv[3],sys.argv[4],int(sys.argv[5]),sys.argv[6]=='ignore')
        elif command=='ack-task':
            result={'ok':True,'changed':acknowledge_task(cache,sys.argv[2],sys.argv[3])}
        elif command=='tmux':result=tmux_action(sys.argv[2],cache,allow_recent='--allow-recent' in sys.argv[3:])
        else:raise ValueError('Unknown command')
        atomic_json(cachepath,cache);print(json.dumps(result,ensure_ascii=False))

if __name__=='__main__':
    try:main()
    except Exception as e:
        print(json.dumps({'error':str(e)},ensure_ascii=False));sys.exit(1)
