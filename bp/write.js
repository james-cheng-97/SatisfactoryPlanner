// Blueprint writer: spec (JSON from the planner) + templates (from the player's own save/blueprints) → .sbp/.sbpcfg.
//   node write.js <spec.json> <templates.json> <outDir>
// spec: { name, dim, description,
//         entities: [{ id, cls, x, y, z, yaw, recipe? }],                  // cm, blueprint frame (origin = centre, floor z=0)
//         links:    [{ cls, pts:[{x,y,z}...], from:{id,port}|null, to:{id,port}|null }] }   // belts / pipes
const { Parser } = require('@etothepii/satisfactory-file-parser');
const fs = require('fs'), path = require('path');
const [specPath, tplPath, outDir] = process.argv.slice(2);
const spec = JSON.parse(fs.readFileSync(specPath, 'utf8'));
const T = JSON.parse(fs.readFileSync(tplPath, 'utf8'));
const clone = o => JSON.parse(JSON.stringify(o));
const LVL = 'Persistent_Level', PFX = 'Persistent_Level:PersistentLevel.';

// strip runtime state: connections to the world, wires, fluid state, inventory contents, blueprint proxy
function clean(obj, oldName, newName) {
  const rename = p => p.startsWith(oldName) ? newName + p.slice(oldName.length) : p;
  obj.instanceName = rename(obj.instanceName);
  if (obj.parentEntityName) obj.parentEntityName = rename(obj.parentEntityName);
  if (obj.components) obj.components = obj.components.map(c => ({ ...c, pathName: rename(c.pathName) }));
  const props = obj.properties || {};
  for (const k of ['mBlueprintProxy', 'mConnectedComponent', 'mWires', 'mFluidBox', 'mPipeNetworkID', 'mItems', 'mBuildEffectInstigator'])
    delete props[k];
  if (props.mInventoryStacks)
    for (const st of props.mInventoryStacks.values) {
      st.properties.Item.value.itemReference = { levelName: '', pathName: '' };
      st.properties.NumItems.value = 0;
    }
  // any remaining reference to another world object: rename if ours, else drop
  const walk = (o, parent, key) => {
    if (!o || typeof o !== 'object') return;
    if (typeof o.pathName === 'string' && o.pathName.startsWith(PFX)) {
      if (o.pathName.startsWith(oldName)) o.pathName = rename(o.pathName);
      else if (parent && key != null) { if (Array.isArray(parent)) parent.splice(key, 1); else { o.levelName = ''; o.pathName = ''; } }
      return;
    }
    if (Array.isArray(o)) for (let i = o.length - 1; i >= 0; i--) walk(o[i], o, i);
    else for (const k of Object.keys(o)) walk(o[k], o, k);
  };
  walk(props, null, null);
}

// header / config from a blueprint of the target session: versions and player info must match the game/session
const ab = b => b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength);
const base = Parser.ParseBlueprintFiles('base', ab(fs.readFileSync(spec.baseBlueprint + '.sbp')), ab(fs.readFileSync(spec.baseBlueprint + '.sbpcfg')));
const builtBy = base.objects.map(o => o.properties?.BuiltBy).find(Boolean);
const cost = {};
function addCost(cls, n) { for (const i of T.costs[cls] || T.costs[cls.replace(/_(Mk\d)/, '$1')] || []) cost[i.item] = (cost[i.item] || 0) + i.amount * n; }

const objects = [];
const byId = new Map();
let serial = 2140000000;
const recipeRefs = new Set();
function place(cls, x, y, z, yaw) {
  const tpl = T.templates[cls];
  if (!tpl) throw new Error('no template for ' + cls);
  const oldName = tpl.actor.instanceName, newName = PFX + cls + '_' + (serial++);
  const actor = clone(tpl.actor);
  clean(actor, oldName, newName);
  const r = (yaw || 0) * Math.PI / 360;
  actor.transform = { rotation: { x: 0, y: 0, z: Math.sin(r), w: Math.cos(r) }, translation: { x, y, z }, scale3d: { x: 1, y: 1, z: 1 } };
  const comps = tpl.components.map(c => { const n = clone(c); clean(n, oldName, newName); return n; });
  // only keep component references that exist
  const names = new Set(comps.map(c => c.instanceName));
  actor.components = (actor.components || []).filter(c => names.has(c.pathName));
  if (builtBy && actor.properties.BuiltBy) actor.properties.BuiltBy = clone(builtBy);
  objects.push(actor, ...comps);
  const br = actor.properties.mBuiltWithRecipe?.value?.pathName;
  if (br) recipeRefs.add(br);
  return { actor, comps, name: newName };
}
function setProp(obj, name, value) {
  obj.properties[name] = { type: 'ObjectProperty', name, propertyTagType: { name: 'ObjectProperty', children: [] }, value };
}
function connect(a, portA, b, portB) {
  const ca = a.comps.find(c => c.instanceName === a.name + '.' + portA), cb = b.comps.find(c => c.instanceName === b.name + '.' + portB);
  if (!ca || !cb) throw new Error(`no port ${a.name}.${portA} / ${b.name}.${portB}`);
  setProp(ca, 'mConnectedComponent', { levelName: LVL, pathName: cb.instanceName });
  setProp(cb, 'mConnectedComponent', { levelName: LVL, pathName: ca.instanceName });
}

for (const e of spec.entities) {
  const p = place(e.cls, e.x, e.y, e.z, e.yaw);
  if (e.recipe) {
    const rp = T.recipes[e.recipe];
    if (!rp) console.warn('unknown recipe path', e.recipe);
    else setProp(p.actor, 'mCurrentRecipe', { levelName: '', pathName: rp });
  }
  if (e.topZ != null && p.actor.properties.mTopTransform) {
    // conveyor lift: the top's height and its turn relative to the lift
    const tp = p.actor.properties.mTopTransform.value.properties;
    tp.Translation.value = { x: 0, y: 0, z: e.topZ };
    const r = (e.topYaw || 0) * Math.PI / 360;
    if (tp.Rotation) tp.Rotation.value = { x: 0, y: 0, z: Math.sin(r), w: Math.cos(r) };
  }
  if (e.overflow && p.actor.properties.mSortRules) {
    // smart splitter: the output toward the overflow box on Overflow, the other outputs in use on Any, unused ones None
    // (rule OutputIndex: 0 = Output1 straight on, 1 = Output2, 2 = Output3 — read off sorting splitters in a save)
    const used = new Set(spec.links.filter(k => k.from?.id === e.id).map(k => k.from.port));
    const R = '/Game/FactoryGame/Resource/FilteringRules/';
    const rule = p.actor.properties.mSortRules.values[0];
    p.actor.properties.mSortRules.values = ['Output1', 'Output2', 'Output3'].map((port, i) => {
      const r = clone(rule), kind = port === e.overflow ? 'Overflow' : used.has(port) ? 'Wildcard' : 'None';
      r.properties.ItemClass.value = { levelName: '', pathName: `${R}Desc_${kind}.Desc_${kind}_C` };
      r.properties.OutputIndex.value = i;
      return r;
    });
  }
  byId.set(e.id, p);
  addCost(e.cls, 1);
}
// belts / pipes: actor at the first point, spline points relative to it (no rotation)
const V = (x, y, z) => ({ x, y, z });
function vecProp(name, v) {
  return { type: 'StructProperty', name, propertyTagType: { name: 'StructProperty', children: [{ name: 'Vector', children: [{ name: '/Script/CoreUObject', children: [] }] }] }, flags: 8, value: v };
}
// the game allows at most 56 m per belt / pipe: longer runs are cut on straight stretches (≥ 3 m from a corner) into
// pieces joined end to end
const MaxLen = 5400;
function cutRun(pts) {
  const d = (a, b) => Math.hypot(b.x - a.x, b.y - a.y, b.z - a.z);
  const total = pts.slice(1).reduce((s, q, i) => s + d(pts[i], q), 0);
  if (total <= MaxLen) return [pts];
  const pieces = [];
  let cur = [pts[0]], acc = 0;
  for (let i = 1; i < pts.length; i++) {
    let a = cur[cur.length - 1], b = pts[i];
    while (acc + d(a, b) > MaxLen) {
      const room = MaxLen - acc, seg = d(a, b);
      // cut inside this leg, keeping 3 m from both of its ends where possible
      const at = Math.max(Math.min(room, seg - 300), Math.min(300, seg / 2));
      if (at <= 1 || at >= seg - 1) break;
      const f = at / seg, c = V(a.x + (b.x - a.x) * f, a.y + (b.y - a.y) * f, a.z + (b.z - a.z) * f);
      cur.push(c); pieces.push(cur);
      cur = [c]; acc = 0; a = c;
    }
    acc += d(a, b); cur.push(b);
  }
  pieces.push(cur);
  return pieces;
}
const expanded = [];
for (const l of spec.links) {
  const runs = cutRun(l.pts);
  runs.forEach((pts, i) => expanded.push({ ...l, pts, from: i === 0 ? l.from : { chain: true }, to: i === runs.length - 1 ? l.to : { chain: true } }));
}
let prevPiece = null;
let rampAtPort = 0; // ramps left starting right at a port (no level stretch to take 1 m from)
for (const l of expanded) {
  const o = l.pts[0];
  const raw = l.pts.map(q => V(q.x - o.x, q.y - o.y, q.z - o.z));
  // the game's "straight" belts: straight runs joined by ¼-circle bends of 2 m radius (less if the legs are short).
  // Each bend is two spline points (start and end of the arc) with 330-long tangents along the run; straight runs use
  // half their length as tangent; the belt's own ends have a tiny tangent. (Measured from a belt built in game.)
  const sub = (a, b) => V(a.x - b.x, a.y - b.y, a.z - b.z), add = (a, b) => V(a.x + b.x, a.y + b.y, a.z + b.z);
  const mul = (a, k) => V(a.x * k, a.y * k, a.z * k), len = a => Math.hypot(a.x, a.y, a.z);
  const unit = a => { const n = len(a) || 1; return mul(a, 1 / n); };
  const clean = raw.filter((q, i) => i === 0 || len(sub(q, raw[i - 1])) > 0.5);
  const nodes = []; // { p, arrive, leave } with tangents filled in below
  // legs drawn as one smooth S-curve (as the game makes when a belt is dragged between two facing ports): tangents
  // along the main axis, as long as the run; no corner arc at their ends
  // ramps in a row (same way in plan, same way up or down) with under 3 m of level belt between them: one steady slope
  // (as a stair of 2 m steps it looks like a hump in game)
  if (!/Pipeline/.test(l.cls) && !(l.curve || []).length) {
    const isRamp = k => { const v = sub(clean[k + 1], clean[k]); return Math.abs(v.z) > 1 && Math.hypot(v.x, v.y) > 1; };
    const flatDir = k => { const v = sub(clean[k + 1], clean[k]); const h = Math.hypot(v.x, v.y) || 1; return V(v.x / h, v.y / h, 0); };
    const sameWay = (i, j) => { const a = flatDir(i), b = flatDir(j); return Math.abs(a.x * b.x + a.y * b.y - 1) < 1e-3; };
    for (let k = 0; k + 2 < clean.length; k++) {
      if (!isRamp(k)) continue;
      // k: ramp, k+1: short level piece, k+2: ramp the same way (both up or both down)
      const f = sub(clean[k + 2], clean[k + 1]), r1 = sub(clean[k + 1], clean[k]);
      if (k + 3 > clean.length - 1 || !isRamp(k + 2) || Math.abs(f.z) > 1 || Math.hypot(f.x, f.y) >= 300) continue;
      const r2 = sub(clean[k + 3], clean[k + 2]);
      if (!sameWay(k, k + 1) || !sameWay(k, k + 2) || Math.sign(r1.z) !== Math.sign(r2.z)) continue;
      clean.splice(k + 1, 2); k--; // (the merged ramp may join the next one too)
    }
  }
  // room around a ramp: a corner's curve (2 m radius) needs 2 m of level straight between it and a ramp, a port 1 m
  // (a ramp starting inside a curve folds the belt — seen twisted in game; one right at a splitter / merger looks broken).
  // The ramp slides along its straight run, taking the room from the level belt on its other side (same length and
  // slope); where there's no room it's counted and left.
  if (!/Pipeline/.test(l.cls) && !(l.curve || []).length) {
    const flatV = (a, b) => { const v = sub(b, a); return Math.abs(v.z) <= 1 && Math.hypot(v.x, v.y) > 1; };
    const dirOf = (a, b) => { const v = sub(b, a); const h = Math.hypot(v.x, v.y) || 1; return V(v.x / h, v.y / h, 0); };
    const same = (u, v) => Math.abs(u.x * v.x + u.y * v.y - 1) < 1e-3;
    // straight level runs as one leg (drop points in the middle of one)
    for (let i = clean.length - 2; i >= 1; i--)
      if (flatV(clean[i - 1], clean[i]) && flatV(clean[i], clean[i + 1]) && Math.abs(clean[i + 1].z - clean[i - 1].z) <= 1 && same(dirOf(clean[i - 1], clean[i]), dirOf(clean[i], clean[i + 1]))) clean.splice(i, 1);
    const connected = e => e && !e.chain && e.port !== 'chain';
    for (let k = 0; k + 1 < clean.length; k++) {
      const v = sub(clean[k + 1], clean[k]);
      if (!(Math.abs(v.z) > 1 && Math.hypot(v.x, v.y) > 1)) continue; // (a ramp)
      const d = dirOf(clean[k], clean[k + 1]);
      // level run before it (same way) and what's behind that: the belt's start (a port?) or a corner
      let Lb = 0, needB;
      if (k === 0) needB = connected(l.from) ? 100 : 0;
      else if (flatV(clean[k - 1], clean[k]) && same(dirOf(clean[k - 1], clean[k]), d)) { Lb = Math.hypot(clean[k].x - clean[k - 1].x, clean[k].y - clean[k - 1].y); needB = k - 1 === 0 ? (connected(l.from) ? 100 : 0) : 200; }
      else needB = 200;
      let La = 0, needA;
      const e = k + 1;
      if (e === clean.length - 1) needA = connected(l.to) ? 100 : 0;
      else if (flatV(clean[e], clean[e + 1]) && same(dirOf(clean[e], clean[e + 1]), d)) { La = Math.hypot(clean[e + 1].x - clean[e].x, clean[e + 1].y - clean[e].y); needA = e + 1 === clean.length - 1 ? (connected(l.to) ? 100 : 0) : 200; }
      else needA = 200;
      let shift = 0;
      if (Lb < needB) shift = needB - Lb;           // forward
      else if (La < needA) shift = -(needA - La);   // back
      if (!shift) continue;
      if (La - shift < needA - 1e-6 || Lb + shift < needB - 1e-6 || (shift > 0 && !La) || (shift < 0 && !Lb)) { rampAtPort++; continue; }
      // (no level leg on the side it moves into: insert one first)
      const mv = mul(d, shift);
      if (shift > 0 && (k === 0 || !(flatV(clean[k - 1], clean[k]) && same(dirOf(clean[k - 1], clean[k]), d)))) { clean.splice(k + 1, 0, { ...clean[k] }); k++; }
      if (shift < 0 && (k + 2 >= clean.length || !(flatV(clean[k + 1], clean[k + 2]) && same(dirOf(clean[k + 1], clean[k + 2]), d)))) clean.splice(k + 2, 0, { ...clean[k + 1] });
      const e2 = k + 1;
      clean[k] = add(clean[k], mv); clean[e2] = add(clean[e2], mv);
      // (a level leg the ramp now fully covers: gone)
      if (e2 + 1 < clean.length && Math.hypot(clean[e2 + 1].x - clean[e2].x, clean[e2 + 1].y - clean[e2].y) < 1 && Math.abs(clean[e2 + 1].z - clean[e2].z) <= 1) clean.splice(e2 + 1, 1);
      if (k > 0 && Math.hypot(clean[k].x - clean[k - 1].x, clean[k].y - clean[k - 1].y) < 1 && Math.abs(clean[k].z - clean[k - 1].z) <= 1) { clean.splice(k, 1); k--; }
    }
  }
  const curve = new Set(l.curve || []);
  // a belt ramp (straight in plan, rising / falling): one smooth slope, level where it meets the belt before and after —
  // as the game builds one (tangents horizontal, as long as the run). As two bends it humps (the tangents of a flat 90°
  // turn are far too long for a slope change on a 3 m run; seen in game).
  const ramp = new Set();
  if (!/Pipeline/.test(l.cls))
    for (let k = 0; k + 1 < clean.length; k++) {
      const v = sub(clean[k + 1], clean[k]);
      if (Math.abs(v.z) > 1 && Math.hypot(v.x, v.y) > 1) { ramp.add(k); curve.add(k); }
    }
  const axisOf = k => { const v = sub(clean[k + 1], clean[k]); return ramp.has(k) ? V(v.x, v.y, 0) : Math.abs(v.x) >= Math.abs(v.y) ? V(v.x, 0, v.z) : V(0, v.y, v.z); };
  nodes.push({ p: clean[0], idx: 0 });
  for (let i = 1; i < clean.length - 1; i++) {
    if (curve.has(i - 1) || curve.has(i)) { nodes.push({ p: clean[i], idx: i }); continue; }
    const d1 = unit(sub(clean[i], clean[i - 1])), d2 = unit(sub(clean[i + 1], clean[i]));
    const straight = Math.abs(d1.x * d2.x + d1.y * d2.y + d1.z * d2.z - 1) < 1e-3;
    if (straight) continue;
    // radius 2 m (the game's minimum); a straight piece shared by two bends gives each half, an end piece all of it
    // straight run on each side in plan view (a ramp start on the same line doesn't end it); up to the next bend
    const flat = v => Math.hypot(v.x, v.y);
    const sameXY = (u, v) => Math.abs(u.x * v.y - u.y * v.x) < 1e-6 && u.x * v.x + u.y * v.y > 0;
    let back = 0, bi = i, backEnd = true;
    while (bi > 0) { const s = sub(clean[bi], clean[bi - 1]); if (!sameXY(s, sub(clean[i], clean[i - 1]))) { backEnd = false; break; } back += flat(s); bi--; }
    let fwd = 0, fi = i, fwdEnd = true;
    while (fi < clean.length - 1) { const s = sub(clean[fi + 1], clean[fi]); if (!sameXY(s, sub(clean[i + 1], clean[i]))) { fwdEnd = false; break; } fwd += flat(s); fi++; }
    // pipes bend on 1 m (tangent 164, flat or up / down — measured on a pipe built in game), belts on 2 m (330)
    const pipe = /Pipeline/.test(l.cls), R = pipe ? 100 : 200, K = pipe ? 164 : 330;
    // (a vertical leg counts its height as its length)
    const legLen = v => pipe ? len(v) : flat(v);
    if (pipe) {
      back = 0; bi = i;
      while (bi > 0) { const s = sub(clean[bi], clean[bi - 1]); if (len(sub(unit(s), d1)) > 1e-3) break; back += legLen(s); bi--; }
      fwd = 0; fi = i;
      while (fi < clean.length - 1) { const s = sub(clean[fi + 1], clean[fi]); if (len(sub(unit(s), d2)) > 1e-3) break; fwd += legLen(s); fi++; }
      backEnd = bi === 0; fwdEnd = fi === clean.length - 1;
    }
    const r = Math.min(R, back / (backEnd ? 1 : 2), fwd / (fwdEnd ? 1 : 2));
    if (r < R - 1 && Math.abs(d1.z) < 0.01 && Math.abs(d2.z) < 0.01)
      console.warn('tight flat bend: r', Math.round(r), 'legs', Math.round(len(sub(clean[i], clean[i - 1]))), Math.round(len(sub(clean[i + 1], clean[i]))), 'pts', clean.length, 'at', i, 'cls', l.cls.replace('Build_', ''), 'from', l.from?.port, 'to', l.to?.port);
    const k = K * r / R;
    nodes.push({ p: sub(clean[i], mul(d1, r)), arcLeave: mul(d1, k) });
    nodes.push({ p: add(clean[i], mul(d2, r)), arcArrive: mul(d2, k) });
  }
  nodes.push({ p: clean[clean.length - 1], idx: clean.length - 1 });
  for (const n of nodes) {
    if (n.idx === undefined) continue;
    if (curve.has(n.idx) && n.idx < clean.length - 1) n.arcLeave = axisOf(n.idx);
    if (curve.has(n.idx - 1)) n.arcArrive = axisOf(n.idx - 1);
  }
  // a bend whose arc takes a whole end leg (a belt cut short right after a curve, by a border) ends exactly where the
  // leg did: two points in one spot and a zero tangent there (the snap point then faces anywhere). Merged into one
  // point keeping the arc's tangents.
  for (let i = nodes.length - 1; i > 0; i--)
    if (len(sub(nodes[i].p, nodes[i - 1].p)) < 1) {
      const a = nodes[i - 1], b = nodes[i];
      nodes.splice(i - 1, 2, { p: a.p, arcArrive: a.arcArrive || b.arcArrive, arcLeave: b.arcLeave || a.arcLeave, idx: a.idx ?? b.idx });
    }
  // one belt per piece — each straight run, each curve — joined end to end: short pieces, easy to fix in game
  const cuts = [0];
  for (let i = 1; i < nodes.length - 1; i++) if (nodes[i].arcArrive || nodes[i].arcLeave) cuts.push(i);
  cuts.push(nodes.length - 1);
  const isPipe = /Pipeline/.test(l.cls);
  const [inPort, outPort] = isPipe ? ['PipelineConnection0', 'PipelineConnection1'] : ['ConveyorAny0', 'ConveyorAny1'];
  for (let c = 0; c + 1 < cuts.length; c++) {
    const seg = nodes.slice(cuts[c], cuts[c + 1] + 1);
    if (seg.length < 2) continue;
    const s0 = add(o, seg[0].p);
    const p = place(l.cls, s0.x, s0.y, s0.z, 0);
    const values = seg.map((n, i) => {
      const toPrev = i > 0 ? sub(n.p, seg[i - 1].p) : null, toNext = i < seg.length - 1 ? sub(seg[i + 1].p, n.p) : null;
      // a curve's own tangents where it starts / ends; straight: half the run; the piece's far ends a short tangent
      // along the piece (that direction is its snap point's)
      const arrive = (i > 0 && n.arcArrive) || (i === 0 ? (n.arcLeave ? unit(n.arcLeave) : unit(toNext)) : mul(unit(toPrev), len(toPrev) / 2));
      const leave = (i < seg.length - 1 && n.arcLeave) || (i === seg.length - 1 ? (n.arcArrive ? unit(n.arcArrive) : unit(toPrev)) : mul(unit(toNext), len(toNext) / 2));
      return { type: 'SplinePointData', properties: { Location: vecProp('Location', sub(n.p, seg[0].p)), ArriveTangent: vecProp('ArriveTangent', arrive), LeaveTangent: vecProp('LeaveTangent', leave) } };
    });
    const sp = T.templates[l.cls].actor.properties.mSplineData;
    p.actor.properties.mSplineData = { ...clone(sp), values };
    let total = 0; for (let i = 1; i < seg.length; i++) total += len(sub(seg[i].p, seg[i - 1].p));
    addCost(l.cls, Math.max(1, Math.ceil(total / 200))); // belts and pipes: one recipe per started 2 m (matches the game's own blueprints)
    const first = c === 0, last = c + 2 === cuts.length;
    if (!first || l.from?.chain || l.from?.port === 'chain') connect(prevPiece, outPort, p, inPort);
    else if (l.from) connect(byId.get(l.from.id), l.from.port, p, inPort);
    if (last && l.to && !l.to.chain && l.to.port !== 'chain') connect(p, outPort, byId.get(l.to.id), l.to.port);
    prevPiece = p;
  }
}

// parts joined port to port with no belt between (a conveyor lift right at a machine)
for (const d of spec.direct || []) connect(byId.get(d.a.id), d.a.port, byId.get(d.b.id), d.b.port);

// power lines: spec.wires = [{ a: id, b: id }] between buildings / outlets (the power plug is looked up per building)
for (const w of spec.wires || []) {
  const A = byId.get(w.a), B = byId.get(w.b);
  const plug = x => x.comps.find(c => c.instanceName === x.name + '.' + T.power[x.actor.typePath.split('.').pop()]);
  const pa = plug(A), pb = plug(B);
  if (!pa || !pb) { console.warn('no power plug on', w.a, w.b); continue; }
  const posA = A.actor.transform.translation, posB = B.actor.transform.translation;
  const la = { x: posA.x, y: posA.y, z: posA.z + (w.az ?? 150) }, lb = { x: posB.x, y: posB.y, z: posB.z + (w.bz ?? 150) };
  const line = place('Build_PowerLine_C', la.x, la.y, la.z, 0);
  line.actor.specialProperties = { type: 'PowerLineSpecialProperties', source: { levelName: LVL, pathName: pa.instanceName }, target: { levelName: LVL, pathName: pb.instanceName } };
  const wi = line.actor.properties.mWireInstances;
  if (wi?.values?.[0]?.properties?.Locations) {
    const locs = wi.values[0].properties.Locations;
    locs[0].value = la; locs[locs.length - 1].value = lb;
  }
  if (line.actor.properties.mCachedLength) line.actor.properties.mCachedLength.value = Math.hypot(lb.x - la.x, lb.y - la.y, lb.z - la.z);
  for (const pc of [pa, pb]) {
    const ref = { levelName: LVL, pathName: line.name };
    if (pc.properties.mWires) pc.properties.mWires.values.push(ref);
    else pc.properties.mWires = { ...clone(T.wiresProp), values: [ref] };
  }
  addCost('Build_PowerLine_C', 1);
}

const bp = base;
bp.name = spec.name;
bp.header.designerDimension = { x: spec.dim, y: spec.dim, z: spec.dim };
bp.header.recipeReferences = [...recipeRefs].map(p => ({ levelName: '', pathName: p }));
bp.header.itemCosts = Object.entries(cost).filter(([k]) => T.items[k]).map(([k, n]) => [{ levelName: '', pathName: T.items[k] }, n]);
bp.config.description = spec.description || '';
bp.objects = objects;
if (process.env.DUMP_OBJECTS) { fs.mkdirSync(outDir, { recursive: true }); fs.writeFileSync(path.join(outDir, spec.name + '.objects.json'), JSON.stringify(objects)); } // (save injection: the objects as built)

let header, chunks = [];
const r = Parser.WriteBlueprintFiles(bp, h => header = h, c => chunks.push(c));
fs.mkdirSync(outDir, { recursive: true });
const main = Buffer.concat([Buffer.from(header), ...chunks.map(c => Buffer.from(c))]);
fs.writeFileSync(path.join(outDir, spec.name + '.sbp'), main);
fs.writeFileSync(path.join(outDir, spec.name + '.sbpcfg'), Buffer.from(r.configFileBinary));
// verify: parse our own output back
const again = Parser.ParseBlueprintFiles(spec.name, ab(main), ab(Buffer.from(r.configFileBinary)));
if (rampAtPort) console.warn('ramp right at a port (no room for 1 m level):', rampAtPort);
console.log(JSON.stringify({ ok: true, name: spec.name, objects: again.objects.length, actors: again.objects.filter(o => o.transform).length, bytes: main.length }));
