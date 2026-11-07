using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Dwight.Agent
{
  /// <summary>
  /// U102: THE SCRIPT'S OUTPUT, READ FROM STREAMS DWIGHT OWNS. Rhino's <c>RunContext(true, true)</c> gives the script a
  /// <c>PlatformStream</c> for output and for errors: write-only (<c>CanRead</c> and <c>CanSeek</c> false, <c>Length</c> and
  /// <c>Position</c> throw), wired to Rhino's command line. So what a script printed went to the command line, and the report's
  /// "Printed output" was always "none" (found by instrumenting Rhino.Runtime.Code 8.36.26293). The fix is to assign a
  /// <see cref="MemoryStream"/> to each before the run and read it back after; <c>OutputStream</c> and <c>ErrorStream</c> are
  /// read-write <see cref="Stream"/> properties on the context.
  ///
  /// GH-free and reflection-shaped (ADR-091), so the seam is tested with a fake context whose stream properties are settable.
  /// </summary>
  internal static class ScriptStreams
  {
    /// <summary>Can a <see cref="MemoryStream"/> be assigned to this property? The probe refuses to run a script whose output it
    /// cannot read.</summary>
    public static bool Assignable(PropertyInfo p) =>
      p != null && p.CanWrite && p.GetSetMethod() != null && p.PropertyType.IsAssignableFrom(typeof(MemoryStream));

    /// <summary>Give the context two fresh streams, before the run.</summary>
    public static (MemoryStream Out, MemoryStream Err) Attach(object context, PropertyInfo outProp, PropertyInfo errProp)
    {
      var o = new MemoryStream();
      var e = new MemoryStream();
      outProp.SetValue(context, o);
      errProp.SetValue(context, e);
      return (o, e);
    }

    /// <summary>
    /// What was written, from the start, as UTF-8; null when nothing was. Read with <see cref="MemoryStream.ToArray"/>, which is the
    /// whole buffer from position 0 whatever the stream's position, and which still works if the runtime disposed the stream when it
    /// finished. A byte-order mark at the start is dropped.
    /// </summary>
    public static string Read(MemoryStream s)
    {
      if (s == null) return null;
      byte[] bytes;
      try { bytes = s.ToArray(); } catch { return null; }
      if (bytes.Length == 0) return null;
      var text = Encoding.UTF8.GetString(bytes);
      return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
    }
  }
}
