import {readFile,readdir,writeFile} from 'node:fs/promises';import{resolve,join,relative}from'node:path';import{createHash}from'node:crypto';import{fileURLToPath}from'node:url';
const root=fileURLToPath(new URL('.',import.meta.url)),out=resolve(root,'../JX3Artist/web');
let notices='JX3 Artist embedded UI third-party notices\nOnly local application assets are loaded. Project-wide public release rights remain a separate review.\n\n';
const modules=join(root,'node_modules');const folders=[];
for(const item of await readdir(modules,{withFileTypes:true})){if(!item.isDirectory()||item.name.startsWith('.'))continue;if(item.name.startsWith('@'))for(const entry of await readdir(join(modules,item.name),{withFileTypes:true})){if(entry.isDirectory())folders.push(join(modules,item.name,entry.name));}else folders.push(join(modules,item.name));}
for(const dir of folders){try{const p=JSON.parse(await readFile(join(dir,'package.json'),'utf8'));const names=(await readdir(dir)).filter(n=>/^(license|licence|copying|notice)(\.|$)/i.test(n));if(!names.length)continue;notices+=`\n===== ${p.name} ${p.version} (${p.license||'see included notice'}) =====\n`;for(const file of names)try{notices+=await readFile(join(dir,file),'utf8')+'\n';}catch{}}catch{}}
for(const file of ['LICENSE','NOTICE.md'])notices+='\n===== Stageshow '+file+' =====\n'+await readFile(join(root,'vendor/stageshow',file),'utf8');
await writeFile(join(out,'THIRD-PARTY-NOTICES.txt'),notices);
const files=[];async function walk(dir){for(const e of await readdir(dir,{withFileTypes:true})){const p=join(dir,e.name);if(e.isDirectory())await walk(p);else if(e.name!=='ui-manifest.json')files.push({path:relative(out,p).replaceAll('\\','/'),sha256:createHash('sha256').update(await readFile(p)).digest('hex').toUpperCase()});}}await walk(out);
const {version}=JSON.parse(await readFile(join(root,'package.json'),'utf8'));
await writeFile(join(out,'ui-manifest.json'),JSON.stringify({version,entry:'index.html',files},null,2));console.log('Embedded UI manifest:',files.length,'files');
