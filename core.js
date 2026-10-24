/* Browser edition: file contents stay on the device. */
(function (scope) {
  'use strict';
  const key = value => value.normalize('NFC').toLowerCase();
  const natural = (a,b) => a.localeCompare(b, 'zh-CN', {numeric:true,sensitivity:'base'});
  const parseNames = text => text.split(/[\r\n]/).map(s=>s.trim()).filter(Boolean);
  function validate(name) {
    if (!name || name==='.' || name==='..') return '名称为空或不可用';
    if (/[\\/:*?"<>|\u0000-\u001f\u007f]/.test(name)) return '名称包含非法字符';
    if (/[. ]$/.test(name)) return '名称不能以点或空格结尾';
    const stem=name.split('.')[0].replace(/[¹²³]/g,c=>({'¹':'1','²':'2','³':'3'}[c]));
    if (/^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i.test(stem)) return '系统保留名称不可用';
    if(name.length>255) return '名称超过 255 字符';
    return '';
  }
  function plan(items,text,keepExt,occupants) {
    const names=parseNames(text);
    const rows=items.map((item,index)=>{
      const raw=names[index],dot=item.name.lastIndexOf('.');
      const extension=keepExt&&dot>0?item.name.slice(dot):'';
      const target=raw===undefined?'':raw+extension;
      let reason=raw===undefined?'名称不足':validate(raw)||validate(target);
      if(!reason&&key(target)===key(item.name))reason='名称未变化';
      return {item,index,source:item.name,target,reason};
    });
    const counts=new Map();for(const row of rows)if(!row.reason)counts.set(key(row.target),(counts.get(key(row.target))||0)+1);
    for(const row of rows)if(!row.reason&&counts.get(key(row.target))>1)row.reason='目标名称重复';
    // Repeat until chains targeting a skipped source are also blocked.
    let changed;
    do {
      changed=false;const moving=new Set(rows.filter(r=>!r.reason).map(r=>key(r.source)));
      for(const row of rows)if(!row.reason&&occupants.has(key(row.target))&&!moving.has(key(row.target))){row.reason='目标名称已被其他文件或文件夹占用';changed=true;}
    }while(changed);
    const moving=new Set(rows.filter(r=>!r.reason).map(r=>key(r.source)));
    for(const row of rows){row.swap=!row.reason&&moving.has(key(row.target));row.status=row.reason|| (row.swap?'批次内名称交换':'可以重命名');}
    return rows;
  }
  async function digest(file) {
    const bytes=await crypto.subtle.digest('SHA-256',await file.arrayBuffer());
    return Array.from(new Uint8Array(bytes),b=>b.toString(16).padStart(2,'0')).join('');
  }
  async function entries(dir){const map=new Map();for await(const [name,h] of dir.entries())map.set(key(name),{name,handle:h});return map;}
  async function writeJson(dir,name,value){const h=await dir.getFileHandle(name,{create:true});const w=await h.createWritable();try{await w.write(JSON.stringify(value,null,2));await w.close();}catch(e){await w.abort().catch(()=>{});throw e;}}
  async function copyNew(dir,name,file,hash) {
    if((await entries(dir)).has(key(name)))throw Error('目标已存在：'+name);
    const h=await dir.getFileHandle(name,{create:true}),w=await h.createWritable();
    try{await w.write(file);await w.close();}catch(e){await w.abort().catch(()=>{});throw e;}
    if(await digest(await h.getFile())!==hash)throw Error('内容校验失败：'+name);
    return h;
  }
  async function checkedRemove(dir,name,hash){const h=await dir.getFileHandle(name);if(await digest(await h.getFile())!==hash)throw Error('文件内容已变化，停止操作：'+name);await dir.removeEntry(name);}
  async function execute(dir,rows,progress=()=>{}) {
    const active=rows.filter(r=>!r.reason);if(!active.length)throw Error('没有可执行项');
    const live=await entries(dir),moving=new Set(active.map(r=>key(r.source)));
    for(const r of active){
      const old=live.get(key(r.source));if(!old||old.handle.kind!=='file'||!await old.handle.isSameEntry(r.item.handle))throw Error('源文件已变化，请重新选择文件夹：'+r.source);
      if(live.has(key(r.target))&&!moving.has(key(r.target)))throw Error('目标已被占用：'+r.target);
    }
    const backupName='.linerename-backup-'+Date.now()+'-'+crypto.randomUUID().slice(0,8);
    if((await entries(dir)).has(key(backupName)))throw Error('备份目录已存在');
    const backup=await dir.getDirectoryHandle(backupName,{create:true});
    const tx={version:1,state:'backing-up',backupName,records:[]};
    const save=()=>writeJson(backup,'journal.json',tx);
    await save();
    try{
      for(const [i,r] of active.entries()){
        progress('正在备份校验 '+(i+1)+' / '+active.length+'：'+r.source);
        const file=await r.item.handle.getFile();const hash=await digest(file),backupFile='file-'+i;
        await copyNew(backup,backupFile,file,hash);
        tx.records.push({source:r.source,target:r.target,backupFile,hash,sourceRemoved:false,outputCreated:false});await save();
      }
      tx.state='removing-sources';await save();
      for(const r of tx.records){progress('正在处理：'+r.source);await checkedRemove(dir,r.source,r.hash);r.sourceRemoved=true;await save();}
      tx.state='writing-targets';await save();
      for(const r of tx.records){
        progress('正在写入：'+r.target);const file=await (await backup.getFileHandle(r.backupFile)).getFile();
        // Journal intent first: if a write fails, recovery must inspect that path.
        r.outputAttempted=true;await save();await copyNew(dir,r.target,file,r.hash);r.outputCreated=true;await save();
      }
      tx.state='complete';await save();return {dir,backup,tx};
    }catch(error){
      tx.state='interrupted';tx.error=error.message;await save().catch(()=>{});
      const failure=Error(error.message+'。已保存的备份保留在 '+backupName+'，请勿删除该目录。');failure.transaction={dir,backup,tx};throw failure;
    }
  }
  async function restore(transaction,progress=()=>{}) {
    const {dir,backup,tx}=transaction;
    // Preflight every affected path before deleting any generated output.
    let live=await entries(dir);const generated=new Map(tx.records.filter(r=>r.outputCreated||r.outputAttempted).map(r=>[key(r.target),r]));
    for(const r of tx.records){
      if(await digest(await (await backup.getFileHandle(r.backupFile)).getFile())!==r.hash)throw Error('恢复备份校验失败：'+r.source);
      const current=live.get(key(r.source));
      if(current&&r.sourceRemoved&&!generated.has(key(r.source)))throw Error('原位置已有文件，无法覆盖：'+r.source);
    }
    for(const r of generated.values()){
      const current=live.get(key(r.target));if(current&&(current.handle.kind!=='file'||await digest(await current.handle.getFile())!==r.hash))throw Error('目标文件已变化，请根据备份手动恢复：'+r.target);
    }
    tx.state='restoring';await writeJson(backup,'journal.json',tx);
    for(const r of generated.values()){
      if((await entries(dir)).has(key(r.target)))await checkedRemove(dir,r.target,r.hash);
      r.outputCreated=false;r.outputAttempted=false;await writeJson(backup,'journal.json',tx);
    }
    for(const r of tx.records){
      if(!r.sourceRemoved)continue;progress('正在恢复：'+r.source);
      const file=await (await backup.getFileHandle(r.backupFile)).getFile();
      await copyNew(dir,r.source,file,r.hash);r.sourceRemoved=false;await writeJson(backup,'journal.json',tx);
    }
    tx.state='restored';await writeJson(backup,'journal.json',tx);
  }
  scope.LineCore={key,natural,parseNames,validate,plan,digest,entries,execute,restore};
})(typeof module!=='undefined'?module.exports:window);
