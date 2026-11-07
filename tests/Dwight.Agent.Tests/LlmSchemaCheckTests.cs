using System.Collections.Generic;
using System.Text.Json.Nodes;
using Dwight.Agent;
using Dwight.Llm;
using Xunit;

namespace Dwight.Agent.Tests
{
  /// <summary>
  /// The schema pre-flight (#823). The defect it exists for: a malformed schema was caught and replaced
  /// with an empty object, so the tool reached the provider advertised as taking NO PARAMETERS - visible
  /// to the model, called by the model, and then refused by our own parser for missing fields, with
  /// nothing anywhere saying why.
  ///
  /// <para>The empty object is what makes it dangerous rather than merely wrong: it is SHAPED LIKE A
  /// VALID ANSWER, so every downstream check passes and the only thing it lies about is what the tool
  /// permits. These tests hold both halves - the refusal happens, and the empty fallback is gone.</para>
  /// </summary>
  public class LlmSchemaCheckTests
  {
    private const string Good = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";

    private static LlmRequest WithTool(string schema, string name = "get_region") => new LlmRequest
    {
      Tools = new List<LlmTool> { new LlmTool { Name = name, Description = "d", InputSchemaJson = schema } },
    };

    // ---- the refusal, and what it says -----------------------------------------------------------

    /// <summary>The #820 shape exactly: a description carrying a quotation mark broke the concatenated
    /// schema. The point is not that it is refused but that the sentence NAMES THE TOOL - a refusal that
    /// does not say which of twenty-four tools is broken sends the reader everywhere except the schema.</summary>
    [Fact]
    public void An_unparseable_schema_is_refused_and_the_tool_is_named()
    {
      var broken = "{\"type\":\"object\",\"description\":\"it names its claims as \"preserves\"\"}";

      var problem = LlmSchemaCheck.Check(WithTool(broken, "edit_script"));

      Assert.NotNull(problem);
      Assert.Contains("edit_script", problem);
      Assert.Contains("not valid JSON", problem);
    }

    /// <summary>PARSING IS NOT THE BAR. An array is valid JSON and cannot be an input_schema; a check
    /// that only tried to parse would publish it and learn nothing until the model called the tool.</summary>
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"a bare string\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void Valid_json_that_is_not_an_object_is_refused_too(string notAnObject)
    {
      Assert.NotNull(LlmSchemaCheck.Check(WithTool(notAnObject)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_tool_with_no_schema_at_all_is_refused(string missing)
    {
      var problem = LlmSchemaCheck.Check(WithTool(missing));

      Assert.NotNull(problem);
      Assert.Contains("get_region", problem);
    }

    [Fact]
    public void An_unnamed_tool_is_still_refused_and_said_to_be_unnamed()
    {
      var problem = LlmSchemaCheck.Check(WithTool("{", name: null));

      Assert.NotNull(problem);
      Assert.Contains("unnamed", problem);
    }

    [Fact]
    public void A_good_schema_passes()
    {
      Assert.Null(LlmSchemaCheck.Check(WithTool(Good)));
    }

    [Fact]
    public void A_request_with_no_tools_at_all_passes()
    {
      Assert.Null(LlmSchemaCheck.Check(new LlmRequest()));
      Assert.Null(LlmSchemaCheck.Check(null));
    }

    // ---- the structured path, which is the one all five verbs take -------------------------------

    [Fact]
    public void A_malformed_structured_schema_is_refused_by_its_name()
    {
      var problem = LlmSchemaCheck.Check(new LlmRequest { StructuredName = "diagnosis", StructuredSchemaJson = "{oops" });

      Assert.NotNull(problem);
      Assert.Contains("diagnosis", problem);
    }

    /// <summary>A free-form (prose) request carries no structured name, and its schema field is not read.
    /// Refusing on it would break every Explain.</summary>
    [Fact]
    public void A_prose_request_is_not_judged_on_a_schema_it_does_not_use()
    {
      Assert.Null(LlmSchemaCheck.Check(new LlmRequest { StructuredSchemaJson = "{oops" }));
    }

    // ---- the check against REAL input ------------------------------------------------------------

    /// <summary>A check that has only ever run against hand-made bad schemas is a check with an untested
    /// opinion. This runs it over the shipped catalogue - the 22 schemas actually published to the
    /// provider - so "it refuses bad input" is joined by "it accepts ours".</summary>
    [Fact]
    public void Every_shipped_catalogue_schema_passes_the_check()
    {
      var tools = new List<LlmTool>();
      foreach (var t in AgentToolCatalog.All())
        tools.Add(new LlmTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson });

      Assert.NotEmpty(tools);
      Assert.Null(LlmSchemaCheck.Check(new LlmRequest { Tools = tools }));
    }

    /// <summary>One bad schema among twenty-two good ones is still refused, and still named. The catalogue
    /// passing as a whole would otherwise be consistent with the check not looking past the first tool.</summary>
    [Fact]
    public void One_bad_schema_among_the_shipped_ones_is_found_and_named()
    {
      var tools = new List<LlmTool>();
      foreach (var t in AgentToolCatalog.All())
        tools.Add(new LlmTool { Name = t.Name, Description = t.Description, InputSchemaJson = t.InputSchemaJson });
      tools.Add(new LlmTool { Name = "late_arrival", Description = "d", InputSchemaJson = "{\"type\":" });

      var problem = LlmSchemaCheck.Check(new LlmRequest { Tools = tools });

      Assert.NotNull(problem);
      Assert.Contains("late_arrival", problem);
    }

    // ---- and the fallback is actually gone -------------------------------------------------------

    /// <summary>THE HALF THAT MATTERS MOST. The check can be bypassed - a fourth provider, a new call
    /// site - and what must never happen then is a tool published with no parameters. The body now
    /// throws instead, because a loud failure hands the defect back to whoever caused it while an empty
    /// object hands it to the model and the user.</summary>
    [Fact]
    public void The_request_body_never_publishes_an_empty_schema_for_a_broken_one()
    {
      var request = WithTool("{\"type\":");

      Assert.ThrowsAny<System.Exception>(() =>
        AnthropicRequestBody.Build(request, "claude-sonnet-5", ThinkingWireState.Omit));
    }

    /// <summary>And the same body, given good schemas, still puts them on the wire intact - the guard
    /// must not have been bought by breaking the thing it guards.</summary>
    [Fact]
    public void A_good_schema_still_reaches_the_wire_as_an_object()
    {
      var body = AnthropicRequestBody.Build(WithTool(Good), "claude-sonnet-5", ThinkingWireState.Omit);

      var schema = JsonNode.Parse(body)["tools"][0]["input_schema"];
      Assert.IsType<JsonObject>(schema);
      Assert.Equal("object", schema["type"].GetValue<string>());
    }
  }
}
