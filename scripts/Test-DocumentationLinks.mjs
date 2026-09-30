import fs from 'node:fs';
import path from 'node:path';
const root=path.resolve(import.meta.dirname,'..');
const files=['README.md','native/README.md',...fs.readdirSync(path.join(root,'docs')).filter(n=>n.startsWith('Relay-')&&n.endsWith('.md')).map(n=>'docs/'+n),'docs/RELEASE-CHECKLIST.md'];
const failures=[];let checked=0;
for(const relative of files){const full=path.join(root,relative);const text=fs.readFileSync(full,'utf8');for(const match of text.matchAll(/\]\(<?([^)>]+)>?\)/g)){let target=match[1].replace(/#.*$/,'');if(!target||/^https?:\/\//i.test(target))continue;target=decodeURIComponent(target);const resolved=path.resolve(path.dirname(full),target);checked++;if(!fs.existsSync(resolved))failures.push({file:relative,target});}}
console.log(JSON.stringify({checked,failures},null,2));if(failures.length)process.exitCode=1;
