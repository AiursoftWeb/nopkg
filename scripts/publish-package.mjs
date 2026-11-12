#!/usr/bin/env node
// Run on the connected distribution worker, after building and approving one tarball.
import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {readFileSync, writeFileSync, mkdtempSync, rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {resolve, join} from 'node:path';

const [artifactArg, ...args] = process.argv.slice(2);
const usage = 'Usage: node scripts/publish-package.mjs package.tgz [--target both|internal|public] [--tag latest] [--report publish-results.json]';
if (!artifactArg || args.length % 2 || args.some((v,i) => i % 2 === 0 && !['--target','--tag','--report'].includes(v))) {
  console.error(usage);process.exit(2);
}
const flags = Object.fromEntries(Array.from({length: args.length / 2}, (_,i) => [args[i*2],args[i*2+1]]));
const target = flags['--target'] ?? 'both', tag = flags['--tag'] ?? 'latest';
if (!['both','internal','public'].includes(target) || !/^[a-zA-Z][a-zA-Z0-9._-]{0,127}$/.test(tag)) {
  console.error(usage);process.exit(2);
}
const artifact = resolve(artifactArg), bytes = readFileSync(artifact);
const integrity = 'sha512-' + createHash('sha512').update(bytes).digest('base64');
const sha256 = createHash('sha256').update(bytes).digest('hex');
const targets = [
  {name:'internal',registry: process.env.NOPKG_REGISTRY ?? 'https://nopkg.aiursoft.com/npm/',token: process.env.NOPKG_TOKEN},
  {name:'public',registry: process.env.NPM_REGISTRY ?? 'https://registry.npmjs.org/',token: process.env.NPM_TOKEN},
].filter(x => target === 'both' || x.name === target);
const secrets = targets.map(x=>x.token).filter(Boolean);
const redact = value => secrets.reduce((text,secret)=>text.split(secret).join('[REDACTED]'),String(value));
const temporary = mkdtempSync(join(tmpdir(),'nopkg-publish-'));
const report = {artifact,sha256,integrity,tag,targets:[]};
function npm(args, config) {
  const result = spawnSync('npm',[...args,'--ignore-scripts','--provenance=false','--userconfig='+config],{
    cwd:temporary,encoding:'utf8',env:{...process.env,npm_config_cache:join(temporary,'cache')},timeout:180000,maxBuffer:4*1024*1024,
  });
  return {ok:result.status === 0,stdout:redact(result.stdout ?? ''),stderr:redact(result.stderr ?? ''),status:result.status};
}
try {
  const npmVersion = spawnSync('npm',['--version'],{encoding:'utf8',timeout:10000});
  if (npmVersion.status !== 0 || Number(npmVersion.stdout.trim().split('.')[0]) < 11)
    throw Error('The dual-publish helper requires npm 11 or newer to enforce CLI registry selection.');
  const snapshot = join(temporary,'artifact.tgz');writeFileSync(snapshot,bytes,{mode:0o400});
  const emptyConfig = join(temporary,'empty.npmrc');writeFileSync(emptyConfig,'',{mode:0o600});
  const inspection = npm(['pack',snapshot,'--dry-run','--json'],emptyConfig);
  if (!inspection.ok) throw Error('Cannot inspect the supplied tarball: '+inspection.stderr);
  const inspected = JSON.parse(inspection.stdout);
  const info = Array.isArray(inspected) ? inspected[0] : (inspected.name ? inspected : Object.values(inspected)[0]);
  if (!info?.name || !info?.version || info.integrity !== integrity) throw Error('The supplied artifact could not be identified without changing its bytes.');
  report.package = info.name;report.version = info.version;
  for (const destination of targets) {
    const receipt = {target:destination.name,status:'failed'};report.targets.push(receipt);
    try {
      const registry = new URL(destination.registry);
      if ((registry.protocol !== 'https:' && !(registry.protocol === 'http:' && ['localhost','127.0.0.1','[::1]'].includes(registry.hostname))) ||
          registry.username || registry.password || registry.search || registry.hash) throw Error('Registry must use HTTPS without credentials, query or fragment.');
      registry.pathname = registry.pathname.replace(/\/?$/,'/');receipt.registry = registry.href;
      if (!destination.token || /[\r\n]/.test(destination.token)) throw Error('A valid token is required for this target.');
      // Modern npm honors explicit CLI registry flags over publishConfig. Keep credentials scoped to this origin and path.
      const config = join(temporary,destination.name+'.npmrc');
      writeFileSync(config,`registry=${registry.href}\n//${registry.host}${registry.pathname}:_authToken=${destination.token}\n`,{mode:0o600});
      const scope = info.name.startsWith('@') ? info.name.split('/')[0] : null;
      const common = ['--registry='+registry.href,...(scope ? ['--'+scope+':registry='+registry.href] : [])];
      const published = npm(['publish',snapshot,'--tag='+tag,'--access=public',...common],config);
      if (published.ok) receipt.status = 'published';
      else {
        // Never use skip-duplicate alone: a matching version must contain the identical archive.
        const view = npm(['view',`${info.name}@${info.version}`,'dist.integrity','--json',...common],config);
        const existing = view.ok ? JSON.parse(view.stdout) : null;
        const existingIntegrity = Array.isArray(existing) && existing.length === 1 ? existing[0] : existing;
        if (!view.ok || existingIntegrity !== integrity) throw Error(published.stderr || 'Publishing failed; the existing artifact did not match.');
        const tagged = npm(['dist-tag','add',`${info.name}@${info.version}`,tag,...common],config);
        if (!tagged.ok) throw Error('The existing archive matched, but updating its tag failed: '+tagged.stderr);
        receipt.status = 'already-published-identical';
      }
    } catch (error) {receipt.error = redact(error.message).slice(0,4000);}
  }
} catch (error) {report.error = redact(error.message).slice(0,4000);}
finally {
  rmSync(temporary,{recursive:true,force:true});
  writeFileSync(resolve(flags['--report'] ?? 'publish-results.json'),JSON.stringify(report,null,2)+'\n',{mode:0o600});
}
console.log(JSON.stringify(report));
if (report.error || report.targets.some(x => x.status === 'failed')) process.exitCode = 1;
