namespace Dwight.UI
{
  /// <summary>
  /// Inlines the shared token + component CSS into a surface's HTML by replacing the placeholders
  /// (<c>/*__DWIGHT_TOKENS__*/</c> and <c>/*__DWIGHT_COMPONENTS__*/</c>). WebView2's
  /// <c>LoadHtml(html, null)</c> resolves no relative &lt;link&gt;s, so each surface carries one inlined
  /// copy of the shared base - one substrate, every surface consistent (ADR-048; spec §10).
  /// </summary>
  internal static class ShellAssets
  {
    private static string _tokens;
    private static string _components;
    private static string _fonts;
    private static string _icons;
    private static string _dropdown;
    private static string _links;
    private static string _deck;

    private static string Tokens => _tokens ??= (WebViewSupport.LoadResource("Dwight.Tokens.css") ?? "");
    private static string Components => _components ??= (WebViewSupport.LoadResource("Dwight.Components.css") ?? "");
    // WP-2.2: the self-hosted brand fonts (data URIs), inlined so the shells need no external network.
    private static string Fonts => _fonts ??= (WebViewSupport.LoadResource("Dwight.Fonts.css") ?? "");
    // Brief BO: the one icon table, an inline sprite (ADR-088: no font, no network). An icon is <svg class="ic"><use href="#ic-NAME"/></svg>.
    private static string Icons => _icons ??= (WebViewSupport.LoadResource("Dwight.Icons.svg") ?? "");
    // Brief BO: the one dropdown, for the shells that offer a choice.
    private static string Dropdown => _dropdown ??= (WebViewSupport.LoadResource("Dwight.Dropdown.js") ?? "");
    // ADR-088's second wall, for the shells that draw model text.
    private static string Links => _links ??= (WebViewSupport.LoadResource("Dwight.Links.js") ?? "");
    // Brief EI: Learn's deck, one renderer for the panel and the Focus bar.
    private static string Deck => _deck ??= (WebViewSupport.LoadResource("Dwight.Deck.js") ?? "");

    /// <summary>Replace the fonts + token + component placeholders in <paramref name="html"/> with the
    /// embedded CSS. Safe on null/placeholder-free input (returns it unchanged).</summary>
    public static string Inline(string html)
    {
      if (string.IsNullOrEmpty(html)) return html;
      return html
        .Replace("/*__DWIGHT_FONTS__*/", Fonts)
        .Replace("/*__DWIGHT_TOKENS__*/", Tokens)
        .Replace("/*__DWIGHT_COMPONENTS__*/", Components)
        .Replace("<!--__DWIGHT_ICONS__-->", Icons)
        .Replace("/*__DWIGHT_DROPDOWN__*/", Dropdown)
        .Replace("/*__DWIGHT_LINKS__*/", Links)
        .Replace("/*__DWIGHT_DECK__*/", Deck)
        .Replace("<!--__DWIGHT_MARK__-->", Dwight.Branding.DwightIcon.MarkSvg());  // one beet, every surface
    }
  }
}
