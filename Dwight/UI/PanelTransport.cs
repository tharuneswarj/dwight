using System;

namespace Dwight.UI
{
  /// <summary>GH-free helpers for the JS -&gt; C# panel bridge, so the transport parsing is headless-testable
  /// (link-compiled into the test projects, the BehaviourFingerprintCore pattern).</summary>
  internal static class PanelTransport
  {
    /// <summary>RR8 #16: the title-transport fallback (`document.title = "dwight:" + cmd + ":" + Date.now()`)
    /// forces a title CHANGE by appending a millisecond nonce - because DocumentTitleChanged only fires when the
    /// title actually differs, so a repeated command needs a fresh tail. The old parse (`Split(':')[0]`) stripped
    /// that nonce but ALSO the command's own payload, so `simplifyapply:1,2,3` arrived as `simplifyapply` with an
    /// EMPTY ticked set - the intermittent "Nothing selected". Strip ONLY a trailing `:&lt;all-digits&gt;` nonce,
    /// preserving every inner colon, so payload commands (simplifyapply, arrange, moveto, resize) survive intact.</summary>
    public static string StripTitleNonce(string cmd)
    {
      if (string.IsNullOrEmpty(cmd)) return cmd;
      var i = cmd.LastIndexOf(':');
      if (i <= 0 || i >= cmd.Length - 1) return cmd;   // no separator, or nothing after it
      for (var k = i + 1; k < cmd.Length; k++)
        if (cmd[k] < '0' || cmd[k] > '9') return cmd;  // tail is not a pure nonce -> leave the command untouched
      return cmd.Substring(0, i);
    }
  }
}
