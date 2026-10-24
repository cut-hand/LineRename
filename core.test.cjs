const assert=require('node:assert/strict');
const {LineCore:C}=require('./core.js');
class MemFile {
 constructor(name,data=''){this.kind='file';this.name=name;this.data=data;}
 async getFile(){return new File([this.data],this.name,{lastModified:1});}
 async isSameEntry(other){return this===other;}
 async createWritable(){let pending;return {write:async data=>{pending=typeof data==='string'?data:await data.text();},close:async()=>{this.data=pending;},abort:async()=>{}};}
}
class MemDir {
 constructor(){this.kind='directory';this.items=new Map();}
 async *entries(){yield* this.items;}
 async getFileHandle(name,options={}){const existing=this.items.get(name);if(existing){if(existing.kind!=='file')throw Object.assign(Error('directory'),{name:'TypeMismatchError'});return existing;}if(options.create){const h=new MemFile(name);this.items.set(name,h);return h;}throw Object.assign(Error('missing'),{name:'NotFoundError'});}
 async getDirectoryHandle(name,options={}){if(this.items.has(name))return this.items.get(name);if(options.create){const d=new MemDir();this.items.set(name,d);return d;}throw Object.assign(Error('missing'),{name:'NotFoundError'});}
 async removeEntry(name){if(!this.items.has(name))throw Error('missing');this.items.delete(name);}
 add(name,data){const h=new MemFile(name,data);this.items.set(name,h);return {name,handle:h};}
}
(async()=>{
 assert.deepEqual(C.parseNames(' a\r\nb\n \n'),['a','b']);
 for(const name of ['CON.txt','com¹','x/../y','bad.','bad ','a'.repeat(256)])assert(C.validate(name),name);
 const d=new MemDir(),items=[d.add('a.txt','AAA'),d.add('b.txt','BBB')];
 const occ=await C.entries(d);
 assert(C.plan(items,'x\nx',true,occ).every(r=>r.reason==='目标名称重复'));
 assert(C.plan(items,'b\nb',true,occ).every(r=>r.reason));
 assert.equal(C.plan(items,'new',true,occ)[1].reason,'名称不足');
 const swapped=C.plan(items,'b\na',true,occ);assert(swapped.every(r=>r.swap));
 const tx=await C.execute(d,swapped);assert.equal((await d.getFileHandle('a.txt')).data,'BBB');assert.equal((await d.getFileHandle('b.txt')).data,'AAA');
 await C.restore(tx);assert.equal((await d.getFileHandle('a.txt')).data,'AAA');assert.equal((await d.getFileHandle('b.txt')).data,'BBB');
 const d2=new MemDir(),one=d2.add('source.txt','original');const tx2=await C.execute(d2,C.plan([one],'target',true,await C.entries(d2)));
 d2.items.get('target.txt').data='user edit';await assert.rejects(C.restore(tx2),/已变化/);assert.equal(d2.items.get('target.txt').data,'user edit');
 const d3=new MemDir(),three=[d3.add('a.txt','A'),d3.add('b.txt','B')],nativeRemove=d3.removeEntry.bind(d3);let count=0;
 d3.removeEntry=async name=>{if(++count===2)throw Error('injected failure');await nativeRemove(name);};
 let failure;try{await C.execute(d3,C.plan(three,'x\ny',true,await C.entries(d3)));}catch(e){failure=e;}
 assert(failure.transaction);d3.removeEntry=nativeRemove;await C.restore(failure.transaction);assert.equal(d3.items.get('a.txt').data,'A');assert.equal(d3.items.get('b.txt').data,'B');
 const d4=new MemDir(),four=[d4.add('a.txt','A')];d4.add('x.txt','OUTSIDE');assert(C.plan(four,'x',true,await C.entries(d4))[0].reason);
 const d5=new MemDir(),five=[d5.add('a.txt','A')],r=C.plan(five,'x',true,await C.entries(d5));d5.items.set('a.txt',new MemFile('a.txt','replaced'));await assert.rejects(C.execute(d5,r),/源文件已变化/);
 console.log('PASS: name validation, duplicate targets, skipped chains, missing names, swap, undo, edited-output protection, interrupted restore, outside collisions and source identity');
})().catch(e=>{console.error(e);process.exitCode=1;});
