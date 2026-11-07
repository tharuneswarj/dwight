using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Dwight.Llm
{
  /// <summary>One dispatched Server-Sent Event: its 1-based position in the stream, the line it started on,
  /// its <c>event:</c> name (null when none was sent), and its data with multi-line <c>data:</c> fields joined
  /// by a newline, exactly as the SSE specification dispatches them.</summary>
  internal sealed class SseEvent
  {
    public int Index;
    public int Line;
    public string Name;
    public string Data;
  }

  /// <summary>
  /// The reply stream is damaged at a named place (U38, #873). Thrown by the reader and by each provider's
  /// assembler; each provider turns it into the user's failed run with <see cref="SseReader.Refusal"/>.
  /// <see cref="EventIndex"/> is the 1-based event the damage was found in, or 0 when the damage belongs to the
  /// stream as a whole (it ended early); <see cref="Line"/> is the stream line, or 0 when not known.
  /// </summary>
  internal sealed class StreamDamageException : Exception
  {
    public int EventIndex { get; }
    public int Line { get; }
    public string Cause { get; }

    public StreamDamageException(int eventIndex, int line, string cause)
      : base(SseReader.Describe(eventIndex, line, cause))
    {
      EventIndex = eventIndex;
      Line = line;
      Cause = cause;
    }
  }

  /// <summary>
  /// ONE spec-correct reader of a Server-Sent Events reply stream, for every provider (U38, ruled at #873).
  ///
  /// <para>WHAT IT REPLACES. All three providers read their stream with <c>try { JsonNode.Parse(payload) }
  /// catch { continue; }</c>: an event that failed to parse was DROPPED and the stream carried on to a normal
  /// stop. A dropped <c>text_delta</c> is words silently missing from an answer; a dropped argument fragment is
  /// a tool call dispatched with an argument missing, invisible to every check downstream; and the same loop
  /// reads the structured output all five verbs use. <b>Now a damaged reply is REFUSED, naming the event and
  /// the cause, before anything in it is used.</b></para>
  ///
  /// <para>WHY "SPEC-CORRECT" IS THE POINT, NOT A NICETY. The old loops read one <c>data:</c> line as one
  /// event. The specification says an event may carry its data over several <c>data:</c> lines, joined by a
  /// newline, and ends at a blank line. A refusal built on the old reading would fire on a legitimate
  /// multi-line event - a refusal false in a reachable state (#613). Joining the fields is what makes the
  /// refusal TRUE. The rules implemented (WHATWG HTML, "Interpreting an event stream"):</para>
  /// <list type="bullet">
  /// <item>a line ends at CRLF, LF or CR (<see cref="TextReader.ReadLine"/> honours all three); a leading BOM is dropped;</item>
  /// <item>a line starting with <c>:</c> is a comment;</item>
  /// <item><c>name:value</c> - one space after the colon is stripped; a line with no colon is a field with an empty value;</item>
  /// <item><c>data</c> lines accumulate, joined by <c>\n</c>; <c>event</c> names the event; <c>id</c> and <c>retry</c> are read and unused;</item>
  /// <item>a blank line dispatches the event - and an event with no data is not dispatched.</item>
  /// </list>
  ///
  /// <para>WHERE IT IS STRICTER THAN THE SPEC, AND WHY EACH IS STILL TRUE. A browser may forgive what a reader
  /// that DISPATCHES TOOL CALLS must not:</para>
  /// <list type="bullet">
  /// <item><b>A stream that ends inside an event</b> (fields read, no closing blank line) is refused; the spec
  /// discards that event in silence, which is the old defect in another place. Every provider this reads ends
  /// its last event with a blank line - all seven captured streams do.</item>
  /// <item><b>A line that cannot be a field</b> - a field name holding anything but letters, digits, <c>-</c>
  /// or <c>_</c>, such as the second half of a data line broken in two, or a JSON line that lost its
  /// <c>data:</c> - is refused. The spec ignores an unknown field, and ignoring THIS one drops the data it
  /// held. A well-formed unknown field (<c>x-trace: 1</c>) is still ignored, as the spec says.</item>
  /// <item><b>An event with an <c>event:</c> name but no data</b> is refused: every provider this reads sends
  /// data with every named event, so a named event without it has lost its data line.</item>
  /// </list>
  ///
  /// GH-free (ADR-091): tested on the providers' real captured streams (<c>tests/.../Fixtures/streams/</c>),
  /// and on each of them damaged in one place.
  /// </summary>
  internal static class SseReader
  {
    /// <summary>The failed run the user sees, in Dwight's words (U38). It says what was damaged and that
    /// nothing from the reply was used - never a shorter answer.</summary>
    public static LlmException Refusal(StreamDamageException damage) =>
      new LlmException("Dwight could not read the model's reply in full, so it did not act on it: " +
                       (damage?.Message ?? "the reply stream was damaged") + ". Nothing from this reply was used.");

    /// <summary>The place and the cause, as one clause: "event 14 (line 40) is not valid JSON ...".</summary>
    internal static string Describe(int eventIndex, int line, string cause)
    {
      var where = eventIndex > 0
        ? "event " + eventIndex + (line > 0 ? " (line " + line + ")" : "") + " of the reply stream"
        : "the reply stream";
      return where + " " + (cause ?? "was damaged");
    }

    /// <summary>
    /// Read the stream's events in order, spec-correct, refusing the damage described above. Every raw line is
    /// handed to <paramref name="cap"/> (the capture window, #873) before it is interpreted, so a damaged
    /// stream is captured too.
    /// </summary>
    public static IEnumerable<SseEvent> Events(TextReader reader, StreamCapture.Recording cap = null,
      CancellationToken cancel = default)
    {
      var data = new StringBuilder();
      var hasData = false;
      string name = null;
      var startLine = 0;   // the line the pending event's first field sat on; 0 = no event pending
      var lineNo = 0;
      var dispatched = 0;

      string line;
      while ((line = reader.ReadLine()) != null)
      {
        cancel.ThrowIfCancellationRequested();
        cap?.Line(line);
        lineNo++;
        if (lineNo == 1 && line.Length > 0 && line[0] == '﻿') line = line.Substring(1);

        if (line.Length == 0)
        {
          if (startLine > 0)
          {
            if (hasData)
            {
              dispatched++;
              cap?.Data();
              yield return new SseEvent { Index = dispatched, Line = startLine, Name = name, Data = data.ToString() };
            }
            else if (name != null)
              throw new StreamDamageException(dispatched + 1, startLine,
                "is named \"" + name + "\" but carries no data, so its data line was lost");
          }
          data.Clear(); hasData = false; name = null; startLine = 0;
          continue;
        }

        if (line[0] == ':') continue;   // a comment (a keep-alive, on some servers)

        string field, value;
        var colon = line.IndexOf(':');
        if (colon < 0) { field = line; value = ""; }
        else
        {
          field = line.Substring(0, colon);
          value = line.Substring(colon + 1);
          if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);
        }

        if (!IsFieldName(field))
          throw new StreamDamageException(dispatched + 1, lineNo,
            "holds a line that is not an SSE field, so a data line was broken or lost its \"data:\"");

        if (startLine == 0) startLine = lineNo;
        switch (field)
        {
          case "data":
            if (hasData) data.Append('\n');
            data.Append(value);
            hasData = true;
            break;
          case "event":
            name = value;
            break;
          // "id" and "retry" are read and unused (no provider reconnects mid-reply); any other well-formed
          // field name is ignored, as the specification says.
        }
      }

      if (startLine > 0)
        throw new StreamDamageException(dispatched + 1, startLine,
          "was cut off: the stream ended before the blank line that closes it");
    }

    /// <summary>
    /// <see cref="Events"/>, with each event's data parsed as JSON. An event whose data is not JSON is REFUSED
    /// - the defect this unit removes - unless it is exactly <paramref name="sentinel"/> (OpenAI's
    /// <c>[DONE]</c>), which is returned with a null node. A refused payload is also recorded as a capture
    /// sample, so the measurement #873 began keeps counting.
    /// </summary>
    public static IEnumerable<(SseEvent Event, JsonNode Json)> JsonEvents(TextReader reader, string sentinel = null,
      StreamCapture.Recording cap = null, CancellationToken cancel = default)
    {
      foreach (var ev in Events(reader, cap, cancel))
      {
        if (sentinel != null && ev.Data == sentinel) { yield return (ev, null); continue; }

        JsonNode node;
        try { node = JsonNode.Parse(ev.Data); }
        catch (JsonException ex)
        {
          cap?.FailedToParse(ev.Data);
          var at = ex.LineNumber.HasValue
            ? " at data line " + (ex.LineNumber.Value + 1) + ", byte " + (ex.BytePositionInLine ?? 0)
            : "";
          throw new StreamDamageException(ev.Index, ev.Line, "is not valid JSON" + at + ", so part of the reply is missing or broken");
        }
        if (node == null)
          throw new StreamDamageException(ev.Index, ev.Line, "carries a null payload where the reply was expected");
        yield return (ev, node);
      }
    }

    /// <summary>
    /// Run <paramref name="read"/> on one event's JSON, and turn a wrong-SHAPE failure (a field of the wrong
    /// type: <c>GetValue</c> throws <see cref="InvalidOperationException"/> or <see cref="FormatException"/>)
    /// into a refusal that names the event. Without it, a malformed-but-parseable event would surface as an
    /// unexplained crash. The assemblers read with <c>?.</c> throughout, so a MISSING field is theirs to judge.
    /// </summary>
    public static void Shape(SseEvent ev, Action read)
    {
      try { read(); }
      catch (InvalidOperationException) { throw Unexpected(ev); }
      catch (FormatException) { throw Unexpected(ev); }
    }

    private static StreamDamageException Unexpected(SseEvent ev) =>
      new StreamDamageException(ev.Index, ev.Line, "does not have the shape this provider's events have");

    /// <summary>A field name a server could legitimately send: letters, digits, '-' and '_'. A blank-named
    /// field (a line starting with ':') never reaches here - it is a comment.</summary>
    private static bool IsFieldName(string f)
    {
      if (string.IsNullOrEmpty(f)) return false;
      foreach (var c in f)
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_'))
          return false;
      return true;
    }
  }
}
