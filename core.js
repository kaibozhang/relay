/* 留白 / Lumen — dependency-free data core. UI must render user strings as text.
 * Limits reject entire operations; imports never truncate or drop user data.
 * IDs/time defaults are the only entropy sources; supplied objects are never mutated.
 */
(function (root, factory) {
  'use strict';
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else if (typeof define === 'function' && define.amd) define([], factory);
  else root.LumenCore = factory();
}(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';
  const LIMIT = { notes: 10000, tasks: 10000, sessions: 50000, tags: 50, title: 300, body: 200000, tag: 300, id: 128 };
  const NOTE = ['id','title','body','tags','pinned','createdAt','updatedAt','deletedAt'];
  const TASK = ['id','title','done','priority','dueDate','createdAt','completedAt'];
  const SESSION = ['id','taskId','taskTitle','minutes','completedAt'];
  const TIMER = ['id','taskId','taskTitle','durationMs','remainingMs','deadline','status','startedAt'];
  let sequence = 0;

  function fail(path, reason) { throw new Error('数据无效：' + path + '，' + reason + '。'); }
  function record(value, fields, path, optional) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) fail(path, '应为对象');
    const prototype = Object.getPrototypeOf(value);
    if (prototype !== Object.prototype && prototype !== null) fail(path, '应为普通数据对象');
    const keys = Reflect.ownKeys(value);
    for (const key of keys) {
      if (typeof key !== 'string' || !fields.includes(key)) fail(path, '包含不支持的字段');
      const descriptor = Object.getOwnPropertyDescriptor(value, key);
      if (!descriptor || !('value' in descriptor) || !descriptor.enumerable) fail(path, '字段必须是普通可枚举数据');
    }
    if (!optional && fields.some(key => !Object.prototype.hasOwnProperty.call(value, key))) fail(path, '缺少必要字段');
    return value;
  }
  function text(value, max, path, nonempty) {
    if (typeof value !== 'string') fail(path, '应为文字');
    if (value.length > max) fail(path, '超过 ' + max + ' 字符上限');
    if (nonempty && !value.trim()) fail(path, '不能为空');
    return value;
  }
  function number(value, min, max, path) {
    if (typeof value !== 'number' || !Number.isFinite(value) || value < min || value > max) fail(path, '应为 ' + min + ' 至 ' + max + ' 之间的有限数字');
    return value;
  }
  function bool(value, path) { if (typeof value !== 'boolean') fail(path, '应为布尔值'); return value; }
  function choice(value, options, path) { if (!options.includes(value)) fail(path, '选项不受支持'); return value; }
  function list(value, max, path, validate) {
    if (!Array.isArray(value) || value.length > max) fail(path, '应为不超过 ' + max + ' 项的数组');
    if (Reflect.ownKeys(value).length !== value.length + 1) fail(path, '数组不能有空项或附加字段');
    const result = [];
    for (let i = 0; i < value.length; i++) {
      const descriptor = Object.getOwnPropertyDescriptor(value, String(i));
      if (!descriptor || !('value' in descriptor)) fail(path, '数组不能有空项或访问器');
      result.push(validate(descriptor.value, path + '[' + i + ']'));
    }
    return result;
  }
  function epoch(now) {
    number(now, -62167219200000, 253402300799999, '时间');
    if (!Number.isInteger(now)) fail('时间', '应为整数毫秒');
    return now;
  }
  function isoNow(now) { return new Date(epoch(now)).toISOString(); }
  function iso(value, path) {
    text(value, 24, path, true);
    const parsed = Date.parse(value);
    if (value.length !== 24 || !Number.isFinite(parsed) || new Date(parsed).toISOString() !== value) fail(path, '应为有效的 ISO 日期时间');
    return value;
  }
  function nullableIso(value, path) { return value === null ? null : iso(value, path); }
  function dueDate(value, path) {
    text(value, 10, path);
    if (value === '') return value;
    if (!/^[0-9]{4}-[0-9]{2}-[0-9]{2}$/.test(value)) fail(path, '应为有效的年月日');
    const parsed = Date.parse(value + 'T00:00:00.000Z');
    if (!Number.isFinite(parsed) || new Date(parsed).toISOString().slice(0,10) !== value) fail(path, '日期不存在');
    return value;
  }
  function identifier(value, path) { return text(value, LIMIT.id, path, true); }
  function taskRef(value, path) { return value === null ? null : identifier(value, path); }
  function tags(value, path) { return list(value, LIMIT.tags, path, (tag, p) => text(tag, LIMIT.tag, p, true)); }
  function noteData(value, path) {
    record(value, NOTE, path);
    return {id:identifier(value.id,path+'.id'),title:text(value.title,LIMIT.title,path+'.title'),body:text(value.body,LIMIT.body,path+'.body'),tags:tags(value.tags,path+'.tags'),pinned:bool(value.pinned,path+'.pinned'),createdAt:iso(value.createdAt,path+'.createdAt'),updatedAt:iso(value.updatedAt,path+'.updatedAt'),deletedAt:nullableIso(value.deletedAt,path+'.deletedAt')};
  }
  function taskData(value, path) {
    record(value, TASK, path);
    const result = {id:identifier(value.id,path+'.id'),title:text(value.title,LIMIT.title,path+'.title',true),done:bool(value.done,path+'.done'),priority:choice(value.priority,['low','normal','high'],path+'.priority'),dueDate:dueDate(value.dueDate,path+'.dueDate'),createdAt:iso(value.createdAt,path+'.createdAt'),completedAt:nullableIso(value.completedAt,path+'.completedAt')};
    if (result.done !== (result.completedAt !== null)) fail(path, '完成状态与完成时间不一致');
    return result;
  }
  function sessionData(value, path) {
    record(value, SESSION, path);
    return {id:identifier(value.id,path+'.id'),taskId:taskRef(value.taskId,path+'.taskId'),taskTitle:text(value.taskTitle,LIMIT.title,path+'.taskTitle'),minutes:number(value.minutes,1,180,path+'.minutes'),completedAt:iso(value.completedAt,path+'.completedAt')};
  }
  function timerData(value, path) {
    record(value, TIMER, path);
    const result = {id:identifier(value.id,path+'.id'),taskId:taskRef(value.taskId,path+'.taskId'),taskTitle:text(value.taskTitle,LIMIT.title,path+'.taskTitle'),durationMs:number(value.durationMs,60000,10800000,path+'.durationMs'),remainingMs:number(value.remainingMs,0,value.durationMs,path+'.remainingMs'),deadline:value.deadline === null ? null : epoch(value.deadline),status:choice(value.status,['running','paused'],path+'.status'),startedAt:iso(value.startedAt,path+'.startedAt')};
    if (!Number.isInteger(result.durationMs) || !Number.isInteger(result.remainingMs)) fail(path, '计时毫秒必须是整数');
    if ((result.status === 'running') !== (result.deadline !== null)) fail(path, '计时状态与截止时间不一致');
    return result;
  }

  function uid() {
    if (typeof globalThis !== 'undefined' && globalThis.crypto && typeof globalThis.crypto.randomUUID === 'function') return globalThis.crypto.randomUUID();
    sequence += 1;
    return 'lumen-' + Date.now().toString(36) + '-' + sequence.toString(36) + '-' + Math.random().toString(36).slice(2,12);
  }
  function todayKey(now = Date.now()) {
    const date = new Date(epoch(now));
    return String(date.getFullYear()).padStart(4,'0') + '-' + String(date.getMonth()+1).padStart(2,'0') + '-' + String(date.getDate()).padStart(2,'0');
  }
  function newNote(input = {}, now = Date.now()) {
    record(input,['title','body','tags'],'笔记',true);
    const at = isoNow(now);
    return noteData({id:uid(),title:input.title === undefined ? '未命名笔记' : input.title,body:input.body === undefined ? '' : input.body,tags:input.tags === undefined ? [] : input.tags,pinned:false,createdAt:at,updatedAt:at,deletedAt:null},'笔记');
  }
  function newTask(input = {}, now = Date.now()) {
    record(input,['title','priority','dueDate'],'任务',true);
    return taskData({id:uid(),title:input.title,done:false,priority:input.priority === undefined ? 'normal' : input.priority,dueDate:input.dueDate === undefined ? '' : input.dueDate,createdAt:isoNow(now),completedAt:null},'任务');
  }
  function createInitialState(now = Date.now()) {
    const notes = [
      {title:'欢迎来到留白',tags:['开始','指南'],body:'# 给想法一个落脚点\n\n留白是只属于你的本地工作台。不必整理好再开始，先写下此刻的想法。\n\n- 用 [[灵感收集箱]] 捕捉转瞬即逝的念头\n- 在 [[每日回顾]] 里看见进展\n- 试试 [[专注的仪式]]，只做一件事\n\n这些是可编辑、可删除的示例笔记。数据保存在当前浏览器，请定期到设置导出备份。'},
      {title:'灵感收集箱',tags:['灵感','示例'],body:'# 先收集，不评判\n\n遇到一个值得留下的问题、一句话、一本书，先写在这里。\n\n- 今天有什么让我好奇？\n- 哪个想法值得再花二十五分钟？\n- 把零散片段连到 [[阅读札记]]\n\n每周挑选一个小想法，放进 [[每日回顾]]。'},
      {title:'每日回顾',tags:['复盘','示例'],body:'# 用三分钟结束一天\n\n## 今天推进了什么\n写下一件具体的小事，不需要宏大的成果。\n\n## 哪件事值得感谢\n留意日常里被忽略的光。\n\n## 明天的第一步\n把下一步写成一个可以行动的任务，再用 [[专注的仪式]] 开始。\n\n没有完成也没关系，回到 [[欢迎来到留白]]，重新留一点空间。'},
      {title:'专注的仪式',tags:['专注','方法'],body:'# 一次只做一件事\n\n1. 选择一个明确的小任务。\n2. 移开手机，准备好水和纸。\n3. 开启二十五分钟专注；需要时可以暂停。\n4. 结束后站起来，记录一个发现。\n\n计时刷新后仍会继续；完成记录只来自真实结束的计时，不预填任何成绩。\n\n结束后，给 [[每日回顾]] 添上一句。'},
      {title:'阅读札记',tags:['阅读','示例'],body:'# 把阅读变成自己的理解\n\n## 一句话概括\n这段内容真正想说什么？\n\n## 与我有什么关系\n用一个自己的例子解释它。\n\n## 一个可以尝试的行动\n行动越小越好，读完就能开始。\n\n把新问题放进 [[灵感收集箱]]，把实践写进 [[每日回顾]]。'}
    ].map(note => newNote(note,now));
    notes[0] = {...notes[0],pinned:true};
    const tasks = [newTask({title:'示例 · 写下今天最重要的一件事',priority:'high'},now),newTask({title:'示例 · 试一次 25 分钟专注',priority:'normal'},now),newTask({title:'示例 · 导出第一份本地备份',priority:'low'},now)];
    return {version:1,notes,tasks,sessions:[],timer:null,settings:{dailyGoal:120},updatedAt:isoNow(now)};
  }
  function extractLinks(body) {
    text(body,LIMIT.body,'笔记正文');
    const links = new Set();
    const pattern = /\[\[([^\[\]\r\n]+)\]\]/g;
    let match;
    while ((match = pattern.exec(body))) { const title = match[1].trim(); if (title) links.add(title); }
    return Array.from(links);
  }
  function getGraph(notes) {
    const nodes = notes.filter(note => note.deletedAt === null);
    const titles = new Map();
    // Resolve duplicate titles to the first active note, matching UI navigation.
    for (const note of nodes) { const title = note.title.trim(); if (!titles.has(title)) titles.set(title,note.id); }
    const edges = [];
    for (const note of nodes) {
      const targets = new Set();
      for (const title of extractLinks(note.body)) {
        const target = titles.get(title);
        if (target !== undefined && target !== note.id && !targets.has(target)) { targets.add(target); edges.push({source:note.id,target}); }
      }
    }
    return {nodes,edges};
  }
  function searchNotes(notes, query) {
    text(query,LIMIT.body,'搜索内容');
    const tokens = query.toLowerCase().trim().split(/\s+/).filter(Boolean);
    return notes.filter(note => {
      if (note.deletedAt !== null) return false;
      const haystack = [note.title,note.body,...note.tags].join('\n').toLowerCase();
      const tagSet = new Set(note.tags.map(tag => tag.toLowerCase()));
      return tokens.every(token => token.startsWith('#') ? tagSet.has(token.slice(1)) : haystack.includes(token));
    });
  }
  function validateState(value) {
    record(value,['version','notes','tasks','sessions','timer','settings','updatedAt'],'工作台');
    if (value.version !== 1) fail('版本','仅支持版本 1');
    record(value.settings,['dailyGoal'],'设置');
    const state = {version:1,notes:list(value.notes,LIMIT.notes,'笔记',noteData),tasks:list(value.tasks,LIMIT.tasks,'任务',taskData),sessions:list(value.sessions,LIMIT.sessions,'专注记录',sessionData),timer:value.timer === null ? null : timerData(value.timer,'计时器'),settings:{dailyGoal:number(value.settings.dailyGoal,1,1440,'每日目标')},updatedAt:iso(value.updatedAt,'更新时间')};
    const ids = new Set();
    for (const entity of [...state.notes,...state.tasks,...state.sessions]) { if (ids.has(entity.id)) fail('标识','存在重复 ID'); ids.add(entity.id); }
    // An already-settled timer can share its session ID: finishTimer clears it idempotently.
    if (state.timer && ids.has(state.timer.id)) {
      const existing = state.sessions.find(session => session.id === state.timer.id);
      if (!existing || existing.taskId !== state.timer.taskId || existing.taskTitle !== state.timer.taskTitle || existing.minutes !== state.timer.durationMs/60000) fail('计时器标识','与其他记录冲突');
    }
    return state;
  }
  function checkBackupSize(value) {
    const max = 20 * 1024 * 1024;
    text(value,max,'备份文件',true);
    let bytes = 0;
    for (let i = 0; i < value.length; i++) {
      const code = value.charCodeAt(i);
      if (code < 128) bytes++;
      else if (code < 2048) bytes += 2;
      else if (code >= 0xD800 && code <= 0xDBFF && i+1 < value.length && value.charCodeAt(i+1) >= 0xDC00 && value.charCodeAt(i+1) <= 0xDFFF) { bytes += 4; i++; }
      else bytes += 3;
      if (bytes > max) fail('备份文件','超过 20 MiB 大小上限');
    }
    return value;
  }
  function importJSON(textValue) {
    checkBackupSize(textValue);
    let value;
    try { value = JSON.parse(textValue); } catch (_) { fail('备份文件','不是有效的 JSON'); }
    // JSON.parse accepts duplicate keys by silently overwriting the earlier value.
    // Scan valid JSON before accepting it so a backup can never lose such data.
    const stack = [];
    for (let i = 0; i < textValue.length; i++) {
      const char = textValue[i];
      if (char === '"') {
        const start = i++;
        while (i < textValue.length && textValue[i] !== '"') {
          if (textValue[i] === '\\') i++;
          i++;
        }
        const frame = stack[stack.length - 1];
        if (frame && frame.keys && frame.keyExpected) {
          const key = JSON.parse(textValue.slice(start,i+1));
          if (frame.keys.has(key)) fail('备份文件','存在重复字段');
          frame.keys.add(key);
          frame.keyExpected = false;
        }
      } else if (char === '{' || char === '[') {
        stack.push({keys:char === '{' ? new Set() : null,keyExpected:true});
        if (stack.length > 16) fail('备份文件','嵌套层级过深');
      } else if (char === '}' || char === ']') stack.pop();
      else if (char === ',' && stack.length) stack[stack.length-1].keyExpected = true;
    }
    return validateState(value);
  }
  function exportJSON(state) { return checkBackupSize(JSON.stringify(validateState(state),null,2)); }
  function startTimer(minutes, task = null, now = Date.now()) {
    number(minutes,1,180,'专注分钟数'); epoch(now);
    const durationMs = Math.round(minutes * 60000);
    return timerData({id:uid(),taskId:task === null ? null : identifier(task.id,'任务标识'),taskTitle:task === null ? '自由专注' : text(task.title,LIMIT.title,'任务标题',true),durationMs,remainingMs:durationMs,deadline:now+durationMs,status:'running',startedAt:isoNow(now)},'计时器');
  }
  function remainingTime(timer, now = Date.now()) {
    epoch(now);
    if (timer === null) return 0;
    const valid = timerData(timer,'计时器');
    return valid.status === 'paused' ? valid.remainingMs : Math.max(0,Math.min(valid.remainingMs,valid.deadline-now));
  }
  function pauseTimer(timer, now = Date.now()) {
    const valid = timerData(timer,'计时器');
    return {...valid,remainingMs:remainingTime(valid,now),deadline:null,status:'paused'};
  }
  function resumeTimer(timer, now = Date.now()) {
    epoch(now); const valid = timerData(timer,'计时器');
    if (valid.status === 'running') return valid;
    return timerData({...valid,status:'running',deadline:now+valid.remainingMs},'计时器');
  }
  function finishTimer(state, now = Date.now()) {
    epoch(now);
    const timer = state.timer;
    if (timer === null || timer.status !== 'running' || remainingTime(timer,now) > 0) return {state,completed:false};
    const existing = state.sessions.some(session => session.id === timer.id);
    if (!existing && state.sessions.length >= LIMIT.sessions) fail('专注记录','已达上限，请先备份，无法新增记录');
    const sessions = existing ? state.sessions : [...state.sessions,{id:timer.id,taskId:timer.taskId,taskTitle:timer.taskTitle,minutes:timer.durationMs/60000,completedAt:isoNow(timer.deadline)}];
    return {state:{...state,sessions,timer:null,updatedAt:isoNow(now)},completed:!existing};
  }
  function stats(state, now = Date.now()) {
    epoch(now);
    const totals = new Map();
    let totalMinutes = 0;
    for (const session of state.sessions) {
      const date = todayKey(Date.parse(session.completedAt));
      totalMinutes += session.minutes; totals.set(date,(totals.get(date) || 0)+session.minutes);
    }
    const today = todayKey(now);
    const week = [];
    for (let offset = 6; offset >= 0; offset--) {
      // Calendar arithmetic at noon avoids DST gaps/repeats around midnight.
      const day = new Date(now); day.setHours(12,0,0,0); day.setDate(day.getDate()-offset);
      const date = todayKey(day.getTime());
      week.push({date,label:['周日','周一','周二','周三','周四','周五','周六'][day.getDay()],minutes:totals.get(date) || 0});
    }
    return {todayMinutes:totals.get(today) || 0,totalMinutes,completedTasks:state.tasks.filter(task=>task.done).length,activeNotes:state.notes.filter(note=>note.deletedAt === null).length,week};
  }
  return Object.freeze({uid,todayKey,createInitialState,newNote,newTask,extractLinks,getGraph,searchNotes,validateState,importJSON,exportJSON,startTimer,remainingTime,pauseTimer,resumeTimer,finishTimer,stats});
}));
