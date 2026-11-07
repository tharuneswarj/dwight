using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Dwight.Feedback;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// Brief ES unit 3: About shows which build this is, the version and the Release, read from the assembly and never typed, and Report a problem sends the
  /// same line, so the two agree because they are one string. The line is GH-free and tested here; where it is read and shown is pinned by source.
  /// </summary>
  public class BuildLineTests
  {
    [Theory]
    [InlineData("0.9.0+2331576c68447a57ebf14a77a3e829c166e6df26", "0.9.0.0", "0.9.0+2331576")]
    [InlineData("0.9.0+2331576", "0.9.0.0", "0.9.0+2331576")]
    [InlineData("0.9.0+abc", "0.9.0.0", "0.9.0+abc")]
    [InlineData("0.9.0+", "0.9.0.0", "0.9.0")]
    [InlineData("1.0.0", "1.0.0.0", "1.0.0")]
    [InlineData(null, "0.9.0.0", "0.9.0.0")]
    [InlineData("  ", "0.9.0.0", "0.9.0.0")]
    public void The_version_is_the_assemblys_with_its_commit_cut_as_git_prints_it_short(string informational, string assembly, string expected)
    {
      Assert.Equal(expected, BuildLine.Version(informational, assembly));
    }

    [Fact]
    public void No_version_at_all_is_none()
    {
      Assert.Null(BuildLine.Version(null, null));
      Assert.Equal("unknown", BuildLine.Text(null, null));
    }

    [Fact]
    public void The_release_is_the_first_sixteen_hex_of_the_files_sha256_as_the_gates_name_a_build()
    {
      var bytes = Encoding.ASCII.GetBytes("abc");
      using (var s = new MemoryStream(bytes)) Assert.Equal("BA7816BF8F01CFEA", BuildLine.Release(s));   // SHA-256("abc") begins BA7816BF 8F01CFEA, the standard's own test vector
      Assert.Null(BuildLine.Release(null));
    }

    [Fact]
    public void The_line_is_the_version_and_the_release_or_the_version_alone()
    {
      Assert.Equal("0.9.0+2331576 · Release 78E49132F60B43EF", BuildLine.Text("0.9.0+2331576", "78E49132F60B43EF"));
      Assert.Equal("0.9.0+2331576", BuildLine.Text("0.9.0+2331576", null));
      Assert.Equal("unknown · Release 78E49132F60B43EF", BuildLine.Text(null, "78E49132F60B43EF"));
    }

    private static string Sha16(string path)
    {
      using (var sha = SHA256.Create())
        return string.Concat(sha.ComputeHash(File.ReadAllBytes(path)).Take(8).Select(b => b.ToString("X2")));
    }

    [Fact]
    public void An_assemblys_line_is_read_from_its_attributes_and_its_file()
    {
      var assembly = typeof(BuildLineTests).Assembly;
      var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
      var expected = BuildLine.Version(info, assembly.GetName().Version?.ToString()) + " · Release " + Sha16(assembly.Location);
      Assert.Equal(expected, BuildLine.Of(assembly));
    }

    [Fact]
    public void An_assembly_loaded_from_bytes_takes_its_release_from_the_file_the_host_names_and_only_then()
    {
      var path = typeof(BuildLineTests).Assembly.Location;
      var context = new AssemblyLoadContext("bytes", isCollectible: true);
      try
      {
        Assembly fromBytes;
        using (var s = new MemoryStream(File.ReadAllBytes(path))) fromBytes = context.LoadFromStream(s);
        Assert.Equal("", fromBytes.Location);   // as Grasshopper's COFF loading leaves it

        Assert.EndsWith(" · Release " + Sha16(path), BuildLine.Of(fromBytes, () => path));
        Assert.DoesNotContain("Release", BuildLine.Of(fromBytes));   // no file named: the version alone, never a hash of something else

        var asked = 0;
        BuildLine.Of(typeof(BuildLineTests).Assembly, () => { asked++; return path; });
        Assert.Equal(0, asked);   // an assembly with a location of its own never asks the host
      }
      finally { context.Unload(); }
    }

    [Fact]
    public void The_report_sends_the_line_whole()
    {
      var line = BuildLine.Text("0.9.0+2331576", "78E49132F60B43EF");
      var block = ProblemReport.DiagnosticBlock(new ProblemFacts { DwightVersion = line });
      Assert.Contains("- Dwight: 0.9.0+2331576 · Release 78E49132F60B43EF\n", block.Replace("\r\n", "\n") + "\n");
    }

    // ---- where the line is read and shown, pinned by source (ADR-091) ------------------------------------------------------------------------------------------------------

    private static string Repo(params string[] parts)
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Dwight.sln"))) dir = dir.Parent;
      Assert.True(dir != null, "the repository root (Dwight.sln) could not be found");
      return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
    }

    [Fact]
    public void About_and_the_report_read_one_line_from_one_place()
    {
      var feedback = Repo("Dwight", "UI", "DwightFeedbackPanel.cs");
      Assert.Contains("internal static string ThisBuild => _thisBuild ?? (_thisBuild = BuildLine.Of(typeof(DwightFeedbackPanel).Assembly, GhaFile));", feedback);
      Assert.Contains("try { f.DwightVersion = ThisBuild; } catch { }", feedback);
      Assert.Contains("if (lib is DwightInfo) return lib.Location;", feedback);
      Assert.DoesNotContain("AssemblyInformationalVersionAttribute", feedback);   // no second reading of the version for the two to disagree on

      var settings = Repo("Dwight", "UI", "DwightSettingsPanel.cs");
      Assert.Contains("[\"build\"] = DwightFeedbackPanel.ThisBuild,", settings);

      var page = Repo("Dwight", "Resources", "settings.html");
      var about = page.Substring(page.IndexOf("data-pane=\"About\"", StringComparison.Ordinal));
      Assert.Contains("<div class=\"hint\" id=\"buildline\" hidden></div>", about.Substring(0, about.IndexOf("</section>", StringComparison.Ordinal)));
      Assert.Contains("build.textContent = d.build ? 'Dwight ' + d.build : ''; build.hidden = !d.build;", page);
    }
  }
}
