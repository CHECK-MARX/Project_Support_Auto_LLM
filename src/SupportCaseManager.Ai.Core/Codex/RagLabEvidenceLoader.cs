using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportCaseManager.Ai.Core.Codex;

public interface IRagLabEvidenceLoader
{
    Task<RagLabEvidenceLoadResult> LoadAsync(
        RagLabEvidenceLoadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class RagLabEvidenceLoader : IRagLabEvidenceLoader
{
    private const int DefaultMaxItems = 3;
    private const int AbsoluteMaxItems = 5;
    private const long MaximumJsonBytes = 10 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<RagLabEvidenceLoadResult> LoadAsync(
        RagLabEvidenceLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsEnabled)
        {
            return new RagLabEvidenceLoadResult
            {
                IsEnabled = false,
                FallbackReason = "Disabled",
            };
        }

        try
        {
            var readinessPath = ResolveExistingJsonPath(request.BaselineReadinessFilePath, "baseline-readiness");
            var readiness = await ReadJsonAsync<RagLabBaselineReadinessDocument>(readinessPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(readiness.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return Fallback("BaselineNotReady", $"RAG Lab baseline-readinessがreadyではありません: {readiness.Status ?? "(未設定)"}");
            }

            var evidencePath = ResolveExistingJsonPath(request.EvidenceFilePath, "Evidence");
            var document = await ReadJsonAsync<RagLabEvidenceDocument>(evidencePath, cancellationToken)
                .ConfigureAwait(false);
            var sourceEvidence = document.SelectedEvidence ?? [];
            if (sourceEvidence.Count == 0)
            {
                return Fallback("EmptyEvidence", "RAG Lab Evidence JSONに根拠がありません。", isBaselineReady: true);
            }

            var warnings = new List<string>();
            var selected = new List<RagLabEvidenceItem>();
            var maxItems = Math.Clamp(
                request.MaxItems <= 0 ? DefaultMaxItems : request.MaxItems,
                1,
                AbsoluteMaxItems);
            foreach (var item in sourceEvidence)
            {
                if (string.IsNullOrWhiteSpace(item.DocumentId) || string.IsNullOrWhiteSpace(item.Text))
                {
                    warnings.Add("documentIdまたはtextがないEvidenceを除外しました。");
                    continue;
                }

                if (IsProductMismatch(item, request.ExpectedProduct))
                {
                    warnings.Add($"対象製品と一致しないEvidenceを除外しました: {item.DocumentId}");
                    continue;
                }

                var itemWarnings = (item.Warnings ?? [])
                    .Where(static warning => !string.IsNullOrWhiteSpace(warning))
                    .ToList();
                if (IsVersionMismatch(item, request.ExpectedVersion)
                    && !itemWarnings.Any(static warning => warning.Contains("バージョン", StringComparison.Ordinal)))
                {
                    itemWarnings.Add("対象バージョンと根拠のバージョンが一致しません。");
                }

                selected.Add(item with { Warnings = itemWarnings });
                if (selected.Count == maxItems)
                {
                    break;
                }
            }

            if (selected.Count == 0)
            {
                return new RagLabEvidenceLoadResult
                {
                    IsEnabled = true,
                    IsBaselineReady = true,
                    Query = document.Query ?? string.Empty,
                    Warnings = warnings,
                    FallbackReason = "NoApplicableEvidence",
                };
            }

            return new RagLabEvidenceLoadResult
            {
                IsEnabled = true,
                IsBaselineReady = true,
                Query = document.Query ?? string.Empty,
                Evidence = selected,
                Warnings = warnings,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            return Fallback("ReadFailed", $"RAG Lab Evidenceを読み込めませんでした: {ex.Message}");
        }
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (file.Length == 0)
        {
            throw new JsonException($"JSONファイルが空です: {file.Name}");
        }
        if (file.Length > MaximumJsonBytes)
        {
            throw new IOException($"JSONファイルが上限{MaximumJsonBytes:N0} bytesを超えています: {file.Name}");
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"JSONのルートを読み込めませんでした: {file.Name}");
    }

    private static string ResolveExistingJsonPath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{label} JSONのパスが設定されていません。");
        }

        var fullPath = Path.GetFullPath(path.Trim());
        if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{label}はJSONファイルを指定してください。");
        }
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"{label} JSONが見つかりません。", fullPath);
        }

        return fullPath;
    }

    private static bool IsProductMismatch(RagLabEvidenceItem item, string? expectedProduct)
    {
        if (item.ProductMatch is false)
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(expectedProduct)
            && !string.IsNullOrWhiteSpace(item.Product)
            && !string.Equals(item.Product.Trim(), expectedProduct.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVersionMismatch(RagLabEvidenceItem item, string? expectedVersion)
    {
        if (item.VersionMatch is false)
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(expectedVersion)
            && !string.IsNullOrWhiteSpace(item.Version)
            && !string.Equals(item.Version.Trim(), expectedVersion.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static RagLabEvidenceLoadResult Fallback(
        string reason,
        string warning,
        bool isBaselineReady = false)
    {
        return new RagLabEvidenceLoadResult
        {
            IsEnabled = true,
            IsBaselineReady = isBaselineReady,
            Warnings = [warning],
            FallbackReason = reason,
        };
    }

    private sealed record RagLabEvidenceDocument
    {
        [JsonPropertyName("query")]
        public string? Query { get; init; }

        [JsonPropertyName("selectedEvidence")]
        public IReadOnlyList<RagLabEvidenceItem>? SelectedEvidence { get; init; } = [];
    }

    private sealed record RagLabBaselineReadinessDocument
    {
        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }
}
