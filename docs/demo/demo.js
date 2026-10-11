/* Deterministic, dependency-free motion graphics. All times are in seconds. */
const canvas = document.querySelector('#film');
const ctx = canvas.getContext('2d');
const japanese = document.documentElement.lang === 'ja';
const localization = japanese ? window.panelDemoJapanese : undefined;
const translate = value => localization?.labels[String(value)] ?? value;
const C = { bg: '#f1f3ed', ink: '#273d35', muted: '#788477', green: '#4d735e', light: '#e3ecd9', line: '#cbd4c5', white: '#fcfdf9', orange: '#bd6849', red: '#ce796b' };
const font = japanese ? '"Segoe UI", "Yu Gothic", Meiryo, sans-serif' : '"Segoe UI", system-ui, sans-serif';
const mono = 'Consolas, "Courier New", monospace';
const chapters = [
  { name: 'Meet Panel+', duration: 6, title: ['See what', 'flows.'], body: 'A clearer view of your Grasshopper data. Right on the canvas.', captions: [[0, 'Connect any typed data tree to D. Start exploring.']], transcript: 'Find Panel+ in Params → Util in Grasshopper for Rhino 8. Connect a typed data tree to D. The panel groups items by branch and shows compact type hints. With tree tools and units off, the output forwards the incoming tree unchanged.' },
  { name: 'Find & filter', duration: 8, title: ['Less scrolling.', 'More finding.'], body: 'Search, filter, and sort the view. Keep your data flowing.', captions: [[0, 'Choose Value and enter > 800 to find matching numbers.'], [4.2, 'Search, filters, and sorting affect the view. The output stays intact.']], transcript: 'Search text, use regular expressions, or compare numeric values. Filter by type or invalid data, sort values, and jump to an exact branch. In this example, Value > 800 leaves three visible numbers. These view operations do not change the output tree.' },
  { name: 'Measure', duration: 8, title: ['Geometry.', 'With context.'], body: 'Length, area, and volume appear alongside the details that matter.', captions: [[0, 'Visible curves, surfaces, and Breps measure automatically in the background.'], [4.2, 'Filter by Length, Area, or Volume using full-precision measurements.']], transcript: 'Visible curves, surfaces, and Breps measure automatically in the background. Rows retain geometry metadata while adding length, area, or solid volume. Invalid geometry has a visible label and a reason in its tooltip. Length, Area, and Volume filters compare supported primary metrics at full precision, including eligible offscreen rows. This example filters for surface area of at least 2,000,000 mm².' },
  { name: 'Select & copy', duration: 8, title: ['From a row', 'to your model.'], body: 'Select geometry. See exactly where it belongs.', captions: [[0, 'Click to frame and highlight geometry. Ctrl / Cmd-click adds to the selection.'], [4.3, 'Right-click to copy values or geometry. Native geometry copying: Windows.']], transcript: 'Click a geometry row to frame it in Rhino and highlight it in Grasshopper’s selected preview color. Ctrl / Cmd-click toggles rows; Shift-click selects a range. Branch headings select matching rows, including offscreen items. Right-click for Frame selection, Copy value(s), or Copy geometry for Rhino. Native geometry clipboard copying is supported on Windows and does not bake objects.' },
  { name: 'Shape the tree', duration: 10, title: ['A little structure.', 'A lot of control.'], body: 'Four tools. One predictable order.', captions: [[0, 'Simplify removes shared path indices. Trim removes trailing path levels.'], [5, 'Flatten merges branches. Reverse flips item order within each branch.']], transcript: 'Tree tools change the typed output, preserving upstream data. They run in this order: Simplify, Trim, Flatten, Reverse. The example removes a shared leading 7, trims one trailing level and merges matching paths, flattens to {0}, then reverses the items to 40, 30, 20, 10. Settings are saved with the definition and support undo.' },
  { name: 'Units & precision', duration: 8, title: ['The right units.', 'The right detail.'], body: 'Convert values. Format decimals. Keep the precision.', captions: [[0, 'Units converts numeric output. Matching geometry metrics change display units only.'], [4.2, 'Decimal places change displayed and copied text, preserving output precision.']], transcript: 'The Units menu converts numbers between mm and m, square or cubic units, and radians and degrees. Matching geometry measurements use converted display units without scaling the geometry. In this example, 1500.125 becomes 1.500125 with mm → m. Two decimal places displays 1.50, while the numeric output remains 1.500125. Integers become Numbers when converted.' },
  { name: 'Make room', duration: 6, title: ['More room', 'to think.'], body: 'Switch between Full and Half. Keep every connection in place.', captions: [[0, 'One chevron toggles Full / Half. Wire grips stay fixed.']], transcript: 'The header chevron toggles Full and Half height. Both wire grips stay fixed. Filters, selection, and data remain intact, and the display mode is saved with the definition. Drag the corner to resize.' },
  { name: 'Reset & go', duration: 6, title: ['Keep the flow.', 'Find the signal.'], body: 'Everything you need to understand what is passing through.', captions: [[0, 'Reset clears view settings, tree tools, and conversion. One Undo brings them back.'], [3.5, 'Panel+ for Rhino 8 · Params → Util · Connect to D']], transcript: 'Reset restores the original incoming data by clearing tree tools, unit conversion, decimal formatting, search, filters, sorting, and selection. It keeps panel size, display mode, and wire connections. One Undo restores the previous settings. Install better-gh-panel in Rhino’s PackageManager with prereleases enabled, then find Panel+ in Params → Util.' },
];
if (localization) chapters.forEach((chapter, index) => Object.assign(chapter, localization.chapters[index]));
let start = 0;
for (const chapter of chapters) { chapter.start = start; start += chapter.duration; }
const duration = start;
const clamp = (v, lo = 0, hi = 1) => Math.min(hi, Math.max(lo, v));
const ease = v => 1 - (1 - clamp(v)) ** 3;
const mix = (a, b, v) => a + (b - a) * v;

function rect(x, y, w, h, fill, radius = 0, stroke) {
  ctx.beginPath(); ctx.roundRect(x, y, w, h, radius);
  if (fill) { ctx.fillStyle = fill; ctx.fill(); }
  if (stroke) { ctx.strokeStyle = stroke; ctx.lineWidth = 1.5; ctx.stroke(); }
}
function text(value, x, y, size = 22, color = C.ink, weight = 400, align = 'left', family = font) {
  value = translate(value);
  ctx.font = `${weight} ${size}px ${family}`; ctx.fillStyle = color; ctx.textAlign = align; ctx.textBaseline = 'alphabetic'; ctx.fillText(String(value), x, y);
}
function wrapped(value, x, y, width, size = 25, color = C.muted, lineHeight = 37) {
  // Japanese copy uses editorial line breaks to preserve natural phrasing.
  if (value.includes('\n')) {
    value.split('\n').forEach((part, index) => text(part, x, y + index * lineHeight, size, color));
    return;
  }
  ctx.font = `400 ${size}px ${font}`;
  let line = '';
  for (const word of value.split(' ')) {
    const next = line ? `${line} ${word}` : word;
    if (ctx.measureText(next).width > width && line) { text(line, x, y, size, color); y += lineHeight; line = word; } else line = next;
  }
  text(line, x, y, size, color);
}
function line(x1, y1, x2, y2, color = C.line, width = 2) {
  ctx.beginPath(); ctx.moveTo(x1, y1); ctx.lineTo(x2, y2); ctx.strokeStyle = color; ctx.lineWidth = width; ctx.stroke();
}
function circle(x, y, r, fill, stroke) {
  ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2);
  if (fill) { ctx.fillStyle = fill; ctx.fill(); }
  if (stroke) { ctx.strokeStyle = stroke; ctx.lineWidth = 2; ctx.stroke(); }
}
function pill(label, x, y, fill = C.light, color = C.green, size = 17) {
  label = translate(label);
  ctx.font = `500 ${size}px ${font}`; const w = ctx.measureText(label).width + 30;
  rect(x, y, w, 34, fill, 17); text(label, x + 15, y + 23, size, color, 500); return w;
}
function cursor(x, y, click = -1) {
  ctx.save(); ctx.translate(x, y);
  if (click >= 0 && click < .7) { ctx.globalAlpha *= 1 - click / .7; circle(0, 0, 12 + click * 42, null, C.green); ctx.globalAlpha = 1; }
  ctx.beginPath(); ctx.moveTo(0, 0); ctx.lineTo(1, 27); ctx.lineTo(8, 20); ctx.lineTo(14, 32); ctx.lineTo(20, 29); ctx.lineTo(14, 17); ctx.lineTo(25, 17); ctx.closePath();
  ctx.fillStyle = C.ink; ctx.fill(); ctx.strokeStyle = '#fff'; ctx.lineWidth = 2; ctx.stroke(); ctx.restore();
}
function chevron(x, y, up = false) { line(x - 6, y + (up ? 3 : -3), x, y + (up ? -3 : 3), C.muted); line(x, y + (up ? -3 : 3), x + 6, y + (up ? 3 : -3), C.muted); }
function toolIcon(kind, x, y, active) {
  ctx.save(); ctx.translate(x, y); ctx.scale(1.35, 1.35);
  const col = active ? C.green : C.muted;
  if (active) { rect(-5, -5, 26, 26, C.light, 3); line(-5, 21, 21, 21, C.green, 1.3); }
  if (kind === 'Simplify') { const p = [[2.5,3],[13.5,3],[9,8],[9,12],[7,13],[7,8],[2.5,3]]; for (let i=1;i<p.length;i++) line(...p[i-1], ...p[i], col, 1.3); }
  if (kind === 'Trim') { for (const a of [2,7,12]) line(a,2.5,a+2,2.5,col,1.3); line(8,5,8,13.5,col,1.3); line(3,5,8,9,col,1.3); line(8,9,13,5,col,1.3); line(5.5,13.5,10.5,13.5,col,1.3); }
  if (kind === 'Flatten') { line(8,2.5,8,10,col,1.3); line(4,6.5,8,10.5,col,1.3); line(8,10.5,12,6.5,col,1.3); line(2.5,13.5,13.5,13.5,col,1.3); }
  if (kind === 'Reverse') { line(4.5,13,4.5,3,col,1.3); line(1.5,6,4.5,3,col,1.3); line(4.5,3,7.5,6,col,1.3); line(11.5,3,11.5,13,col,1.3); line(8.5,10,11.5,13,col,1.3); line(11.5,13,14.5,10,col,1.3); }
  ctx.restore();
}

const numberRows = [{ path: '{0;0}' }, ...[120,450,820,1250,1830].map((v,i)=>({value:String(v),type:'Num',index:i}))];
const mixedRows = [{path:'{0;0}'},{value:'120',type:'Num',index:0},{value:'1250',type:'Num',index:1},{value:'(12, 8, 0)',type:'Pt',index:2},{path:'{0;1}'},{value:'Crv · degree 3 · L 4250 mm',index:0},{value:'Srf · planar · A 2400000 mm²',index:1}];
function panel({ x = 675, y = 215, w = 815, h = 500, rows = mixedRows, count = '5 items · 2 branches', search = 'Search…', type = 'All types', sort = 'Src', units = 'Units', stats = '', selected = [], tools = [], status = 'Ready', gripY, half = false } = {}) {
  const gy = gripY ?? y + h * .55;
  ctx.save();
  ctx.strokeStyle = '#91a18b'; ctx.lineWidth = 2;
  ctx.beginPath(); ctx.moveTo(x - 65, gy - 28); ctx.bezierCurveTo(x - 40, gy - 28, x - 30, gy, x, gy); ctx.stroke();
  ctx.beginPath(); ctx.moveTo(x + w, gy); ctx.bezierCurveTo(x+w+35,gy,x+w+45,gy+25,x+w+85,gy+25); ctx.stroke();
  ctx.shadowColor = '#2c493018'; ctx.shadowBlur = 32; ctx.shadowOffsetY = 12;
  rect(x, y, w, h, C.white, 3, '#aebca7'); ctx.shadowColor = 'transparent';
  rect(x+1,y+1,5,45,C.green);
  text('P+',x+20,y+32,25,C.green,700); text(count,x+82,y+31,20,C.muted);
  text('↺',x+w-75,y+32,29,C.muted); chevron(x+w-27,y+23,!half);
  circle(x,gy,6,C.white,C.ink); circle(x+w,gy,6,C.white,C.ink);
  text('D',x-22,gy+6,15,C.muted,500);
  const searchW = w * .45;
  text(search,x+20,y+77,19,search==='Search…'?C.muted:C.green,search==='Search…'?400:500);
  chevron(x+searchW-10,y+71); line(x+15,y+91,x+searchW,y+91);
  text(type,x+searchW+15,y+77,18,C.muted); chevron(x+w*.71,y+71); line(x+searchW+8,y+91,x+w*.73,y+91);
  text(sort,x+w*.75,y+77,18,C.muted); text('Paths',x+w*.87,y+77,18,C.muted); chevron(x+w-23,y+71);
  let ry = y+107, row = 0;
  ctx.save(); ctx.beginPath(); ctx.rect(x+9,y+100,w-18,Math.max(0,h-182)); ctx.clip();
  for (const item of rows) {
    if (item.path) { rect(x+10,ry-2,w-30,32,'#eaf0e6'); text(item.path,x+27,ry+20,18,C.green,400,'left',mono); ry+=36; }
    else {
      const active = selected.includes(row);
      rect(x+10,ry-2,w-30,37,active?'#d5e5d8':row%2?'#f2f5ee':C.white);
      if (item.invalid) rect(x+11,ry+4,3,24,C.orange);
      text(item.index ?? row,x+28,ry+24,17,C.muted,400,'left',mono);
      text(item.value,x+78,ry+24,w<600?18:21,item.invalid?C.orange:C.ink);
      if(item.type) text(item.type,x+w-35,ry+24,18,C.muted,400,'right');
      ry+=39; row++;
    }
  }
  ctx.restore();
  rect(x+w-15,y+106,5,Math.max(16,h-198),'#e5eade',2); rect(x+w-15,y+106,5,Math.min(84,Math.max(16,h-198)), '#a5b59a',2);
  if(stats) text(stats,x+18,y+h-56,18,C.ink);
  text(status,x+18,y+h-20,w<600?15:17,C.muted);
  for (const [i,tool] of ['Trim','Simplify','Flatten','Reverse'].entries()) toolIcon(tool,x+w-325+i*41,y+h-37,tools.includes(tool));
  if(units!=='Units') rect(x+w-152,y+h-43,137,34,C.light,2);
  text(units,x+w-140,y+h-19,17,units==='Units'?C.muted:C.green); chevron(x+w-25,y+h-26);
  line(x+w-14,y+h-8,x+w-7,y+h-15,C.muted,1); line(x+w-9,y+h-8,x+w-7,y+h-10,C.muted,1);
  ctx.restore();
  return { x, y, w, h };
}
function note(label, x=697, y=747, color=C.green) { circle(x,y-5,4,color); text(label,x+15,y,17,color); }
function intro(chapter, t) {
  const reveal = .35 + .65 * ease(t/1.3);
  ctx.save(); ctx.translate((1-reveal)*85,0); ctx.globalAlpha *= reveal;
  panel({stats:'min 120    max 1250    avg 685'});
  ctx.restore();
  pill('RHINO 8 + GRASSHOPPER',80,474);
  note('Typed data in. Typed data out.', 697, 755);
  const p = (t%2)/2;
  circle(615+p*60,490+ease(p)*0,5,C.green);
  if(t>2.2) { ctx.save(); ctx.globalAlpha=clamp((t-2.2)*2); pill('Params → Util → Panel+',80,531,C.white,C.muted,18); ctx.restore(); }
}
function filter(chapter,t) {
  const applied = t>3.2;
  const query = t<1.2?'Search…':`Value: ${'> 800'.slice(0,Math.floor((t-1.2)*5))}`;
  panel({rows:applied?[numberRows[0],...numberRows.slice(3)]:numberRows,count:applied?'3 / 5 items · 1 branch':'5 items · 1 branch',search:query,type:'Num',stats:applied?'min 820    max 1830    avg 1300':'min 120    max 1830    avg 894'});
  pill('VIEW ONLY',80,492);
  if(applied) { note('3 matches in the view'); pill('OUTPUT  /  5 items, unchanged',1010,151,C.white,C.green); }
  cursor(mix(1280,836,ease(t/.9)),mix(590,285,ease(t/.9)),t-1.1);
  if(t>.4 && t<1.7) {
    rect(692,305,245,177,C.white,7,C.line);
    ['Text','Regex','Value','Length · Area · Volume'].forEach((v,i)=>{if(i===2)rect(697,381,235,37,C.light,3);text(v,710,335+i*37,i===3?15:18,i===2?C.green:C.muted);});
  }
}
function measure(chapter,t) {
  const ready=t>1.9, filtered=t>5.5;
  const rows=[{path:'{0;1}'},{value:ready?'Crv · degree 3 · L 4250 mm':'Crv · degree 3 · measuring…',index:0},{value:ready?'Srf · planar · A 2400000 mm²':'Srf · planar · measuring…',index:1},{value:ready?'Brep · closed · V 1800000000 mm³':'Brep · closed · measuring…',index:2},{value:'Invalid · <Curve>',type:'Crv',index:3,invalid:true}];
  panel({rows:filtered?[rows[0],rows[2]]:rows,count:filtered?'1 / 4 items · 1 branch':'4 items · 1 branch', search:t>4.1?'Area: ≥ 2000000':'Search…',status:ready?'Measurements cached':'Measuring visible geometry…'});
  pill('AUTOMATIC MEASUREMENTS',80,492);
  if(t>2.2 && t<4.1) { rect(760,540,590,70,'#f7eadf',6); text('Invalid geometry is labeled. Hover for the reason.',782,582,20,C.orange); }
  if(filtered) note('1 surface matches · area ≥ 2000000 mm²');
  else if(ready) note('Metadata stays visible as measurements arrive');
  if(t>3.7) cursor(850,286,t-4.1);
}
function viewport(x,y,w,h,t) {
  rect(x,y,w,h,'#e7ebe2',8,C.line);
  text('RHINO / PERSPECTIVE',x+19,y+30,13,C.muted,600);
  ctx.save(); ctx.beginPath(); ctx.rect(x+5,y+45,w-10,h-75); ctx.clip();
  const zoom=1+ease((t-1.4)/.8)*.12;
  ctx.translate(x+w/2,y+h/2+10); ctx.scale(zoom,zoom);
  for(let i=-7;i<9;i++){line(-280,i*28,280,i*28+100,'#cdd6c7',1);line(i*40-110,-200,i*40+110,210,'#cdd6c7',1);}
  const shapes=[[-75,-70],[-20,25],[75,-30]];
  for(const [i,[sx,sy]]of shapes.entries()){
    const active=i===0?t>1.3:i===1?t>2.8:false;
    const col=active?'#508550':C.red;
    ctx.save(); ctx.translate(sx,sy);
    ctx.beginPath(); ctx.moveTo(-55,45); ctx.bezierCurveTo(-25,-85,30,80,60,-40); ctx.strokeStyle=col; ctx.lineWidth=active?5:3; ctx.stroke();
    circle(-55,45,4,col); circle(60,-40,4,col); ctx.restore();
  }
  ctx.restore();
  pill(t>2.8?'2 curves selected':t>1.3?'1 curve selected':'Model preview',x+16,y+h-48,C.white,C.green,15);
}
function select(chapter,t) {
  const rows=[{path:'{0;1}'},{value:'Crv · L 4250 mm',index:0},{value:'Crv · L 3100 mm',index:1},{value:'Crv · L 2700 mm',index:2}];
  panel({x:628,y:250,w:495,h:415,rows,count:'3 items · 1 branch',selected:t>2.8?[0,1]:t>1.3?[0]:[],status:t>2.8?'2 selected':t>1.3?'1 selected':'Ready'});
  viewport(1160,250,360,415,t);
  pill('FRAME + HIGHLIGHT',80,492);
  cursor(815,t>2.8?445:407,t>2.8?t-2.8:t-1.3);
  if(t>2.8 && t<4.3) pill('Ctrl / Cmd + click',771,557,C.ink,C.white,16);
  if(t>4.3){
    rect(812,450,305,151,C.white,6,C.line);
    ['Frame selection','Copy value(s)','Copy geometry for Rhino'].forEach((v,i)=>{if(i===2&&t>5.4)rect(817,545,295,43,C.light,3);text(v,829,482+i*45,18,C.ink);});
    if(t>5.4)cursor(999,572,t-5.4);
    note('Copy geometry for Rhino · Windows',805,726);
  }
}
function treeCard(x,y,w,label,groups,accent=false) {
  rect(x,y,w,281,C.white,8,C.line); text(label,x+24,y+35,15,C.muted,600);
  let yy=y+78;
  for(const [path,values]of groups){
    rect(x+17,yy-23,122,34,accent?C.light:'#edf1e7',4); text(path,x+29,yy,19,C.green,400,'left',mono);
    values.forEach((v,i)=>{rect(x+162+i*70,yy-27,58,43,accent?'#deead6':'#eff2e9',6);text(v,x+191+i*70,yy+2,22,C.ink,500,'center',mono);});
    yy+=64;
  }
}
function tree(chapter,t){
  // Each stage shows the actual cumulative order used by PanelTreeOperations.
  const stage=t<1.4?0:t<3.3?1:t<5.2?2:t<7.1?3:4;
  const stages=[
    [['{7;0;0}',[10,20]],['{7;0;1}',[30]],['{7;1;0}',[40]]],
    [['{0;0}',[10,20]],['{0;1}',[30]],['{1;0}',[40]]],
    [['{0}',[10,20,30]],['{1}',[40]]],
    [['{0}',[10,20,30,40]]],
    [['{0}',[40,30,20,10]]]
  ];
  text('Four tools. One predictable order.',80,355,27,C.muted);
  const names=['Simplify','Trim 1','Flatten','Reverse'];
  names.forEach((name,i)=>{
    const x=80+i*357, active=stage>i;
    rect(x,405,322,66,active?C.light:C.white,8,active?'#91aa7f':C.line);
    toolIcon(['Simplify','Trim','Flatten','Reverse'][i],x+22,426,active);
    text(name,x+65,446,22,active?C.green:C.muted,500); text(String(i+1).padStart(2,'0'),x+294,445,15,C.muted,400,'right',mono);
    if(i<3)text('→',x+332,446,22,C.muted);
  });
  treeCard(80,495,625,'UPSTREAM / PRESERVED',stages[0]);
  text('→',764,650,36,C.green,400,'center');
  treeCard(820,495,690,stage?`OUTPUT / ${names[stage-1].toUpperCase()} APPLIED`:'OUTPUT / ORIGINAL TREE',stages[stage],stage>0);
  if(stage>0) cursor(80+(stage-1)*357+235,441,t-[0,1.4,3.3,5.2,7.1][stage]);
}
function units(chapter,t){
  const converted=t>2.1,formatted=t>4.6;
  panel({rows:[{path:'{0;0}'},{value:formatted?'1.50':converted?'1.500125':'1500.125',type:'Num',index:0},{value:formatted?'2.75':converted?'2.750375':'2750.375',type:'Num',index:1}],count:'2 items · 1 branch',units:formatted?'m · 2dp':converted?'mm → m':'Units',status:'Numeric output',stats:formatted?'min 1.50    max 2.75    avg 2.13':''});
  pill('CONVERSION + FORMATTING',80,492);
  if(t>.4 && t<4.9){
    rect(1130,371,342,283,C.white,7,C.line);
    ['No conversion','mm → m','mm² → m²','mm³ → m³','rad → deg','Decimal places…  2'].forEach((v,i)=>{
      if((i===1&&t<3.3)||(i===5&&t>=3.3))rect(1136,379+i*44,330,39,C.light,3);
      text(v,1151,405+i*44,19,C.ink);
    });
    cursor(1348,t<3.3?449:624,t<3.3?t-2.1:t-4.6);
  }
  if(formatted){note('Displayed: 1.50 m');pill('OUTPUT  /  1.500125',1125,151,C.white,C.green,19);}
  else if(converted)note('1500.125 mm → 1.500125 m');
}
function compact(chapter,t){
  const half=ease((t-1.2)/.75)*(1-ease((t-4.1)/.75));
  const h=mix(500,250,half),y=215+(500-h)/2;
  panel({y,h,gripY:490,half:half>.5,rows:[numberRows[0],...numberRows.slice(3)],count:'3 / 5 items · 1 branch',search:'Value: > 800'});
  pill('FULL ↔ HALF',80,492);
  for(const x of [675,1490]){circle(x,490,16,null,'#9aae8d');line(x,519,x,571,C.line,1);}
  note('Same wire grips. Same data.',1000,747);
  cursor(1460,y+24,t>4.1?t-4.1:t-1.2);
}
function reset(chapter,t){
  const clean=t>1.6;
  panel({rows:clean?numberRows:[{path:'{0}'},{value:'1.83',type:'Num',index:0},{value:'1.25',type:'Num',index:1}],count:clean?'5 items · 1 branch':'2 / 5 items · 1 branch',search:clean?'Search…':'Value: > 1',units:clean?'Units':'m · 2dp',tools:clean?[]:['Flatten','Reverse'],selected:clean?[]:[0],status:clean?'Original input restored':'1 selected'});
  if(t<3.1)cursor(1419,239,t-1.6);
  pill('PANEL+ / RHINO 8',80,490,C.green,C.white,18);
  text('Params → Util → Panel+',80,570,23,C.green,500);
  text('better-gh-panel',80,612,19,C.muted,400,'left',mono);
  if(clean)note('Reset data settings · preserve size and wires');
}
const scenes=[intro,filter,measure,select,tree,units,compact,reset];

function draw(time){
  const index=chapters.findIndex((chapter,i)=>time<chapter.start+chapter.duration||i===chapters.length-1);
  const chapter=chapters[index],t=time-chapter.start;
  ctx.clearRect(0,0,1600,900);rect(0,0,1600,900,C.bg);
  for(let x=28;x<1600;x+=32)for(let y=124;y<778;y+=32)circle(x,y,.85,'#d7dfcf');
  rect(0,0,1600,116,C.bg);
  rect(80,46,43,43,C.green,10);text('P+',101.5,76,23,C.white,650,'center');text('Panel+',139,78,28,C.ink,600);
  text('SEE WHAT FLOWS',282,76,13,C.muted,600);
  text('ILLUSTRATED WALKTHROUGH · SAMPLE DATA',1520,75,13,C.muted,500,'right');line(80,115,1520,115);
  const enter=index===0?1:ease(t/.6);
  ctx.save();ctx.globalAlpha=enter;ctx.translate(0,(1-enter)*18);
  text(`${String(index+1).padStart(2,'0')} / ${chapter.name.toUpperCase()}`,80,177,15,C.green,600);
  if(index===4){text(chapter.title.join(' '),80,277,japanese?54:58,C.ink,600);}
  else {chapter.title.forEach((s,i)=>text(s,80,278+i*78,japanese?56:66,C.ink,600));wrapped(chapter.body,80,413,index===3?440:465,25,C.muted,37);}
  scenes[index](chapter,t);ctx.restore();
  rect(0,792,1600,108,C.bg);line(80,792,1520,792);
  const caption=chapter.captions.filter(([at])=>t>=at).at(-1)?.[1] ?? '';
  text(caption,800,837,23,C.ink,450,'center');
  let px=80;
  for(let i=0;i<chapters.length;i++){
    const w=chapters[i].duration/duration*1384;
    rect(px,872,w,3,'#d5decd',1.5);
    const p=clamp((time-chapters[i].start)/chapters[i].duration);
    if(p>0)rect(px,872,w*p,3,C.green,1.5);px+=w+8;
  }
  return index;
}

const params=new URLSearchParams(location.search);
if(params.has('film'))document.body.classList.add('film-only');
const reducedMotion=matchMedia('(prefers-reduced-motion: reduce)');
let playing=!params.has('paused')&&!reducedMotion.matches;
let position=0,speed=1,lastFrame=0,activeChapter=-1;
const playButton=document.querySelector('#play'),seek=document.querySelector('#seek'),elapsed=document.querySelector('#elapsed');
function formatTime(seconds){return `${Math.floor(seconds/60)}:${String(Math.floor(seconds%60)).padStart(2,'0')}`;}
const chapterButtons=chapters.map((chapter,i)=>{
  const button=document.createElement('button');button.className='chapter';
  const label=document.createElement('span');label.textContent=`${String(i+1).padStart(2,'0')} / ${formatTime(chapter.start)}`;
  const title=document.createElement('strong');title.textContent=chapter.name;button.append(label,title);
  button.addEventListener('click',()=>setPosition(chapter.start));document.querySelector('#chapters').append(button);
  const heading=document.createElement('h2');heading.textContent=`${formatTime(chapter.start)} — ${chapter.name}`;
  const paragraph=document.createElement('p');paragraph.textContent=chapter.transcript;document.querySelector('#transcript').append(heading,paragraph);
  return button;
});
function render(){
  const index=draw(position);seek.value=position;elapsed.textContent=formatTime(position);
  seek.setAttribute('aria-valuetext',japanese?`${duration}秒中${Math.floor(position)}秒：${chapters[index].name}`:`${Math.floor(position)} seconds of ${duration} seconds: ${chapters[index].name}`);
  canvas.setAttribute('aria-label',`${chapters[index].name}. ${chapters[index].transcript}`);
  if(index!==activeChapter){chapterButtons.forEach((b,i)=>b.setAttribute('aria-current',String(i===index)));activeChapter=index;}
  playButton.textContent=japanese?(playing?'一時停止':position>=duration?'もう一度':'再生'):(playing?'Pause':position>=duration?'Replay':'Play');
  playButton.setAttribute('aria-label',japanese?(playing?'アニメーションを一時停止':'アニメーションを再生'):(playing?'Pause animation':'Play animation'));
}
function setPosition(value){position=clamp(Number(value)||0,0,duration);lastFrame=0;render();}
function setPlaying(value){if(value&&position>=duration)position=0;playing=Boolean(value);lastFrame=0;render();}
playButton.addEventListener('click',()=>setPlaying(!playing));
document.querySelector('#restart').addEventListener('click',()=>{setPosition(0);setPlaying(true);});
seek.addEventListener('input',()=>setPosition(seek.value));
document.querySelector('#speed').addEventListener('click',event=>{const rates=[1,1.5,2,.75];speed=rates[(rates.indexOf(speed)+1)%rates.length];event.currentTarget.textContent=`${speed}×`;event.currentTarget.setAttribute('aria-label',japanese?`再生速度：${speed}倍`:`Playback speed: ${speed} times`);});
document.addEventListener('keydown',event=>{
  if(/INPUT|BUTTON|A|SUMMARY/.test(event.target.tagName))return;
  if(event.code==='Space'){event.preventDefault();setPlaying(!playing);}
  if(event.code==='ArrowRight'){event.preventDefault();setPosition(position+5);}
  if(event.code==='ArrowLeft'){event.preventDefault();setPosition(position-5);}
});
reducedMotion.addEventListener('change',event=>{if(event.matches)setPlaying(false);});
function tick(now){
  if(playing&&lastFrame){position=Math.min(duration,position+(now-lastFrame)/1000*speed);if(position>=duration)playing=false;render();}
  lastFrame=now;requestAnimationFrame(tick);
}
// Used for deterministic review and recording through the collaborative browser.
window.panelDemo={seek:setPosition,play:()=>setPlaying(true),pause:()=>setPlaying(false),get state(){return {position,playing,speed,duration,chapter:chapters[activeChapter]?.name};},chapters:chapters.map(({name,start,duration})=>({name,start,duration}))};
render();requestAnimationFrame(tick);
