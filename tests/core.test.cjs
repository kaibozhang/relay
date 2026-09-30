'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const C = require('../core.js');
const NOW = Date.parse('2026-07-17T04:30:00.000Z');
const ISO = new Date(NOW).toISOString();
const chinese = /[一-鿿]/;
function empty() { return {version:1,notes:[],tasks:[],sessions:[],timer:null,settings:{dailyGoal:120},updatedAt:ISO}; }
function copy(value) { return JSON.parse(JSON.stringify(value)); }
function freeze(value) { if (value && typeof value === 'object') { Object.freeze(value); Object.values(value).forEach(freeze); } return value; }
function rejects(change) { const state = empty(); change(state); assert.throws(() => C.validateState(state), chinese); }
function session(id,minutes,at) { return {id,taskId:null,taskTitle:'自由专注',minutes,completedAt:new Date(at).toISOString()}; }

test('exact CommonJS API and browser UMD without dependencies', () => {
  const names = ['uid','todayKey','createInitialState','newNote','newTask','extractLinks','getGraph','searchNotes','validateState','importJSON','exportJSON','startTimer','remainingTime','pauseTimer','resumeTimer','finishTimer','stats'];
  assert.deepEqual(Object.keys(C).sort(),names.sort());
  const sandbox = {}; vm.runInNewContext(fs.readFileSync(require.resolve('../core.js'),'utf8'),sandbox);
  assert.equal(typeof sandbox.LumenCore.newNote,'function'); assert.equal(sandbox.LumenCore.createInitialState(NOW).version,1);
});
test('five linked Chinese welcome notes and three explicitly sample tasks, no fake achievements', () => {
  const s = C.createInitialState(NOW);
  assert.equal(s.version,1); assert.equal(s.notes.length,5); assert.equal(s.tasks.length,3);
  assert.deepEqual(s.sessions,[]); assert.equal(s.timer,null); assert.equal(s.settings.dailyGoal,120); assert.equal(s.updatedAt,ISO);
  assert.ok(s.notes.every(n => chinese.test(n.title) && n.tags.length && C.extractLinks(n.body).length));
  assert.ok(s.tasks.every(t => /示例/.test(t.title) && !t.done && t.completedAt === null));
  assert.ok(C.getGraph(s.notes).edges.length >= 4); assert.deepEqual(C.validateState(s),s);
  assert.equal(C.stats(s,NOW).totalMinutes,0); assert.equal(C.stats(s,NOW).completedTasks,0);
});
test('rapid ids stay unique', () => {
  const ids = Array.from({length:10000},()=>C.uid()); assert.equal(new Set(ids).size,ids.length);
  assert.ok(ids.every(id=>typeof id === 'string' && id.length && id.length <= 128));
});
test('creation defaults, exact content, detached tags and timestamps', () => {
  const input = freeze({title:'  想法  ',body:'正文',tags:['工作','灵感']}); const n = C.newNote(input,NOW);
  assert.equal(n.title,input.title); assert.equal(n.body,input.body); assert.deepEqual(n.tags,input.tags); assert.notEqual(n.tags,input.tags);
  assert.equal(n.pinned,false); assert.equal(n.deletedAt,null); assert.equal(n.createdAt,ISO); assert.equal(n.updatedAt,ISO);
  assert.equal(C.newNote({},NOW).title,'未命名笔记'); assert.equal(C.newNote({},NOW).body,'');
  const t = C.newTask({title:'下一步',priority:'high',dueDate:'2028-02-29'},NOW);
  assert.equal(t.done,false); assert.equal(t.priority,'high'); assert.equal(t.dueDate,'2028-02-29'); assert.equal(t.completedAt,null); assert.equal(t.createdAt,ISO);
  assert.equal(C.newTask({title:'默认'},NOW).priority,'normal'); assert.equal(C.newTask({title:'默认'},NOW).dueDate,'');
});
test('creation rejects wrong types and excessive content without coercion or truncation', () => {
  for (const x of [{title:7},{body:null},{tags:'标签'},{tags:[null]},{title:'x'.repeat(301)},{body:'x'.repeat(200001)},{tags:Array(51).fill('x')}]) assert.throws(()=>C.newNote(x,NOW),chinese);
  for (const x of [{title:''},{title:'  '},{title:8},{title:'x',priority:'urgent'},{title:'x',dueDate:'2025-02-29'},{title:'x',dueDate:'2026-04-31'}]) assert.throws(()=>C.newTask(x,NOW),chinese);
  for (const now of [NaN,Infinity,'2026-01-01',null]) assert.throws(()=>C.newNote({},now),chinese);
});
test('immutable CRUD edit, trash, restore and completion survive backup', () => {
  const original = freeze(C.createInitialState(NOW));
  const edited = {...original,notes:original.notes.map((n,i)=>i?n:{...n,title:'修改标题',body:'新正文',pinned:true}),tasks:original.tasks.map((t,i)=>i?t:{...t,done:true,completedAt:ISO})};
  const deleted = {...edited,notes:edited.notes.map((n,i)=>i?n:{...n,deletedAt:ISO})};
  assert.equal(C.searchNotes(deleted.notes,'修改标题').length,0);
  const restored = {...deleted,notes:deleted.notes.map(n=>({...n,deletedAt:null}))};
  assert.equal(C.searchNotes(restored.notes,'修改标题').length,1); assert.deepEqual(C.importJSON(C.exportJSON(restored)),restored);
  assert.notEqual(original.notes[0].title,'修改标题');
});
test('wiki links trim, dedup and ignore empty and malformed syntax', () => {
  assert.deepEqual(C.extractLinks('[[甲]] [[ 乙 ]] [[甲]] [[]] [[   ]] [[丙\n丁]] [普通] [[未闭合'),['甲','乙']);
  assert.deepEqual(C.extractLinks('[[__proto__]] [[constructor]] [[<img onerror=alert(1)>]]'),['__proto__','constructor','<img onerror=alert(1)>']);
});
test('graph excludes deleted, dangling and self links, dedups directed edges', () => {
  const a = {...C.newNote({title:'甲',body:'[[乙]] [[乙]] [[不存在]] [[甲]] [[回收站]]'},NOW),id:'a'};
  const b = {...C.newNote({title:'乙',body:'[[甲]]'},NOW),id:'b'};
  const trash = {...C.newNote({title:'回收站'},NOW),id:'trash',deletedAt:ISO};
  const notes = freeze([a,b,trash]); const g = C.getGraph(notes);
  assert.deepEqual(g.nodes,[a,b]); assert.deepEqual(g.edges,[{source:'a',target:'b'},{source:'b',target:'a'}]); assert.notEqual(g.nodes,notes);
});
test('graph handles prototype-like and duplicate note titles', () => {
  const notes = ['起点','__proto__','__proto__','constructor'].map((title,i)=>({...C.newNote({title,body:i?'':'[[__proto__]] [[constructor]]'},NOW),id:String(i)}));
  assert.deepEqual(C.getGraph(notes).edges,[{source:'0',target:'1'},{source:'0',target:'3'}]);
});
test('search combines case-insensitive text and exact tags with AND tokens', () => {
  const notes = freeze([C.newNote({title:'Lumen 灵感',body:'设计离线工具',tags:['工作','设计']},NOW),C.newNote({title:'生活',body:'散步',tags:['生活']},NOW),{...C.newNote({title:'Lumen 已删除',tags:['工作']},NOW),deletedAt:ISO}]);
  for (const q of ['LUMEN #工作','离线 设计','工作']) assert.deepEqual(C.searchNotes(notes,q),[notes[0]]);
  assert.deepEqual(C.searchNotes(notes,'#生活'),[notes[1]]); assert.deepEqual(C.searchNotes(notes,'#工'),[]);
  assert.deepEqual(C.searchNotes(notes,'lumen 散步'),[]); assert.deepEqual(C.searchNotes(notes,'  '),notes.slice(0,2)); assert.deepEqual(C.searchNotes(notes,'[.*'),[]);
});
test('validation produces detached sanitized data and exact JSON roundtrip', () => {
  const s = freeze(C.createInitialState(NOW)); const sanitized = C.validateState(s);
  assert.deepEqual(sanitized,s); assert.notEqual(sanitized,s); assert.notEqual(sanitized.notes[0],s.notes[0]); assert.notEqual(sanitized.notes[0].tags,s.notes[0].tags);
  assert.deepEqual(C.importJSON(C.exportJSON(s)),s); sanitized.notes[0].tags.push('新标签'); assert.ok(!s.notes[0].tags.includes('新标签'));
});
test('malicious strings remain inert unchanged data', () => {
  const payload = '<script>globalThis.pwned=true</script><img src=x onerror=alert(1)> javascript:alert(1)';
  const s = empty(); s.notes=[C.newNote({title:payload,body:payload,tags:['__proto__',payload]},NOW)];
  const saved = C.importJSON(C.exportJSON(s)); assert.equal(saved.notes[0].body,payload); assert.equal(saved.notes[0].title,payload); assert.equal(globalThis.pwned,undefined);
});
test('malformed roots, versions, unknown/missing fields and prototype keys rejected', () => {
  for (const value of [null,[],true,0,'{}',{},new Date(),{...empty(),version:2},{...empty(),version:'1'}]) assert.throws(()=>C.validateState(value),chinese);
  rejects(s=>delete s.notes); rejects(s=>{s.extra='never discard';}); rejects(s=>{s.settings.extra=1;});
  for (const text of ['{','null',JSON.stringify(empty()).replace('"version":1','"__proto__":{"polluted":true},"version":1')]) assert.throws(()=>C.importJSON(text),chinese);
  assert.equal({}.polluted,undefined); assert.throws(()=>C.importJSON(empty()),chinese);
});
test('strict finite numbers, ISO dates, real calendar dates and enums', () => {
  for (const dailyGoal of [NaN,Infinity,-1,0,'120',null,1441]) rejects(s=>{s.settings.dailyGoal=dailyGoal;});
  for (const at of ['yesterday','2026-02-30T00:00:00.000Z','2026-13-01T00:00:00.000Z','2026-01-01T25:00:00.000Z','2026-01-01',null]) rejects(s=>{s.updatedAt=at;});
  for (const date of ['2026-02-29','2026-04-31','2026-00-01','2026-01-00','26-01-01','2026-1-01','2026-01-01T00:00:00Z']) rejects(s=>{s.tasks=[{...C.newTask({title:'任务'},NOW),dueDate:date}];});
  for (const patch of [{pinned:1},{deletedAt:false},{tags:[2]},{id:''},{title:'x'.repeat(301)},{body:'x'.repeat(200001)},{extra:1}]) rejects(s=>{s.notes=[{...C.newNote({},NOW),...patch}];});
  for (const patch of [{priority:'urgent'},{done:'false'},{done:true,completedAt:null},{done:false,completedAt:ISO}]) rejects(s=>{s.tasks=[{...C.newTask({title:'任务'},NOW),...patch}];});
  for (const minutes of [0,-1,181,NaN,Infinity,'25']) rejects(s=>{s.sessions=[session('s',minutes,NOW)];});
});
test('duplicate ids, oversized/sparse arrays, accessor fields reject explicitly', () => {
  const n = C.newNote({},NOW); rejects(s=>{s.notes=[n,copy(n)];});
  rejects(s=>{s.notes=[n];s.tasks=[{...C.newTask({title:'任务'},NOW),id:n.id}];});
  rejects(s=>{s.sessions=[session('dup',25,NOW),session('dup',25,NOW)];});
  rejects(s=>{s.notes=Array.from({length:10001},(_,i)=>({...n,id:'n'+i}));});
  rejects(s=>{s.tasks=Array(10001).fill(null);}); rejects(s=>{s.sessions=Array(50001).fill(null);});
  rejects(s=>{s.notes=[{...n,tags:Array(51).fill('标签')}];}); rejects(s=>{s.notes=Array(1);});
  const s = empty(); Object.defineProperty(s,'notes',{enumerable:true,get(){throw Error('accessor executed');}}); assert.throws(()=>C.validateState(s),chinese);
});
test('timer bounds, task snapshot, immutable wall-clock countdown and persisted reload', () => {
  const task = freeze(C.newTask({title:'深度阅读'},NOW)); const timer = C.startTimer(25,task,NOW);
  assert.equal(timer.taskId,task.id); assert.equal(timer.taskTitle,task.title); assert.equal(timer.durationMs,1500000); assert.equal(timer.remainingMs,1500000); assert.equal(timer.deadline,NOW+1500000); assert.equal(timer.status,'running'); assert.equal(timer.startedAt,ISO);
  assert.equal(C.remainingTime(freeze(timer),NOW+10000),1490000); assert.equal(C.remainingTime(timer,NOW+2000000),0); assert.equal(C.remainingTime(timer,NOW-10000),1500000);
  const loaded = C.importJSON(C.exportJSON({...empty(),tasks:[task],timer})); assert.equal(C.remainingTime(loaded.timer,NOW+60000),1440000);
  assert.equal(C.remainingTime(null,NOW),0); assert.equal(C.startTimer(1.5,null,NOW).durationMs,90000);
  for (const minutes of [0,0.5,181,NaN,Infinity,'25',null]) assert.throws(()=>C.startTimer(minutes,null,NOW),chinese);
});
test('pause freezes duration, resume creates new deadline without mutating original', () => {
  const timer = freeze(C.startTimer(1,null,NOW)); const paused = freeze(C.pauseTimer(timer,NOW+20000));
  assert.equal(timer.status,'running'); assert.equal(paused.status,'paused'); assert.equal(paused.deadline,null); assert.equal(paused.remainingMs,40000);
  assert.equal(C.remainingTime(paused,NOW+10000000),40000); assert.deepEqual(C.pauseTimer(paused,NOW+50000),paused);
  const resumed = C.resumeTimer(paused,NOW+50000); assert.equal(resumed.status,'running'); assert.equal(resumed.deadline,NOW+90000); assert.equal(resumed.id,timer.id); assert.equal(resumed.startedAt,timer.startedAt);
  assert.deepEqual(C.resumeTimer(resumed,NOW+60000),resumed); assert.deepEqual(C.importJSON(C.exportJSON({...empty(),timer:paused})).timer,paused);
});
test('completion only handles expired running timers once, records actual deadline', () => {
  const task = C.newTask({title:'任务快照'},NOW); const timer = C.startTimer(25,task,NOW); const original = freeze({...empty(),timer});
  assert.equal(C.finishTimer(original,NOW+1499999).completed,false); const result = C.finishTimer(original,NOW+1800000);
  assert.equal(result.completed,true); assert.equal(result.state.timer,null); assert.equal(result.state.sessions.length,1);
  assert.deepEqual(result.state.sessions[0],{id:timer.id,taskId:task.id,taskTitle:'任务快照',minutes:25,completedAt:new Date(timer.deadline).toISOString()});
  assert.equal(result.state.updatedAt,new Date(NOW+1800000).toISOString()); assert.equal(original.sessions.length,0); assert.equal(original.timer,timer);
  assert.deepEqual(C.finishTimer(result.state,NOW+2000000),{state:result.state,completed:false});
  const again = C.finishTimer({...result.state,timer},NOW+2000000); assert.equal(again.completed,false); assert.equal(again.state.timer,null); assert.equal(again.state.sessions.length,1);
  assert.equal(C.finishTimer({...empty(),timer:C.pauseTimer(timer,NOW+1000)},NOW+99999999).completed,false);
});
test('invalid persisted timer combinations reject', () => {
  const timer = C.startTimer(25,null,NOW);
  for (const patch of [{durationMs:0},{durationMs:Infinity},{remainingMs:-1},{remainingMs:1500001},{status:'done'},{deadline:null},{deadline:NaN},{deadline:'123'},{status:'paused',deadline:NOW},{taskId:4}]) rejects(s=>{s.timer={...timer,...patch};});
});
test('stats count real data by local dates instead of rolling 24-hour buckets', () => {
  const now = new Date(2026,6,17,0,30).getTime(); const previous = new Date(2026,6,16,23,59).getTime(); const s = empty();
  s.notes=[C.newNote({},NOW),{...C.newNote({},NOW),deletedAt:ISO}]; s.tasks=[{...C.newTask({title:'完成'},NOW),done:true,completedAt:ISO},C.newTask({title:'未完成'},NOW)];
  s.sessions=[session('today',25,now),session('previous',45,previous),session('old',60,new Date(2026,6,1,12).getTime())];
  const r = C.stats(freeze(s),now); assert.equal(r.todayMinutes,25); assert.equal(r.totalMinutes,130); assert.equal(r.activeNotes,1); assert.equal(r.completedTasks,1);
  assert.deepEqual(r.week.map(d=>d.date),['2026-07-11','2026-07-12','2026-07-13','2026-07-14','2026-07-15','2026-07-16','2026-07-17']); assert.deepEqual(r.week.map(d=>d.minutes),[0,0,0,0,0,45,25]); assert.ok(r.week.every(d=>typeof d.label === 'string' && d.label.length)); assert.equal(C.todayKey(now),'2026-07-17');
});
test('local week crosses DST, new year and leap day without skipping dates', () => {
  // Run suite in UTC, Asia/Shanghai and America/New_York.
  for (const [now,expected] of [[new Date(2026,2,10,0,15).getTime(),['2026-03-04','2026-03-05','2026-03-06','2026-03-07','2026-03-08','2026-03-09','2026-03-10']],[new Date(2026,0,2,0,15).getTime(),['2025-12-27','2025-12-28','2025-12-29','2025-12-30','2025-12-31','2026-01-01','2026-01-02']],[new Date(2028,2,1,0,15).getTime(),['2028-02-24','2028-02-25','2028-02-26','2028-02-27','2028-02-28','2028-02-29','2028-03-01']]]) assert.deepEqual(C.stats(empty(),now).week.map(d=>d.date),expected);
});

test('empty editor note is valid and duplicate tags retain exact data', () => {
  const note = C.newNote({title:'',body:'',tags:['重复','重复']},NOW);
  assert.deepEqual(C.validateState({...empty(),notes:[note]}).notes[0],note);
  assert.equal(C.createInitialState(NOW).notes[0].body.includes(String.fromCharCode(92)+'n'),false);
  assert.equal(C.createInitialState(NOW).notes[0].body.includes(String.fromCharCode(10)),true);
});
test('JSON duplicate keys and escaped duplicate keys are rejected without losing data', () => {
  const base = JSON.stringify(empty());
  for (const text of [base.replace('"version":1','"version":2,"version":1'),base.replace('"version":1','"version":1,"\\u0076ersion":1'),base.replace('"dailyGoal":120','"dailyGoal":5,"dailyGoal":120')]) assert.throws(()=>C.importJSON(text),chinese);
  const n = C.newNote({title:'引号 " { } ,',body:'字面数据："version":1,"version":2'},NOW);
  assert.deepEqual(C.importJSON(C.exportJSON({...empty(),notes:[n]})).notes[0],n);
});
test('import rejects nonfinite JSON numbers and excessive nesting', () => {
  const base = JSON.stringify(empty());
  assert.throws(()=>C.importJSON(base.replace('"dailyGoal":120','"dailyGoal":1e999')),chinese);
  assert.throws(()=>C.importJSON('['.repeat(30)+'0'+']'.repeat(30)),chinese);
});
test('timer finishes at exact deadline; paused zero cannot settle until resumed', () => {
  const timer = C.startTimer(1,null,NOW); const state = freeze({...empty(),timer});
  assert.equal(C.finishTimer(state,NOW+60000).completed,true);
  const paused = C.pauseTimer(timer,NOW+60000);
  assert.equal(C.finishTimer({...empty(),timer:paused},NOW+70000).completed,false);
  const resumed = C.resumeTimer(paused,NOW+70000);
  assert.equal(C.finishTimer({...empty(),timer:resumed},NOW+70000).completed,true);
  for (const patch of [{durationMs:60000.5},{remainingMs:0.5}]) rejects(s=>{s.timer={...timer,...patch};});
});
test('completion preserves deleted task snapshot and session-capacity failure preserves timer', () => {
  const task = C.newTask({title:'以后会删除的任务'},NOW); const timer = C.startTimer(1,task,NOW);
  const s = freeze({...empty(),timer}); const result = C.finishTimer(s,NOW+60000);
  assert.equal(result.state.sessions[0].taskId,task.id); assert.deepEqual(C.validateState(result.state),result.state);
  const full = freeze({...empty(),timer,sessions:Array.from({length:50000},(_,i)=>session('s'+i,1,NOW))});
  assert.throws(()=>C.finishTimer(full,NOW+60000),chinese); assert.equal(full.timer,timer); assert.equal(full.sessions.length,50000);
});
test('imported timer id may match its exact settled session but not unrelated records', () => {
  const timer = C.startTimer(1,null,NOW); const finished = C.finishTimer({...empty(),timer},NOW+60000).state;
  assert.doesNotThrow(()=>C.validateState({...finished,timer}));
  assert.throws(()=>C.validateState({...finished,timer:{...timer,taskTitle:'其他专注'}}),chinese);
  assert.throws(()=>C.validateState({...empty(),timer,notes:[{...C.newNote({},NOW),id:timer.id}]}),chinese);
});

test('duplicate titles resolve deterministically without quadratic edge growth', () => {
  const base = C.newNote({title:'相同标题',body:'[[相同标题]]'},NOW);
  const notes = Array.from({length:10000},(_,i)=>({...base,id:'n'+i}));
  const graph = C.getGraph(notes);
  assert.equal(graph.edges.length,9999); assert.equal(graph.edges[0].target,'n0');
  assert.equal(C.getGraph([{...notes[0],deletedAt:ISO},...notes.slice(1)]).edges[0].target,'n1');
});
test('import and export enforce the same 20 MiB UTF8 byte ceiling', () => {
  const s = empty(); const note = C.newNote({body:'中'.repeat(200000)},NOW);
  s.notes=Array.from({length:35},(_,i)=>({...note,id:'n'+i}));
  assert.doesNotThrow(()=>C.validateState(s));
  assert.throws(()=>C.exportJSON(s),/20 MiB/);
  assert.throws(()=>C.importJSON(JSON.stringify(s)),/20 MiB/);
  s.notes=s.notes.slice(0,2); s.notes[1]={...s.notes[1],body:'😀'.repeat(100000)};
  assert.deepEqual(C.importJSON(C.exportJSON(s)),s);
});

