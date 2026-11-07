using System.Linq;
using Dwight.Labelling;
using Xunit;

namespace Dwight.Labelling.Tests
{
  /// <summary>
  /// U29 (#918): the overview's region vocabulary never shares a word with a Grasshopper component type.
  ///
  /// The installed set cannot be enumerated here (ComponentWhitelist.All() needs ComponentServer, so it needs
  /// Rhino); this fixture is the native names most likely to collide with a plain English noun, including every
  /// one the vocabulary could plausibly have chosen - Cluster, Group, Region (as a word inside "Region Union"),
  /// Relay, Panel, Jump, Sketch. The deploy probe runs the same function over the live set on a machine with
  /// Rhino; that run, not this one, is the witness for THAT machine's plugins.
  /// </summary>
  public class RegionVocabularyTests
  {
    public static readonly string[] NativeTypeNames =
    {
      "Cluster", "Cluster Input", "Cluster Output", "Group", "Scribble", "Jump", "Sketch", "Relay", "Panel",
      "Number Slider", "Boolean Toggle", "Value List", "Colour Swatch", "Button", "MD Slider", "Gradient",
      "Region Union", "Region Difference", "Region Intersection", "Region Slits", "Trim with Region", "Trim with Regions",
      "Boundary Surfaces", "Rectangle", "Line", "Circle", "Point", "Curve", "Surface", "Brep", "Mesh", "Geometry",
      "Loft", "Divide Curve", "Divide Domain2", "Isotrim", "Area", "Move", "Rotate", "Scale", "Extrude", "Offset Curve",
      "Construct Domain", "Construct Point", "Deconstruct Brep", "List Item", "Flatten Tree", "Graft Tree", "Partition List",
      "Python 3 Script", "C# Script", "GhPython Script", "Galapagos", "Timer", "Data Dam", "Data Recorder",
      "Custom Preview", "Text Tag", "Text Tag 3D", "Legend", "Dot Display", "Bake", "Zone", "Section", "Domain", "Block",
    };

    [Fact]
    public void The_region_words_collide_with_no_native_type_name()
    {
      Assert.Empty(RegionVocabulary.Collisions(NativeTypeNames));
    }

    [Fact]
    public void The_old_word_cluster_is_the_positive_control()
    {
      // No null without a positive control (#212): the same function, handed the word the product used to use,
      // finds the collision that caused the harm.
      Assert.Equal(new[] { "Cluster" }, RegionVocabulary.Collisions(NativeTypeNames, new[] { "cluster" }));
      Assert.Equal(new[] { "Group" }, RegionVocabulary.Collisions(NativeTypeNames, new[] { "Group" }));
    }

    [Fact]
    public void Matching_is_case_and_whitespace_insensitive_and_whole_name_only()
    {
      // The collision is reported as the machine spells the type, trimmed - that is the name the model would meet.
      Assert.Equal(new[] { "CLUSTER" }, RegionVocabulary.Collisions(new[] { "  CLUSTER " }, new[] { "cluster" }));
      // A type whose name merely CONTAINS the word is not the collision the rule is about: "Region Union" is not
      // what a model would call get_cluster over, and the word is the noun get_region already uses.
      Assert.Empty(RegionVocabulary.Collisions(new[] { "Region Union" }));
    }

    [Fact]
    public void The_generic_label_uses_the_noun_and_is_recognised_as_generic()
    {
      Assert.Equal("Region 7", RegionVocabulary.GenericLabel(7));
      Assert.True(RegionVocabulary.IsGeneric("Region 7"));
      Assert.True(RegionVocabulary.IsGeneric(""));
      Assert.False(RegionVocabulary.IsGeneric("Cluster 7"));      // the old word is not ours any more
      Assert.False(RegionVocabulary.IsGeneric("Roof massing"));
      Assert.False(RegionVocabulary.IsGeneric("Region of interest"));
    }

    [Fact]
    public void Every_word_the_vocabulary_declares_starts_with_the_noun()
    {
      foreach (var w in RegionVocabulary.Words) Assert.StartsWith(RegionVocabulary.Noun, w);
    }

    [Fact]
    public void Collisions_tolerates_nulls_and_blanks()
    {
      Assert.Empty(RegionVocabulary.Collisions(null));
      Assert.Empty(RegionVocabulary.Collisions(new string[] { null, "", "   " }));
    }
  }
}
