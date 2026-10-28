"""Exercise the release CLI through pipes: no .NET or Lua process in a match."""
import hashlib
import json
from pathlib import Path
import subprocess
import uuid

root=Path(__file__).resolve().parent
cards=json.loads((root.parent/'CardExamples/pack-lua.json').read_text(encoding='utf-8-sig'))['cards']
setup=json.loads((root.parent/'CardExamples/match.json').read_text(encoding='utf-8-sig'))
exe=root/'out/headless-sim.exe'

def state_hash(state):
    return hashlib.sha256(json.dumps(state,ensure_ascii=False,sort_keys=True,separators=(',',':')).encode()).hexdigest()

def run():
    with subprocess.Popen([str(exe)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding='utf-8') as process:
        def call(op,**fields):
            process.stdin.write(json.dumps({'op':op,'cards':cards,**fields})+'\n');process.stdin.flush()
            line=process.stdout.readline()
            if not line:raise RuntimeError('Core closed its output: '+process.stderr.read())
            response=json.loads(line)
            if not response['ok']:raise RuntimeError(response['error'])
            return response['result']
        state=call('new',setup=setup)
        steps=[]
        while state['outcome'] is None and len(steps)<2000:
            command=call('think',state=state)
            state=call('apply',state=state,command=command,expected_revision=state['revision'])
            steps.append((command,state_hash(state)))
        assert state['outcome'] is not None,'Standalone match failed to finish'
        replay=call('new',setup=setup)
        for command,expected in steps:
            replay=call('apply',state=replay,command=command,expected_revision=replay['revision'])
            assert state_hash(replay)==expected,'Standalone replay diverged'
        process.stdin.close()
        assert process.wait(timeout=10)==0
        report={'standalone':True,'dotnet_during_match':False,'lua_during_match':False,'actions':len(steps),'outcome':state['outcome'],'replay_verified':True,'final_hash':state_hash(state)}
        path=root.parent/'HeadlessKards/artifacts/standalone-core-report.json'
        path.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
        print(json.dumps(report))

run()

artifacts=root.parent/'HeadlessKards/artifacts/author-rejections'
artifacts.mkdir(parents=True,exist_ok=True)
cases={
    'infinite_loop':'while true do end',
    'ambient_random':'return math.random()',
    'unknown_operation':'''return {version=1,id="invalid",cards={{id="bad",title="bad",kind="infantry",cost=1,operation_cost=1,attack=1,defense=1,blitz=false,effects={{on="deploy",effects={{op="does_not_exist"}}}}}}}'''
}
rejected=[]
for name,source in cases.items():
    file=artifacts/(name+'.lua')
    target=artifacts/(name+'-'+uuid.uuid4().hex+'.json')
    file.write_text(source,encoding='utf-8')
    result=subprocess.run([str(root/'out/card-lua.exe'),str(file),str(target)],capture_output=True,text=True,encoding='utf-8',timeout=10)
    assert result.returncode!=0 and not target.exists(),f'{name} unexpectedly exported a pack'
    rejected.append(name)
report={'rejected':rejected,'partial_output_created':False}
(artifacts.parent/'author-rejection-report.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report))
