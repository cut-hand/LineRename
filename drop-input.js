/* Request directory permissions only from an explicit user action. */
function installFileDrop({zone,isBusy,accept,message}) {
  let pending=null,working=false,depth=0;
  const button=zone.querySelector('button');
  const hint=zone.querySelector('[data-drop-hint]');
  function reset(){depth=0;zone.classList.remove('drag-active');}
  function blocked(){return working||isBusy();}
  async function deliver(root,handles){
    const paths=[];
    for(const handle of handles){
      if(await root.isSameEntry(handle)){paths.push([]);continue;}
      const path=await root.resolve(handle);
      if(!path)throw Error('请选择包含所有拖入项目的上级目录。');
      paths.push(path);
    }
    await accept(root,paths,handles);
    pending=null;button.hidden=true;hint.textContent='拖入文件或文件夹 · 不会上传文件';
  }
  async function authorize(){
    if(!pending||blocked())return;
    working=true;button.disabled=true;
    try{
      const handles=pending;
      if(handles.length===1&&handles[0].kind==='directory'){
        const root=handles[0];
        if(await root.requestPermission({mode:'readwrite'})!=='granted')throw Error('未获得读写授权，请再次点击授权按钮。');
        await deliver(root,handles);
      }else{
        // Files do not expose a parent-directory handle. Ask the user for it.
        const root=await showDirectoryPicker({mode:'readwrite'});
        await deliver(root,handles);
      }
    }catch(e){message(e.name==='AbortError'?'已取消授权，拖入项目仍保留，可点击按钮重试。':e.message);}
    finally{working=false;button.disabled=false;}
  }
  button.onclick=authorize;
  const hasFiles=e=>Array.from(e.dataTransfer?.types||[]).includes('Files');
  document.addEventListener('dragenter',e=>{if(hasFiles(e)){e.preventDefault();depth++;zone.classList.add('drag-active');}});
  document.addEventListener('dragover',e=>{if(hasFiles(e)){e.preventDefault();e.dataTransfer.dropEffect=blocked()?'none':'copy';}});
  document.addEventListener('dragleave',e=>{if(hasFiles(e)&&--depth<=0)reset();});
  document.addEventListener('drop',async e=>{
    if(!hasFiles(e))return;e.preventDefault();reset();
    if(blocked()){message('请先完成当前操作，再添加文件。');return;}
    // Capture all handle promises before leaving the drop event's synchronous turn.
    const dropped=Array.from(e.dataTransfer.items).filter(i=>i.kind==='file');
    if(!dropped.length)return;
    if(dropped.some(i=>!i.getAsFileSystemHandle)){message('当前浏览器不支持拖拽读写，请使用新版 Chrome 或 Edge。');return;}
    const promises=dropped.map(i=>i.getAsFileSystemHandle());
    working=true;
    try{
      const handles=await Promise.all(promises);if(handles.some(h=>!h))throw Error('无法读取拖入项目，请从电脑文件管理器重新拖入。');
      pending=handles;
      if(handles.length===1&&handles[0].kind==='directory'&&await handles[0].queryPermission({mode:'readwrite'})==='granted')await deliver(handles[0],handles);
      else{button.hidden=false;hint.textContent='已接收 '+handles.length+' 个项目，点击按钮授权后添加';button.textContent=handles.length===1&&handles[0].kind==='directory'?'授权并添加目录':'选择所在上级目录并添加';message('已接收拖拽项目。请点击拖拽区内的授权按钮；只添加拖入的项目。');}
    }catch(e){message('添加失败：'+e.message);}finally{working=false;}
  });
}
