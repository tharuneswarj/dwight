  // ADR-088's second wall. The first is ModelMarkdown, which leaves a link its target only when that is http or https. This one is here for any anchor that gets past it: a click on an
  // anchor whose target is anything else goes nowhere and runs nothing. The shells allow inline script, which is what would let a javascript: target run, and script here can reach
  // dwightSend. One listener, on the document and capturing, so it stands ahead of everything a page draws model text into, however it was drawn. Inlined by ShellAssets, from
  // /*__DWIGHT_LINKS__*/, into the shells that draw model text: a security rule in three copies is how one copy drifts.
  //
  // The one exception is the plugin's own: ChatAnchors and BuildProgress write class="anch" href="#", whose click is their onclick. Model text cannot write a class, and a [x](#) is stopped by the first wall.
  (function(){
    function guard(e){ var a=e.target&&e.target.closest?e.target.closest('a'):null; if(!a||!a.hasAttribute('href')) return;
      var href=a.getAttribute('href');
      if(href==='#'&&a.classList.contains('anch')) return;
      // The target as it stands, neither decoded nor trimmed. The test is anchored and allows, so anything in front of the scheme fails, encoded or not. Decoding or trimming could refuse nothing more,
      // since http: decodes to itself: all either could do is let %68ttp: or a leading space through. The render leaves a kept target with nothing in front of it.
      if(/^https?:/i.test(href)) return;
      e.preventDefault(); e.stopPropagation(); }
    document.addEventListener('click',guard,true);
    document.addEventListener('auxclick',guard,true);   // a middle click follows a link as well
  })();
