using System;
using System.Drawing;
using Grasshopper.Kernel;
using Dwight.Branding;

namespace Dwight
{
  /// <summary>
  /// The Dwight plugin's identity card. Grasshopper reads this when it loads the
  /// .gha: the library name shown in the ribbon/about, its description, author, and
  /// the assembly GUID. (Set once; the GUID must not change.)
  /// </summary>
  public class DwightInfo : GH_AssemblyInfo
  {
    public override string Name => "Dwight";

    // The pixel-art Dwight mark (lime on transparent), drawn from a code-defined grid (Phase-3 brand).
    public override Bitmap Icon => DwightIcon.Create(24);

    public override string Description =>
      "A Grasshopper plugin that adds five tools to the canvas: Explain, Diagnose, Reorganise, Simplify and Build. " +
      "Select components, press Ctrl+Shift+Right-click, and pick one. Open source (Apache-2.0), on your own API key or fully local with Ollama.";

    public override Guid Id => new Guid("4a884184-c587-47a5-bace-8b7126901638");

    public override string AuthorName => "Tharun Eswar";

    public override string AuthorContact => "https://tha.run";

    // Mirrors the assembly version set in the .csproj.
    public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
  }
}
