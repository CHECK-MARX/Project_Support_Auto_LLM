namespace SupportCaseManager.Ai.Core.Indexing;

/// <summary>
/// Publishes a validated staging embedding generation without changing source indexes.
/// The previous active generation is retained for an explicit rollback.
/// </summary>
public sealed class EmbeddingIndexGenerationPublisher
{
    public async Task PublishAsync(
        string stagedIndexPath,
        string activeProductIndexFolder,
        string productName,
        string embeddingModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedIndexPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeProductIndexFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);

        var staged = await EmbeddingIndexUpdater.LoadAsync(stagedIndexPath, cancellationToken);
        if (staged is null || string.IsNullOrWhiteSpace(staged.EmbeddingModelDigest))
        {
            throw new InvalidOperationException("Staged embedding index has no verified model digest.");
        }

        var validation = await EmbeddingIndexUpdater.ValidateAsync(
            stagedIndexPath, productName, activeProductIndexFolder, embeddingModel, cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }

        Directory.CreateDirectory(activeProductIndexFolder);
        var activePath = Path.Combine(activeProductIndexFolder, EmbeddingIndexDocument.FileName);
        var previousPath = PreviousPath(activePath);
        var temporaryPath = $"{activePath}.{Guid.NewGuid():N}.staging";
        try
        {
            File.Copy(stagedIndexPath, temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(activePath))
            {
                File.Replace(temporaryPath, activePath, previousPath);
            }
            else
            {
                File.Move(temporaryPath, activePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task RollbackAsync(
        string activeProductIndexFolder,
        string productName,
        CancellationToken cancellationToken = default)
    {
        var activePath = Path.Combine(activeProductIndexFolder, EmbeddingIndexDocument.FileName);
        var previousPath = PreviousPath(activePath);
        var previous = await EmbeddingIndexUpdater.LoadAsync(previousPath, cancellationToken)
            ?? throw new InvalidOperationException("Previous embedding generation was not found.");
        if (string.IsNullOrWhiteSpace(previous.EmbeddingModelDigest))
        {
            throw new InvalidOperationException("Previous embedding generation has no verified model digest.");
        }

        var validation = await EmbeddingIndexUpdater.ValidateAsync(
            previousPath, productName, activeProductIndexFolder, previous.EmbeddingModel, cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Message);
        }

        var temporaryPath = $"{activePath}.{Guid.NewGuid():N}.rollback";
        try
        {
            File.Copy(previousPath, temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, activePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string PreviousPath(string activePath) => $"{activePath}.previous";
}
