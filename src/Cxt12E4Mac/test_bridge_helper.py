import importlib.util
import json
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
    def test_pending_only_current_turn(self):
        request=response('function_call',name='functions.request_user_input',call_id='x')
        self.assertEqual(self.state(event('task_started')+request)[0],4)
        self.assertEqual(self.state(event('task_started')+request+response('function_call_output',call_id='x'))[0],2)
        self.assertEqual(self.state(event('task_started')+request+event('task_started'))[0],2)
    def test_async_question_does_not_block(self):
        self.assertEqual(self.state(event('task_started')+response('function_call',name='functions.request_user_input_async',call_id='x'))[0],2)
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
        with patch.object(h,'catalog',side_effect=lambda:[dict(row)]),patch.object(h,'panes',return_value=[]),patch.object(h,'rollout_state',return_value=(1,'')):
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
            h.atomic_json(Path(tmp)/'status-cache.json',{'terminalSlots':{'$1:10:11':1},'taskSeen':{'old':True}})
            h.main()
            cache=json.loads((Path(tmp)/'status-cache.json').read_text())
            self.assertEqual(cache,{'terminalSlots':{'$1:10:11':1}})
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
    def test_historical_completion_and_new_revision(self):
        row={'id':'a','title':'A','rollout_path':'p'};cache={}
        with patch.object(h,'catalog',side_effect=lambda:[dict(row)]),patch.object(h,'panes',return_value=[]),patch.object(h,'rollout_state',return_value=(3,'v1')):
            self.assertEqual(h.status(cache)['tasks'][0]['state'],1)
        with patch.object(h,'catalog',side_effect=lambda:[dict(row)]),patch.object(h,'panes',return_value=[]),patch.object(h,'rollout_state',return_value=(3,'v2')):
            self.assertEqual(h.status(cache)['tasks'][0]['state'],3)
            cache['taskAck']['a']='v1'
            self.assertEqual(h.status(cache)['tasks'][0]['state'],3)
            cache['taskAck']['a']='v2'
            self.assertEqual(h.status(cache)['tasks'][0]['state'],1)
    def test_tmux_failure_keeps_last_inventory(self):
        cache={'lastTerminals':[{'id':'a','state':2,'revision':0}]+[None]*5}
        with patch.object(h,'catalog',return_value=[]),patch.object(h,'panes',side_effect=RuntimeError('timeout')):
            self.assertEqual(h.status(cache)['terminals'][0]['state'],2)

if __name__=='__main__':unittest.main()
