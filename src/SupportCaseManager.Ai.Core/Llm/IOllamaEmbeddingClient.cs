namespace SupportCaseManager.Ai.Core.Llm;

public interface IOllamaEmbeddingClient
{
    Task<IReadOnlyList<IReadOnlyList<float>>> EmbedAsync(
        string endpoint,
        string model,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default);
}
