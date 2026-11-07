using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dwight.Data;

namespace Dwight.Agent
{
  /// <summary>
  /// THE STATE DWIGHT LEFT A COMPONENT IN. Not a fingerprint - see below, and the name is load-bearing.
  ///
  /// WHAT IT ANSWERS: has the user ADOPTED this component since Dwight placed it? A rebuild may remove
  /// what Dwight placed and left alone; it must not remove something the user has since taken over, and
  /// the consent card has to be able to tell them which is which. A card that says "9 components" when 3
  /// of them are ones they rewired last week has stated a scope they cannot act on.
  ///
  /// WHY IT IS NOT CALLED A FINGERPRINT, AND WHY THAT MATTERS MORE THAN IT SOUNDS. It carries the
  /// NICKNAME and the behaviour fingerprint deliberately does not. If both were called fingerprints,
  /// someone would eventually notice the discrepancy, believe they were fixing an oversight, and unify
  /// them - and unifying them the wrong way puts NickName into the BEHAVIOUR fingerprint, at which point
  /// a rename counts as a behaviour change and Reorganise's byte-identical guard fails on a rename. A bad
  /// day caused by tidiness. So this is named for the question it answers rather than for what it
  /// resembles, and <see cref="BehaviourFingerprintCore"/> carries the matching note: after that,
  /// unification is not an oversight anyone can commit by accident - it requires deliberately overriding
  /// a written reason, which is the most a comment can ever buy.
  ///
  /// THE TWO QUESTIONS, stated once each:
  ///   - the behaviour fingerprint asks DOES THIS DEFINITION STILL COMPUTE THE SAME THING? A rename does
  ///     not change what a definition computes, so it is excluded - correctly.
  ///   - the as-left record asks HAS THE USER MADE THIS THEIRS? A rename is the strongest signal of
  ///     that there is, and the most likely to happen, because Dwight NAMES what it places (Width,
  ///     Height, Zero1) - so a user renaming one has taken it over by definition.
  ///
  /// WHEN IT IS TAKEN: after wiring and after the solve, never at placement. A token captured at
  /// AddObject would record zero sources, because build_region adds components and wires them
  /// afterwards - so every component would read as CHANGED at the next rebuild, on a canvas nobody had
  /// touched. That is worse than not having the feature: AN ADVISORY LINE THAT IS OFTEN WRONG IS NOT A
  /// WEAK FEATURE, IT IS ONE THAT DESTROYS THE CREDIBILITY OF EVERY OTHER LINE BESIDE IT.
  /// </summary>
  internal static class AsLeftRecord
  {
    /// <summary>
    /// A deterministic token for one component's as-left state. Compared only against itself over time,
    /// so the format is private and needs no cross-version stability - only internal consistency.
    /// </summary>
    /// <param name="stateToken">
    /// The component's WHOLE held state, read by the shim (<see cref="StateToken.Of"/> over
    /// <c>GhBuildState.Read</c>) - null for a type that holds none. REQUIRED, never defaulted (#680).
    ///
    /// The behaviour fingerprint's value token is right for ITS question: a slider's domain or rounding that leaves the
    /// value where it was changes no output. This record asks a different one - HAS ANYTHING ABOUT THIS COMPONENT
    /// CHANGED? - and borrowing the value-only token made a domain-only edit invisible to it: a slider the user (or an
    /// approved set_value) re-ranged still read as Dwight left it, and a rebuild would remove it. So the record reads
    /// the whole state itself, and the fingerprint is left exactly as it is.
    /// </param>
    public static string Of(FpObject o, string nickName, string stateToken)
    {
      if (o == null) return null;

      // #779: A SCRIPT WHOSE SOURCE WE COULD NOT READ HAS NO HONEST AS-LEFT TOKEN. Its body is the thing
      // most likely to have been changed and the one thing we cannot see, so any token composed here
      // would claim more than we know - and this record's whole job is to decide whether a component is
      // still Dwight's to remove. Null is the existing vocabulary for "we cannot tell" (see Unchanged
      // below), and BOTH resolution paths already fail toward LEAVE IT on it: the grouped path compares a
      // null token as CHANGED, so the component is adopted and left standing; the loose path excludes it
      // from the claim. The behaviour fingerprint takes the OPPOSITE answer from the same read, on
      // purpose, so that one locked script cannot abort Reorganise - see FpObject.ScriptSourceUnreadable.
      if (o.ScriptSourceUnreadable) return null;

      var parts = new List<string>
      {
        "t:" + (o.TypeName ?? ""),
        // THE FIELD THE BEHAVIOUR FINGERPRINT DOES NOT HAVE, and the reason this type exists.
        "n:" + (nickName ?? ""),
        "v:" + (o.ValueToken ?? ""),
        // #680: the WHOLE held state (a slider's domain, rounding and decimals as well as its value), read by the shim.
        "st:" + (stateToken ?? ""),
        "lock:" + (o.Locked ? "1" : "0"),
        "pd:" + (o.HasPersistentData ? "1" : "0"),
        "map:" + (o.DataMappingNone ? "0" : "1") + (o.Simplify ? "s" : "") + (o.Reverse ? "r" : ""),
      };

      foreach (var i in o.Inputs ?? new List<FpInput>())
      {
        parts.Add("i:" + i.Guid.ToString("N") + ":" + (i.MappingToken ?? "") + ":" + (i.PersistentToken ?? ""));
        // ORDERED sources: a rewire that only changes input ORDER is still a rewire.
        parts.Add("s:" + string.Join(",", (i.Sources ?? new List<Guid>()).Select(g => g.ToString("N"))));
      }

      using (var sha = SHA256.Create())
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", parts))));
    }

    /// <summary>Is this component still as Dwight left it? An unknown token on either side answers
    /// FALSE - "we cannot tell" is treated as "changed", so the safe direction is to LEAVE it.</summary>
    public static bool Unchanged(string asLeft, string now) =>
      !string.IsNullOrEmpty(asLeft) && !string.IsNullOrEmpty(now) &&
      string.Equals(asLeft, now, StringComparison.Ordinal);
  }
}
