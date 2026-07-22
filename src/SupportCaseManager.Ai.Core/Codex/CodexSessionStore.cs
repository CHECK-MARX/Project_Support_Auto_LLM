using System.Text.Json;

namespace SupportCaseManager.Ai.Core.Codex;

public sealed record CodexSessionMessage
{
    public string Role { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record CodexSession
{
    public string SupportId { get; init; } = string.Empty;
    public Guid? ProductId { get; init; }
    public string CompanyName { get; init; } = string.Empty;
    public string CaseFolder { get; init; } = string.Empty;
    public string CodexThreadId { get; init; } = string.Empty;
    public string LastTurnId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastUsedAt { get; init; }
    public string CodexVersion { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string SessionStatus { get; init; } = string.Empty;
    public IReadOnlyList<CodexSessionMessage> Messages { get; init; } = [];
}

public sealed record CodexSessionLoadResult(IReadOnlyList<CodexSession> Sessions, string? Warning = null);

public interface ICodexSessionStore
{
    string FilePath { get; }
    Task<CodexSessionLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task<CodexSession?> FindAsync(string supportId, Guid? productId, string caseFolder, CancellationToken cancellationToken = default);
    Task SaveAsync(CodexSession session, CancellationToken cancellationToken = default);
}

public sealed class CodexSessionStore : ICodexSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public CodexSessionStore(string? filePath = null, string? localApplicationData = null)
    {
        var localRoot = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        FilePath = filePath ?? Path.Combine(localRoot, "itoke", "SupportCaseManager", "codex-sessions.json");
    }

    public string FilePath { get; }

    public async Task<CodexSessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CodexSession?> FindAsync(
        string supportId,
        Guid? productId,
        string caseFolder,
        CancellationToken cancellationToken = default)
    {
        var result = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var normalizedFolder = NormalizePath(caseFolder);
        return result.Sessions
            .Where(session => string.Equals(session.SupportId, supportId, StringComparison.OrdinalIgnoreCase))
            .Where(session => ProductIdsAreCompatible(session.ProductId, productId))
            .OrderByDescending(session => string.Equals(
                NormalizePath(session.CaseFolder),
                normalizedFolder,
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static session => session.LastUsedAt)
            .FirstOrDefault();
    }

    public async Task SaveAsync(CodexSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var sessions = loaded.Sessions.ToList();
            var index = sessions.FindIndex(item =>
                IsSameCase(item, session)
                && string.Equals(
                    NormalizePath(item.CaseFolder),
                    NormalizePath(session.CaseFolder),
                    StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                index = sessions.FindIndex(item => IsSameCase(item, session));
            }
            if (index >= 0)
            {
                sessions[index] = session;
            }
            else
            {
                sessions.Add(session);
            }

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = FilePath + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, sessions, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CodexSessionLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return new CodexSessionLoadResult([]);
        }

        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var sessions = await JsonSerializer.DeserializeAsync<List<CodexSession>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return new CodexSessionLoadResult(sessions ?? []);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new CodexSessionLoadResult([], $"Codex Thread履歴を読み込めませんでした。履歴を使わず続行します: {ex.Message}");
        }
    }

    private static string NormalizePath(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return path?.Trim() ?? string.Empty;
        }
    }

    private static bool IsSameCase(CodexSession left, CodexSession right)
    {
        return string.Equals(left.SupportId, right.SupportId, StringComparison.OrdinalIgnoreCase)
            && ProductIdsAreCompatible(left.ProductId, right.ProductId);
    }

    private static bool ProductIdsAreCompatible(Guid? left, Guid? right)
    {
        return !left.HasValue || !right.HasValue || left == right;
    }
}
