using System;
using System.Collections.Generic;
using System.Threading;
using Dwight.Llm;

namespace Dwight.AgentEval
{
  /// <summary>
  /// The run's spend cap and circuit breaker (#144: arms were lost whole to credit exhaustion). Spend is added as each reply arrives and
  /// checked before each request, so a run can overshoot its cap by at most what the requests already in flight cost. The breaker opens per
  /// provider after <see cref="BreakAfter"/> failures in a row, and a success closes the count: one provider's outage must not stop another's rows.
  /// </summary>
  internal sealed class RunGuard
  {
    public const int BreakAfter = 3;
    private readonly object _gate = new object();
    private readonly Dictionary<string, int> _failures = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly HashSet<string> _open = new HashSet<string>(StringComparer.Ordinal);

    public double CapCents { get; }
    public double SpentCents { get { lock (_gate) return _spent; } }
    private double _spent;

    public RunGuard(double capCents) { CapCents = capCents; }

    public bool OverCap { get { lock (_gate) return _spent >= CapCents; } }

    public bool BreakerOpen(string provider) { lock (_gate) return _open.Contains(provider ?? ""); }

    public void Spent(double cents) { lock (_gate) _spent += cents; }

    public void Succeeded(string provider) { lock (_gate) _failures[provider ?? ""] = 0; }

    public void Failed(string provider)
    {
      lock (_gate)
      {
        _failures.TryGetValue(provider ?? "", out var n);
        _failures[provider ?? ""] = ++n;
        if (n >= BreakAfter) _open.Add(provider ?? "");
      }
    }

    /// <summary>The whole provider is out: a failed pre-flight names it, before any row is spent on it.</summary>
    public void Open(string provider) { lock (_gate) _open.Add(provider ?? ""); }
  }

  /// <summary>
  /// The real adapter with the guard around it. Everything the driver can ask of a provider passes straight through, the forcing fact
  /// included: this wrapper changes what is allowed to be sent, never what is sent.
  /// </summary>
  internal sealed class GuardedProvider : ILlmProvider
  {
    private readonly ILlmProvider _inner;
    private readonly RunGuard _guard;
    private readonly string _provider;
    private readonly Func<LlmUsage, double> _centsOf;

    public const string CapMessage = "The run's spend cap was reached, so this request was not sent.";
    public const string BreakerMessage = "The circuit breaker is open for this provider, so this request was not sent.";

    public GuardedProvider(ILlmProvider inner, RunGuard guard, string provider, Func<LlmUsage, double> centsOf)
    {
      _inner = inner; _guard = guard; _provider = provider; _centsOf = centsOf;
    }

    public string ModelId => _inner.ModelId;
    public ProviderCapability Capability => _inner.Capability;
    public bool SendsForcedTool => _inner.SendsForcedTool;
    public bool ThinksWhileForced => _inner.ThinksWhileForced;
    public bool StructuredArrivesAsText => _inner.StructuredArrivesAsText;
    public bool KeepsHistoryAppendOnly => _inner.KeepsHistoryAppendOnly;

    public LlmResponse Send(LlmRequest request, CancellationToken cancel = default)
    {
      if (_guard.OverCap) throw new LlmException(CapMessage);
      if (_guard.BreakerOpen(_provider)) throw new LlmException(BreakerMessage);

      LlmResponse response;
      try { response = _inner.Send(request, cancel); }
      catch (OperationCanceledException) { throw; }
      catch (Exception)
      {
        _guard.Failed(_provider);
        throw;
      }
      _guard.Succeeded(_provider);
      if (response?.Usage != null) _guard.Spent(_centsOf(response.Usage));
      return response;
    }
  }
}
