using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace Dwight.Feedback
{
  /// <summary>
  /// Brief ES unit 3: WHICH BUILD THIS IS, in one line, read from the assembly and never typed. The version is the assembly's informational version with its
  /// source revision cut to seven characters; the Release is the first sixteen hex characters of the plugin file's SHA-256, the name every gate gives a build.
  /// About shows this line and Report a problem sends it, so the build a person names and the build their report names are one string. GH-free (ADR-091).
  /// </summary>
  internal static class BuildLine
  {
    /// <summary>"0.9.0+2331576" from "0.9.0+2331576c68...": the version, and the commit it was built from as git prints it short. The assembly's own version where there is no informational one.</summary>
    public static string Version(string informational, string assemblyVersion)
    {
      var v = string.IsNullOrWhiteSpace(informational) ? assemblyVersion : informational.Trim();
      if (string.IsNullOrWhiteSpace(v)) return null;
      var plus = v.IndexOf('+');
      if (plus < 0) return v;
      var revision = v.Substring(plus + 1);
      return revision.Length == 0 ? v.Substring(0, plus) : v.Substring(0, plus) + "+" + (revision.Length > 7 ? revision.Substring(0, 7) : revision);
    }

    /// <summary>The first sixteen hex characters of the file's SHA-256, upper case, as Get-FileHash prints them and the gates record them.</summary>
    public static string Release(Stream file)
    {
      if (file == null) return null;
      using (var sha = SHA256.Create())
        return Convert.ToHexString(sha.ComputeHash(file), 0, 8);
    }

    /// <summary>"0.9.0+2331576 · Release 78E49132F60B43EF", or the version alone where the file could not be read.</summary>
    public static string Text(string version, string release)
    {
      var v = string.IsNullOrWhiteSpace(version) ? "unknown" : version;
      return string.IsNullOrWhiteSpace(release) ? v : v + " · Release " + release;
    }

    /// <summary>
    /// The line for an assembly: its version from its attributes and its Release from its file. <paramref name="fileWhenLoadedFromBytes"/> names the file
    /// when the host loaded the assembly from bytes and it has no location of its own (Grasshopper's COFF loading); it is asked only then.
    /// </summary>
    public static string Of(Assembly assembly, Func<string> fileWhenLoadedFromBytes = null)
    {
      if (assembly == null) return Text(null, null);
      string version = null, release = null;
      try
      {
        var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute));
        version = Version(info?.InformationalVersion, assembly.GetName().Version?.ToString());
      }
      catch { }
      try
      {
        var path = assembly.Location;
        if (string.IsNullOrEmpty(path)) path = fileWhenLoadedFromBytes?.Invoke();
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
          using (var s = File.OpenRead(path)) release = Release(s);
      }
      catch { }
      return Text(version, release);
    }
  }
}
