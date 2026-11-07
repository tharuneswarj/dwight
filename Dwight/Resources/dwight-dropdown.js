  // Brief BO (Tharun: "let the dropdowns be dropdowns"): the one dropdown. Every choice of two or more, in every shell that has one. The closed control
  // shows the option's name; the menu shows its name and its sentence, so the explanation sits where the choice is made. A native-feeling listbox: arrows,
  // Home, End, Enter, Space and Esc, and the menu opens where it has room. Inlined into a shell by ShellAssets, from /*__DWIGHT_DROPDOWN__*/.
  var _ddOpen=null, _ddSeq=0;
  function _ddClose(focus){ if(!_ddOpen) return; var d=_ddOpen; _ddOpen=null;
    d.menu.remove(); d.menu=null; d.btn.setAttribute('aria-expanded','false'); d.root.classList.remove('open'); d.btn.removeAttribute('aria-activedescendant');
    if(focus) d.btn.focus(); }
  // Below if it fits, above if it fits there, otherwise whole over its control; only a menu taller than the window scrolls.
  function _ddPlace(d){ var m=d.menu, r=d.btn.getBoundingClientRect(), H=window.innerHeight, EDGE=8, GAP=4;
    m.style.left=r.left+'px'; m.style.width=r.width+'px'; m.style.top='0'; m.style.maxHeight='none';
    var h=m.offsetHeight, below=H-EDGE-r.bottom-GAP, above=r.top-EDGE-GAP, top;
    if(h<=below) top=r.bottom+GAP; else if(h<=above) top=r.top-GAP-h; else if(h<=H-2*EDGE) top=H-EDGE-h; else { top=EDGE; m.style.maxHeight=(H-2*EDGE)+'px'; }
    m.style.top=top+'px'; }
  function _ddMark(d,i){ d.active=i; for(var j=0;j<d.menu.children.length;j++){ var c=d.menu.children[j]; c.classList.toggle('act',j===i);
      if(j===i){ d.btn.setAttribute('aria-activedescendant',c.id); if(c.offsetTop<d.menu.scrollTop) d.menu.scrollTop=c.offsetTop; else if(c.offsetTop+c.offsetHeight>d.menu.scrollTop+d.menu.clientHeight) d.menu.scrollTop=c.offsetTop+c.offsetHeight-d.menu.clientHeight; } } }
  function _ddOpenMenu(d){ _ddClose(); var m=d.menu=document.createElement('div'); m.className='ddm'; m.setAttribute('role','listbox'); m.setAttribute('aria-label',d.label);
    d.options.forEach(function(x,i){ var on=x.value===d.value, r=document.createElement('div'); r.className='ddo'+(on?' on':''); r.id='ddo-'+d.id+'-'+i; r.setAttribute('role','option'); r.setAttribute('aria-selected',on?'true':'false');
      var head=document.createElement('div'); head.className='ddo-head'; var n=document.createElement('span'); n.className='ddo-name'; n.textContent=x.name; head.appendChild(n);
      if(on) head.insertAdjacentHTML('beforeend','<svg class="ic sm" aria-hidden="true"><use href="#ic-check"/></svg>'); r.appendChild(head);
      if(x.sentence){ var s=document.createElement('div'); s.className='ddo-sent'; s.textContent=x.sentence; r.appendChild(s); }
      r.addEventListener('mousedown',function(e){ e.preventDefault(); }); r.addEventListener('click',function(){ _ddPick(d,x.value); }); r.addEventListener('mousemove',function(){ if(d.active!==i) _ddMark(d,i); });
      m.appendChild(r); });
    document.body.appendChild(m); _ddOpen=d; d.root.classList.add('open'); d.btn.setAttribute('aria-expanded','true'); _ddPlace(d);
    var cur=0; d.options.forEach(function(x,i){ if(x.value===d.value) cur=i; }); _ddMark(d,cur); }
  function _ddName(d){ var f=d.options.filter(function(x){ return x.value===d.value; })[0]; return f?f.name:''; }
  function _ddPick(d,v){ d.value=v; d.txt.textContent=_ddName(d); _ddClose(true); if(d.onPick) d.onPick(v); }
  // Fill a .dd: { label, value, options:[{value,name,sentence}], onPick }. Called again, it refreshes in place and keeps its focus.
  function ddMake(root,o){ var d=root._dd;
    if(!d){ d=root._dd={ root:root, id:root.id||('dd'+(++_ddSeq)), menu:null, active:-1 }; root.textContent='';
      var b=d.btn=document.createElement('button'); b.type='button'; b.className='dd-sel'; b.setAttribute('role','combobox'); b.setAttribute('aria-haspopup','listbox'); b.setAttribute('aria-expanded','false');
      var t=d.txt=document.createElement('span'); t.className='dd-txt'; b.appendChild(t); b.insertAdjacentHTML('beforeend','<svg class="ic sm dd-car" aria-hidden="true"><use href="#ic-caret"/></svg>'); root.appendChild(b);
      b.addEventListener('click',function(){ if(_ddOpen===d) _ddClose(); else _ddOpenMenu(d); });
      b.addEventListener('keydown',function(e){ var open=_ddOpen===d, n=d.options.length;
        if(!open){ if(e.key==='ArrowDown'||e.key==='ArrowUp'||e.key==='Enter'||e.key===' '){ e.preventDefault(); _ddOpenMenu(d); } return; }
        if(e.key==='ArrowDown'){ e.preventDefault(); _ddMark(d,Math.min(n-1,d.active+1)); }
        else if(e.key==='ArrowUp'){ e.preventDefault(); _ddMark(d,Math.max(0,d.active-1)); }
        else if(e.key==='Home'){ e.preventDefault(); _ddMark(d,0); }
        else if(e.key==='End'){ e.preventDefault(); _ddMark(d,n-1); }
        else if(e.key==='Enter'||e.key===' '){ e.preventDefault(); _ddPick(d,d.options[d.active].value); }
        else if(e.key==='Escape'){ e.preventDefault(); e.stopPropagation(); _ddClose(true); }
        else if(e.key==='Tab') _ddClose(); }); }
    d.label=o.label; d.options=o.options||[]; d.onPick=o.onPick; d.value=o.value; d.btn.setAttribute('aria-label',o.label); d.txt.textContent=_ddName(d);
    root.dwightNames=d.options.map(function(x){ return x.name; }); return d; }
  function ddSet(root,v){ var d=root&&root._dd; if(!d) return; d.value=v; d.txt.textContent=_ddName(d); }
  document.addEventListener('mousedown',function(e){ if(_ddOpen&&!e.target.closest('.ddm')&&!e.target.closest('.dd')) _ddClose(); });
  window.addEventListener('blur',function(){ _ddClose(); });
