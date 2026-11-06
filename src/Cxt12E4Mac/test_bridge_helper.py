import importlib.util
import json
import sqlite3
import tempfile
import subprocess
from pathlib import Path
import unittest
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('bridge_helper',Path(__file__).with_name('bridge_helper.py'))
h=importlib.util.module_from_spec(spec);spec.loader.exec_module(h)

def event(t,**kw):return json.dumps({'type':'event_msg','payload':{'type':t,**kw}})+'\n'
def response(t,**kw):return json.dumps({'type':'response_item','payload':{'type':t,**kw}})+'\n'

class Tests(unittest.TestCase):
    def state(self,text):
        with tempfile.NamedTemporaryFile() as f:
            f.write(text.encode());f.flush();return h.rollout_state(f.name)
    def test_thinking_across_large_turn(self):
        self.assertEqual(self.state(event('task_started')+'{}\n'*100000)[0],2)
    def test_completion(self):
        state,rev=self.state(event('task_started')+event('task_complete'));self.assertEqual(state,3);self.assertTrue(rev)
    def test_completion_is_structural_and_order_independent(self):
        complete=json.dumps({'payload':{'turn_id':'a','type':'task_complete'},'timestamp':'2026-10-22T01:00:00Z','type':'event_msg'})+'\n'
        self.assertEqual(self.state(event('task_started',turn_id='a')+complete)[0],3)
        for text in (json.dumps({'type':'not_event_msg','payload':{'type':'task_complete'}}),
                     response('function_call_output',call_id='x',output=event('task_complete'))):
            self.assertEqual(self.state(event('task_started')+text)[0],2)
    def test_final_answer_formats_and_commentary_are_distinct(self):
        finals=[response('message',role='assistant',phase='final_answer'),response('message',role='assistant',channel='final'),
                event('item_completed',item={'type':'AgentMessage','phase':'final_answer'})]
        for final in finals:
            with self.subTest(final=final):
                self.assertEqual(self.state(event('task_started')+final)[0],3)
                self.assertEqual(self.state(final+event('task_started'))[0],2)
        for nonfinal in (response('message',role='assistant',phase='commentary',status='completed'),
                         response('message',role='user',phase='final_answer'),
                         event('item_completed',item={'type':'CommandExecution','status':'completed','phase':'final_answer'}),
                         event('item_completed',item={'type':'AgentMessage','phase':'commentary','status':'completed'})):
            self.assertEqual(self.state(event('task_started')+nonfinal)[0],2)
    def test_failure_or_stop_wins_over_final_message(self):
        final=response('message',role='assistant',phase='final_answer')
        for terminal,expected in [(event('task_complete',error={'codex_error_info':'server_overloaded'}),5),
                                  (event('turn_aborted',reason='interrupted'),1)]:
            for text in (final+terminal,terminal+final):
                self.assertEqual(self.state(event('task_started')+text),(expected,''))
        for error in (None,False,0,'', '   ',{},[]):
            self.assertEqual(self.state(event('task_complete',error=error))[0],3)
    def test_late_events_from_previous_turn_cannot_end_current_turn(self):
        for late in (event('task_complete',turn_id='old'),event('turn_aborted',turn_id='old',reason='interrupted'),
                     event('item_completed',turn_id='old',item={'type':'AgentMessage','phase':'final_answer'})):
            self.assertEqual(self.state(event('task_started',turn_id='new')+late)[0],2)
        late=json.dumps({'type':'event_msg','turnId':'old','payload':{'type':'task_complete'}})+'\n'
        self.assertEqual(self.state(event('task_started',turnId='new')+late)[0],2)
    def test_completion_revision_is_stable_byte_offset(self):
        prefix=response('message',role='assistant',phase='commentary',content='测试')
        text=prefix+event('task_complete')
        with tempfile.NamedTemporaryFile() as f:
            f.write(text.encode());f.flush()
            first=h.rollout_state(f.name)
            self.assertEqual(first,(3,f'{f.name}:{len(prefix.encode())}'))
            f.write(event('thread_settings_applied').encode());f.flush()
            self.assertEqual(h.rollout_state(f.name),first)
    def test_final_and_lifecycle_completion_share_turn_revision(self):
        with tempfile.NamedTemporaryFile() as f:
            f.write((event('task_started',turn_id='a')+response('message',role='assistant',phase='final_answer')).encode());f.flush()
            before=h.rollout_state(f.name)
            f.write(event('task_complete',turn_id='a').encode());f.flush()
            self.assertEqual(before,h.rollout_state(f.name))
            f.write((event('task_started',turn_id='b')+event('task_complete',turn_id='b')).encode());f.flush()
            self.assertNotEqual(before[1],h.rollout_state(f.name)[1])
    def test_reverse_reader_handles_chunk_boundaries_and_partial_json(self):
        text=response('function_call_output',output='x'*140000)+event('task_started')+event('task_complete')+'{"partial":'
        self.assertEqual(self.state(text)[0],3)
    def local_log(self,source='exec',start='2026-10-22T00:26:08Z',activity='2026-10-22T00:36:35Z'):
        rows=[{'type':'session_meta','payload':{'source':source}},
              {'timestamp':start,'type':'event_msg','payload':{'type':'task_started','turn_id':'a'}},
              {'timestamp':activity,'type':'event_msg','payload':{'type':'item_completed','turn_id':'a','item':{'type':'CommandExecution','status':'completed'}}},
              {'timestamp':'2026-10-22T03:19:00Z','type':'event_msg','payload':{'type':'thread_settings_applied'}}]
        return ''.join(json.dumps(r)+'\n' for r in rows)
    def test_preboot_local_running_or_waiting_turn_is_interrupted(self):
        with patch.object(h,'system_boot_time',return_value=h.event_time('2026-10-22T00:38:01Z')):
            for source in ('cli','vscode','exec'):
                self.assertEqual(self.state(self.local_log(source)),(1,''))
            request=json.dumps({'timestamp':'2026-10-22T00:36:36Z','type':'response_item','payload':{'type':'function_call','name':'request_user_input','call_id':'x'}})+'\n'
            self.assertEqual(self.state(self.local_log()+request),(1,''))
    def test_boot_recovery_does_not_override_completion_or_error(self):
        with patch.object(h,'system_boot_time',return_value=h.event_time('2026-10-22T00:38:01Z')):
            self.assertEqual(self.state(self.local_log()+event('task_complete'))[0],3)
            self.assertEqual(self.state(self.local_log()+event('task_complete',error={'code':'failed'}))[0],5)
    def test_boot_recovery_requires_local_source_valid_time_and_no_new_work(self):
        with patch.object(h,'system_boot_time',return_value=h.event_time('2026-10-22T00:38:01Z')):
            for text in (self.local_log(source='cloud'),self.local_log(source={'subagent':{}}),self.local_log(source=None),
                         self.local_log(start=None),self.local_log(activity=None),
                         self.local_log(start='2026-10-22T00:40:00Z',activity='2026-10-22T00:41:00Z'),
                         self.local_log(activity='2026-10-22T00:40:00Z')):
                self.assertEqual(self.state(text)[0],2)
        with patch.object(h,'system_boot_time',return_value=None):self.assertEqual(self.state(self.local_log())[0],2)
    def test_pending_only_current_turn(self):
        request=response('function_call',name='functions.request_user_input',call_id='x')
        self.assertEqual(self.state(event('task_started')+request)[0],4)
        self.assertEqual(self.state(event('task_started')+request+response('function_call_output',call_id='x'))[0],2)
        self.assertEqual(self.state(event('task_started')+request+event('task_started'))[0],2)
    def async_question(self,count=1):
        return response('function_call',name='functions.request_user_input_async',call_id='x',arguments=json.dumps({'questions':[{'title':'test'}]*count}))
    def async_reply(self,index=0,call='x'):
        return '<send_user_message_question_reply>'+json.dumps([{'questionItemId':json.dumps(['request_user_input_async',call,index]),'answer':'ok'}])+'</send_user_message_question_reply>'
    def test_async_question_is_yellow_until_user_reply_not_acceptance(self):
        pending=event('task_started')+self.async_question()
        pending+=event('item_completed',item={'type':'AgentMessage','id':'x','phase':'final_answer','delivery':'async','questions':[{'title':'test'}]})
        accepted=response('function_call_output',call_id='x',output=json.dumps({'accepted':True}))
        reply=response('message',role='user',content=[{'type':'input_text','text':self.async_reply()}])
        for text in (pending,pending+accepted,pending+accepted+event('item_completed',item={'type':'CommandExecution','status':'completed'})):
            self.assertEqual(self.state(text)[0],4)
        self.assertEqual(self.state(pending+accepted+reply)[0],2)
        self.assertEqual(self.state(pending+accepted+reply+event('task_complete'))[0],3)
    def test_question_display_is_not_a_final_answer(self):
        for extra in ({'delivery':'async'},{'questions':[{'title':'test'}]}):
            for display in (event('item_completed',item={'type':'AgentMessage','phase':'final_answer',**extra}),
                            response('message',role='assistant',phase='final_answer',**extra)):
                self.assertEqual(self.state(event('task_started')+self.async_question()+display)[0],4)
    def test_async_answers_match_each_question_and_call(self):
        pending=event('task_started')+self.async_question(2)
        first=response('message',role='user',content=[{'type':'input_text','text':self.async_reply(0)}])
        second=event('item_completed',item={'type':'UserMessage','content':[{'type':'text','text':self.async_reply(1)}]})
        self.assertEqual(self.state(pending+first+first)[0],4)
        self.assertEqual(self.state(pending+first+second)[0],2)
        for ignored in (response('message',role='user',content=[{'type':'input_text','text':'continue'}]),
                        response('message',role='user',content=[{'type':'input_text','text':self.async_reply(call='other')}]),
                        response('message',role='assistant',content=[{'type':'text','text':self.async_reply()}]),
                        response('function_call_output',call_id='other',output=self.async_reply())):
            self.assertEqual(self.state(event('task_started')+self.async_question()+ignored)[0],4)
    def test_rejected_stopped_or_superseded_async_question_does_not_leave_yellow(self):
        pending=event('task_started')+self.async_question()
        for output in ({'accepted':False},{'error':'tool rejected'},{'isError':True}):
            self.assertEqual(self.state(pending+response('function_call_output',call_id='x',output=json.dumps(output)))[0],2)
        self.assertEqual(self.state(pending+event('task_started'))[0],2)
        self.assertEqual(self.state(pending+event('task_complete'))[0],4)
        self.assertEqual(self.state(pending+event('turn_aborted',reason='interrupted'))[0],1)
    def test_unanswered_question_survives_final_and_successful_turn_end(self):
        pending=event('task_started',turn_id='a')+self.async_question()
        accepted=response('function_call_output',call_id='x',output=json.dumps({'accepted':True}))
        for end in (response('message',role='assistant',phase='final_answer'),
                    event('item_completed',item={'type':'AgentMessage','phase':'final_answer'}),
                    event('task_complete',turn_id='a'),
                    response('message',role='assistant',phase='final_answer')+event('task_complete',turn_id='a')):
            with self.subTest(end=end):
                self.assertEqual(self.state(pending+accepted+end),(4,''))
                self.assertEqual(self.state(pending+end+accepted),(4,''))
        self.assertEqual(self.state(pending+event('task_complete',error='failure')),(5,''))
        self.assertEqual(self.state(pending+event('turn_aborted',reason='interrupted')),(1,''))
    def test_late_reply_or_rejection_resolves_only_the_matching_completed_question(self):
        pending=event('task_started',turn_id='a')+self.async_question(2)+event('task_complete',turn_id='a')
        reply=lambda index:response('message',role='user',content=[{'type':'input_text','text':self.async_reply(index)}])
        self.assertEqual(self.state(pending+reply(0)+reply(0)),(4,''))
        self.assertEqual(self.state(pending+reply(0)+reply(1))[0],3)
        self.assertEqual(self.state(pending+event('task_started',turn_id='b')+reply(0)),(2,''))
        rejected=response('function_call_output',call_id='x',output=json.dumps({'accepted':False}))
        self.assertEqual(self.state(pending+rejected)[0],3)
        unrelated=response('function_call_output',call_id='other',output=json.dumps({'accepted':False}))
        self.assertEqual(self.state(pending+unrelated),(4,''))
    def test_opening_waiting_task_cannot_acknowledge_the_future_completion(self):
        with tempfile.NamedTemporaryFile() as f:
            f.write((event('task_started',turn_id='a')+self.async_question()+event('task_complete',turn_id='a')).encode());f.flush()
            row={'id':'a','title':'A','rollout_path':f.name};cache={}
            with patch.object(h,'catalog',return_value=[row]),patch.object(h,'panes',return_value=[]):
                waiting=h.status(cache)['tasks'][0]
                self.assertEqual((waiting['state'],waiting['revision']),(4,''))
                self.assertFalse(h.acknowledge_task(cache,'a',waiting['revision']))
                f.write(response('message',role='user',content=[{'type':'input_text','text':self.async_reply()}]).encode());f.flush()
                self.assertEqual(h.status(cache)['tasks'][0]['state'],3)
    def test_successful_turn_with_unanswered_question_remains_yellow_across_boot(self):
        records=[{'type':'session_meta','payload':{'source':'cli'}},
                 json.loads(event('task_started')),json.loads(self.async_question()),json.loads(event('task_complete'))]
        text=''.join(json.dumps({**r,'timestamp':'2026-10-22T00:30:00Z'})+'\n' for r in records)
        with patch.object(h,'system_boot_time',return_value=h.event_time('2026-10-22T00:38:01Z')):
            self.assertEqual(self.state(text),(4,''))
    def test_abort_and_partial_line(self):
        self.assertEqual(self.state(event('turn_aborted')+'{"partial":')[0],5)
    def test_user_stop_is_idle_and_clears_pending_or_completed_state(self):
        stopped=event('turn_aborted',reason='interrupted')
        request=response('function_call',name='functions.request_user_input',call_id='x')
        for prior in (event('task_started'),event('task_started')+request,event('task_complete')):
            self.assertEqual(self.state(prior+stopped+'{"partial":'),(1,''))
        self.assertEqual(self.state(stopped+event('task_started')),(2,''))
    def test_other_abort_reasons_remain_errors(self):
        for reason in ('error','failed','unknown'):
            self.assertEqual(self.state(event('turn_aborted',reason=reason)),(5,''))
    def test_previously_red_task_returns_idle_without_resetting_cache(self):
        row={'id':'stopped','title':'Stopped','rollout_path':'p'}
        cache={'tasks':[{**row,'state':5,'revision':''}],'taskSeen':{'stopped':True},'taskAck':{'stopped':'old'}}
        with patch.object(h,'catalog',side_effect=lambda *args:[dict(row)]),patch.object(h,'panes',return_value=[]),patch.object(h,'rollout_state',return_value=(1,'')):
            self.assertEqual(h.status(cache)['tasks'][0]['state'],1)
    def test_duplicate_slot_fails_closed(self):
        inv=[dict(session_name='01-a',session_id='$1',session_created='10',pid='11'),dict(session_name='01-b',session_id='$2',session_created='10',pid='11')]
        self.assertIsNone(h.session_slots(inv)[0])
    def test_generation_and_aggregation(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(h,'ROOT',Path(tmp)):
            p=dict(session_name='01-a',session_id='$1',session_created='10',pid='11',pane_id='%1')
            path=Path(tmp)/'terminal-status/pane-1.json'
            h.atomic_json(path,{**p,'state':'waiting','revision':100})
            self.assertEqual(h.session_slots([p])[0]['state'],4)
            self.assertEqual(h.session_slots([{**p,'pid':'12'}])[0]['state'],1)
            slots=h.session_slots([p,{**p,'session_name':'010-a','session_id':'$2','pane_id':'%2'}])
            self.assertEqual([x['name'] for x in slots if x],['01-a','010-a'])
    def pane(self,name,number,created='10'):
        return dict(session_name=name,session_id=f'${number}',session_created=created,pid='11',pane_id=f'%{number}')
    def test_agent_status_survives_rename_and_renumber(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(h,'ROOT',Path(tmp)):
            pane=self.pane('01-agent',1)
            path=Path(tmp)/'terminal-status/pane-1.json'
            for state,code in [('thinking',2),('waiting',4),('completed',3)]:
                h.atomic_json(path,{**pane,'state':state,'revision':100})
                cache={};before=h.session_slots([pane],cache)[0]
                for name,slot in [('renamed',0),('03-agent',2)]:
                    with self.subTest(state=state,name=name):
                        after=h.session_slots([{**pane,'session_name':name}],cache)[slot]
                        self.assertEqual(after,{**before,'name':name})
                        self.assertEqual(after['state'],code)
                        self.assertEqual(after['revision'],100 if state=='completed' else 0)
    def test_stale_agent_generations_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(h,'ROOT',Path(tmp)):
            pane=self.pane('01-agent',1)
            path=Path(tmp)/'terminal-status/pane-1.json'
            for key,value in [('pid','12'),('session_id','$2'),('session_created','20'),('pane_id','%2')]:
                for replacement in (value,None):
                    with self.subTest(key=key,replacement=replacement):
                        record={**pane,'state':'completed','revision':100,key:replacement}
                        h.atomic_json(path,record)
                        result=h.session_slots([pane])[0]
                        self.assertEqual((result['state'],result['revision']),(1,0))
    def test_completion_ack_survives_renumber(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(h,'ROOT',Path(tmp)),patch.object(h,'catalog',return_value=[]):
            pane=self.pane('01-agent',1);identity=h.session_identity(pane)
            h.atomic_json(Path(tmp)/'terminal-status/pane-1.json',{**pane,'state':'completed','revision':100})
            cache={'terminalSeen':{identity:True},'terminalAck':{identity:99}}
            for name,slot in [('01-agent',0),('03-renamed',2)]:
                with patch.object(h,'panes',return_value=[{**pane,'session_name':name}]):
                    self.assertEqual(h.status(cache)['terminals'][slot]['state'],3)
            cache['terminalAck'][identity]=100
            with patch.object(h,'panes',return_value=[{**pane,'session_name':'02-renamed'}]):
                self.assertEqual(h.status(cache)['terminals'][1]['state'],1)
    def test_conditional_reports_require_current_generation_but_allow_rename(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(h,'ROOT',Path(tmp)):
            original=self.pane('01-agent',1);renamed={**original,'session_name':'03-renamed'}
            path=Path(tmp)/'terminal-status/pane-1.json'
            with patch.object(h,'panes',return_value=[renamed]):
                for report,state,result in [('idle-if-active','thinking','idle'),('thinking-if-waiting','waiting','thinking')]:
                    h.atomic_json(path,{**original,'state':state,'revision':100})
                    h.report_agent(report,'%1')
                    record=json.loads(path.read_text())
                    self.assertEqual(record['state'],result)
                    self.assertEqual(record['session_name'],renamed['session_name'])
                    stale={**original,'pid':'12','state':state,'revision':100}
                    h.atomic_json(path,stale)
                    h.report_agent(report,'%1')
                    self.assertEqual(json.loads(path.read_text()),stale)
    def test_default_and_named_sessions_get_empty_slots(self):
        cache={};a=self.pane('0',0);b=self.pane('project',1);fixed=self.pane('03-fixed',2)
        slots=h.session_slots([fixed,b,a],cache)
        self.assertEqual([s['name'] if s else None for s in slots],['0','project','03-fixed',None,None,None])
        self.assertEqual(slots[0]['state'],1)
    def test_automatic_slots_do_not_shift_on_removal_or_rename(self):
        cache={};a=self.pane('0',0);b=self.pane('project',1)
        h.session_slots([a,b],cache)
        renamed={**b,'session_name':'renamed'}
        slots=h.session_slots([renamed],cache)
        self.assertIsNone(slots[0]);self.assertEqual(slots[1]['name'],'renamed')
        slots=h.session_slots([self.pane('new',2),renamed],cache)
        self.assertEqual([s['name'] for s in slots if s],['new','renamed'])
    def test_numbered_slot_preempts_automatic_and_duplicate_stays_empty(self):
        cache={};auto=self.pane('0',0)
        h.session_slots([auto],cache)
        slots=h.session_slots([auto,self.pane('01-a',1)],cache)
        self.assertEqual([s['name'] for s in slots if s],['01-a','0'])
        slots=h.session_slots([auto,self.pane('01-a',1),self.pane('01-b',2)],cache)
        self.assertIsNone(slots[0]);self.assertEqual(slots[1]['name'],'0')
    def test_switch_uses_same_automatic_slot_as_lights(self):
        cache={};inventory=[self.pane('0',0),self.pane('work',1)]
        h.session_slots(inventory,cache)
        with patch.object(h,'panes',return_value=inventory[1:]),patch.object(h,'clients',return_value=[{'client_name':'client'}]),patch.object(h,'call_tmux') as call:
            self.assertTrue(h.tmux_action('session:2',cache)['ok'])
            call.assert_called_once_with('switch-client','-c','client','-t','$1')
            with self.assertRaises(RuntimeError):h.tmux_action('session:1',cache)
    def test_automatic_slot_capacity_and_stale_identity(self):
        cache={};inventory=[self.pane(f'project-{i}',i) for i in range(7)]
        slots=h.session_slots(inventory,cache)
        self.assertEqual(len([s for s in slots if s]),6)
        h.session_slots([],cache);self.assertEqual(cache['terminalSlots'],{})
        slots=h.session_slots([self.pane('new-server',0,'20')],cache)
        self.assertEqual(slots[0]['name'],'new-server')
    def test_restart_preserves_slot_assignments(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(h,'ROOT',Path(tmp)),patch.object(h.sys,'argv',['bridge_helper.py','reset']),patch('builtins.print'):
            h.atomic_json(Path(tmp)/'status-cache.json',{'terminalSlots':{'$1:10:11':1},'taskSeen':{'old':True},'taskAck':{'a':['turn-a']}})
            h.main()
            cache=json.loads((Path(tmp)/'status-cache.json').read_text())
            self.assertEqual(cache,{'terminalSlots':{'$1:10:11':1},'taskAck':{'a':['turn-a']},'taskSlots':[None]*6,'ignoredQuestions':{}})
    def test_multiple_clients_refused(self):
        with patch.object(h,'clients',return_value=[{'client_tty':'a'},{'client_tty':'b'}]):
            with self.assertRaises(RuntimeError):h.active_client()
            self.assertEqual(h.active_client('a')['client_tty'],'a')
    def test_session_switch_requires_explicit_recent_client_opt_in(self):
        rows=[{'client_name':'recent','client_tty':'a','client_pid':'100'},{'client_name':'older','client_tty':'b','client_pid':'200'}]
        with patch.object(h,'panes',return_value=[self.pane('01-work',1)]),patch.object(h,'clients',return_value=rows),patch.object(h,'call_tmux') as call:
            with self.assertRaises(RuntimeError):h.tmux_action('session:1',{})
            call.assert_not_called()
            result=h.tmux_action('session:1',{},allow_recent=True)
            call.assert_called_once_with('switch-client','-c','recent','-t','$1')
            self.assertEqual((result['clientPID'],result['clientTTY'],result['attachedClients']),('100','a',2))
            call.reset_mock()
            h.tmux_action('session:1',{},tty='b',allow_recent=True)
            call.assert_called_once_with('switch-client','-c','older','-t','$1')
    def test_stable_session_record_survives_renumber_but_rejects_new_generation(self):
        pane=self.pane('03-renamed',1);cache={}
        with patch.object(h,'panes',return_value=[pane]),patch.object(h,'clients',return_value=[{'client_name':'client'}]),patch.object(h,'call_tmux') as call:
            result=h.tmux_action('session-id:$1:10:11',cache)
            self.assertTrue(result['ok'])
            call.assert_called_once_with('switch-client','-c','client','-t','$1')
            self.assertIn('$1:10:11',cache['terminalAck'])
            for action in ('session-id:$1:20:11','session-id:$1:10:12','session-id:$2','session-id:work','session-id:$1;new-session'):
                with self.subTest(action=action):
                    call.reset_mock()
                    with self.assertRaises(RuntimeError):h.tmux_action(action,{})
                    call.assert_not_called()
    def test_previous_next_session_only_switch_existing_client(self):
        with patch.object(h,'clients',return_value=[{'client_name':'client'}]),patch.object(h,'panes') as panes,patch.object(h,'call_tmux') as call:
            for action,flag in [('previous-session','-p'),('next-session','-n')]:
                call.reset_mock()
                self.assertTrue(h.tmux_action(action,{})['ok'])
                call.assert_called_once_with('switch-client','-c','client',flag)
            panes.assert_not_called()
    def test_directional_pane_actions_target_exact_active_pane(self):
        c={'client_name':'client','pane_id':'%42','window_id':'@7','session_id':'$5'}
        pane={**self.pane('work',5),'pane_id':'%42','window_id':'@7'}
        with patch.object(h,'clients',return_value=[c]),patch.object(h,'panes',return_value=[pane]),patch.object(h,'call_tmux') as call:
            for direction,flag in [('left','-L'),('right','-R'),('up','-U'),('down','-D')]:
                call.reset_mock()
                self.assertTrue(h.tmux_action('pane-'+direction,{})['ok'])
                call.assert_called_once_with('select-pane','-t','%42',flag)
    def test_absent_server_is_empty_inventory_but_never_successful_mutation(self):
        for error in ('no server running on /tmp/tmux-test/default','error connecting to /tmp/tmux-test/default (No such file or directory)'):
            result=subprocess.CompletedProcess([],1,'',error)
            with patch.object(h.subprocess,'run',return_value=result):
                self.assertEqual(h.call_tmux('list-panes','-a'),'')
                self.assertEqual(h.call_tmux('list-clients'),'')
                with self.assertRaises(RuntimeError):h.call_tmux('switch-client','-c','x','-t','$1')
        with patch.object(h.subprocess,'run',return_value=subprocess.CompletedProcess([],1,'','cannot open config: No such file or directory')):
            with self.assertRaises(RuntimeError):h.call_tmux('list-panes','-a')
    def test_completed_is_green_until_viewed_then_white(self):
        row={'id':'a','title':'A','rollout_path':'p'};cache={'taskAck':{}}
        with patch.object(h,'catalog',side_effect=lambda *args:[dict(row)]),patch.object(h,'panes',return_value=[]),patch.object(h,'rollout_state',return_value=(3,'v1')):
            self.assertEqual(h.status(cache)['tasks'][0]['state'],3)
            self.assertTrue(h.acknowledge_task(cache,'a','v1'))
            self.assertEqual(h.status(cache)['tasks'][0]['state'],1)
            # The persisted confirmation survives a new helper process / Bridge restart.
            self.assertEqual(h.status(json.loads(json.dumps(cache)))['tasks'][0]['state'],1)
        with patch.object(h,'catalog',side_effect=lambda *args:[dict(row)]),patch.object(h,'panes',return_value=[]),patch.object(h,'rollout_state',return_value=(3,'v2')):
            self.assertEqual(h.status(cache)['tasks'][0]['state'],3)
            h.acknowledge_task(cache,'a','v1')
            self.assertEqual(h.status(cache)['tasks'][0]['state'],3)
            h.acknowledge_task(cache,'a','v2')
            h.acknowledge_task(cache,'a','v1')
            self.assertEqual(h.status(cache)['tasks'][0]['state'],1)
            self.assertEqual(h.status({})['tasks'][0]['state'],3)
    def test_viewing_one_task_does_not_ack_another_or_clear_active_state(self):
        row={'id':'a','title':'A','rollout_path':'p'};cache={}
        self.assertFalse(h.acknowledge_task(cache,'a',''))
        h.acknowledge_task(cache,'other','v1')
        with patch.object(h,'catalog',side_effect=lambda *args:[dict(row)]),patch.object(h,'panes',return_value=[]):
            with patch.object(h,'rollout_state',return_value=(3,'v1')):self.assertEqual(h.status(cache)['tasks'][0]['state'],3)
            h.acknowledge_task(cache,'a','v1')
            for state in (1,2,4,5):
                with patch.object(h,'rollout_state',return_value=(state,'')):self.assertEqual(h.status(cache)['tasks'][0]['state'],state)
    def test_legacy_view_confirmation_migrates_to_current_turn_identity(self):
        with tempfile.NamedTemporaryFile() as f:
            start=event('task_started',turn_id='a');f.write((start+event('task_complete',turn_id='a')).encode());f.flush()
            row={'id':'a','title':'A','rollout_path':f.name}
            cache={'taskAck':{'a':f'{f.name}:{len(start.encode())}'}}
            with patch.object(h,'catalog',side_effect=lambda *args:[dict(row)]),patch.object(h,'panes',return_value=[]):
                self.assertEqual(h.status(cache)['tasks'][0]['state'],1)
                self.assertIn(f'{f.name}:turn:a',cache['taskAck']['a'])
    def test_ack_command_persists_only_the_requested_completion(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(h,'ROOT',Path(tmp)),patch('builtins.print'):
            with patch.object(h.sys,'argv',['bridge_helper.py','ack-task','a','v1']):h.main()
            with patch.object(h.sys,'argv',['bridge_helper.py','ack-task','a','v2']):h.main()
            with patch.object(h.sys,'argv',['bridge_helper.py','ack-task','a','v1']):h.main()
            self.assertEqual(json.loads((Path(tmp)/'status-cache.json').read_text())['taskAck']['a'],['v1','v2'])
    def test_tmux_failure_keeps_last_inventory(self):
        cache={'lastTerminals':[{'id':'a','state':2,'revision':0}]+[None]*5}
        with patch.object(h,'catalog',return_value=[]),patch.object(h,'panes',side_effect=RuntimeError('timeout')):
            self.assertEqual(h.status(cache)['terminals'][0]['state'],2)

class TaskSlotTests(unittest.TestCase):
    def row(self,identity,recency):
        return {'id':identity,'title':identity.upper(),'rollout_path':'log-'+identity,'recency_at_ms':recency}
    def ids(self,slots):return [row['id'] if row else None for row in slots]
    def reconcile(self,rows,assigned=(),initialized=False):
        return h.reconcile_task_slots(rows,assigned,initialized)
    def test_initial_recent_order_and_fewer_than_six(self):
        rows=[self.row('a',10),self.row('b',30),self.row('c',20)]
        self.assertEqual(self.ids(self.reconcile(rows)),['b','c','a',None,None,None])
        self.assertEqual(self.ids(self.reconcile([])),[None]*6)
    def test_recent_activity_never_reorders_assigned_tasks(self):
        assigned=list('abcdef')
        rows=[self.row(identity,100+index) for index,identity in enumerate(assigned)]
        self.assertEqual(self.ids(self.reconcile(list(reversed(rows)),assigned,True)),assigned)
        self.assertEqual(self.ids(self.reconcile(rows,assigned,True)),assigned)
    def test_new_task_replaces_only_the_least_recent_slot(self):
        assigned=list('abcdef')
        rows=[self.row(identity,100+index) for index,identity in enumerate(assigned)]
        rows += [self.row('new',999),self.row('also-new',998)]
        self.assertEqual(self.ids(self.reconcile(rows,assigned,True)),['new','b','c','d','e','f'])
        self.assertEqual(self.ids(self.reconcile(rows[:6]+[self.row('old',99)],assigned,True)),assigned)
        self.assertEqual(self.ids(self.reconcile(rows[:6]+[self.row('equal',100)],assigned,True)),assigned)
    def test_archived_or_deleted_task_fills_only_its_empty_slot(self):
        rows=[self.row('a',30),self.row('c',20),self.row('replacement',10)]
        self.assertEqual(self.ids(self.reconcile(rows,['a','removed','c'],True)),['a','replacement','c',None,None,None])
        self.assertEqual(self.ids(self.reconcile(rows[:2],['a','removed','c'],True)),['a',None,'c',None,None,None])
    def test_duplicate_or_invalid_ids_cannot_alias_two_physical_keys(self):
        rows=[self.row('a',20),self.row('b',10),{},None,{'id':123}]
        self.assertEqual(self.ids(self.reconcile(rows,['a','a','b'],True)),['a',None,'b',None,None,None])
        conflicting=[self.row('a',20),{**self.row('a',30),'rollout_path':'other-log'},self.row('b',10)]
        self.assertEqual(self.ids(self.reconcile(conflicting,['a','b'],True)),[None,'b',None,None,None,None])
    def test_status_migrates_displayed_order_and_persists_it_across_restarts(self):
        cache={'tasks':[{'id':'a'},{'id':'b'},{'id':'c'}],'taskAck':{'c':['seen']}}
        rows=[self.row('a',10),self.row('b',30),self.row('c',20)]
        def state(path,legacy,ignored=()):return ({'log-a':4,'log-b':2,'log-c':3}[path],'seen' if path=='log-c' else '')
        with patch.object(h,'catalog',return_value=rows) as read,patch.object(h,'panes',return_value=[]),patch.object(h,'rollout_state',side_effect=state):
            result=h.status(cache)
            read.assert_called_once_with(['a','b','c',None,None,None])
            self.assertEqual([r['id'] for r in result['tasks']],['a','b','c'])
            self.assertEqual([r['state'] for r in result['tasks']],[4,2,1])
            self.assertEqual(cache['taskSlots'],['a','b','c',None,None,None])
            restarted=json.loads(json.dumps(cache))
            self.assertEqual(h.status(restarted)['tasks'],result['tasks'])
            self.assertEqual(restarted['taskAck'],{'c':['seen']})
        self.assertTrue(all('rollout_path' in row for row in rows),'status must not mutate the catalog snapshot')
    def test_interior_holes_are_off_and_cannot_navigate_to_another_task(self):
        cache={'taskSlots':['a','removed','c',None,None,None]}
        with patch.object(h,'catalog',return_value=[self.row('a',20),self.row('c',10)]),patch.object(h,'panes',return_value=[]),patch.object(h,'rollout_state',return_value=(2,'')):
            tasks=h.status(cache)['tasks']
            self.assertEqual(len(tasks),3)
            self.assertEqual(tasks[1],{'state':0,'revision':''})
            self.assertEqual(tasks[2]['id'],'c')
    def test_catalog_resolves_assigned_ids_outside_recent_window(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(h,'CODEX',Path(tmp)):
            path=Path(tmp)/'state_5.sqlite'
            with sqlite3.connect(path) as db:
                db.execute('CREATE TABLE threads(id TEXT PRIMARY KEY,title TEXT,rollout_path TEXT,recency_at_ms INTEGER,archived INTEGER,thread_source TEXT)')
                db.executemany('INSERT INTO threads VALUES(?,?,?,?,?,?)',[(str(i),'Task '+str(i),'log-'+str(i),i,0,None) for i in range(40)])
                db.executemany('INSERT INTO threads VALUES(?,?,?,?,?,?)',[('archived','Archived','log-a',999,1,None),('child','Child','log-c',999,0,'subagent')])
            rows=h.catalog(['0','1','archived','child','missing'])
            ids={row['id'] for row in rows}
            self.assertEqual(len(rows),34)
            self.assertTrue({'0','1','39'}.issubset(ids))
            self.assertFalse({'2','archived','child','missing'} & ids)
            stable=self.reconcile(rows,['0','1','39','38','37','36'],True)
            self.assertEqual(self.ids(stable),['35','1','39','38','37','36'])
    def test_missing_catalog_is_not_an_empty_success_and_preserves_cache(self):
        cache={'taskSlots':['a',None,'c',None,None,None],'tasks':[{'id':'a'},{'state':0},{'id':'c'}],'taskAck':{'a':['seen']}}
        original=json.loads(json.dumps(cache))
        with tempfile.TemporaryDirectory() as tmp,patch.object(h,'CODEX',Path(tmp)):
            with self.assertRaisesRegex(RuntimeError,'catalog is unavailable'):h.status(cache)
        self.assertEqual(cache,original)
    def test_reset_retains_task_slots_and_completion_acknowledgements(self):
        with tempfile.TemporaryDirectory() as tmp,patch.object(h,'ROOT',Path(tmp)):
            cache={'taskSlots':['a',None,'c',None,None,None],'taskAck':{'a':['seen']}}
            h.atomic_json(Path(tmp)/'status-cache.json',cache)
            with patch('sys.argv',['helper','reset']),patch('builtins.print'):h.main()
            reset=json.loads((Path(tmp)/'status-cache.json').read_text())
            self.assertEqual(reset['taskSlots'],cache['taskSlots'])
            self.assertEqual(reset['taskAck'],cache['taskAck'])

class QuestionIgnoreTests(unittest.TestCase):
    def question(self,call='call',count=2):
        return response('function_call',name='functions.request_user_input_async',call_id=call,
                        arguments=json.dumps({'questions':[{'title':'同名问题 '+str(i)} for i in range(count)]}))
    def snapshot(self,text,ignored=(),**kwargs):
        with tempfile.NamedTemporaryFile() as f:
            f.write(text.encode());f.flush()
            return h.rollout_snapshot(f.name,ignored_questions=ignored,include_questions=True,**kwargs)
    def test_only_the_selected_question_is_ignored_and_restorable(self):
        text=event('task_started',turn_id='turn')+self.question()
        first=['turn','call',0];second=['turn','call',1]
        one=self.snapshot(text,[first]);self.assertEqual(one['state'],4)
        self.assertEqual([q['ignored'] for q in one['questions']],[True,False])
        self.assertEqual(self.snapshot(text,[first,second])['state'],2)
        self.assertEqual(self.snapshot(text,[second])['state'],4)
        for wrong in ([['old','call',0]],[['turn','other',0]],[['turn','call',True]],[['turn','call',-1]]):
            self.assertFalse(any(q['ignored'] for q in self.snapshot(text,wrong)['questions']))
    def test_completion_is_not_acknowledged_by_ignore(self):
        text=event('task_started',turn_id='turn')+self.question(count=1)+event('task_complete',turn_id='turn')
        self.assertEqual(self.snapshot(text)['revision'],'')
        done=self.snapshot(text,[['turn','call',0]])
        self.assertEqual(done['state'],3);self.assertTrue(done['revision'])
        # A synchronous request during an active turn cannot be ignored here.
        active=event('task_started',turn_id='turn')+self.question(count=1)+response('function_call',name='request_user_input',call_id='sync')
        self.assertEqual(self.snapshot(active,[['turn','call',0],['turn','sync',0]])['state'],4)
    def test_new_turn_stop_error_reply_and_rejection(self):
        text=event('task_started',turn_id='turn')+self.question(count=1);ignored=[['turn','call',0]]
        self.assertEqual(self.snapshot(text+event('task_started',turn_id='next')+self.question(count=1),ignored)['state'],4)
        for suffix,state in [(event('turn_aborted',reason='interrupted'),1),(event('task_complete',error='failed'),5),
                              (response('function_call_output',call_id='call',output={'accepted':False}),2)]:
            r=self.snapshot(text+suffix,ignored);self.assertEqual(r['state'],state);self.assertEqual(r['questions'],[])
        reply=response('message',role='user',content=[{'type':'input_text','text':'<send_user_message_question_reply>'+json.dumps([{'questionItemId':json.dumps(['request_user_input_async','call',0]),'answer':'yes'}])+'</send_user_message_question_reply>'}])
        self.assertEqual(self.snapshot(text+reply,ignored)['questions'],[])
    def test_identity_guard_restart_restore_and_no_title_persistence(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'log';path.write_text(event('task_started',turn_id='turn')+self.question(count=1))
            rows=[{'id':t,'title':'同名任务','rollout_path':str(path),'recency_at_ms':i} for i,t in enumerate(['one','two'])]
            cache={'taskSlots':['one','two'],'taskAck':{'one':['old-completion']}}
            with patch.object(h,'catalog',return_value=rows),patch.object(h,'panes',return_value=[]):
                q=h.question_list(cache);self.assertEqual([x['key'] for x in q],[1,2]);self.assertEqual(q[0]['title'],'同名问题 0')
                h.set_question_ignored(cache,'one','turn','call',0,True)
                restored=json.loads(json.dumps(cache));self.assertEqual([t['state'] for t in h.status(restored)['tasks']],[2,4])
                self.assertEqual(cache['taskAck'],{'one':['old-completion']});self.assertNotIn('同名问题',json.dumps(cache,ensure_ascii=False))
                h.set_question_ignored(cache,'one','turn','call',0,False);self.assertEqual(h.status(cache)['tasks'][0]['state'],4)
                h.set_question_ignored(cache,'one','turn','call',0,True)
                path.write_text(event('task_started',turn_id='next')+self.question(count=1))
                before=json.dumps(cache)
                with self.assertRaisesRegex(RuntimeError,'已结束或已更新'):h.set_question_ignored(cache,'one','turn','call',0,True)
                self.assertEqual(json.dumps(cache),before);self.assertEqual(h.status(cache)['tasks'][0]['state'],4)
            with patch.object(h,'catalog',return_value=[]):
                with self.assertRaises(RuntimeError):h.set_question_ignored(cache,'one','next','call',0,True)
            with patch.object(h,'ROOT',Path(tmp)),patch('sys.argv',['helper','reset']),patch('builtins.print'):
                (Path(tmp)/'status-cache.json').write_text(json.dumps(cache));h.main()
                self.assertEqual(json.loads((Path(tmp)/'status-cache.json').read_text())['ignoredQuestions'],cache['ignoredQuestions'])
    def test_preboot_or_unidentified_questions_cannot_be_managed(self):
        self.assertEqual(self.snapshot(event('task_started')+self.question())['questions'],[])
        rows=[{'type':'session_meta','payload':{'source':'exec'}},
              {'timestamp':'2026-10-22T00:00:00Z','type':'event_msg','payload':{'type':'task_started','turn_id':'turn'}},
              {'timestamp':'2026-10-22T00:00:01Z','type':'response_item','payload':{'type':'function_call','name':'request_user_input_async','call_id':'call'}}]
        r=self.snapshot(''.join(json.dumps(x)+'\n' for x in rows),[['turn','call',0]],boot_time=h.event_time('2026-10-22T01:00:00Z'))
        self.assertEqual(r['state'],1);self.assertEqual(r['questions'],[])

if __name__=='__main__':unittest.main()
