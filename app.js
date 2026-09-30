/* 留白 / Lumen — no network, no build step, your space. */
(function () {
  'use strict';
  const C = window.LumenCore;
  const $ = (q, root = document) => root.querySelector(q);
  const $$ = (q, root = document) => [...root.querySelectorAll(q)];
  const esc = value => String(value ?? '').replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
  const icons = {
    home:'<path d="m3 10 9-7 9 7v10H3Z"/><path d="M9 20v-7h6v7"/>',
    note:'<path d="M5 3h11l3 3v15H5Z"/><path d="M15 3v5h4M8 12h8M8 16h6"/>',
    task:'<rect x="3" y="3" width="18" height="18" rx="4"/><path d="m7 12 3 3 7-7"/>',
    graph:'<circle cx="12" cy="5" r="3"/><circle cx="5" cy="18" r="3"/><circle cx="19" cy="17" r="3"/><path d="m10.5 8-4 7m7-7 4 6M8 18h8"/>',
    focus:'<circle cx="12" cy="13" r="8"/><path d="M12 9v5l3 2M9 2h6M12 2v3"/>',
    settings:'<path d="M4 6h16M4 12h16M4 18h16"/><circle cx="9" cy="6" r="2"/><circle cx="15" cy="12" r="2"/><circle cx="8" cy="18" r="2"/>',
    plus:'<path d="M12 5v14M5 12h14"/>',
    arrow:'<path d="M5 12h14m-5-5 5 5-5 5"/>',
    pin:'<path d="m9 3 6 0-1 6 4 4H6l4-4ZM12 13v8"/>',
    trash:'<path d="M3 6h18M9 6V3h6v3M6 6l1 15h10l1-15M10 10v7M14 10v7"/>',
    play:'<path d="m8 4 12 8-12 8Z"/>',
    pause:'<path d="M8 4v16M16 4v16"/>',
    check:'<path d="m5 12 4 4L19 6"/>',
    download:'<path d="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5"/>',
    search:'<circle cx="10" cy="10" r="6"/><path d="m15 15 6 6"/>',
    leaf:'<path d="M20 3C4 1 1 12 7 17c7 7 15-2 13-14ZM5 21 16 9"/>',
    link:'<path d="m10 8 3-3a4 4 0 0 1 6 6l-3 3M14 16l-3 3a4 4 0 0 1-6-6l3-3m1 6 6-6"/>',
    close:'<path d="m6 6 12 12M6 18 18 6"/>',
  };
  const icon = name => '<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'+(icons[name] || icons.note)+'</svg>';
  const KEY = 'lumen.space.v1';
  let state, storageHealthy = true, rawRecovery = null, blockedSave = false;
  let lastSaved = null, saveTimeout, lastBackup = null;
  let view = 'home', selectedNoteId = null, notesFilter = 'all', notesQuery = '', taskFilter = 'open', graphQuery = '', preview = false;
  let focusDuration = 25, focusTaskId = '', focusImmersive = false;
  let commandIndex = 0, commandActions = [], confirmCallback = null, timerErrorId = null;
  let captureDraft = '';
  const routes = {home:['今日','home','TODAY'],notes:['笔记','note','NOTES'],tasks:['任务','task','TASKS'],graph:['星图','graph','CONSTELLATION'],focus:['专注','focus','FOCUS'],settings:['空间设置','settings','YOUR SPACE']};

  function warnStorage(message) {
    storageHealthy = false;
    $('#storage-warning').hidden = false;
    $('#storage-warning').textContent = message;
    $('#storage-status').textContent = '尚未安全保存 · 请导出备份';
    $('.storage-indicator').classList.add('has-error');
  }
  function loadState() {
    try {
      const raw = localStorage.getItem(KEY);
      lastSaved = raw;
      if (raw) return C.importJSON(raw);
      return C.createInitialState();
    } catch (error) {
      try { rawRecovery = localStorage.getItem(KEY); } catch (_) { /* blocked storage */ }
      blockedSave = !!rawRecovery;
      warnStorage(rawRecovery ? '检测到无法读取的历史数据，已保护原始内容，不会覆盖。请到「空间设置」下载原始数据，再导入有效备份。当前修改只保存在内存。' : '浏览器限制了本地存储。当前内容只在内存中，请在关闭前导出备份；建议使用普通模式的 Edge 或 Chrome。');
      return C.createInitialState();
    }
  }
  function persist() {
    clearTimeout(saveTimeout);
    saveTimeout = null;
    state.updatedAt = new Date().toISOString();
    if (blockedSave) { updateSaveLabel('仅内存 · 请备份'); return false; }
    try {
      const current = localStorage.getItem(KEY);
      if (current !== lastSaved) {
        blockedSave = true;
        warnStorage('另一个标签页修改了这个空间。为避免覆盖，本页已暂停保存；请先导出本页备份，再刷新查看最新内容。');
        updateSaveLabel('有冲突 · 请备份');
        return false;
      }
      const raw = C.exportJSON(state);
      localStorage.setItem(KEY, raw);
      lastSaved = raw;
      storageHealthy = true;
      $('#storage-warning').hidden = true;
      $('#storage-status').textContent = '已保存在此浏览器';
      $('.storage-indicator').classList.remove('has-error');
      updateSaveLabel('已保存到本地');
      return true;
    } catch (error) {
      warnStorage('未能保存到浏览器：可能是存储已满或隐私限制。内容仍在本页，请立即导出备份，避免关闭后丢失。');
      updateSaveLabel('保存失败 · 请备份');
      return false;
    }
  }
  function scheduleSave() { updateSaveLabel('正在保存…'); clearTimeout(saveTimeout); saveTimeout = setTimeout(persist, 400); }
  function updateSaveLabel(text) { const el = $('#editor-save-status'); if (el) el.textContent = text; }
  function toast(message, kind = '') {
    const node = document.createElement('div'); node.className = 'toast ' + kind; node.textContent = message;
    $('#toast-container').append(node); setTimeout(() => node.remove(), 4500);
  }
  function confirmAction(title, message, callback, label = '确认') {
    confirmCallback = callback;
    $('#confirm-title').textContent = title; $('#confirm-message').textContent = message; $('#confirm-ok').textContent = label;
    $('#confirm-dialog').showModal(); $('#confirm-cancel').focus();
  }
  function dateShort(value) { return new Date(value).toLocaleDateString('zh-CN', {month:'numeric',day:'numeric'}); }
  function cleanExcerpt(body, length = 84) { return String(body).replace(/[#*>\x60]/g,'').replace(/\[\[([^\]]+)\]\]/g,'$1').replace(/\s+/g,' ').trim().slice(0,length); }
  function activeNotes() { return state.notes.filter(n => !n.deletedAt); }
  function sortedNotes(notes) { return [...notes].sort((a,b) => Number(b.pinned)-Number(a.pinned) || b.updatedAt.localeCompare(a.updatedAt)); }
  function uniqueTitle(base) { let title=base.slice(0,300),n=2;while(activeNotes().some(note=>note.title===title)){const suffix=' '+n++;title=base.slice(0,300-suffix.length)+suffix;}return title; }
  function pageHeading(title, sub, actions = '', eyebrow = '') { return '<div class="page-heading"><div>'+(eyebrow ? '<p class="eyebrow">'+eyebrow+'</p>' : '')+'<h1 class="page-title">'+title+'</h1><p class="page-subtitle">'+sub+'</p></div><div class="heading-actions">'+actions+'</div></div>'; }
  function emptyState(title, sub, action = '') { return '<div class="empty-state"><div class="empty-icon">'+icon('leaf')+'</div><h3>'+title+'</h3><p>'+sub+'</p>'+action+'</div>'; }
  function navButton(target, label, extra = '') { return '<button class="button ghost small" data-action="navigate" data-view="'+target+'">'+label+extra+'</button>'; }
  function formatTags(tags) { return tags.some(t=>/[,，]/.test(t)||t.trim()!==t||t.startsWith('['))||new Set(tags).size!==tags.length?JSON.stringify(tags):tags.join(', '); }
  function tagsHTML(tags) { return tags.map(t => '<span class="tag">'+esc(t)+'</span>').join(''); }
  function updateNav() {
    $('#main-nav').innerHTML = Object.entries(routes).filter(([key]) => key !== 'settings').map(([key,[label,glyph]]) => '<button class="nav-item '+(view===key?'active':'')+'" data-action="navigate" data-view="'+key+'" '+(view===key?'aria-current="page"':'')+'>'+icon(glyph)+'<span>'+label+'</span>'+(key==='notes'?'<span class="nav-count">'+activeNotes().length+'</span>':key==='tasks'?'<span class="nav-count">'+state.tasks.filter(t=>!t.done).length+'</span>':'')+'</button>').join('');
    $('#settings-nav').innerHTML = icon('settings')+'<span>空间设置</span>';
    $('#settings-nav').classList.toggle('active',view==='settings');
    $('#breadcrumb-current').textContent = routes[view][0];
    $('#header-date').textContent = new Date().toLocaleDateString('zh-CN',{month:'long',day:'numeric',weekday:'long'});
  }
  function navigate(target, id) {
    if (!Object.hasOwn(routes,target)) target = 'home';
    if (saveTimeout) persist();
    view = target;
    if (id) selectedNoteId = id;
    closeSidebar();
    if ($('#command-dialog').open) $('#command-dialog').close();
    const hash = '#'+view+(view==='notes'&&selectedNoteId?'/'+encodeURIComponent(selectedNoteId):'');
    if (location.hash !== hash) history.replaceState(null,'',hash);
    render();
    $('#view').focus({preventScroll:true});
    window.scrollTo({top:0,behavior:'instant'});
  }
  function render() {
    updateNav();
    document.body.classList.toggle('is-immersive', view==='focus' && focusImmersive);
    const renderers = {home:renderHome,notes:renderNotes,tasks:renderTasks,graph:renderGraph,focus:renderFocus,settings:renderSettings};
    $('#view').innerHTML = renderers[view]();
    if(view==='home'&&$('#capture-input'))$('#capture-input').value=captureDraft;
    updateTimerDisplay();
    if(view==='graph') drawGraph();
    document.title = (state.timer ? formatTime(C.remainingTime(state.timer))+' · ' : '') + routes[view][0] + ' · 留白 Lumen';
  }
  function taskRow(task, compact = false) {
    const late = task.dueDate && task.dueDate < C.todayKey() && !task.done;
    return '<div class="task-row '+(task.done?'done':'')+'" data-task-id="'+esc(task.id)+'"><button class="task-check" data-action="toggle-task" data-id="'+esc(task.id)+'" aria-label="'+(task.done?'重新打开':'完成')+'：'+esc(task.title)+'" aria-pressed="'+task.done+'">'+(task.done?icon('check'):'')+'</button><div class="task-content"><span class="task-title">'+esc(task.title)+'</span><div class="task-meta"><span class="priority-dot priority-'+task.priority+'" title="'+({high:'高优先级',normal:'普通优先级',low:'低优先级'}[task.priority])+'"></span>'+(task.dueDate?'<span class="'+(late?'overdue':'')+'">'+(late?'已逾期 · ':'')+esc(task.dueDate)+'</span>':'<span>随时开始</span>')+'</div></div><div class="task-actions">'+(!task.done?'<button class="button ghost icon-button small" data-action="focus-task" data-id="'+esc(task.id)+'" title="专注这个任务" aria-label="专注：'+esc(task.title)+'">'+icon('play')+'</button>':'')+(!compact?'<button class="button ghost icon-button small" data-action="delete-task" data-id="'+esc(task.id)+'" title="删除任务" aria-label="删除：'+esc(task.title)+'">'+icon('trash')+'</button>':'')+'</div></div>';
  }
  function noteCard(note) { return '<button class="note-card" data-action="open-note" data-id="'+esc(note.id)+'"><div class="note-card-top">'+icon('note')+(note.pinned?'<span class="pin-indicator">'+icon('pin')+'</span>':'')+'</div><h3 class="note-card-title">'+esc(note.title || '无标题笔记')+'</h3><p class="note-excerpt">'+esc(cleanExcerpt(note.body))+'</p><div class="note-card-footer"><span class="note-tags">'+tagsHTML(note.tags.slice(0,2))+'</span><time>'+dateShort(note.updatedAt)+'</time></div></button>'; }
  function renderHome() {
    const stats = C.stats(state), hour = new Date().getHours();
    const greeting = hour<6?'夜深了':hour<12?'上午好':hour<18?'下午好':'晚上好';
    const due = state.tasks.filter(t=>!t.done).sort((a,b)=>(a.dueDate||'9999').localeCompare(b.dueDate||'9999') || ({high:0,normal:1,low:2}[a.priority]-{high:0,normal:1,low:2}[b.priority])).slice(0,4);
    const recent = sortedNotes(activeNotes()).slice(0,3);
    const max = Math.max(30,...stats.week.map(d=>d.minutes));
    return pageHeading(greeting+'，给今天一点留白。','收集微小的灵感，把注意力还给真正重要的事。','<button class="button primary" data-action="new-note">'+icon('plus')+' 记一笔</button>','A LITTLE SPACE, A LITTLE CLARITY')+
    '<div class="dashboard-grid"><section class="hero-card"><div class="hero-copy"><span class="eyebrow">YOUR MIND, A LITTLE LIGHTER</span><h2 class="hero-title">想法有处安放，<br>日子就有了方向。</h2><p class="hero-desc">这里不追逐更多，只留下值得的。<br>从一个想法、一件小事、一段专注开始。</p><button class="button secondary" data-action="navigate" data-view="graph">看看我的星图 '+icon('arrow')+'</button></div><div class="hero-art" aria-hidden="true"><span class="art-orbit orbit-one"></span><span class="art-orbit orbit-two"></span><span class="art-stem"></span><span class="art-leaf leaf-one"></span><span class="art-leaf leaf-two"></span><span class="art-leaf leaf-three"></span><span class="art-sun"></span><span class="art-caption">LET YOUR IDEAS GROW</span></div></section>'+
    '<div class="stat-strip"><div class="stat-item"><span class="stat-value">'+stats.activeNotes+'<small> 篇</small></span><span class="stat-label">积累的想法</span></div><div class="stat-item"><span class="stat-value">'+state.tasks.filter(t=>!t.done).length+'<small> 件</small></span><span class="stat-label">等待的小事</span></div><div class="stat-item"><span class="stat-value">'+stats.todayMinutes+'<small> 分钟</small></span><span class="stat-label">今日专注</span></div><div class="stat-item"><span class="stat-value">'+C.getGraph(state.notes).edges.length+'<small> 条</small></span><span class="stat-label">想法的连接</span></div></div>'+
    '<section class="panel capture-panel"><div class="panel-header"><h2 class="panel-title">此刻，在想什么？</h2><span class="muted">QUICK CAPTURE</span></div><form class="capture-form" id="capture-form"><textarea id="capture-input" maxlength="200000" rows="3" placeholder="一个念头、一段摘录，或还没想明白的事…" aria-label="快速记录想法"></textarea><div class="capture-footer"><span class="muted">不用整理，先记下来。 <kbd class="kbd">Ctrl ↵</kbd></span><button class="button primary small" type="submit">收进笔记 '+icon('arrow')+'</button></div></form></section>'+
    '<section class="focus-card"><div class="focus-orbit" aria-hidden="true"></div><div class="focus-card-top"><span class="eyebrow">ONE THING AT A TIME</span>'+icon('focus')+'</div><h2 class="focus-label">'+(state.timer?'正在为自己留出时间':'世界很吵，先专注一会儿。')+'</h2><div class="focus-time" data-timer-display>'+formatTime(state.timer?C.remainingTime(state.timer):25*60000)+'</div><div class="focus-controls"><span class="focus-caption">'+(state.timer?esc(state.timer.taskTitle||'自由专注'):'25 分钟，只做一件事')+'</span><button class="button light small" data-action="'+(state.timer?'navigate':'quick-focus')+'" data-view="focus">'+icon('play')+(state.timer?'回到专注':'开始专注')+'</button></div></section>'+
    '<section class="panel"><div class="panel-header"><h2 class="panel-title">接下来，做点什么</h2>'+navButton('tasks','全部任务 '+icon('arrow'))+'</div><div class="task-list">'+(due.length?due.map(t=>taskRow(t,true)).join(''):emptyState('轻装上阵','没有未完成的任务。给自己留一点空白。'))+'</div><button class="button ghost small panel-add" data-action="navigate" data-view="tasks">'+icon('plus')+' 添加一件小事</button></section>'+
    '<section class="panel rhythm-panel"><div class="panel-header"><h2 class="panel-title">自己的节奏</h2><span class="muted">最近 7 天</span></div><div class="rhythm-summary"><strong>'+stats.week.reduce((s,d)=>s+d.minutes,0)+'</strong><span> 分钟的认真，都会留下痕迹。</span></div><div class="week-chart">'+stats.week.map(d=>'<div class="chart-column" title="'+esc(d.date)+'：'+d.minutes+' 分钟"><span class="chart-value">'+(d.minutes||'')+'</span><div class="chart-track"><div class="chart-bar '+(d.date===C.todayKey()?'today':'')+'" style="height:'+Math.max(d.minutes?6:2,d.minutes/max*100)+'%"></div></div><span class="chart-label">'+(d.date===C.todayKey()?'今天':esc(d.label))+'</span></div>').join('')+'</div></section></div>'+
    '<section class="recent-section"><div class="section-heading"><div><p class="eyebrow">LITTLE THINGS, KEPT</p><h2>最近留下的想法</h2></div>'+navButton('notes','打开笔记库 '+icon('arrow'))+'</div><div class="note-grid">'+(recent.length?recent.map(noteCard).join(''):emptyState('第一篇笔记，从这里开始','随手记下一个想法，之后再慢慢连接。'))+'</div></section>';
  }

  function renderNotes() {
    let notes = notesFilter==='trash'?state.notes.filter(n=>n.deletedAt):C.searchNotes(state.notes,notesQuery);
    if(notesFilter==='pinned') notes = notes.filter(n=>n.pinned);
    if(notesFilter==='trash'&&notesQuery) notes=notes.filter(n=>(n.title+' '+n.body+' '+n.tags.join(' ')).toLowerCase().includes(notesQuery.toLowerCase()));
    notes = sortedNotes(notes);
    if(!notes.some(n=>n.id===selectedNoteId)) selectedNoteId = notes[0]?.id || null;
    const selected = notes.find(n=>n.id===selectedNoteId);
    return pageHeading('让想法，有处安放。','不必一次写好。值得留下的，慢慢生长。','<button class="button primary" data-action="new-note">'+icon('plus')+' 新建笔记</button>','YOUR NOTES')+
    '<div class="notes-layout"><aside class="notes-sidebar"><div class="notes-toolbar"><label class="search-field">'+icon('search')+'<input class="search-input" id="notes-search" value="'+esc(notesQuery)+'" placeholder="搜索笔记或 #标签" aria-label="搜索笔记"></label><div class="segmented">'+[['all','全部'],['pinned','置顶'],['trash','回收站']].map(([id,label])=>'<button class="filter-chip '+(notesFilter===id?'active':'')+'" data-action="notes-filter" data-filter="'+id+'">'+label+'</button>').join('')+'</div><span class="muted notes-count">'+notes.length+' 篇笔记</span></div><div class="note-list">'+notes.map(n=>'<button class="note-list-item '+(n.id===selectedNoteId?'active':'')+'" data-action="select-note" data-id="'+esc(n.id)+'"><span class="note-list-title">'+(n.pinned?'⌖ ':'')+esc(n.title||'无标题笔记')+'</span><span class="note-list-excerpt">'+esc(cleanExcerpt(n.body,65)||'还没有正文')+'</span><span class="note-list-date">'+dateShort(n.updatedAt)+' '+esc(n.tags.slice(0,2).map(t=>'#'+t).join(' '))+'</span></button>').join('')+(notes.length?'':'<div class="empty-list">这里还很安静。<br>试试别的关键词，或记下新想法。</div>')+'</div></aside><section class="note-editor" id="note-editor">'+(selected?editorHTML(selected):emptyState(notesFilter==='trash'?'回收站是空的':'留一页，给下一个想法。',notesQuery?'没有匹配的笔记。':'点击「新建笔记」开始记录。'))+'</section></div>';
  }
  function editorHTML(note) {
    const incoming=new Set(C.getGraph(state.notes).edges.filter(edge=>edge.target===note.id).map(edge=>edge.source));
    const backlinks = activeNotes().filter(n=>incoming.has(n.id));
    if(note.deletedAt) return '<div class="editor-toolbar"><span class="muted">已移入回收站 · '+dateShort(note.deletedAt)+'</span><div><button class="button secondary small" data-action="restore-note" data-id="'+esc(note.id)+'">恢复笔记</button> <button class="button danger small" data-action="purge-note" data-id="'+esc(note.id)+'">永久删除</button></div></div><h2 class="trashed-title">'+esc(note.title)+'</h2><div class="markdown-preview">'+markdown(note.body)+'</div>';
    return '<div class="editor-toolbar"><span id="editor-save-status" class="muted">'+(storageHealthy?'已保存到本地':'仅内存 · 请备份')+'</span><div class="editor-actions"><button class="button ghost small '+(note.pinned?'is-active':'')+'" data-action="pin-note" data-id="'+esc(note.id)+'" title="'+(note.pinned?'取消置顶':'置顶笔记')+'" aria-label="'+(note.pinned?'取消置顶':'置顶笔记')+'">'+icon('pin')+'</button><button class="button ghost small" data-action="note-preview">'+(preview?'返回编辑':'阅读预览')+'</button><button class="button ghost small" data-action="export-note" title="导出 Markdown" aria-label="导出 Markdown">'+icon('download')+'</button><button class="button ghost small" data-action="trash-note" data-id="'+esc(note.id)+'" title="移入回收站" aria-label="移入回收站">'+icon('trash')+'</button></div></div><input class="editor-title" id="note-title" maxlength="300" aria-label="笔记标题" placeholder="无标题笔记" value="'+esc(note.title)+'" '+(preview?'readonly':'')+'><div class="editor-meta"><span>'+icon('note')+' <span id="word-count">'+note.body.length+'</span> 字符</span><span>'+icon('link')+' [[标题]] 连接想法</span></div><label class="editor-tags-label"><span>标签</span><input class="tag-input" id="note-tags" aria-label="笔记标签，可输入 JSON 数组或逗号分隔文字" maxlength="20000" placeholder="逗号分隔，或 JSON 数组" value="'+esc(formatTags(note.tags))+'" '+(preview?'readonly':'')+'></label>'+(preview?'<article class="markdown-preview">'+markdown(note.body)+'</article>':'<div class="editor-body"><textarea id="note-body" aria-label="笔记正文" maxlength="200000" spellcheck="false" placeholder="在这里开始。不必完美，也不必完整。&#10;&#10;支持 Markdown，用 [[另一篇笔记的标题]] 把想法连接起来。">'+esc(note.body)+'</textarea></div>')+'<div class="backlinks"><div class="backlinks-heading">'+icon('link')+' 被这些想法提及 <span class="badge">'+backlinks.length+'</span></div><div class="backlink-items">'+(backlinks.length?backlinks.map(n=>'<button class="button ghost small" data-action="open-note" data-id="'+esc(n.id)+'">'+esc(n.title)+' ↗</button>').join(''):'<span class="muted">在其他笔记中写下 [['+esc(note.title)+']]，连接就会出现。</span>')+'</div></div>';
  }

  // Deliberately limited Markdown: escaped input, no HTML or remote images.
  function inlineMarkdown(text) {
    const tokens = [];
    const token = html => { const i=tokens.push(html)-1; return '\u0000'+i+'\u0000'; };
    let value = String(text).replace(/\u0000/g,'').replace(/\x60([^\x60]+)\x60/g,(_,code)=>token('<code>'+esc(code)+'</code>'));
    value = value.replace(/\[\[([^\]\n]+)\]\]/g,(_,title)=>token('<button class="wiki-link" data-action="wiki-link" data-title="'+esc(title.trim())+'">'+esc(title.trim())+'</button>'));
    value = value.replace(/!?\[([^\]\n]+)\]\(([^)\s]+)\)/g,(whole,label,url)=> {
      if(whole.startsWith('!')) return token('<span class="muted">[图片：'+esc(label)+'，本地预览不加载远程图片]</span>');
      let safe = false; try { safe = ['http:','https:','mailto:'].includes(new URL(url).protocol); } catch(_) { /* text only */ }
      return token(safe?'<a href="'+esc(url)+'" target="_blank" rel="noopener noreferrer">'+esc(label)+'</a>':esc(label)+' <span class="muted">（不支持的链接）</span>');
    });
    value = esc(value).replace(/\*\*([^*]+)\*\*/g,'<strong>$1</strong>').replace(/\*([^*]+)\*/g,'<em>$1</em>');
    return value.replace(/\u0000(\d+)\u0000/g,(_,i)=>tokens[Number(i)]||'');
  }
  function markdown(body) {
    let result='', inCode=false, code=[], list=null;
    const closeList=()=>{if(list){result+='</'+list+'>';list=null;}};
    for(const line of String(body).split(/\r?\n/)) {
      if(/^\s*\x60\x60\x60/.test(line)){closeList();if(inCode){result+='<pre><code>'+esc(code.join('\n'))+'</code></pre>';code=[];}inCode=!inCode;continue;}
      if(inCode){code.push(line);continue;}
      let match;
      if((match=line.match(/^(#{1,4})\s+(.+)$/))){closeList();const n=match[1].length+1;result+='<h'+n+'>'+inlineMarkdown(match[2])+'</h'+n+'>';}
      else if((match=line.match(/^\s*([-*]|\d+\.)\s+(.+)$/))){const type=/\d/.test(match[1])?'ol':'ul';if(list!==type){closeList();list=type;result+='<'+type+'>';}result+='<li>'+inlineMarkdown(match[2])+'</li>';}
      else if((match=line.match(/^>\s?(.*)$/))){closeList();result+='<blockquote>'+inlineMarkdown(match[1])+'</blockquote>';}
      else if(/^\s*---+\s*$/.test(line)){closeList();result+='<hr>';}
      else if(line.trim()){closeList();result+='<p>'+inlineMarkdown(line)+'</p>';}
      else closeList();
    }
    closeList();if(inCode)result+='<pre><code>'+esc(code.join('\n'))+'</code></pre>';
    return result||'<p class="muted">这一页还没有文字。</p>';
  }

  function renderTasks() {
    let tasks = state.tasks.filter(t=>taskFilter==='all'||(taskFilter==='done'?t.done:taskFilter==='today'?!t.done&&t.dueDate&&t.dueDate<=C.todayKey():!t.done));
    tasks.sort((a,b)=>Number(a.done)-Number(b.done)||({high:0,normal:1,low:2}[a.priority]-{high:0,normal:1,low:2}[b.priority])||(a.dueDate||'9999').localeCompare(b.dueDate||'9999'));
    return pageHeading('一件一件，慢慢来。','把想做的事放在这里，让大脑轻一点。','','MAKE ROOM FOR WHAT MATTERS')+
    '<section class="panel task-entry-panel"><form class="task-entry-form" id="task-form"><input class="input task-title-input" name="title" required maxlength="300" placeholder="下一件值得做的小事…" aria-label="新任务名称"><label class="compact-field"><span>计划日期</span><input class="input" type="date" name="dueDate" aria-label="计划日期"></label><label class="compact-field"><span>优先级</span><select class="select" name="priority" aria-label="优先级"><option value="normal">普通</option><option value="high">重要</option><option value="low">不着急</option></select></label><button class="button primary" type="submit">'+icon('plus')+' 添加任务</button></form></section>'+
    '<div class="filter-bar">'+[['open','待完成'],['today','今日 / 逾期'],['done','已完成'],['all','全部']].map(([id,label])=>'<button class="filter-chip '+(taskFilter===id?'active':'')+'" data-action="task-filter" data-filter="'+id+'">'+label+'</button>').join('')+'<span class="filter-count">'+tasks.length+' 件小事</span></div><section class="panel task-section"><div class="task-list">'+(tasks.length?tasks.map(t=>taskRow(t)).join(''):emptyState(taskFilter==='done'?'每一次完成，都值得记住。':'这里没有待办，只有可能。',taskFilter==='today'?'今天没有到期或逾期任务。':'用上面的输入框，给下一步一个名字。'))+'</div></section><p class="task-footnote">'+icon('leaf')+' 完成不是为了填满清单，而是腾出空间。</p>';
  }

  function renderGraph() {
    const graph = C.getGraph(state.notes);
    return pageHeading('每个想法，都不是孤岛。','用 [[笔记标题]] 连接笔记。散落的片段，也会长成自己的星座。','<button class="button secondary" data-action="new-note">'+icon('plus')+' 添一颗星</button>','YOUR CONSTELLATION')+
    '<div class="graph-layout"><section class="graph-stage"><div class="graph-stage-top"><label class="search-field">'+icon('search')+'<input class="search-input" id="graph-search" value="'+esc(graphQuery)+'" placeholder="寻找一颗星…" aria-label="搜索星图"></label><span class="graph-live"><span class="dot"></span> '+graph.nodes.length+' 个想法 · '+graph.edges.length+' 条连接</span></div><svg id="graph-svg" viewBox="0 0 800 560" role="group" aria-label="笔记链接星图，可用 Tab 选择笔记，Enter 打开"></svg><div class="graph-legend"><span><i class="legend-dot"></i> 一个想法</span><span><i class="legend-line"></i> 一次连接</span><span>点击星星，回到笔记 ↗</span></div></section><aside class="graph-sidebar"><span class="eyebrow">CONNECT THE DOTS</span><h2>连接，比收集<br>更有力量。</h2><p>当两个想法相遇，新的东西就开始生长。</p><div class="graph-instruction"><span>01</span><div><strong>写下一个想法</strong><p>从一篇短短的笔记开始。</p></div></div><div class="graph-instruction"><span>02</span><div><strong>把它与另一篇相连</strong><p>在正文输入 <code>[[标题]]</code>，标题需与另一篇笔记一致。</p></div></div><div class="graph-instruction"><span>03</span><div><strong>回来看一看</strong><p>笔记之间的线，就是你的思考轨迹。</p></div></div><div class="graph-suggestion"><span class="muted">还未连接的想法</span><div id="unlinked-notes"></div></div></aside></div>';
  }
  function drawGraph() {
    const graph = C.getGraph(state.notes), svg = $('#graph-svg'); if(!svg)return;
    const allNodes = sortedNotes(graph.nodes);
    const query = graphQuery.trim().toLowerCase();
    const matched = query ? allNodes.filter(n=>(n.title+' '+n.tags.join(' ')).toLowerCase().includes(query)) : allNodes;
    const nodes = matched.slice(0,90);
    if(!nodes.length){svg.innerHTML='<text x="400" y="270" text-anchor="middle" class="graph-empty">'+(query?'没有找到这颗星，试试别的名字。':'第一颗星，等你点亮。')+'</text>';}
    else {
      const ids = new Set(nodes.map(n=>n.id)), edges=graph.edges.filter(e=>ids.has(e.source)&&ids.has(e.target));
      const degree = new Map(nodes.map(n=>[n.id,0]));edges.forEach(e=>{degree.set(e.source,degree.get(e.source)+1);degree.set(e.target,degree.get(e.target)+1);});
      const positions = nodes.map((n,i)=>{const angle=i*2.39996-.8;const radius=nodes.length===1?0:Math.sqrt((i+.8)/nodes.length)*215;return {n,x:400+Math.cos(angle)*radius*1.25,y:280+Math.sin(angle)*radius};});
      // Deterministic relaxation, only on view entry. No animation loop or hidden CPU work.
      for(let step=0;step<90;step++){
        for(let i=0;i<positions.length;i++)for(let j=i+1;j<positions.length;j++){const a=positions[i],b=positions[j],dx=a.x-b.x,dy=a.y-b.y,dist=Math.max(1,Math.hypot(dx,dy)),force=Math.min(5,1500/(dist*dist));a.x+=dx/dist*force;b.x-=dx/dist*force;a.y+=dy/dist*force;b.y-=dy/dist*force;}
        const map=new Map(positions.map(p=>[p.n.id,p]));
        edges.forEach(e=>{const a=map.get(e.source),b=map.get(e.target),dx=b.x-a.x,dy=b.y-a.y,dist=Math.max(1,Math.hypot(dx,dy)),f=(dist-145)*.008;a.x+=dx/dist*f;b.x-=dx/dist*f;a.y+=dy/dist*f;b.y-=dy/dist*f;});
        positions.forEach(p=>{p.x=Math.max(90,Math.min(710,p.x+(400-p.x)*.002));p.y=Math.max(80,Math.min(465,p.y+(270-p.y)*.002));});
      }
      const map=new Map(positions.map(p=>[p.n.id,p]));
      svg.innerHTML='<defs><radialGradient id="star-glow"><stop offset="0%" stop-color="#d6dfcc" stop-opacity=".65"/><stop offset="100%" stop-color="#d6dfcc" stop-opacity="0"/></radialGradient></defs><circle cx="400" cy="280" r="210" fill="url(#star-glow)"/><circle cx="400" cy="280" r="220" class="graph-guide"/><circle cx="400" cy="280" r="130" class="graph-guide"/>'+edges.map(e=>{const a=map.get(e.source),b=map.get(e.target);return '<line class="graph-edge" x1="'+a.x+'" y1="'+a.y+'" x2="'+b.x+'" y2="'+b.y+'"/>';}).join('')+positions.map((p,i)=>'<g class="graph-node '+(degree.get(p.n.id)>1?'connected':'')+'" transform="translate('+p.x+','+p.y+')" tabindex="0" role="button" aria-label="打开笔记：'+esc(p.n.title)+'" data-action="open-note" data-id="'+esc(p.n.id)+'"><title>'+esc(p.n.title)+' · '+degree.get(p.n.id)+' 条连接</title><circle class="node-halo" r="'+(23+Math.min(10,degree.get(p.n.id)*2))+'"/><circle class="node-dot" r="'+(7+Math.min(6,degree.get(p.n.id)*1.2))+'"/><text text-anchor="middle" y="38">'+esc(p.n.title.length>16?p.n.title.slice(0,15)+'…':p.n.title)+'</text></g>').join('');
      if(matched.length>90)svg.insertAdjacentHTML('beforeend','<text x="400" y="548" text-anchor="middle" class="graph-empty">为保持流畅，仅显示前 90 篇；可搜索其他笔记。</text>');
    }
    const connected=new Set(graph.edges.flatMap(e=>[e.source,e.target]));
    $('#unlinked-notes').innerHTML=allNodes.filter(n=>!connected.has(n.id)).slice(0,5).map(n=>'<button class="button ghost small" data-action="open-note" data-id="'+esc(n.id)+'">'+esc(n.title)+' ↗</button>').join('')||'<p class="muted">'+(allNodes.length?'每个想法都找到了连接。':'开始记录，就会有星光。')+'</p>';
  }

  function formatTime(ms) { const seconds=Math.max(0,Math.ceil(ms/1000));return String(Math.floor(seconds/60)).padStart(2,'0')+':'+String(seconds%60).padStart(2,'0'); }
  function renderFocus() {
    const timer=state.timer, stats=C.stats(state), duration=timer?timer.durationMs/60000:focusDuration;
    return pageHeading('把这一刻，还给自己。','不用同时抵达所有地方。现在，只做一件事。','<button class="button ghost" data-action="immersive">'+(focusImmersive?'退出沉浸 ↗':'进入沉浸 ↗')+'</button>','LESS, BUT BETTER')+
    '<div class="focus-layout"><section class="focus-main"><div class="timer-mode"><span class="dot"></span><span id="timer-state-label">'+(timer?(timer.status==='paused'?'暂停一下，也没关系':'专注进行中'):'准备好，慢慢开始')+'</span></div><div class="timer-ring"><svg viewBox="0 0 320 320" aria-hidden="true"><circle class="timer-track" cx="160" cy="160" r="146"/><circle class="timer-progress" id="timer-progress" cx="160" cy="160" r="146"/></svg><div class="timer-display"><span class="timer-digits" data-timer-display>'+formatTime(timer?C.remainingTime(timer):duration*60000)+'</span><span class="timer-description">'+(timer?esc(timer.taskTitle||'自由专注'):'一小段不被打扰的时间')+'</span></div></div>'+
    '<div class="duration-options">'+[25,45,60].map(m=>'<button class="duration-option '+(duration===m?'active':'')+'" data-action="duration" data-minutes="'+m+'" '+(timer?'disabled':'')+'>'+m+' 分钟</button>').join('')+'<label class="custom-duration"><input id="custom-duration" type="number" min="1" max="180" step="1" value="'+duration+'" aria-label="自定义专注分钟" '+(timer?'disabled':'')+'> 分钟</label></div><label class="focus-task-label" for="focus-task">这段时间，留给</label><select class="select focus-task-select" id="focus-task" '+(timer?'disabled':'')+'><option value="">自由专注 · 不绑定任务</option>'+state.tasks.filter(t=>!t.done||timer?.taskId===t.id).map(t=>'<option value="'+esc(t.id)+'" '+((timer?timer.taskId:focusTaskId)===t.id?'selected':'')+'>'+esc(t.title)+'</option>').join('')+'</select><div class="focus-main-controls">'+(timer?'<button class="button primary" data-action="toggle-timer">'+icon(timer.status==='running'?'pause':'play')+(timer.status==='running'?'暂停一下':'继续专注')+'</button><button class="button ghost" data-action="stop-timer">结束本次</button>':'<button class="button primary" data-action="start-focus">'+icon('play')+' 开始这一段</button>')+'</div><p class="focus-note">'+(timer?'刷新不会丢失进度。浏览器关闭期间仍会计时，回来时自动结算。':'完成的专注会记下来。中途结束不计入统计。')+'</p></section><aside class="focus-history"><section class="panel"><div class="panel-header"><h2 class="panel-title">今日的小小积累</h2>'+icon('leaf')+'</div><div class="focus-today"><strong>'+stats.todayMinutes+'</strong><span> / '+state.settings.dailyGoal+' 分钟</span><div class="goal-track"><div style="width:'+Math.min(100,stats.todayMinutes/state.settings.dailyGoal*100)+'%"></div></div><p class="muted">'+(stats.todayMinutes>=state.settings.dailyGoal?'今天的目标完成了，给自己一点掌声。':'不必与别人比较，找到自己的节奏。')+'</p></div></section><section class="panel"><div class="panel-header"><h2 class="panel-title">留下的专注</h2><span class="muted">最近 12 次</span></div><div class="session-list">'+(state.sessions.length?[...state.sessions].sort((a,b)=>b.completedAt.localeCompare(a.completedAt)).slice(0,12).map(s=>'<div class="session-row"><span class="session-icon">'+icon('check')+'</span><div><strong>'+esc(s.taskTitle||'自由专注')+'</strong><span>'+dateShort(s.completedAt)+' · '+new Date(s.completedAt).toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit'})+'</span></div><span class="session-minutes">'+s.minutes+' min</span></div>').join(''):emptyState('第一段专注，等你开始。','从 25 分钟开始，留下一点认真。'))+'</div></section></aside></div>';
  }
  function updateTimerDisplay() {
    const ms = state.timer?C.remainingTime(state.timer):((view==='focus'?focusDuration:25)*60000);
    $$('[data-timer-display]').forEach(el=>el.textContent=formatTime(ms));
    const ring=$('#timer-progress');if(ring){const perimeter=2*Math.PI*146;ring.style.strokeDasharray=String(perimeter);ring.style.strokeDashoffset=String(perimeter*(1-(state.timer?ms/state.timer.durationMs:1)));}
    if(state.timer)document.title=formatTime(ms)+' · '+(state.timer.status==='paused'?'已暂停':'专注中')+' · 留白';
  }
  function tick() {
    if(!state.timer||timerErrorId===state.timer.id)return;
    let result;
    try{result=C.finishTimer(state);if(result.state!==state)C.exportJSON(result.state);}catch(error){timerErrorId=state.timer.id;toast('专注记录未能结算：'+error.message+' 请先备份，可手动结束本次计时。','error');return;}
    if(result.state!==state){
      state=result.state;persist();
      if(result.completed)toast('这一段专注完成了。辛苦了，起来走一走吧。','success');
      if(view==='focus')render();else if(view==='home'){const area=$('#capture-input'),editing=document.activeElement===area;const selection=editing?[area.selectionStart,area.selectionEnd]:null;render();if(editing){$('#capture-input').focus();$('#capture-input').setSelectionRange(...selection);}}
      else{updateNav();document.title=routes[view][0]+' · 留白 Lumen';}
      return;
    }
    updateTimerDisplay();
  }
  function startFocus(minutes,taskId) {
    if(state.timer){navigate('focus');toast('已经有一段专注在进行，先照顾好这一件事。');return;}
    try {if(state.sessions.length>=50000)throw Error('专注记录已达上限，请先导出备份并整理空间。');const timer=C.startTimer(minutes,state.tasks.find(t=>t.id===taskId)||null);C.exportJSON({...state,timer});state.timer=timer;timerErrorId=null;focusDuration=minutes;persist();navigate('focus');toast('这一段时间，只属于你。');}catch(error){toast(error.message,'error');}
  }

  function renderSettings() {
    const bytes=new Blob([JSON.stringify(state)]).size;
    return pageHeading('这是你的空间。','不需要账号，不上传内容。保留多少，带走什么，都由你决定。','','LOCAL FIRST, ALWAYS YOURS')+
    '<div class="settings-layout"><section class="settings-card"><span class="eyebrow">01 / SAFE KEEPING</span><h2>把重要的东西，握在自己手里。</h2><p>内容自动保存在当前浏览器中，不是直接写进这个文件夹。换浏览器、移动文件夹或清理缓存前，请先导出备份。</p><div class="data-summary"><div><strong>'+activeNotes().length+'</strong><span>篇笔记</span></div><div><strong>'+state.tasks.length+'</strong><span>件任务</span></div><div><strong>'+state.sessions.length+'</strong><span>段专注</span></div><div><strong>'+(bytes/1024).toFixed(1)+'</strong><span>KB 数据</span></div></div><div class="data-actions"><button class="button primary" data-action="export">'+icon('download')+' 导出完整备份</button><button class="button secondary" data-action="import">导入备份</button><button class="button ghost" data-action="export-markdown">导出全部 Markdown</button></div><p class="muted">'+(lastBackup?'本次会话上次发起备份：'+new Date(lastBackup).toLocaleTimeString('zh-CN'):'建议每周备份一次。导出会下载到浏览器的默认下载位置。')+'</p>'+(rawRecovery?'<div class="notice warning">原始损坏数据仍受保护。<button class="button secondary small" data-action="recovery">下载原始数据</button></div>':'')+'</section>'+
    '<section class="settings-card"><span class="eyebrow">02 / YOUR RHYTHM</span><h2>自己的节奏，自己设定。</h2><form id="settings-form" class="setting-row"><label for="daily-goal"><strong>每日专注目标</strong><p class="muted">它只是一个方向，不是需要赶上的进度。</p></label><div class="goal-input-group"><input class="input" type="number" id="daily-goal" min="1" max="1440" step="1" required value="'+state.settings.dailyGoal+'" aria-label="每日专注目标分钟"><span>分钟</span><button class="button secondary small" type="submit">保存</button></div></form></section>'+
    '<section class="settings-card"><span class="eyebrow">03 / A FEW SMALL THINGS</span><h2>关于留白</h2><div class="about-grid"><div><h3>本地，且离线</h3><p>无追踪、无远程字体、无第三方请求。除了你主动打开的外部链接，内容不会发送到网络。</p></div><div><h3>轻巧，不锁住你</h3><p>笔记能导出成 Markdown，完整空间能导出为 JSON。没有账号，也没有订阅。</p></div><div><h3>键盘也很顺手</h3><p><kbd class="kbd">Ctrl / ⌘ K</kbd> 搜索与跳转<br><kbd class="kbd">Ctrl / ⌘ Enter</kbd> 快速收进笔记<br><kbd class="kbd">Esc</kbd> 关闭弹窗</p></div><div><h3>一些诚实的边界</h3><p>不支持云同步或文件附件；隐私模式可能不持久保存。浏览器关闭时不会发系统提醒，但回来会结算计时。</p></div></div></section><section class="settings-card quiet-card"><span class="eyebrow">A FRESH PAGE</span><h2>如果你想，从空白开始。</h2><p>欢迎笔记和示例任务也属于你，可以逐项删除。也可以清空空间，留下干净的一页。</p><button class="button danger" data-action="reset">清空整个空间…</button></section></div>';
  }

  function download(content, filename, type) {
    const url=URL.createObjectURL(new Blob([content],{type}));const a=document.createElement('a');a.href=url;a.download=filename;document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),30000);
  }
  function exportBackup() { if(saveTimeout)persist();download(C.exportJSON(state),'留白-完整备份-'+C.todayKey()+'.json','application/json;charset=utf-8');lastBackup=Date.now();toast('已发起备份下载，请确认文件已保存。');if(view==='settings')render(); }
  function exportNote(note) {download('# '+note.title+'\n\n'+(note.tags.length?note.tags.map(t=>'#'+t).join(' ')+'\n\n':'')+note.body,('笔记-'+(note.title||'无标题').replace(/[<>:"/\\|?*\u0000-\x1f]/g,'_').replace(/[. ]+$/g,'').slice(0,80))+'.md','text/markdown;charset=utf-8');}
  function addEntity(kind, entity) { const candidate={...state,[kind]:[entity,...state[kind]]};C.exportJSON(candidate);state=candidate; }
  function createNote(title='',body='',tags=[]) {const note=C.newNote({title:uniqueTitle(title||'新的想法'),body,tags});addEntity('notes',note);notesFilter='all';notesQuery='';preview=false;selectedNoteId=note.id;persist();navigate('notes',note.id);$('#note-title')?.focus();$('#note-title')?.select();return note;}
  function patchNoteFromEditor(field) {
    const note=state.notes.find(n=>n.id===selectedNoteId);if(!note||note.deletedAt||preview)return;
    const next={...note};
    if(field==='note-title')next.title=$('#note-title').value;
    if(field==='note-body')next.body=$('#note-body').value;
    if(field==='note-tags'){
      const input=$('#note-tags').value;
      try{next.tags=input.trim().startsWith('[')?JSON.parse(input):input.split(/[,，]/).map(t=>t.trim()).filter(Boolean);}catch(_){updateSaveLabel('标签 JSON 格式未完成');return;}
    }
    next.updatedAt=new Date().toISOString();
    try{C.exportJSON({...state,notes:state.notes.map(n=>n.id===note.id?next:n)});}catch(error){updateSaveLabel('未保存：'+error.message);return;}
    Object.assign(note,next);
    $('#word-count').textContent=String(note.body.length);
    const item=$$('.note-list-item').find(el=>el.dataset.id===note.id);if(item){$('.note-list-title',item).textContent=(note.pinned?'⌖ ':'')+(note.title||'无标题笔记');$('.note-list-excerpt',item).textContent=cleanExcerpt(note.body,65)||'还没有正文';}
    scheduleSave();
  }
  function openNote(id) {notesFilter='all';notesQuery='';preview=false;navigate('notes',id);}
  function showCommand() {commandIndex=0;$('#command-input').value='';renderCommands('');$('#command-dialog').showModal();$('#command-input').focus();}
  function renderCommands(query) {
    const q=query.trim().toLowerCase();
    const commands=Object.entries(routes).map(([id,[name,glyph]])=>({label:'前往'+name,detail:'页面',glyph,run:()=>navigate(id)}));
    commands.unshift({label:'新建笔记',detail:'开始一个想法',glyph:'plus',run:()=>createNote()});
    commandActions=commands.filter(c=>(c.label+c.detail).toLowerCase().includes(q));
    if(q){commandActions.push(...C.searchNotes(state.notes,q).slice(0,8).map(n=>({label:n.title||'无标题笔记',detail:'笔记 · '+cleanExcerpt(n.body,44),glyph:'note',run:()=>openNote(n.id)})));commandActions.push(...state.tasks.filter(t=>t.title.toLowerCase().includes(q)).slice(0,5).map(t=>({label:t.title,detail:t.done?'已完成的任务':'任务 · 开始专注',glyph:'task',run:()=>{focusTaskId=t.id;navigate(t.done?'tasks':'focus');}})));}
    commandIndex=Math.min(commandIndex,Math.max(0,commandActions.length-1));
    $('#command-results').innerHTML=commandActions.map((c,i)=>'<button class="command-result '+(i===commandIndex?'selected':'')+'" data-action="command-run" data-index="'+i+'">'+icon(c.glyph)+'<span><strong>'+esc(c.label)+'</strong><small>'+esc(c.detail)+'</small></span><span>↵</span></button>').join('')||'<div class="empty-list">没有找到。试试更短的关键词。</div>';
  }
  function closeSidebar() {const hadFocus=$('#sidebar').contains(document.activeElement);$('#sidebar').classList.remove('open');$('.main-area').inert=false;$('#sidebar-scrim').hidden=true;$('#mobile-toggle').setAttribute('aria-expanded','false');if(hadFocus&&matchMedia('(max-width:760px)').matches)$('#mobile-toggle').focus();}

  const actions = {
    navigate:el=>navigate(el.dataset.view),
    command:showCommand,
    'close-dialog':()=>$('#command-dialog').close(),
    'command-run':el=>{const command=commandActions[Number(el.dataset.index)];$('#command-dialog').close();command?.run();},
    'new-note':()=>createNote(),
    'open-note':el=>openNote(el.dataset.id),
    'select-note':el=>{if(saveTimeout)persist();selectedNoteId=el.dataset.id;render();},
    'notes-filter':el=>{notesFilter=el.dataset.filter;selectedNoteId=null;render();},
    'note-preview':()=>{if(saveTimeout)persist();preview=!preview;render();},
    'pin-note':el=>{const n=state.notes.find(n=>n.id===el.dataset.id);if(n){n.pinned=!n.pinned;persist();render();}},
    'trash-note':el=>{const n=state.notes.find(n=>n.id===el.dataset.id);if(n){n.deletedAt=new Date().toISOString();persist();selectedNoteId=null;render();toast('已移入回收站，可以随时恢复。');}},
    'restore-note':el=>{const n=state.notes.find(n=>n.id===el.dataset.id);if(n){n.deletedAt=null;persist();render();toast('想法已经回来了。');}},
    'purge-note':el=>confirmAction('永久删除这篇笔记？','这个操作无法撤销。你也可以先恢复笔记，再导出它。',()=>{state.notes=state.notes.filter(n=>n.id!==el.dataset.id);persist();render();toast('笔记已永久删除。');},'永久删除'),
    'export-note':()=>{const n=state.notes.find(n=>n.id===selectedNoteId);if(n)exportNote(n);},
    'wiki-link':el=>{const title=el.dataset.title;const note=activeNotes().find(n=>n.title.trim()===title.trim());if(note)openNote(note.id);else confirmAction('这个想法还没有自己的页面','新建「'+title+'」并把它留在空间里？',()=>createNote(title),'新建笔记');},
    'toggle-task':el=>{const t=state.tasks.find(t=>t.id===el.dataset.id);if(t){t.done=!t.done;t.completedAt=t.done?new Date().toISOString():null;persist();render();if(t.done)toast('又完成了一件小事。很好。');}},
    'delete-task':el=>{const t=state.tasks.find(t=>t.id===el.dataset.id);if(t)confirmAction('删除这件小事？','「'+t.title+'」将被删除；已经留下的专注记录仍然保留。',()=>{state.tasks=state.tasks.filter(t=>t.id!==el.dataset.id);persist();render();},'删除任务');},
    'task-filter':el=>{taskFilter=el.dataset.filter;render();},
    'focus-task':el=>{focusTaskId=el.dataset.id;navigate('focus');if(state.timer)toast('当前已有专注，结束后可切换任务。');},
    'quick-focus':()=>startFocus(25,null),
    'duration':el=>{if(!state.timer){focusDuration=Number(el.dataset.minutes);render();}},
    'start-focus':()=>startFocus(Number($('#custom-duration')?.value||focusDuration),$('#focus-task')?.value||focusTaskId),
    'toggle-timer':()=>{tick();if(state.timer){state.timer=state.timer.status==='running'?C.pauseTimer(state.timer):C.resumeTimer(state.timer);persist();render();}},
    'stop-timer':()=>confirmAction('结束这一段专注？','提前结束不会计入专注记录。休息一下也很好，下次再继续。',()=>{state.timer=null;persist();render();toast('先休息一下，随时可以回来。');},'结束本次'),
    immersive:()=>{focusImmersive=!focusImmersive;render();},
    export:exportBackup,
    import:()=>{$('#import-file').value='';$('#import-file').click();},
    'export-markdown':()=>{const notes=sortedNotes(activeNotes());if(!notes.length){toast('还没有可导出的笔记。');return;}download(notes.map(n=>'# '+n.title+'\n\n'+(n.tags.length?n.tags.map(t=>'#'+t).join(' ')+'\n\n':'')+n.body).join('\n\n---\n\n'),'留白-全部笔记-'+C.todayKey()+'.md','text/markdown;charset=utf-8');toast('已发起 Markdown 下载。');},
    recovery:()=>{if(rawRecovery)download(rawRecovery,'留白-原始数据-'+C.todayKey()+'.txt','text/plain;charset=utf-8');},
    reset:()=>confirmAction('给空间换一张白纸？','这会清空所有笔记、回收站、任务、专注记录及进行中的计时。无法撤销，请先导出完整备份。',()=>{state={version:1,notes:[],tasks:[],sessions:[],timer:null,settings:{dailyGoal:120},updatedAt:new Date().toISOString()};selectedNoteId=null;persist();render();toast(storageHealthy?'空间已清空。下一页，由你来写。':'已清空本页，但未能写入存储。');},'确认清空'),
  };

  document.addEventListener('click',event=>{const el=event.target.closest('[data-action]');if(!el||el.disabled)return;const action=actions[el.dataset.action];if(action){event.preventDefault();try{action(el);}catch(error){console.error(error);toast('这一步没有完成：'+error.message,'error');}}});
  document.addEventListener('keydown',event=>{
    if(event.isComposing||event.keyCode===229)return;
    if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='k'){event.preventDefault();if(!$('#confirm-dialog').open)showCommand();}
    if((event.ctrlKey||event.metaKey)&&event.key==='Enter'&&event.target.id==='capture-input'){event.preventDefault();$('#capture-form').requestSubmit();}
    if((event.key==='Enter'||event.key===' ')&&event.target.matches('.graph-node')){event.preventDefault();openNote(event.target.dataset.id);}
    if(event.key==='Tab'&&$('#sidebar').classList.contains('open')){const items=$$('a[href],button:not([disabled])',$('#sidebar'));const first=items[0],last=items.at(-1);if(event.shiftKey&&document.activeElement===first){event.preventDefault();last.focus();}else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first.focus();}}
    if(event.key==='Escape'){closeSidebar();if(focusImmersive){focusImmersive=false;render();}}
    if($('#command-dialog').open&&event.key==='Enter'&&event.target.matches('button')){event.preventDefault();event.target.click();return;}
    if($('#command-dialog').open&&event.target.id==='command-input'&&['ArrowDown','ArrowUp','Enter'].includes(event.key)){
      event.preventDefault();if(event.key==='Enter'){const c=commandActions[commandIndex];if(c){$('#command-dialog').close();c.run();}}else {commandIndex=(commandIndex+(event.key==='ArrowDown'?1:-1)+Math.max(1,commandActions.length))%Math.max(1,commandActions.length);$$('.command-result').forEach((el,i)=>el.classList.toggle('selected',i===commandIndex));$$('.command-result')[commandIndex]?.scrollIntoView({block:'nearest'});}
    }
  });
  document.addEventListener('input',event=>{
    const id=event.target.id;
    if(['note-title','note-body','note-tags'].includes(id))patchNoteFromEditor(id);
    if(id==='capture-input')captureDraft=event.target.value;
    if(event.isComposing)return;
    if(id==='notes-search'){const start=event.target.selectionStart;notesQuery=event.target.value;render();$('#notes-search').focus();$('#notes-search').setSelectionRange(start,start);}
    if(id==='graph-search'){graphQuery=event.target.value;drawGraph();}
    if(id==='command-input'){commandIndex=0;renderCommands(event.target.value);}
    if(id==='custom-duration'){const n=Number(event.target.value);if(Number.isInteger(n)&&n>=1&&n<=180){focusDuration=n;updateTimerDisplay();$$('.duration-option').forEach(el=>el.classList.toggle('active',Number(el.dataset.minutes)===n));}}
  });
  document.addEventListener('compositionend',event=>{if(event.target.id==='notes-search'||event.target.id==='command-input')event.target.dispatchEvent(new Event('input',{bubbles:true}));});
  document.addEventListener('change',event=>{if(event.target.id==='focus-task')focusTaskId=event.target.value;});
  document.addEventListener('submit',event=>{
    const form=event.target;if(!['capture-form','task-form','settings-form'].includes(form.id))return;event.preventDefault();
    try {
      if(form.id==='capture-form'){const body=$('#capture-input').value.trim();if(!body){toast('先留下一个小小的想法。');return;}const title=uniqueTitle(body.split('\n')[0].replace(/^#+\s*/, '').slice(0,38)||'随手记');addEntity('notes',C.newNote({title,body,tags:['随手记']}));captureDraft='';persist();form.reset();render();toast('想法收好了。去笔记库继续写吧。');}
      if(form.id==='task-form'){const data=new FormData(form);const title=String(data.get('title')).trim();if(!title){toast('给这件事一个名字吧。');return;}addEntity('tasks',C.newTask({title,priority:data.get('priority'),dueDate:data.get('dueDate')}));persist();taskFilter='open';render();$('[name="title"]')?.focus();toast('记下来了，一件一件来。');}
      if(form.id==='settings-form'){const value=Number($('#daily-goal').value);if(!Number.isInteger(value)||value<1||value>1440)throw Error('每日目标请输入 1 到 1440 的整数。');state.settings.dailyGoal=value;persist();toast(storageHealthy?'你的节奏，记下来了。':'目标已修改，但请备份未保存的数据。');}
    }catch(error){toast(error.message,'error');}
  });
  $('#import-file').addEventListener('change',async event=>{
    const file=event.target.files[0];if(!file)return;
    if(file.size>20*1024*1024){toast('备份超过 20 MB，无法导入。','error');return;}
    try {
      let expected;try{expected=localStorage.getItem(KEY);}catch(_){expected=null;}
      const next=C.importJSON(await file.text());
      confirmAction('用备份恢复这个空间？','将用备份中的 '+next.notes.length+' 篇笔记、'+next.tasks.length+' 件任务和 '+next.sessions.length+' 段专注替换当前内容。建议先取消并导出当前空间。',()=>{let current=null;try{current=localStorage.getItem(KEY);}catch(_){}if(current!==expected){toast('导入期间其他标签页更新了数据，本次恢复已取消，请重新选择备份。','error');return;}clearTimeout(saveTimeout);state=next;blockedSave=false;lastSaved=expected;selectedNoteId=null;notesFilter='all';notesQuery='';if(persist())rawRecovery=null;tick();render();toast(storageHealthy?'空间已从备份恢复。':'备份已载入内存，但保存失败，请勿直接关闭。');},'恢复备份');
    }catch(error){toast('没有导入，原数据未改动：'+error.message,'error');}
  });
  $('#confirm-cancel').addEventListener('click',()=>{$('#confirm-dialog').close();confirmCallback=null;});
  $('#confirm-ok').addEventListener('click',()=>{const cb=confirmCallback;confirmCallback=null;$('#confirm-dialog').close();if(cb){try{cb();}catch(error){toast(error.message,'error');}}});
  $('#confirm-dialog').addEventListener('cancel',()=>{confirmCallback=null;});
  $$('.modal').forEach(dialog=>dialog.addEventListener('click',event=>{if(event.target===dialog){const box=dialog.getBoundingClientRect();if(event.clientX<box.left||event.clientX>box.right||event.clientY<box.top||event.clientY>box.bottom)dialog.close();}}));
  $('#mobile-toggle').addEventListener('click',()=>{const open=$('#sidebar').classList.toggle('open');$('#sidebar-scrim').hidden=!open;$('#mobile-toggle').setAttribute('aria-expanded',String(open));$('.main-area').inert=open;if(open)$('#sidebar .nav-item').focus();});
  matchMedia('(max-width:760px)').addEventListener('change',closeSidebar);
  $('#sidebar-scrim').addEventListener('click',closeSidebar);
  window.addEventListener('beforeunload',event=>{if(saveTimeout)persist();if(!storageHealthy||blockedSave){event.preventDefault();event.returnValue='';}});
  document.addEventListener('visibilitychange',()=>{if(document.hidden&&saveTimeout)persist();if(!document.hidden)tick();});
  window.addEventListener('storage',event=>{if(event.key===KEY&&event.newValue!==lastSaved){blockedSave=true;warnStorage('另一个标签页更新了空间。本页已暂停保存以避免覆盖；请先导出本页备份，再刷新加载最新版本。');}});
  window.addEventListener('hashchange',()=>{const [route,id]=location.hash.slice(1).split('/');let decoded;try{decoded=id?decodeURIComponent(id):undefined;}catch(_){}if(Object.hasOwn(routes,route))navigate(route,decoded);});

  if(!C){$('#view').innerHTML=emptyState('核心文件没有加载','请确保 index.html、core.js、app.js 和 styles.css 位于同一个文件夹，再重新打开。');return;}
  state=loadState();
  if(!lastSaved&&!blockedSave)persist();
  const [initialView,initialId]=location.hash.slice(1).split('/');
  if(Object.hasOwn(routes,initialView))view=initialView;
  if(initialId){try{selectedNoteId=decodeURIComponent(initialId);}catch(_){selectedNoteId=null;}}
  render();tick();setInterval(tick,500);
})();
