using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Dwight.UI
{
  /// <summary>
  /// WOFF2 TO A FONT GDI CAN READ (Brief DO). The panel's faces ship inside the assembly as WOFF2 data URIs, and GDI's private font collection reads only TrueType and OpenType, so a note on the canvas
  /// unpacks them here: the one Brotli stream, the WOFF2 transform of glyf and loca undone, and the tables written back as an sfnt. No second copy of a font ships, and no tool runs at build time.
  /// Pure and GH-free. A file this cannot read throws, and the caller falls back to the system's faces. The format is the W3C's WOFF2, section 5.
  /// </summary>
  internal static class Woff2
  {
    private static readonly string[] KnownTags =
    {
      "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
      "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
      "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
      "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    };

    private sealed class Entry
    {
      public string Tag;
      public int Transform;
      public int OrigLength;
      public int StoredLength;
      public byte[] Data;
    }

    public static byte[] ToSfnt(byte[] woff2)
    {
      var r = new Reader(woff2);
      if (r.U32() != 0x774F4632) throw new InvalidDataException("Not WOFF2.");
      var flavor = r.U32();
      r.U32();   // length
      int count = r.U16();
      r.U16();   // reserved
      r.U32();   // the sfnt's size, which is only a hint
      var compressed = (int)r.U32();
      r.Skip(4 + 5 * 4);   // version, and the metadata and private blocks, which a renderer has no use for

      var entries = new List<Entry>();
      for (var i = 0; i < count; i++)
      {
        int flags = r.U8();
        var known = flags & 0x3F;
        var tag = known == 63 ? r.Tag() : KnownTags[known];
        var e = new Entry { Tag = tag, Transform = flags >> 6, OrigLength = r.Base128() };
        var glyphTable = tag == "glyf" || tag == "loca";
        var transformed = glyphTable ? e.Transform != 3 : e.Transform != 0;
        if (transformed && !glyphTable) throw new InvalidDataException("A table transform this does not read: " + tag);
        e.StoredLength = transformed ? r.Base128() : e.OrigLength;
        entries.Add(e);
      }

      var stream = new byte[0];
      using (var input = new MemoryStream(woff2, r.Position, compressed))
      using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
      using (var output = new MemoryStream())
      {
        brotli.CopyTo(output);
        stream = output.ToArray();
      }

      var at = 0;
      foreach (var e in entries)
      {
        e.Data = new byte[e.StoredLength];
        Array.Copy(stream, at, e.Data, 0, e.StoredLength);
        at += e.StoredLength;
      }

      var glyf = entries.FirstOrDefault(e => e.Tag == "glyf");
      var loca = entries.FirstOrDefault(e => e.Tag == "loca");
      if (glyf != null && loca != null && glyf.Transform == 0)
      {
        var (g, l) = Glyphs(glyf.Data);
        glyf.Data = g;
        loca.Data = l;
      }
      return Assemble(flavor, entries);
    }

    /// <summary>The glyph table's seven streams back to TrueType's own glyf and loca.</summary>
    private static (byte[] Glyf, byte[] Loca) Glyphs(byte[] t)
    {
      var h = new Reader(t);
      h.U16();   // reserved
      int options = h.U16();
      int numGlyphs = h.U16();
      int indexFormat = h.U16();
      var sizes = new int[7];
      for (var i = 0; i < 7; i++) sizes[i] = (int)h.U32();
      var offset = h.Position;
      var streams = new Reader[7];
      for (var i = 0; i < 7; i++) { streams[i] = new Reader(t, offset, sizes[i]); offset += sizes[i]; }
      var contours = streams[0];
      var points = streams[1];
      var flags = streams[2];
      var glyph = streams[3];
      var composite = streams[4];
      var bboxBitmapLength = 4 * ((numGlyphs + 31) >> 5);
      var bboxBitmap = new byte[bboxBitmapLength];
      Array.Copy(t, streams[5].Position, bboxBitmap, 0, bboxBitmapLength);
      var bbox = streams[5];
      bbox.Skip(bboxBitmapLength);
      var instructions = streams[6];
      byte[] overlap = null;
      if ((options & 1) != 0)
      {
        overlap = new byte[(numGlyphs + 7) >> 3];
        Array.Copy(t, offset, overlap, 0, overlap.Length);
      }

      var outGlyf = new MemoryStream();
      var w = new BinaryWriterBE(outGlyf);
      var offsets = new long[numGlyphs + 1];

      for (var i = 0; i < numGlyphs; i++)
      {
        offsets[i] = outGlyf.Position;
        var nContours = (short)contours.U16();
        var hasBox = (bboxBitmap[i >> 3] & (0x80 >> (i & 7))) != 0;
        if (nContours == 0)
        {
          if (hasBox) throw new InvalidDataException("An empty glyph with a bounding box.");
          continue;
        }

        if (nContours == -1)
        {
          if (!hasBox) throw new InvalidDataException("A composite glyph with no bounding box.");
          var start = composite.Position;
          var more = true;
          var hasInstructions = false;
          while (more)
          {
            int f = composite.U16();
            more = (f & 0x20) != 0;
            hasInstructions |= (f & 0x100) != 0;
            var args = 2 + ((f & 1) != 0 ? 4 : 2);
            var scale = (f & 0x8) != 0 ? 2 : (f & 0x40) != 0 ? 4 : (f & 0x80) != 0 ? 8 : 0;
            composite.Skip(args + scale);
          }
          var length = composite.Position - start;
          w.I16(-1);
          w.Bytes(t, bbox.Position, 8);
          bbox.Skip(8);
          w.Bytes(t, start, length);
          if (hasInstructions)
          {
            var n = glyph.U255();
            w.U16(n);
            w.Bytes(t, instructions.Position, n);
            instructions.Skip(n);
          }
        }
        else
        {
          var ends = new int[nContours];
          var total = 0;
          for (var c = 0; c < nContours; c++)
          {
            total += points.U255();
            ends[c] = total - 1;
          }

          var xs = new int[total];
          var ys = new int[total];
          var on = new bool[total];
          int x = 0, y = 0;
          for (var p = 0; p < total; p++)
          {
            var (dx, dy, curve) = Triplet(flags.U8(), glyph);
            x += dx;
            y += dy;
            xs[p] = x;
            ys[p] = y;
            on[p] = curve;
          }

          var instructionLength = glyph.U255();
          w.I16(nContours);
          if (hasBox)
          {
            w.Bytes(t, bbox.Position, 8);
            bbox.Skip(8);
          }
          else
          {
            w.I16((short)(total == 0 ? 0 : xs.Min()));
            w.I16((short)(total == 0 ? 0 : ys.Min()));
            w.I16((short)(total == 0 ? 0 : xs.Max()));
            w.I16((short)(total == 0 ? 0 : ys.Max()));
          }
          foreach (var e in ends) w.U16(e);
          w.U16(instructionLength);
          w.Bytes(t, instructions.Position, instructionLength);
          instructions.Skip(instructionLength);
          Points(w, xs, ys, on, overlap != null && (overlap[i >> 3] & (0x80 >> (i & 7))) != 0);
        }

        while (outGlyf.Position % 4 != 0) outGlyf.WriteByte(0);
      }
      offsets[numGlyphs] = outGlyf.Position;

      var outLoca = new MemoryStream();
      var lw = new BinaryWriterBE(outLoca);
      foreach (var o in offsets)
      {
        if (indexFormat == 0) lw.U16((int)(o / 2));
        else lw.U32((uint)o);
      }
      return (outGlyf.ToArray(), outLoca.ToArray());
    }

    private static (int Dx, int Dy, bool OnCurve) Triplet(int flag, Reader g)
    {
      var on = (flag >> 7) == 0;
      flag &= 0x7F;
      int WithSign(int f, int magnitude) => (f & 1) != 0 ? magnitude : -magnitude;
      int dx, dy;
      if (flag < 10)
      {
        dx = 0;
        dy = WithSign(flag, ((flag & 14) << 7) + g.U8());
      }
      else if (flag < 20)
      {
        dx = WithSign(flag, (((flag - 10) & 14) << 7) + g.U8());
        dy = 0;
      }
      else if (flag < 84)
      {
        var b0 = flag - 20;
        var b1 = g.U8();
        dx = WithSign(flag, 1 + (b0 & 0x30) + (b1 >> 4));
        dy = WithSign(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F));
      }
      else if (flag < 120)
      {
        var b0 = flag - 84;
        var b1 = g.U8();
        var b2 = g.U8();
        dx = WithSign(flag, 1 + ((b0 / 12) << 8) + b1);
        dy = WithSign(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + b2);
      }
      else if (flag < 124)
      {
        var b1 = g.U8();
        var b2 = g.U8();
        var b3 = g.U8();
        dx = WithSign(flag, (b1 << 4) + (b2 >> 4));
        dy = WithSign(flag >> 1, ((b2 & 0x0F) << 8) + b3);
      }
      else
      {
        var b1 = g.U8();
        var b2 = g.U8();
        var b3 = g.U8();
        var b4 = g.U8();
        dx = WithSign(flag, (b1 << 8) + b2);
        dy = WithSign(flag >> 1, (b3 << 8) + b4);
      }
      return (dx, dy, on);
    }

    /// <summary>TrueType's own point encoding, the short forms where a delta fits.</summary>
    private static void Points(BinaryWriterBE w, int[] xs, int[] ys, bool[] on, bool overlapFirst)
    {
      var flags = new List<byte>();
      var dxs = new MemoryStream();
      var dys = new MemoryStream();
      var xw = new BinaryWriterBE(dxs);
      var yw = new BinaryWriterBE(dys);
      int lastX = 0, lastY = 0;
      for (var i = 0; i < xs.Length; i++)
      {
        var f = on[i] ? 1 : 0;
        if (i == 0 && overlapFirst) f |= 0x40;
        var dx = xs[i] - lastX;
        var dy = ys[i] - lastY;
        if (dx == 0) f |= 0x10;
        else if (dx > -256 && dx < 256) { f |= 0x02 | (dx > 0 ? 0x10 : 0); xw.U8(Math.Abs(dx)); }
        else xw.I16((short)dx);
        if (dy == 0) f |= 0x20;
        else if (dy > -256 && dy < 256) { f |= 0x04 | (dy > 0 ? 0x20 : 0); yw.U8(Math.Abs(dy)); }
        else yw.I16((short)dy);
        flags.Add((byte)f);
        lastX = xs[i];
        lastY = ys[i];
      }
      foreach (var f in flags) w.U8(f);
      w.Bytes(dxs.ToArray(), 0, (int)dxs.Length);
      w.Bytes(dys.ToArray(), 0, (int)dys.Length);
    }

    private static byte[] Assemble(uint flavor, List<Entry> entries)
    {
      var sorted = entries.OrderBy(e => e.Tag, StringComparer.Ordinal).ToList();
      var n = sorted.Count;
      var log = 0;
      while ((1 << (log + 1)) <= n) log++;
      var range = (1 << log) * 16;

      foreach (var head in sorted.Where(e => e.Tag == "head")) { head.Data = (byte[])head.Data.Clone(); for (var i = 8; i < 12; i++) head.Data[i] = 0; }

      var ms = new MemoryStream();
      var w = new BinaryWriterBE(ms);
      w.U32(flavor);
      w.U16(n);
      w.U16(range);
      w.U16(log);
      w.U16(n * 16 - range);

      var offset = 12 + 16 * n;
      var headAt = -1;
      foreach (var e in sorted)
      {
        w.Bytes(Encoding.ASCII.GetBytes(e.Tag), 0, 4);
        w.U32(Sum(e.Data));
        w.U32((uint)offset);
        w.U32((uint)e.Data.Length);
        if (e.Tag == "head") headAt = offset;
        offset += (e.Data.Length + 3) & ~3;
      }
      foreach (var e in sorted)
      {
        w.Bytes(e.Data, 0, e.Data.Length);
        while (ms.Position % 4 != 0) ms.WriteByte(0);
      }

      var font = ms.ToArray();
      if (headAt >= 0)
      {
        var adjust = unchecked(0xB1B0AFBA - Sum(font));
        font[headAt + 8] = (byte)(adjust >> 24);
        font[headAt + 9] = (byte)(adjust >> 16);
        font[headAt + 10] = (byte)(adjust >> 8);
        font[headAt + 11] = (byte)adjust;
      }
      return font;
    }

    private static uint Sum(byte[] data)
    {
      uint sum = 0;
      for (var i = 0; i < data.Length; i += 4)
      {
        uint word = 0;
        for (var k = 0; k < 4; k++) word = (word << 8) | (i + k < data.Length ? data[i + k] : (byte)0);
        sum = unchecked(sum + word);
      }
      return sum;
    }

    private sealed class Reader
    {
      private readonly byte[] _d;
      private readonly int _end;
      public int Position { get; private set; }

      public Reader(byte[] d, int start = 0, int length = -1)
      {
        _d = d;
        Position = start;
        _end = length < 0 ? d.Length : start + length;
      }

      public int U8()
      {
        if (Position >= _end) throw new InvalidDataException("A WOFF2 stream ran out.");
        return _d[Position++];
      }

      public int U16() { var a = U8(); return (a << 8) | U8(); }
      public uint U32() { var a = (uint)U16(); return (a << 16) | (uint)U16(); }
      public void Skip(int n) { if (Position + n > _end) throw new InvalidDataException("A WOFF2 stream ran out."); Position += n; }
      public string Tag() { var s = Encoding.ASCII.GetString(_d, Position, 4); Skip(4); return s; }

      public int Base128()
      {
        uint value = 0;
        for (var i = 0; i < 5; i++)
        {
          var b = U8();
          if (i == 0 && b == 0x80) throw new InvalidDataException("A leading zero in a WOFF2 number.");
          value = (value << 7) | (uint)(b & 0x7F);
          if ((b & 0x80) == 0) return (int)value;
        }
        throw new InvalidDataException("A WOFF2 number that does not end.");
      }

      public int U255()
      {
        var code = U8();
        if (code == 253) return U16();
        if (code == 255) return U8() + 253;
        if (code == 254) return U8() + 253 * 2;
        return code;
      }
    }

    private sealed class BinaryWriterBE
    {
      private readonly Stream _s;
      public BinaryWriterBE(Stream s) { _s = s; }
      public void U8(int v) => _s.WriteByte((byte)v);
      public void U16(int v) { U8(v >> 8); U8(v); }
      public void I16(short v) => U16((ushort)v);
      public void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
      public void Bytes(byte[] d, int at, int n) => _s.Write(d, at, n);
    }
  }
}
