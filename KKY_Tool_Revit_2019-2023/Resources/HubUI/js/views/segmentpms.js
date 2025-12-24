import { clear, div, toast, setBusy, showExcelSavedDialog } from '../core/dom.js';
import { renderTopbar } from '../core/topbar.js';
import { post, onHost } from '../core/bridge.js';

const LS_KEY = 'kky_segmentpms_opts';

function loadOpts(){
  try{ return Object.assign({ tolMm:0.01, ndRound:3, unit:'mm' }, JSON.parse(localStorage.getItem(LS_KEY)||'{}')); }
  catch{ return { tolMm:0.01, ndRound:3, unit:'mm' }; }
}
function saveOpts(o){ localStorage.setItem(LS_KEY, JSON.stringify(o)); }

export function renderSegmentPms(){
  const root=document.getElementById('app'); clear(root);
  renderTopbar(root,true); const top=root.firstElementChild; if(top) top.classList.add('hub-topbar');

  const opts=loadOpts();
  const state={
    scanFiles:[],
    scanChecked:new Set(),
    regFiles:[],
    regChecked:new Set(),
    pmsOpts:[],
    defaultMap:new Map(),
    pipes:[],
    maps:new Map(),
    results:null,
    extractStatus:'미실행',
    extractSummary:'',
    pmsLoaded:false,
    busy:false
  };

  const page=div('feature-shell segmentpms-page');
  const header=div('feature-header');
  const heading=div('feature-heading');
  heading.innerHTML=`<span class="feature-kicker">PipeType - PMS</span><h2 class="feature-title">Segment 매핑/검증</h2><p class="feature-sub">RVT를 추출한 뒤 PMS와 매핑하여 ND별 ID/OD를 비교합니다.</p>`;
  const headerActions=div('feature-actions');
  header.append(heading, headerActions);
  page.append(header);

  const control=div('segmentpms-control section');
  control.innerHTML=`<div class="segmentpms-row">
    <label>ND Join Round</label><input type="number" value="${opts.ndRound||3}" min="0" max="6" step="1">
    <label>허용오차(mm)</label><input type="number" value="${opts.tolMm||0.01}" step="0.01">
    <label>PMS 단위</label><select><option value="mm">mm</option><option value="inch">inch</option></select>
  </div>`;
  const numRound=control.querySelector('input[type="number"]');
  const tolBox=control.querySelectorAll('input[type="number"]')[1];
  const unitSel=control.querySelector('select'); unitSel.value=opts.unit||'mm';
  numRound.addEventListener('change', commitOpts); tolBox.addEventListener('change', commitOpts); unitSel.addEventListener('change', commitOpts);
  function commitOpts(){ saveOpts({ ndRound:parseInt(numRound.value||'3'), tolMm:parseFloat(tolBox.value||'0.01'), unit:String(unitSel.value||'mm') }); }
  page.append(control);

  /* Section A: 대상 RVT 파일 */
  const filesSection=div('segmentpms-files section');
  const filesHeader=document.createElement('div'); filesHeader.className='section-header';
  const filesTitle=document.createElement('h3'); filesTitle.textContent='대상 RVT 파일';
  const filesActions=div('segmentpms-actions-row');
  const btnBrowse=cardBtn('폴더 선택', ()=>post('export:browse-folder',{}));
  const btnAddReg=cardBtn('선택 파일 등록', onAddRegistered);
  const btnClearReg=cardBtn('등록 리스트 지우기', ()=>{ state.regFiles=[]; state.regChecked.clear(); renderRegList(); updateButtons(); });
  filesActions.append(btnBrowse, btnAddReg, btnClearReg);
  filesHeader.append(filesTitle, filesActions);

  const filesGrid=div('segmentpms-files-grid');
  /* 스캔 리스트 */
  const scanWrap=document.createElement('div');
  const scanTitle=document.createElement('h4'); scanTitle.textContent='스캔 리스트(현재 폴더 RVT)';
  const scanTable=document.createElement('table'); scanTable.className='segmentpms-table';
  scanTable.innerHTML='<thead><tr><th><input type="checkbox"></th><th>파일명</th><th>폴더</th></tr></thead>';
  const scanBody=document.createElement('tbody'); scanTable.append(scanBody);
  scanWrap.append(scanTitle, scanTable);
  /* 등록 리스트 */
  const regWrap=document.createElement('div'); regWrap.className='segmentpms-reglist';
  const regTitle=document.createElement('h4'); regTitle.textContent='등록된 RVT (추출 대상) — 0개';
  const regList=document.createElement('ul');
  regWrap.append(regTitle, regList);

  filesGrid.append(scanWrap, regWrap);
  filesSection.append(filesHeader, filesGrid);
  page.append(filesSection);

  /* Section B: 추출 */
  const extractSection=div('segmentpms-extract section');
  const extractHeader=document.createElement('div'); extractHeader.className='section-header';
  extractHeader.innerHTML='<h3>추출</h3>';
  const extractActions=div('segmentpms-actions-row');
  const btnExtract=cardBtn('추출 시작', onExtract);
  const btnSaveExtract=cardBtn('추출 데이터 저장', ()=>post('segmentpms:save-extract',{}));
  const btnOpenExtract=cardBtn('추출 파일 불러오기', ()=>post('segmentpms:open-extract',{}));
  extractActions.append(btnExtract, btnSaveExtract, btnOpenExtract);
  extractHeader.append(extractActions);
  const extractInfo=div('segmentpms-summary'); extractInfo.textContent='추출 상태: 미실행';
  extractSection.append(extractHeader, extractInfo);
  page.append(extractSection);

  /* Section C: 매핑 */
  const mappingSection=div('segmentpms-mapping section');
  const mapHeader=document.createElement('div'); mapHeader.className='section-header';
  const mapTitle=document.createElement('h3'); mapTitle.textContent='PipeType별 Segment ↔ PMS Segment 선택';
  mapHeader.append(mapTitle);
  mappingSection.append(mapHeader);
  const mapTable=document.createElement('table'); mapTable.className='segmentpms-table';
  mapTable.innerHTML='<thead><tr><th>파일</th><th>PipeType</th><th>Revit Segment</th><th>PMS Segment (CLASS 포함)</th><th>직접 입력</th><th>Source</th></tr></thead>';
  const mapBody=document.createElement('tbody'); mapTable.append(mapBody); mappingSection.append(mapTable);
  page.append(mappingSection);

  /* Section D: 비교 결과 */
  const resultSection=div('segmentpms-result section');
  const resultHeader=document.createElement('div'); resultHeader.className='section-header';
  const resTitle=document.createElement('h3'); resTitle.textContent='비교 결과';
  const resultActions=div('segmentpms-actions-row');
  const btnRegister=cardBtn('PMS 등록/업데이트', onRegister);
  const btnLoadMap=cardBtn('TXT 기본 매핑 불러오기', onLoadMap);
  const btnRun=cardBtn('검토 시작', onRun);
  const btnSave=cardBtn('결과 엑셀 저장', ()=>{ if(!state.results){ toast('저장할 결과가 없습니다.','err'); return; } post('segmentpms:save-excel', state.results); });
  resultActions.append(btnRegister, btnLoadMap, btnRun, btnSave);
  resultHeader.append(resTitle, resultActions);
  const resInfo=div('segmentpms-summary'); resInfo.textContent='결과 없음';
  const resTable=document.createElement('table'); resTable.className='segmentpms-table';
  resTable.innerHTML='<thead><tr><th>파일</th><th>PipeType</th><th>Rule</th><th>Segment</th><th>CLASS</th><th>PMS Segment</th><th>ND(mm)</th><th>Revit ID/OD</th><th>PMS ID/OD</th><th>Status</th></tr></thead>';
  const resBody=document.createElement('tbody'); resTable.append(resBody);
  resultSection.append(resultHeader, resInfo, resTable);
  page.append(resultSection);

  headerActions.append(btnRegister, btnLoadMap, btnBrowse, btnExtract, btnSaveExtract, btnOpenExtract, btnRun, btnSave);
  root.append(page);

  onHost(handleHost);
  renderScan();
  updateButtons();

  function onRegister(){ setBusy(true,'PMS 등록 중'); post('segmentpms:register-pms',{unit:String(unitSel.value)}); }
  function onLoadMap(){ post('segmentpms:load-defaultmap',{}); }
  function onAddRegistered(){
    const selected=[...state.scanChecked];
    if(!selected.length){ toast('등록할 파일을 선택하세요.','err'); return; }
    selected.forEach(p=>{
      if(!state.regFiles.some(x=>x.toLowerCase()===p.toLowerCase())){
        state.regFiles.push(p);
      }
      state.regChecked.add(p);
    });
    renderRegList(); updateButtons();
  }
  function onExtract(){
    const targets = state.regFiles.filter(f=>state.regChecked.has(f));
    if(!targets.length){ toast('등록된 RVT 파일이 없습니다. 먼저 등록 후 체크하세요.','err'); return; }
    setBusy(true,'RVT 추출 중'); state.busy=true; updateButtons();
    post('segmentpms:extract',{ files: targets, ndRound:parseInt(numRound.value||'3') });
  }
  function onRun(){
    if(!state.pipes.length){ toast('추출 데이터를 먼저 준비하세요.','err'); return; }
    if(!state.pmsLoaded){ toast('PMS를 등록한 후 실행하세요.','err'); return; }
    const maps=[...state.maps.values()].map(m=>({ file:m.file, pipeType:m.pipeType, ruleIndex:m.ruleIndex, segmentId:m.segmentId, segmentKey:m.segmentKey, cls:m.cls, segment:m.segment, source:m.source||'Manual' }));
    setBusy(true,'비교 실행'); state.busy=true; updateButtons();
    post('segmentpms:run',{ maps, ndRound:parseInt(numRound.value||'3'), tolMm:parseFloat(tolBox.value||'0.01') });
  }

  function renderScan(){
    scanBody.innerHTML='';
    const master=scanTable.querySelector('thead input[type="checkbox"]');
    const allChecked = state.scanFiles.length>0 && state.scanFiles.every(f=>state.scanChecked.has(f));
    master.checked = allChecked;
    master.onchange=()=>{
      if(master.checked){ state.scanChecked=new Set(state.scanFiles); }
      else { state.scanChecked=new Set(); }
      renderScan();
    };
    state.scanFiles.forEach(p=>{
      const tr=document.createElement('tr');
      const ck=document.createElement('input'); ck.type='checkbox'; ck.checked=state.scanChecked.has(p);
      ck.onchange=()=>{ if(ck.checked) state.scanChecked.add(p); else state.scanChecked.delete(p); updateButtons(); };
      const tdCk=document.createElement('td'); tdCk.append(ck); tr.append(tdCk);
      const name=shortFile(p); tr.append(td(name));
      const dir=shortDir(p); tr.append(td(dir));
      scanBody.append(tr);
    });
  }

  function renderRegList(){
    regList.innerHTML='';
    regTitle.textContent=`등록된 RVT (추출 대상) — ${state.regFiles.length}개`;
    state.regFiles.forEach(p=>{
      const li=document.createElement('li'); li.title=p;
      const wrap=document.createElement('div'); wrap.style.display='flex'; wrap.style.alignItems='center'; wrap.style.gap='6px';
      const ck=document.createElement('input'); ck.type='checkbox'; ck.checked=state.regChecked.has(p);
      ck.onchange=()=>{ if(ck.checked) state.regChecked.add(p); else state.regChecked.delete(p); updateButtons(); };
      const text=document.createElement('span'); text.textContent=`${shortFile(p)} — ${shortDir(p)}`; text.style.flex='1'; text.style.overflow='hidden'; text.style.textOverflow='ellipsis'; text.style.whiteSpace='nowrap';
      const btnDel=document.createElement('button'); btnDel.type='button'; btnDel.className='btn btn-ghost'; btnDel.textContent='×';
      btnDel.title='제거';
      btnDel.onclick=()=>{
        state.regFiles=state.regFiles.filter(x=>x.toLowerCase()!==p.toLowerCase());
        state.regChecked.delete(p);
        renderRegList(); updateButtons();
      };
      wrap.append(ck, text, btnDel);
      li.append(wrap);
      regList.append(li);
    });
  }

  function buildMappingTable(){
    mapBody.innerHTML=''; state.maps.clear();
    if(!state.pipes.length){
      const tr=document.createElement('tr'); const td1=document.createElement('td'); td1.colSpan=6; td1.textContent='추출 결과가 없습니다.'; tr.append(td1); mapBody.append(tr); updateButtons(); return;
    }
    state.pipes.forEach(row=>{
      const tr=document.createElement('tr');
      tr.append(td(shortFile(row.file)), td(row.pipeType));

      const candSel=document.createElement('select');
      row.candidates.forEach(c=>{ candSel.append(new Option(`${c.ruleIndex} | ${c.segmentKey||c.segmentName||'(미지정)'}`, `${c.ruleIndex}`)); });
      const pmsSel=document.createElement('select');
      const customBox=document.createElement('input'); customBox.type='text'; customBox.placeholder='직접 입력';
      const sourceTag=document.createElement('small'); sourceTag.textContent='';

      const mapKey=`${row.file}|${row.pipeType}`;
      let chosenRule = typeof row.defaultRuleIndex==='number' ? row.defaultRuleIndex : (row.candidates[0]?.ruleIndex||0);
      candSel.value=String(chosenRule);

      const currentCand = ()=>row.candidates.find(c=>String(c.ruleIndex)===candSel.value) || row.candidates[0];

      applyDefaultForSegment(currentCand()?.segmentKey, pmsSel, sourceTag);
      commitMap(mapKey, currentCand(), pmsSel.value, customBox.value, sourceTag.textContent||'None');

      candSel.onchange=()=>{
        applyDefaultForSegment(currentCand()?.segmentKey, pmsSel, sourceTag);
        commitMap(mapKey, currentCand(), pmsSel.value, customBox.value, sourceTag.textContent||'None');
      };

      pmsSel.onchange=()=>{ commitMap(mapKey, currentCand(), pmsSel.value, customBox.value, sourceTag.textContent||'Manual'); };
      customBox.oninput=()=>{ commitMap(mapKey, currentCand(), pmsSel.value, customBox.value, 'Manual'); };

      const tdCand=document.createElement('td'); tdCand.append(candSel);
      const tdPms=document.createElement('td'); tdPms.append(pmsSel);
      const tdCustom=document.createElement('td'); tdCustom.append(customBox);
      const tdSrc=document.createElement('td'); tdSrc.append(sourceTag);
      tr.append(tdCand, tdPms, tdCustom, tdSrc);
      mapBody.append(tr);
    });
    updateButtons();
  }

  function commitMap(mapKey, cand, pmsVal, customVal, source){
    const chosen = cand || {};
    const val=String(pmsVal||'');
    const parts=val.split('|||');
    const cls=parts[0]||'';
    let seg=parts[1]||'';
    let src=source||'Manual';
    if(customVal){ seg=customVal; src='Manual'; }
    const mapObj={
      file:mapKey.split('|')[0],
      pipeType:mapKey.split('|')[1],
      ruleIndex:chosen.ruleIndex||0,
      segmentId:chosen.segmentId||0,
      segmentKey:chosen.segmentKey||'',
      cls:cls,
      segment:seg,
      source:src
    };
    state.maps.set(mapKey,mapObj);
  }

  function applyDefaultForSegment(segKey, pmsSel, srcTag){
    const defaults = state.defaultMap.get(segKey)||[];
    fillPmsOptions(pmsSel, defaults, segKey);
    if(defaults.length===1){
      const match = state.pmsOpts.find(o=>o.segment===defaults[0]);
      if(match){ pmsSel.value=`${match.cls}|||${match.segment}`; srcTag.textContent='DefaultTXT'; return; }
    }
    srcTag.textContent='Manual';
  }

  function fillPmsOptions(sel, defaults, segKey){
    const oldVal=sel.value;
    sel.innerHTML='';
    const dedup=new Set();
    const addOpt=(o, label)=>{
      const key=`${o.cls}|||${o.segment}`;
      if(dedup.has(key)) return;
      dedup.add(key);
      sel.append(new Option(label, key));
    };
    sel.append(new Option('(선택)', ''));
    defaults.forEach(d=>{
      const found=state.pmsOpts.find(o=>o.segment===d);
      if(found) addOpt(found, `${found.label} (TXT추천)`);
    });
    state.pmsOpts.forEach(o=>addOpt(o,o.label));
    sel.value=oldVal;
  }

  function paintResults(payload){ btnSave.disabled=false; state.results=payload; clear(resBody);
    const rows=(payload?.compare)||[];
    resInfo.textContent=`총 ${rows.length}건`;
    rows.slice(0,400).forEach(r=>{
      const tr=document.createElement('tr');
      tr.append(td(r.File), td(r.PipeTypeName), td(r.SegmentRuleIndex), td(r.RevitSegmentKey), td(r.CLASS), td(r.PMS_SegmentKey), td(r.ND_mm));
      tr.append(td(`${r.Revit_ID}/${r.Revit_OD}`));
      tr.append(td(`${r.PMS_ID}/${r.PMS_OD}`));
      const status=td(r.Status); status.dataset.status=String(r.Status||''); tr.append(status); resBody.append(tr);
    });
  }

  function updateButtons(){
    btnExtract.disabled = state.busy || state.regChecked.size===0;
    btnSaveExtract.disabled = state.busy || !state.pipes.length;
    btnOpenExtract.disabled = state.busy ? true : false;
    btnRun.disabled = state.busy || !state.pipes.length || !state.pmsLoaded;
    btnSave.disabled = state.busy || !state.results;
  }

  function td(v){ const t=document.createElement('td'); t.textContent=v==null?'':v; return t; }
  function shortFile(p){ return String(p||'').split(/[/\\]/).pop(); }
  function shortDir(p){ const parts=String(p||'').split(/[/\\]/); parts.pop(); return parts.join('/'); }

  function handleHost(msg){
    if(!msg||!msg.ev) return;
    switch(msg.ev){
      case 'segmentpms:pms-registered':
        setBusy(false); state.busy=false;
        state.pmsOpts = (msg.payload?.options)||[];
        state.pmsLoaded = true;
        toast('PMS 등록 완료','ok');
        if(state.pipes.length) buildMappingTable();
        updateButtons();
        break;
      case 'segmentpms:defaultmap-loaded':
        state.defaultMap = new Map((msg.payload?.items||[]).map(it=>[String(it.revit||''), [].concat(it.pmsList||[])]));
        if(state.pipes.length) buildMappingTable();
        toast('기본 매핑을 불러왔습니다.','ok');
        break;
      case 'segmentpms:extracted':
        setBusy(false); state.busy=false;
        state.extractStatus='완료';
        state.extractSummary=msg.payload?.summary||'';
        extractInfo.textContent=`추출 상태: ${state.extractStatus} · ${state.extractSummary}`;
        state.pipes = msg.payload?.pipes||[];
        state.pmsOpts = msg.payload?.pms||state.pmsOpts;
        buildMappingTable();
        toast('추출이 완료되었습니다.','ok');
        updateButtons();
        break;
      case 'segmentpms:extract-opened':
        setBusy(false); state.busy=false;
        state.extractStatus='로드됨';
        state.extractSummary=msg.payload?.summary||'';
        extractInfo.textContent=`추출 상태: ${state.extractStatus} · ${state.extractSummary}`;
        state.pipes = msg.payload?.pipes||[];
        buildMappingTable();
        toast('추출 파일을 불러왔습니다.','ok');
        updateButtons();
        break;
      case 'segmentpms:result':
        setBusy(false); state.busy=false;
        paintResults(msg.payload||{});
        updateButtons();
        break;
      case 'segmentpms:saved':
        showExcelSavedDialog('엑셀로 저장했습니다.', msg.payload?.path); break;
      case 'segmentpms:extract-saved':
        showExcelSavedDialog('추출 데이터를 저장했습니다.', msg.payload?.path); break;
      case 'segmentpms:error':
        setBusy(false); state.busy=false; updateButtons();
        toast(msg.payload?.message||'오류가 발생했습니다.','err');
        break;
      case 'export:files': {
        const files = Array.isArray(msg.payload?.files)?msg.payload.files:[];
        state.scanFiles = files;
        state.scanChecked = new Set(files);
        renderScan(); updateButtons();
        break; }
      default: break;
    }
  }
}

function cardBtn(label, onclick){
  const b=document.createElement('button');
  b.type='button'; b.className='btn card-btn'; b.textContent=label; b.onclick=onclick; return b;
}

