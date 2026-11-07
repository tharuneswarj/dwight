// Brief EI: Learn's deck of quiz cards, drawn from the host's view and nothing else. Every word arrives written by the plugin and is set as text; every answer goes to
// the host, which grades it, so the page holds no truth. Shared by the panel and the Focus bar, so one card cannot be drawn two ways.
// Brief EO: a why-card can be answered in the person's own words, which the host's judge reads; a name on a card is the anchor the chat draws, and its click asks the host
// to show that component (glow only, never a selection); the result card reads what was missed and offers Understand, Play again and Ask the chat.
var DwightDeck=(function(){
  var cur=null, picks=[];
  function el(tag,cls,text){ var e=document.createElement(tag); if(cls) e.className=cls; if(text!=null) e.textContent=String(text); return e; }
  function send(c){ dwightSend('deck:'+c); }
  function answer(a){ send('answer:'+encodeURIComponent(a)); }
  function field(){ return document.querySelector('.deck-field input'); }
  function ownField(){ return document.querySelector('.deck-own input'); }
  // Words with names in them: a string is text, an object { t, id } is a name the host knows, drawn as the chat's anchor. Names are live only where a click cannot also mean an answer.
  function rich(e,seg,text,live){ e.textContent=''; if(!seg||!seg.length){ e.textContent=text==null?'':String(text); return e; }
    seg.forEach(function(s){ if(typeof s==='string'){ e.appendChild(document.createTextNode(s)); return; }
      if(!live){ e.appendChild(document.createTextNode(s.t)); return; }
      var a=el('span','anch',s.t); a.setAttribute('role','link'); a.tabIndex=0; a.title=s.t;
      a.onclick=function(ev){ ev.stopPropagation(); send('look:'+s.id); };
      a.onkeydown=function(ev){ if(ev.key==='Enter'){ ev.preventDefault(); ev.stopPropagation(); send('look:'+s.id); } };
      e.appendChild(a); });
    return e; }
  function richEl(tag,cls,seg,text,live){ return rich(el(tag,cls),seg,text,live); }
  // Check: a set of ticks, or a typed value, goes to the host whole. An empty answer is not sent.
  function check(){ if(!cur||cur.view!=='card'||cur.turned||cur.busy) return;
    if(cur.form==='all'){ if(!picks.length) return; answer('s:'+picks.slice().sort(function(a,b){return a-b;}).join(',')); }
    else if(cur.form==='enter'){ var f=field(); if(!f) return; var v=f.value.trim(); if(v) answer('v:'+v); } }
  // Brief EO: own words, judged by the host. An empty field sends nothing.
  function own(){ if(!cur||cur.view!=='card'||cur.turned||cur.busy||!cur.own) return; var f=ownField(); if(!f) return; var v=f.value.trim(); if(v) answer('t:'+v); }
  function pick(i){ if(!cur||cur.view!=='card'||cur.turned||cur.busy||!cur.options||i<0||i>=cur.options.length) return;
    if(cur.form==='one'){ answer('o:'+i); return; }
    var at=picks.indexOf(i); if(at<0) picks.push(i); else picks.splice(at,1);
    var b=document.querySelectorAll('.deck-opt')[i]; if(b){ b.classList.toggle('ticked',at<0); b.setAttribute('aria-pressed',at<0?'true':'false'); } }
  function sw(id,label,on,cmd){ var r=el('div','swrow'); var l=el('label',null,label); l.setAttribute('for',id); r.appendChild(l);
    var b=el('button','sw'); b.id=id; b.setAttribute('role','switch'); b.setAttribute('aria-label',label); b.setAttribute('aria-checked',on?'true':'false'); b.appendChild(el('i'));
    b.onclick=function(){ dwightSend(typeof cmd==='function'?cmd(b):cmd); }; r.appendChild(b); return r; }
  function arrow(cls,label,cmd,turned){ var b=el('button','iconbtn '+cls); b.title=label; b.setAttribute('aria-label',label); b.innerHTML='<svg class="ic'+(turned?' turned':'')+'" aria-hidden="true"><use href="#ic-chev"/></svg>'; b.onclick=function(){ send(cmd); }; return b; }
  // The card's footer: back, the card's one action, and in the panel Focus and Dim. On the bar, whose strip has its own arrows and switches, only the action, and no footer at all without one.
  function foot(card,v,opt,main){ var f=el('div','deck-foot'), bar=!!(opt&&opt.bar);
    if(v.back&&!bar) f.appendChild(arrow('deck-back','Back: Left arrow','back',true));
    if(main) f.appendChild(main);
    if(opt&&opt.switches){ f.appendChild(el('span','spacer')); f.appendChild(sw('deck-focus','Focus',opt.focus,'walk:focus')); f.appendChild(sw('deck-dim','Dim',opt.dim,function(b){ return 'walk:dim:'+(b.getAttribute('aria-checked')==='true'?'off':'on'); })); }
    if(f.childNodes.length) card.appendChild(f); }
  function start(card,v,opt){
    var h=el('div','deck-head'); h.appendChild(el('span','deck-kicker',v.kicker)); h.appendChild(el('span','deck-count',v.count)); card.appendChild(h);
    card.appendChild(el('div','deck-title',v.title));
    card.appendChild(el('p','deck-gist',v.gist));
    if(v.cost) card.appendChild(el('p','deck-cost',v.cost));
    var b=el('button','btn primary deck-start',v.start); b.onclick=function(){ send('start'); };
    foot(card,v,opt,b); }
  // "Why", with the reasons the model gave: shown on a turned why-card and walked by Understand.
  function why(w){ var box=el('div','deck-why'); box.appendChild(el('div','deck-whyhead',w.head)); box.appendChild(richEl('div','deck-whytext',w.seg,w.text,true)); return box; }
  function question(card,v,opt){
    var h=el('div','deck-head'); h.appendChild(el('span','deck-of',v.of));
    var t=el('span','deck-ticks'); t.setAttribute('aria-hidden','true'); (v.ticks||[]).forEach(function(k){ t.appendChild(el('span','deck-tick '+k)); }); h.appendChild(t); card.appendChild(h);
    card.appendChild(richEl('div','deck-q',v.q,v.question,true));
    if(v.prompt) card.appendChild(el('div','deck-prompt',v.prompt));
    // An open option is a button that answers; a turned one is a row whose names are doors, because a turned card takes no answer.
    if(v.options){ var os=el('div','deck-opts');
      v.options.forEach(function(o,i){ var b=el(v.turned?'div':'button','deck-opt '+o.state+(o.picked?' picked':'')); if(!v.turned){ b.disabled=!!v.busy; b.onclick=function(){ pick(i); }; }
        if(v.form==='all') b.setAttribute('aria-pressed',o.picked?'true':'false');
        b.appendChild(el('span','deck-key',o.key)); b.appendChild(richEl('span','deck-text',o.seg,o.text,!!v.turned)); os.appendChild(b); });
      card.appendChild(os); }
    if(v.field){ var fw=el('div','deck-field'); var inp=el('input'); inp.type='text'; inp.setAttribute('inputmode','decimal'); inp.setAttribute('aria-label',v.field.placeholder||'');
      inp.placeholder=v.field.placeholder||''; inp.value=v.field.value||''; inp.disabled=!!v.turned; fw.appendChild(inp); card.appendChild(fw); }
    if(v.own){ var ow=el('div','deck-own'); var ol=el('label',null,v.own.label); ol.setAttribute('for','deck-own'); ow.appendChild(ol);
      var row=el('div','deck-ownrow'); var oi=el('input'); oi.id='deck-own'; oi.type='text'; oi.maxLength=300; oi.placeholder=v.own.placeholder||''; oi.setAttribute('aria-label',v.own.placeholder||'');
      oi.addEventListener('keydown',function(e){ if(e.key==='Enter'){ e.preventDefault(); e.stopPropagation(); own(); } });
      var ob=el('button','btn deck-owncheck',v.own.check); ob.onclick=own; row.appendChild(oi); row.appendChild(ob); ow.appendChild(row); card.appendChild(ow); }
    if(v.ownOff) card.appendChild(el('div','deck-line',v.ownOff));
    if(v.judging){ var j=el('div','deck-judging'); j.setAttribute('role','status'); j.appendChild(el('i')); j.appendChild(el('span',null,v.judging.line));
      var stop=el('button','btn sm deck-stop',v.judging.stop); stop.onclick=function(){ send('stop'); }; j.appendChild(stop); card.appendChild(j); }
    if(v.line) card.appendChild(el('div','deck-line',v.line));
    if(v.peek) card.appendChild(el('div','deck-peek',v.peek));
    if(v.turnedAs){ var tu=v.turnedAs, box=el('div','deck-turn '+(tu.verdict||(tu.right?'right':'wrong')));
      if(tu.stamp) card.appendChild(el('div','deck-stamp '+(tu.verdict||'right'),tu.stamp));
      if(tu.given) box.appendChild(el('div','deck-given',tu.given));
      box.appendChild(richEl('div','deck-answer',tu.answerSeg,tu.answer,true));
      box.appendChild(el('div','deck-mark'+(tu.dwight?' dwight':''),tu.mark));
      if(tu.judged){ var jd=richEl('div','deck-judged',tu.judgedSeg,tu.judged,true); box.appendChild(jd); box.appendChild(el('div','deck-by',tu.judgedBy+(tu.cost?' · '+tu.cost:''))); }
      if(tu.hint) box.appendChild(richEl('div','deck-hint',tu.hintSeg,tu.hint,true));
      if(tu.why) box.appendChild(why(tu.why));
      card.appendChild(box); }
    var main=null;
    if(v.turned&&v.next){ main=el('button','btn primary deck-next',v.next); main.onclick=function(){ send('next'); }; }
    else if(v.check&&!v.busy){ main=el('button','btn primary deck-check',v.check); main.onclick=check; }
    foot(card,v,opt,main); }
  function result(card,v,opt){
    if(v.stamp) card.appendChild(el('div','deck-stamp big',v.stamp));
    if(v.streak) card.appendChild(el('div','deck-streak',v.streak));
    if(v.reading) card.appendChild(el('p','deck-reading',v.reading));
    if(v.busy){ var j=el('div','deck-judging'); j.setAttribute('role','status'); j.appendChild(el('i')); j.appendChild(el('span',null,v.line)); card.appendChild(j); }
    else if(v.line) card.appendChild(el('div','deck-line',v.line));
    if(v.missedHead) card.appendChild(el('div','deck-mhead',v.missedHead));
    (v.missed||[]).forEach(function(m){ var b=el('button','deck-miss',m.text); b.onclick=function(){ send('miss:'+m.k); }; card.appendChild(b); });
    if(v.note) card.appendChild(el('p','deck-note',v.note));
    var d=el('div','deck-doors'); (v.doors||[]).forEach(function(o,i){ var b=el('button','btn'+(i===0?' primary':''),o.label); b.title=o.title||o.label; b.onclick=function(){ send(o.cmd); }; d.appendChild(b); });
    if(d.childNodes.length) card.appendChild(d);
    if(v.links&&v.links.length){ var l=el('div','deck-links'); v.links.forEach(function(o){ var b=el('button','deck-link',o.label); b.title=o.title||o.label; b.onclick=function(){ send(o.cmd); }; l.appendChild(b); }); card.appendChild(l); }
    foot(card,v,null,null); }
  // Brief EO unit 4: Understand, one missed card at a time, with no new call.
  function understand(card,v,opt){
    var h=el('div','deck-head'); h.appendChild(el('span','deck-kicker',v.kicker)); h.appendChild(el('span','deck-count',v.of)); card.appendChild(h);
    card.appendChild(richEl('div','deck-q',v.q,v.question,true));
    var box=el('div','deck-turn wrong');
    if(v.given) box.appendChild(el('div','deck-given',v.given));
    box.appendChild(richEl('div','deck-answer',v.answerSeg,v.answer,true));
    box.appendChild(el('div','deck-mark'+(v.dwight?' dwight':''),v.mark));
    if(v.why&&v.why.text) box.appendChild(why(v.why));
    card.appendChild(box);
    var f=el('div','deck-foot');
    if(v.back){ var b=el('button','iconbtn deck-back'); b.title='Back'; b.setAttribute('aria-label','Back'); b.innerHTML='<svg class="ic turned" aria-hidden="true"><use href="#ic-chev"/></svg>'; b.onclick=function(){ send('understand:'+(v.at-1)); }; f.appendChild(b); }
    var n=el('button','btn primary deck-next',v.next); n.onclick=function(){ send(v.next===v.score?'score':'understand:'+(v.at+1)); }; f.appendChild(n);
    if(v.next!==v.score){ var s=el('button','deck-link',v.score); s.onclick=function(){ send('score'); }; f.appendChild(s); }
    card.appendChild(f); }
  // Draw one view into host. opt.switches puts Focus and Dim in the card's footer (the panel); the bar has its own.
  function render(host,v,opt){ host.textContent=''; cur=v; picks=[]; if(!v) return;
    var deck=el('div','deck'), card=el('div','deck-card '+v.view); deck.appendChild(card);
    if(v.view==='start') start(card,v,opt); else if(v.view==='card') question(card,v,opt); else if(v.view==='result') result(card,v,opt); else if(v.view==='understand') understand(card,v,opt);
    host.appendChild(deck);
    var f=field(); if(f&&!f.disabled){ f.addEventListener('keydown',function(e){ if(e.key==='Enter'){ e.preventDefault(); e.stopPropagation(); check(); } }); try{ f.focus(); }catch(e){} } }
  // Keys inside the page (Brief EI): the arrows move between cards, 1 to 4 pick an option, Enter checks or moves on. Each has its visible control. Returns whether it took the key.
  function key(e){ if(!cur||e.ctrlKey||e.altKey||e.metaKey) return false;
    var typing=e.target&&e.target.tagName==='INPUT';
    if(!typing&&(e.key==='ArrowRight'||e.key==='ArrowLeft')){ if(cur.view==='understand') return false; send(e.key==='ArrowRight'?'next':'back'); return true; }
    if(!typing&&cur.view==='card'&&!cur.turned&&!cur.busy&&/^[1-4]$/.test(e.key)){ pick(+e.key-1); return true; }
    if(e.key==='Enter'&&!typing){ if(cur.view==='start'){ send('start'); return true; } if(cur.view==='card'){ if(cur.turned) send('next'); else check(); return true; } }
    return false; }
  return {render:render,key:key,shown:function(){ return !!cur; }};
})();
