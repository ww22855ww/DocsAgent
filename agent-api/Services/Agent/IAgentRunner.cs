namespace AgentApi.Services.Agent;

public interface IAgentRunner
{
    /// <summary>scripted | llm</summary>
    string Mode { get; }

    Task RunAsync(AgentContext context, CancellationToken ct);
}
