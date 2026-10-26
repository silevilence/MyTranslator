using System.IO.Compression;
using System.Text.Json;
using MyTranslator.Api.Data;

namespace MyTranslator.Api.FileTasks;

public sealed partial class FileTaskService
{
    private sealed record BundleFile(string FileName, string FileType, long ByteLength,
        string? Encoding, string Template, string EntryName);

    public async Task<FileTaskSummary> CreateFilesTaskAsync(
        IFormFileCollection files, string? segmentationMode, CancellationToken cancellationToken)
    {
        if (files.Count is < 1 or > 20)
            throw new InvalidFileTaskRequestException("invalid_file_collection", "Select between 1 and 20 files.");
        if (files.Sum(file => file.Length) > MaximumUploadBytes)
            throw new InvalidFileTaskRequestException("content_too_large", "The collection exceeds the upload limit.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.FileName) || file.FileName.IndexOfAny(['/', '\\', ':']) >= 0 ||
                file.FileName is "." or ".." || file.FileName.Any(char.IsControl) || !names.Add(file.FileName))
                throw new InvalidFileTaskRequestException("invalid_file_collection", "File names must be unique and contain no paths.");
        }

        var task = new TranslationTask
        {
            Id = Guid.NewGuid(), SourceKind = "files", FileType = "bundle",
            FileName = Path.GetFileNameWithoutExtension(files[0].FileName) + ".zip",
            MediaType = "application/zip", CreatedAt = DateTimeOffset.UtcNow
        };
        var manifest = new List<BundleFile>();
        using var original = new MemoryStream();
        using (var archive = new ZipArchive(original, ZipArchiveMode.Create, leaveOpen: true))
        {
            var unitOffset = 0;
            foreach (var file in files)
            {
                var type = ResolveFileType(file.FileName, file.ContentType, null);
                var member = await BuildFileTaskAsync(file, type, type == "txt" ? segmentationMode : null, cancellationToken);
                task.ByteLength += member.ByteLength;
                if (task.ByteLength > MaximumUploadBytes)
                    throw new InvalidFileTaskRequestException("content_too_large", "The collection exceeds the upload limit.");
                var entryName = manifest.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await using (var destination = archive.CreateEntry(entryName).Open())
                    await destination.WriteAsync(member.SourceBytes, cancellationToken);
                manifest.Add(new BundleFile(file.FileName, type, member.ByteLength,
                    member.OriginalEncoding, member.ReconstructionTemplate, entryName));

                var lastUnit = member.Segments.Select(segment => segment.SourceUnitOrder)
                    .Concat(member.ProtectedBlocks.Select(block => block.SourceUnitOrder)).DefaultIfEmpty().Max();
                foreach (var segment in member.Segments)
                {
                    segment.TaskId = task.Id;
                    segment.Order = task.Segments.Count + 1;
                    segment.SourceUnitOrder += unitOffset;
                    segment.SourceFileName = file.FileName;
                    task.Segments.Add(segment);
                }
                foreach (var block in member.ProtectedBlocks)
                {
                    block.TaskId = task.Id;
                    block.SourceUnitOrder += unitOffset;
                    task.ProtectedBlocks.Add(block);
                }
                unitOffset += lastUnit;
                task.ChapterCount += member.ChapterCount;
            }
        }
        task.SourceBytes = original.ToArray();
        task.ReconstructionTemplate = JsonSerializer.Serialize(manifest);
        task.ProtectedBlockCount = task.ProtectedBlocks.Count;
        database.TranslationTasks.Add(task);
        await database.SaveChangesAsync(cancellationToken);
        return ToSummary(task);
    }

    private static IReadOnlyList<TaskFileSummary>? BundleFiles(TranslationTask task) => task.FileType != "bundle"
        ? null
        : ReadManifest(task).Select(file => new TaskFileSummary(file.FileName, file.FileType, file.ByteLength)).ToArray();

    private static List<BundleFile> ReadManifest(TranslationTask task) =>
        JsonSerializer.Deserialize<List<BundleFile>>(task.ReconstructionTemplate)
        ?? throw new InvalidFileTaskRequestException("task_not_exportable", "Invalid file collection.");

    private static byte[] ExportContent(TranslationTask task) => task.FileType switch
    {
        "bundle" => ExportBundle(task),
        "epub" => ExportEpub(task),
        _ => EncodeText(RenderTemplate(task.ReconstructionTemplate, task.Segments), task.SourceBytes, task.OriginalEncoding)
    };

    private static byte[] ExportBundle(TranslationTask task)
    {
        using var original = new MemoryStream(task.SourceBytes, writable: false);
        using var source = new ZipArchive(original, ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var destination = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in ReadManifest(task))
            {
                using var bytes = new MemoryStream();
                using (var entry = source.GetEntry(file.EntryName)!.Open()) entry.CopyTo(bytes);
                var member = new TranslationTask
                {
                    FileType = file.FileType, SourceBytes = bytes.ToArray(), OriginalEncoding = file.Encoding,
                    ReconstructionTemplate = file.Template,
                    Segments = task.Segments.Where(segment => segment.SourceFileName == file.FileName).ToList()
                };
                using var target = destination.CreateEntry(file.FileName).Open();
                target.Write(ExportContent(member));
            }
        }
        return output.ToArray();
    }
}
