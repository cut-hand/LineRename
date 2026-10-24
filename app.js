'use strict';
const $=id=>document.getElementById(id),C=LineCore;
let root=null,available=[],items=[],occupants=new Map(),rows=[],selected=-1,busy=false,sortDesc=false,manualOrder=false,undo=null,picked=new Set();
const say=text=>{$('status').textContent=text;};
function node(tag,cls,text){const e=document.createElement(tag);if(cls)e.className=cls;if(text!==undefined)e.textContent=text;return e;}
function controls(){
 for(const id of ['choose','pick','sort','up','down','clear','names','keepExt','recover'])$(id).disabled=busy;
 $('pick').disabled=busy||!root;$('recover').disabled=busy||!root;
 $('up').disabled=busy||selected<=0;$('down').disabled=busy||selected<0||selected>=items.length-1;
 $('run').disabled=busy||!rows.some(r=>!r.reason)||!!undo;
 $('undo').disabled=busy||!undo;
}
function refresh(){
 rows=C.plan(items,$('names').value,$('keepExt').checked,occupants);
 $('rows').replaceChildren();
 for(const row of rows){const tr=node('tr',selected===row.index?'selected':'');tr.tabIndex=0;tr.setAttribute('aria-selected',String(selected===row.index));
  for(const [i,text] of [row.index+1,row.source,row.target||'—',row.status].entries())tr.append(node('td',i===3?(row.reason?'skip':row.swap?'swap':'ready'):'',text));
  tr.onclick=()=>{selected=row.index;refresh();};tr.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();selected=row.index;refresh();}};$('rows').append(tr);
 }
 const names=C.parseNames($('names').value),ready=rows.filter(r=>!r.reason).length;
 $('fileCount').textContent=items.length;$('readyCount').textContent=ready;$('skipCount').textContent=items.length-ready;$('nameCount').textContent=names.length+' 行';
 $('empty').hidden=items.length>0;
 $('countsHint').textContent=names.length===items.length?'行数与文件数量一致。':`名称 ${names.length} 行，文件 ${items.length} 个。${names.length>items.length?'多出的名称不会使用。':'名称不足的文件将跳过。'}`;
 controls();
}
async function scan(){occupants=await C.entries(root);available=[...occupants.values()].filter(e=>e.handle.kind==='file').map(e=>({name:e.name,handle:e.handle})).sort((a,b)=>C.natural(a.name,b.name));}
function renderPicker(){
 $('fileOptions').replaceChildren();const q=$('search').value.toLowerCase();
 for(const file of available.filter(f=>f.name.toLowerCase().includes(q))){const label=node('label','file-option'),input=node('input');input.type='checkbox';input.checked=picked.has(file.name);input.onchange=()=>{if(input.checked)picked.add(file.name);else picked.delete(file.name);};label.append(input,node('span','',file.name));$('fileOptions').append(label);}
 $('confirmPick').textContent='使用所选文件（'+picked.size+'）';
}
function openPicker(){picked=new Set(items.map(i=>i.name));$('search').value='';renderPicker();$('picker').showModal();}
$('choose').onclick=async()=>{
 if(!window.showDirectoryPicker){say('当前浏览器不支持文件夹读写，请使用 Chrome 或 Edge。');return;}
 if(undo){say('请先撤销上次操作或刷新页面。备份目录可供稍后恢复。');return;}
 busy=true;controls();say('等待文件夹选择和读写授权…');
 try{const dir=await showDirectoryPicker({mode:'readwrite'});root=dir;items=[];selected=-1;manualOrder=false;await scan();$('folder').textContent=root.name;say(`已找到 ${available.length} 个文件，请选择需要改名的文件。`);openPicker();}
 catch(e){say(e.name==='AbortError'?'已取消选择，没有修改文件。':'选择失败：'+e.message);}finally{busy=false;refresh();}
};
$('pick').onclick=async()=>{busy=true;controls();try{await scan();openPicker();}catch(e){say('读取文件夹失败：'+e.message);}finally{busy=false;controls();}};
$('closePicker').onclick=()=>$('picker').close();$('search').oninput=renderPicker;
$('selectAll').onclick=()=>{const q=$('search').value.toLowerCase();for(const f of available)if(f.name.toLowerCase().includes(q))picked.add(f.name);renderPicker();};
$('selectNone').onclick=()=>{picked.clear();renderPicker();};
$('confirmPick').onclick=()=>{
 const keep=items.filter(i=>picked.has(i.name)),existing=new Set(keep.map(i=>i.name));
 const added=available.filter(f=>picked.has(f.name)&&!existing.has(f.name));items=keep.concat(added);
 if(!manualOrder)items.sort((a,b)=>C.natural(a.name,b.name));selected=-1;$('picker').close();refresh();say('已选择 '+items.length+' 个文件。请粘贴名称并核对预览。');
};
$('names').oninput=refresh;$('keepExt').onchange=refresh;
$('sort').onclick=()=>{items.sort((a,b)=>(sortDesc?-1:1)*C.natural(a.name,b.name));sortDesc=!sortDesc;manualOrder=true;selected=-1;$('sort').textContent=sortDesc?'自然排序 ↓':'自然排序 ↑';refresh();};
function move(delta){const next=selected+delta;if(selected<0||next<0||next>=items.length)return;[items[selected],items[next]]=[items[next],items[selected]];selected=next;manualOrder=true;refresh();}
$('up').onclick=()=>move(-1);$('down').onclick=()=>move(1);$('clear').onclick=()=>{items=[];selected=-1;manualOrder=false;refresh();};
async function reloadItems(names){await scan();items=names.map(name=>available.find(f=>f.name===name)).filter(Boolean);selected=-1;refresh();}
$('run').onclick=async()=>{
 const active=rows.filter(r=>!r.reason);if(busy||undo||!active.length)return;
 if(!confirm(`将重命名 ${active.length} 个文件，跳过 ${rows.length-active.length} 个。浏览器版保留恢复备份，需要额外磁盘空间。执行期间请勿编辑文件或关闭页面。继续？`))return;
 busy=true;controls();
 try{undo=await C.execute(root,rows,say);const targetNames=rows.map(r=>r.reason?r.source:r.target);await reloadItems(targetNames);say(`已完成 ${active.length} 个文件。备份：${undo.tx.backupName}。可点击“撤销上次”。`);}
 catch(e){say('操作停止：'+e.message);if(e.transaction){undo=e.transaction;try{await C.restore(undo,say);undo=null;await scan();say('执行失败，已恢复原文件名；恢复备份仍保留。原因：'+e.message);}catch(restoreError){say('执行及自动恢复未完成：'+restoreError.message+'\n备份目录：'+undo.tx.backupName+'。请保留备份并按 journal.json 检查文件。');}}}
 finally{busy=false;refresh();}
};
$('undo').onclick=async()=>{if(!undo||busy)return;busy=true;controls();try{const names=undo.tx.records.map(r=>r.source);await C.restore(undo,say);undo=null;await reloadItems(names);say('已恢复原文件名。恢复备份目录仍保留，确认无误后可自行删除。');}catch(e){say('恢复已停止：'+e.message+'。备份仍保留。');}finally{busy=false;refresh();}};
$('recover').onclick=async()=>{
 if(undo){say('当前已有撤销记录，请先使用“撤销上次”。');return;}
 busy=true;controls();$('backupOptions').replaceChildren();
 try{const dirs=[];for await(const [name,h] of root.entries())if(h.kind==='directory'&&name.startsWith('.linerename-backup-'))dirs.push([name,h]);
  for(const [name,backup] of dirs.reverse()){
   try{const file=await (await backup.getFileHandle('journal.json')).getFile(),tx=JSON.parse(await file.text());
    if(tx.version!==1||!Array.isArray(tx.records)||!tx.records.length||tx.state==='restored')continue;
    // Treat recovery files as untrusted input: names must stay inside this folder.
    if(tx.records.some(r=>C.validate(r.source)||C.validate(r.target)||!/^file-\d+$/.test(r.backupFile)||!/^[a-f0-9]{64}$/.test(r.hash)))continue;
    const button=node('button','backup-choice',name+' · '+tx.records.length+' 项 · '+tx.state);
    button.onclick=async()=>{if(!confirm('恢复此备份记录中的文件名？将先校验文件内容。'))return;undo={dir:root,backup,tx};$('recovery').close();await $('undo').onclick();};$('backupOptions').append(button);
   }catch(e){$('backupOptions').append(node('p','','无法读取 '+name+'：'+e.message));}
  }if(!$('backupOptions').childElementCount)$('backupOptions').append(node('p','','没有可恢复的记录。'));$('recovery').showModal();
 }catch(e){say('读取备份失败：'+e.message);}finally{busy=false;controls();}
};
$('closeRecovery').onclick=()=>$('recovery').close();
window.addEventListener('beforeunload',e=>{if(busy){e.preventDefault();e.returnValue='';}});
refresh();if(!window.showDirectoryPicker)say('请使用 Chrome 或 Edge 打开此工具，当前浏览器不支持文件夹读写。');
