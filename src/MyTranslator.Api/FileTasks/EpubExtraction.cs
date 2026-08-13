using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace MyTranslator.Api.FileTasks;

internal static class EpubExtraction
{
    private const int MaximumEntries = 10_000;
    private const long MaximumUncompressedBytes = 200L * 1024 * 1024;

    public static EpubExtractionResult Extract(byte[] bytes, string? selector = null)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            ValidateArchive(archive);
            var entries = archive.Entries.ToDictionary(
                entry => NormalizePath(entry.FullName),
                StringComparer.Ordinal);

            if (!entries.TryGetValue("mimetype", out var mimetype) ||
                ReadText(mimetype) != "application/epub+zip")
            {
                throw new InvalidDataException("The epub mimetype entry is missing or invalid.");
            }

            var container = LoadXml(GetRequiredEntry(entries, "META-INF/container.xml"));
            XNamespace containerNamespace = "urn:oasis:names:tc:opendocument:xmlns:container";
            var packagePath = container
                .Descendants(containerNamespace + "rootfile")
                .Select(element => (string?)element.Attribute("full-path"))
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (packagePath is null)
            {
                throw new InvalidDataException("The epub package path is missing.");
            }

            packagePath = NormalizePath(packagePath);
            var package = LoadXml(GetRequiredEntry(entries, packagePath));
            XNamespace opf = "http://www.idpf.org/2007/opf";
            var packageDirectory = GetDirectory(packagePath);
            var manifest = package
                .Descendants(opf + "item")
                .Select(element => new ManifestItem(
                    (string?)element.Attribute("id") ?? string.Empty,
                    ResolvePath(packageDirectory, (string?)element.Attribute("href") ?? string.Empty),
                    (string?)element.Attribute("media-type") ?? string.Empty,
                    (string?)element.Attribute("properties") ?? string.Empty))
                .Where(item => item.Id.Length > 0)
                .ToDictionary(item => item.Id, StringComparer.Ordinal);

            var documents = new List<EpubDocumentExtraction>();
            var included = new HashSet<string>(StringComparer.Ordinal);
            var spineIndex = 0;
            foreach (var itemReference in package.Descendants(opf + "itemref"))
            {
                var id = (string?)itemReference.Attribute("idref");
                if (id is not null && manifest.TryGetValue(id, out var item) && IsXhtml(item))
                {
                    documents.Add(ExtractDocument(
                        entries,
                        item,
                        documents.Count + 1,
                        spineIndex,
                        "content",
                        selector));
                    included.Add(item.Path);
                }

                spineIndex++;
            }

            foreach (var item in manifest.Values.Where(item => HasProperty(item.Properties, "nav")))
            {
                if (!included.Contains(item.Path))
                {
                    documents.Add(ExtractDocument(
                        entries,
                        item,
                        documents.Count + 1,
                        null,
                        "navigation",
                        selector));
                }
            }

            if (selector is not null &&
                !documents.SelectMany(document => document.Extraction.Units).Any(unit => unit.IsSegment))
            {
                throw new InvalidFileTaskRequestException(
                    "selector_no_match",
                    "The CSS selector did not match any translatable EPUB content.");
            }

            return new EpubExtractionResult(documents);
        }
        catch (InvalidFileTaskRequestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException)
        {
            throw new InvalidFileTaskRequestException(
                "import_parse_failed",
                "The epub file is invalid or unsafe.",
                exception);
        }
    }

    private static EpubDocumentExtraction ExtractDocument(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        ManifestItem item,
        int order,
        int? spineIndex,
        string role,
        string? selector)
    {
        var entry = GetRequiredEntry(entries, item.Path);
        var bytes = ReadBytes(entry);
        return new EpubDocumentExtraction(
            item.Path,
            order,
            spineIndex,
            role,
            HtmlExtraction.Extract(bytes, selector, allowSelectorNoMatch: selector is not null));
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException("The epub contains too many entries.");
        }

        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            _ = NormalizePath(entry.FullName);
            totalLength = checked(totalLength + entry.Length);
            if (totalLength > MaximumUncompressedBytes)
            {
                throw new InvalidDataException("The epub expands beyond the allowed size.");
            }

            if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > 100)
            {
                throw new InvalidDataException("The epub contains a suspicious compression ratio.");
            }
        }
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string ReadText(ZipArchiveEntry entry) =>
        System.Text.Encoding.UTF8.GetString(ReadBytes(entry));

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var destination = new MemoryStream();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static ZipArchiveEntry GetRequiredEntry(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string path) => entries.TryGetValue(NormalizePath(path), out var entry)
        ? entry
        : throw new InvalidDataException($"Required epub entry is missing: {path}");

    private static bool IsXhtml(ManifestItem item) =>
        item.MediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    private static bool HasProperty(string properties, string value) =>
        properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.Ordinal);

    private static string GetDirectory(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string ResolvePath(string directory, string relativePath) =>
        NormalizePath(directory.Length == 0 ? relativePath : $"{directory}/{relativePath}");

    private static string NormalizePath(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>();
        foreach (var segment in segments)
        {
            if (segment is ".")
            {
                continue;
            }

            if (segment is ".." || segment.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidDataException("The epub contains an unsafe path.");
            }

            normalized.Add(segment);
        }

        return string.Join('/', normalized);
    }

    private sealed record ManifestItem(string Id, string Path, string MediaType, string Properties);
}

internal sealed record EpubExtractionResult(IReadOnlyList<EpubDocumentExtraction> Documents);

internal sealed record EpubDocumentExtraction(
    string ResourcePath,
    int Order,
    int? SpineIndex,
    string Role,
    TextExtractionResult Extraction);
