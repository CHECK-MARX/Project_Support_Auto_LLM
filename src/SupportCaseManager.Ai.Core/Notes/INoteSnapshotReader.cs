using SupportCaseManager.Ai.Contracts;

namespace SupportCaseManager.Ai.Core.Notes;

public interface INoteSnapshotReader
{
    Task<IReadOnlyList<NoteSnapshot>> ReadAllAsync(
        string caseFolderPath,
        CancellationToken cancellationToken = default);

    Task<NoteSnapshot?> ReadAsync(
        string noteFilePath,
        CancellationToken cancellationToken = default);
}
