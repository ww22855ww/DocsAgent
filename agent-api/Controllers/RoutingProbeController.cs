using System.Diagnostics;
using AgentApi.Models;
using AgentApi.Services.Agent;
using AgentApi.Services.Llm;
using Microsoft.AspNetCore.Mvc;

namespace AgentApi.Controllers;

/// <summary>
/// Rehearsal surface, not a demo feature. Nothing in the console calls this.
///
/// It answers one question: given a task worded a particular way, which portal
/// screen does each mode go to? A full run takes about a minute, which is too
/// slow to try twenty phrasings or to repeat one enough times to say anything
/// about consistency. This asks the model the same question a run would and
/// stops at the first decision, so a whole matrix takes seconds.
///
/// Both sides are measured through the real implementations: the system prompt
/// comes from LlmAgentRunner and the keyword rule from ScriptedAgentRunner. A
/// probe holding its own copy of either would keep reporting a comparison that
/// had stopped being true.
/// </summary>
[ApiController]
[Route("api/routing")]
public sealed class RoutingProbeController(
    LlmClient llm,
    ToolRegistry tools,
    AppOptions options,
    ILogger<RoutingProbeController> log) : ControllerBase
{
    /// <summary>Fetch tools the agent may reach for. Anything else is a routing miss.</summary>
    private static readonly Dictionary<string, string> Screens = new()
    {
        ["download_documents"] = "scm",
        ["download_esg_surveys"] = "esg",
    };

    public sealed record ProbeRequest(string? Prompt, string[]? Prompts, int? Repeat);

    [HttpPost]
    public async Task<IActionResult> Probe([FromBody] ProbeRequest body, CancellationToken ct)
    {
        var prompts = (body.Prompts ?? [])
            .Concat(string.IsNullOrWhiteSpace(body.Prompt) ? [] : new[] { body.Prompt! })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (prompts.Count == 0)
            return BadRequest(new { error = "Provide prompt or prompts." });

        var repeat = Math.Clamp(body.Repeat ?? 3, 1, 20);
        var definitions = await tools.GetDefinitionsAsync(ct);
        var system = LlmAgentRunner.BuildSystemPrompt(DateTime.Now);

        var results = new List<object>();
        foreach (var prompt in prompts)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ProbeOneAsync(prompt, repeat, definitions, system, ct));
        }

        return Ok(new
        {
            model = options.Primary.Model,
            repeat,
            keywords = ScriptedAgentRunner.EsgKeywords,
            results,
        });
    }

    private async Task<object> ProbeOneAsync(
        string prompt,
        int repeat,
        IReadOnlyList<LlmToolDefinition> definitions,
        string system,
        CancellationToken ct)
    {
        var matched = ScriptedAgentRunner.MatchedKeywords(prompt);

        var picks = new List<string>();
        var latencies = new List<int>();
        string? error = null;

        for (var i = 0; i < repeat && error is null; i++)
        {
            try
            {
                var decision = await llm.CompleteAsync(new LlmRequest
                {
                    Messages = [LlmMessage.System(system), LlmMessage.User(prompt)],
                    Tools = definitions,
                    MaxTokens = options.LlmMaxTokensTool,
                }, ct);

                latencies.Add(decision.LatencyMs);

                // Only the first decision matters. A reply with no tool call is
                // itself a routing failure worth seeing, so it is recorded too.
                var tool = decision.ToolCalls.FirstOrDefault()?.Function.Name;
                picks.Add(tool is null ? "(no tool call)"
                        : Screens.TryGetValue(tool, out var screen) ? screen
                        : $"(other: {tool})");
            }
            catch (LlmException ex)
            {
                error = ex.Message;
                log.LogWarning(ex, "routing probe failed for {Prompt}", prompt);
            }
        }

        var tally = picks.GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());

        return new
        {
            prompt,
            scripted = new
            {
                route = matched.Count > 0 ? "esg" : "scm",
                matchedKeywords = matched,
            },
            agent = new
            {
                runs = picks.Count,
                picks = tally,
                // A mode that answers differently on the same sentence is the
                // thing worth knowing about, so say it plainly.
                consistent = tally.Count <= 1,
                avgMs = latencies.Count > 0 ? (int)latencies.Average() : 0,
                error,
            },
        };
    }
}
