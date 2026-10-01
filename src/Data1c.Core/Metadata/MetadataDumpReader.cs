using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Xml;
using Data1c.Core.Bsl;
using Data1c.Core.Dump;

namespace Data1c.Core.Metadata;

/// <summary>Настройки чтения выгрузки.</summary>
public sealed record MetadataReadOptions
{
    /// <summary>Привязывать файлы модулей (.bsl) к объектам метаданных.</summary>
    public bool AttachModules { get; init; } = true;

    /// <summary>Разбирать права ролей (Ext/Rights.xml). Даёт сотни тысяч рёбер, по умолчанию выключено.</summary>
    public bool IncludeRoleRights { get; init; }

    /// <summary>Ограничить разбор указанными каталогами выгрузки («Catalogs», «CommonModules», ...).</summary>
    public IReadOnlyCollection<string>? Sections { get; init; }

    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    public IProgress<MetadataReadProgress>? Progress { get; init; }
}

public sealed record MetadataReadProgress(int Processed, int Total);

/// <summary>Файл модуля BSL с привязкой к объекту метаданных.</summary>
public sealed record MdModuleRef(DumpFile File, string? OwnerId, BslModuleKind Kind);

/// <summary>Результат чтения метаданных.</summary>
public sealed record MetadataReadResult(
    MdObjectModel Model,
    IReadOnlyList<MdModuleRef> ModuleFiles,
    IReadOnlyList<string> Warnings,
    int ParsedFiles,
    int SkippedFiles,
    int FailedFiles);

/// <summary>
/// Читает выгрузку конфигурации 1С (формат «XML + BSL») в модель метаданных.
/// </summary>
/// <remarks>
/// Разбор устойчив к незавершённой выгрузке: занятые и частично записанные файлы пропускаются
/// с предупреждением, а недостающие объекты остаются в модели в виде ссылок по имени из
/// <c>ChildObjects</c> родительского объекта.
/// </remarks>
public sealed class MetadataDumpReader
{
    private static readonly FrozenSet<string> IgnoredFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Rights.xml",
        "Form.xml",
        "Help.xml",
        "Template.xml",
        "Picture.xml",
        "Package.xml",
        "CommandInterface.xml",
        "ClientApplicationInterface.xml",
        "MainSectionCommandInterface.xml",
        "HomePageWorkArea.xml",
        "Logo.xml",
        "Splash.xml",
        "MainSectionPicture.xml",
        "Schedule.xml",
        "Flowchart.xml",
        "DCS.xml",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
        CheckCharacters = false,
    };

    public MetadataReadResult Read(
        IDumpSource source,
        MetadataReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new MetadataReadOptions();

        var warnings = new ConcurrentBag<string>();
        var xmlFiles = new List<DumpFile>();
        var bslFiles = new List<DumpFile>();
        var rightsFiles = new List<DumpFile>();
        var hasConfigurationFile = false;

        foreach (var file in source.EnumerateFiles(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (file.Extension)
            {
                case ".bsl" when options.AttachModules:
                    if (IsInSections(file, options.Sections))
                    {
                        bslFiles.Add(file);
                    }

                    break;

                case ".xml":
                    if (!IsInSections(file, options.Sections))
                    {
                        break;
                    }

                    var fileName = DumpPath.GetFileName(file.RelativePath);
                    if (string.Equals(fileName, "Configuration.xml", StringComparison.OrdinalIgnoreCase))
                    {
                        hasConfigurationFile = true;
                    }
                    else if (string.Equals(fileName, "Rights.xml", StringComparison.OrdinalIgnoreCase))
                    {
                        if (options.IncludeRoleRights)
                        {
                            rightsFiles.Add(file);
                        }

                        break;
                    }

                    if (IgnoredFileNames.Contains(fileName))
                    {
                        break;
                    }

                    xmlFiles.Add(file);
                    break;
            }
        }

        var parsed = new ConcurrentBag<ParsedFile>();
        var processed = 0;
        var total = xmlFiles.Count;
        var failed = 0;
        var skipped = 0;

        Parallel.ForEach(
            xmlFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = cancellationToken,
            },
            file =>
            {
                try
                {
                    using var stream = source.OpenRead(file);
                    var obj = ParseMetaDataFile(stream, file.RelativePath, warnings);
                    if (obj is null)
                    {
                        Interlocked.Increment(ref skipped);
                    }
                    else
                    {
                        parsed.Add(new ParsedFile(file.RelativePath, obj));
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    warnings.Add($"Файл «{file.RelativePath}» не разобран: {ex.Message}");
                }

                var done = Interlocked.Increment(ref processed);
                if (options.Progress is not null && (done % 256 == 0 || done == total))
                {
                    options.Progress.Report(new MetadataReadProgress(done, total));
                }
            });

        var byPath = new Dictionary<string, MdObject>(StringComparer.Ordinal);
        MdObject? configuration = null;
        foreach (var (path, obj) in parsed)
        {
            obj.IsFileRoot = true;
            if (obj.Kind == MdKind.Configuration)
            {
                configuration ??= obj;
                continue;
            }

            byPath[path] = obj;
        }

        if (hasConfigurationFile && configuration is null)
        {
            warnings.Add("Configuration.xml не прочитан (файл занят или ещё не записан): корень конфигурации создан без свойств.");
        }

        configuration ??= new MdObject(MdKind.Configuration, "Configuration");
        if (configuration.Name.Length == 0)
        {
            configuration.Name = "Configuration";
        }

        LinkHierarchy(byPath, configuration);
        RemoveNameOnlyDuplicates(configuration);

        var allObjects = new List<MdObject> { configuration };
        allObjects.AddRange(configuration.Descendants());

        var dirIndex = BuildDirectoryIndex(allObjects);
        var moduleRefs = AttachModules(bslFiles, dirIndex, configuration);

        if (rightsFiles.Count > 0)
        {
            AttachRoleRights(source, rightsFiles, dirIndex, configuration, warnings, cancellationToken);
        }

        var model = new MdObjectModel(configuration, allObjects);
        var orderedWarnings = warnings.OrderBy(static w => w, StringComparer.Ordinal).ToList();

        return new MetadataReadResult(model, moduleRefs, orderedWarnings, parsed.Count, skipped, failed);
    }

    private static bool IsInSections(DumpFile file, IReadOnlyCollection<string>? sections)
    {
        if (sections is null || sections.Count == 0)
        {
            return true;
        }

        var segments = DumpPath.Segments(file.RelativePath);
        return segments.Length < 2 || sections.Contains(segments[0], StringComparer.OrdinalIgnoreCase);
    }

    private static void LinkHierarchy(Dictionary<string, MdObject> byPath, MdObject configuration)
    {
        foreach (var (path, obj) in byPath.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            var parent = FindParentByPath(path, byPath) ?? configuration;
            obj.Parent = parent;
            parent.ChildrenMutable.Add(obj);
        }
    }

    /// <summary>Ближайший каталог, для которого в выгрузке есть файл «&lt;каталог&gt;.xml».</summary>
    private static MdObject? FindParentByPath(string objectPath, Dictionary<string, MdObject> byPath)
    {
        var directory = DumpPath.GetDirectory(objectPath);
        while (directory.Length > 0)
        {
            if (byPath.TryGetValue(directory + ".xml", out var parent))
            {
                return parent;
            }

            directory = DumpPath.GetDirectory(directory);
        }

        return null;
    }

    /// <summary>
    /// Убирает дубликаты «ссылка по имени + полноценный объект»: в ChildObjects родителя объект
    /// часто указан только именем, а его свойства лежат в отдельном файле.
    /// </summary>
    private static void RemoveNameOnlyDuplicates(MdObject configuration)
    {
        var queue = new Queue<MdObject>();
        queue.Enqueue(configuration);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            var children = parent.ChildrenMutable;
            foreach (var child in children)
            {
                queue.Enqueue(child);
            }

            if (children.Count < 2)
            {
                continue;
            }

            foreach (var group in children.GroupBy(static c => (c.Kind, c.Name)).ToList())
            {
                var groupItems = group.ToList();
                if (groupItems.Count < 2)
                {
                    continue;
                }

                var keep = groupItems.FirstOrDefault(static c => !c.IsNameOnlyReference);
                if (keep is null)
                {
                    continue;
                }

                foreach (var item in groupItems)
                {
                    if (!ReferenceEquals(item, keep))
                    {
                        children.Remove(item);
                    }
                }
            }
        }
    }

    private static Dictionary<string, MdObject> BuildDirectoryIndex(List<MdObject> allObjects)
    {
        var index = new Dictionary<string, MdObject>(StringComparer.Ordinal);
        foreach (var obj in allObjects)
        {
            var directory = ComputeDirectory(obj);
            obj.Directory = directory;
            if (directory.Length > 0)
            {
                index.TryAdd(directory, obj);
            }
        }

        return index;
    }

    private static string ComputeDirectory(MdObject obj)
    {
        if (obj.Kind == MdKind.Configuration)
        {
            return string.Empty;
        }

        if (obj.IsFileRoot && obj.SourcePath is not null)
        {
            // Каталог объекта — это каталог его XML-файла плюс имя файла без расширения,
            // рядом с которым лежат Ext, Forms, Templates и Commands.
            return $"{DumpPath.GetDirectory(obj.SourcePath)}/{DumpPath.GetFileNameWithoutExtension(obj.SourcePath)}";
        }

        if (obj.Parent?.Directory is { Length: > 0 } parentDirectory)
        {
            return $"{parentDirectory}/{FolderFor(obj.Kind)}/{obj.Name}";
        }

        return string.Empty;
    }

    private static string FolderFor(MdKind kind) => kind.Name switch
    {
        "Command" => "Commands",
        "Form" => "Forms",
        "Template" => "Templates",
        "Attribute" => "Attributes",
        "TabularSection" => "TabularSections",
        _ => kind.Name + "s",
    };

    private static List<MdModuleRef> AttachModules(
        List<DumpFile> bslFiles,
        Dictionary<string, MdObject> dirIndex,
        MdObject configuration)
    {
        var result = new List<MdModuleRef>(bslFiles.Count);
        foreach (var file in bslFiles)
        {
            var kind = BslModuleKinds.FromPath(file.RelativePath);
            var owner = FindOwnerByDirectory(file.RelativePath, dirIndex) ?? configuration;
            owner.ModulesMutable.Add(new MdModuleFile(file.RelativePath, kind));
            result.Add(new MdModuleRef(file, owner.Id, kind));
        }

        return result;
    }

    private static MdObject? FindOwnerByDirectory(string relativePath, Dictionary<string, MdObject> dirIndex)
    {
        var directory = DumpPath.GetDirectory(relativePath);
        while (directory.Length > 0)
        {
            if (dirIndex.TryGetValue(directory, out var owner))
            {
                return owner;
            }

            directory = DumpPath.GetDirectory(directory);
        }

        return null;
    }

    private static void AttachRoleRights(
        IDumpSource source,
        List<DumpFile> rightsFiles,
        Dictionary<string, MdObject> dirIndex,
        MdObject configuration,
        ConcurrentBag<string> warnings,
        CancellationToken cancellationToken)
    {
        Parallel.ForEach(
            rightsFiles,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            file =>
            {
                var owner = FindOwnerByDirectory(file.RelativePath, dirIndex) ?? configuration;
                try
                {
                    using var stream = source.OpenRead(file);
                    var references = ParseRights(stream, file.RelativePath);
                    owner.ReferencesMutable.AddRange(references);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Права роли «{file.RelativePath}» не разобраны: {ex.Message}");
                }
            });
    }

    // --- Разбор XML -------------------------------------------------------------------------

    private static MdObject? ParseMetaDataFile(Stream stream, string sourcePath, ConcurrentBag<string> warnings)
    {
        using var reader = XmlReader.Create(DumpTextReader.CreateTextReader(stream), ReaderSettings);
        if (!MoveToElement(reader) || !string.Equals(reader.LocalName, "MetaDataObject", StringComparison.Ordinal))
        {
            return null;
        }

        if (!MoveToElement(reader))
        {
            return null;
        }

        var obj = ParseObject(reader, sourcePath, warnings);
        if (obj.Name.Length == 0)
        {
            obj.Name = DumpPath.GetFileNameWithoutExtension(sourcePath);
        }

        return obj;
    }

    private static bool MoveToElement(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                return true;
            }
        }

        return false;
    }

    private static MdObject ParseObject(XmlReader reader, string sourcePath, ConcurrentBag<string> warnings)
    {
        var obj = new MdObject(new MdKind(reader.LocalName), string.Empty) { SourcePath = sourcePath };
        if (reader.GetAttribute("uuid") is { } uuidText && Guid.TryParse(uuidText, out var uuid))
        {
            obj.Uuid = uuid;
        }

        if (reader.IsEmptyElement)
        {
            obj.IsNameOnlyReference = true;
            return obj;
        }

        var depth = reader.Depth;
        var text = new StringBuilder();
        var sawStructure = false;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    text.Append(reader.Value);
                    break;

                case XmlNodeType.Element:
                    sawStructure = true;
                    switch (reader.LocalName)
                    {
                        case "Properties":
                            ParseProperties(reader, obj);
                            break;
                        case "ChildObjects":
                            ParseChildObjects(reader, obj, sourcePath, warnings);
                            break;
                        case "InternalInfo":
                            SkipCurrent(reader);
                            break;
                        default:
                            ScanAndCollect(reader, obj, ReferenceKindFor(reader.LocalName, MdReferenceKind.Other), reader.LocalName);
                            break;
                    }

                    break;
            }
        }

        var directText = text.ToString().Trim();
        if (directText.Length > 0 && obj.Name.Length == 0)
        {
            obj.Name = directText;
        }

        if (!sawStructure)
        {
            obj.IsNameOnlyReference = true;
        }

        return obj;
    }

    private static void ParseProperties(XmlReader reader, MdObject obj)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "Name":
                    obj.Name = ReadLeafText(reader);
                    break;
                case "Synonym":
                    obj.Synonym ??= ParseSynonym(reader, obj);
                    break;
                case "Comment":
                    obj.Comment = ReadLeafText(reader);
                    break;
                default:
                    var propertyName = reader.LocalName;
                    var value = ScanAndCollect(reader, obj, ReferenceKindFor(propertyName, MdReferenceKind.Other), propertyName);
                    obj.PropertiesMutable[propertyName] = value.Length == 0 ? null : value;
                    break;
            }
        }
    }

    private static void ParseChildObjects(XmlReader reader, MdObject parent, string sourcePath, ConcurrentBag<string> warnings)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var child = ParseObject(reader, sourcePath, warnings);
            child.Parent = parent;
            parent.ChildrenMutable.Add(child);
        }
    }

    private static string? ParseSynonym(XmlReader reader, MdObject obj)
    {
        if (reader.IsEmptyElement)
        {
            return null;
        }

        var depth = reader.Depth;
        string? russian = null;
        string? first = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (!string.Equals(reader.LocalName, "item", StringComparison.Ordinal))
            {
                SkipCurrent(reader);
                continue;
            }

            var itemDepth = reader.Depth;
            string? lang = null;
            string? content = null;
            if (!reader.IsEmptyElement)
            {
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == itemDepth)
                    {
                        break;
                    }

                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    switch (reader.LocalName)
                    {
                        case "lang":
                            lang = ReadLeafText(reader);
                            break;
                        case "content":
                            content = ReadLeafText(reader);
                            break;
                        default:
                            SkipCurrent(reader);
                            break;
                    }
                }
            }

            if (content is null)
            {
                continue;
            }

            first ??= content;
            if (string.Equals(lang, "ru", StringComparison.OrdinalIgnoreCase))
            {
                russian ??= content;
            }
        }

        return russian ?? first;
    }

    /// <summary>Читает текстовое значение элемента (только непосредственный текст) и оставляет ридер на его конце.</summary>
    private static string ReadLeafText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        var depth = reader.Depth;
        var text = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.Depth == depth + 1 &&
                reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                text.Append(reader.Value);
            }
        }

        return text.ToString().Trim();
    }

    /// <summary>Пропускает элемент целиком, оставляя ридер на его конечном теге (или на самом элементе, если он пуст).</summary>
    private static void SkipCurrent(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Читает элемент, собирает его непосредственный текст и проходит по вложенным элементам,
    /// пытаясь распознать в текстах ссылки на объекты метаданных.
    /// </summary>
    private static string ScanAndCollect(XmlReader reader, MdObject obj, MdReferenceKind referenceKind, string propertyPath)
    {
        var text = ScanElement(reader, obj, referenceKind, propertyPath);
        if (text.Length > 0)
        {
            AddReference(obj, text, referenceKind, propertyPath);
        }

        return text;
    }

    private static string ScanElement(XmlReader reader, MdObject obj, MdReferenceKind referenceKind, string propertyPath)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        var depth = reader.Depth;
        var text = new StringBuilder();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (reader.Depth == depth + 1)
                    {
                        text.Append(reader.Value);
                    }

                    break;

                case XmlNodeType.Element:
                    var childKind = ReferenceKindFor(reader.LocalName, referenceKind);
                    ScanAndCollect(reader, obj, childKind, propertyPath + "/" + reader.LocalName);
                    break;
            }
        }

        return text.ToString().Trim();
    }

    private static MdReferenceKind ReferenceKindFor(string localName, MdReferenceKind parentKind) => localName switch
    {
        "Type" => MdReferenceKind.Type,
        "Item" or "Content" or "Value" => MdReferenceKind.Content,
        "Field" => MdReferenceKind.Field,
        "Source" => MdReferenceKind.EventSource,
        "Class" or "Object" => MdReferenceKind.Other,
        "Form" or "DefaultObjectForm" or "DefaultFolderForm" or "DefaultListForm" or "DefaultChoiceForm"
            or "DefaultFolderChoiceForm" or "AuxiliaryObjectForm" or "AuxiliaryFolderForm" or "AuxiliaryListForm"
            or "AuxiliaryChoiceForm" or "AuxiliaryFolderChoiceForm" or "MainForm" => MdReferenceKind.Form,
        "Template" => MdReferenceKind.Template,
        "Command" => MdReferenceKind.Command,
        _ => parentKind,
    };

    private static void AddReference(MdObject obj, string text, MdReferenceKind kind, string propertyPath)
    {
        if (!MdRefParser.TryParse(text, out var targetKind, out var targetName, out var rest))
        {
            return;
        }

        if (targetKind.IsUnknown || targetName.Length == 0)
        {
            return;
        }

        var reference = new MdReference(
            MdNaming.CreateId(targetKind, targetName),
            targetKind,
            targetName,
            kind,
            rest,
            text);

        obj.AddReference(reference);
    }

    private static List<MdReference> ParseRights(Stream stream, string sourcePath)
    {
        var references = new List<MdReference>();
        using var reader = XmlReader.Create(DumpTextReader.CreateTextReader(stream), ReaderSettings);
        if (!MoveToElement(reader))
        {
            return references;
        }

        var depth = reader.Depth;
        if (reader.IsEmptyElement)
        {
            return references;
        }

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (!string.Equals(reader.LocalName, "object", StringComparison.Ordinal))
            {
                SkipCurrent(reader);
                continue;
            }

            ReadRightsObject(reader, references, sourcePath);
        }

        return references;
    }

    private static void ReadRightsObject(XmlReader reader, List<MdReference> references, string sourcePath)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (string.Equals(reader.LocalName, "name", StringComparison.Ordinal))
            {
                var text = ReadLeafText(reader);
                if (MdRefParser.TryParse(text, out var kind, out var name, out var rest) && !kind.IsUnknown)
                {
                    references.Add(new MdReference(
                        MdNaming.CreateId(kind, name),
                        kind,
                        name,
                        MdReferenceKind.RoleRight,
                        rest,
                        text));
                }

                continue;
            }

            SkipCurrent(reader);
        }
    }

    private readonly record struct ParsedFile(string Path, MdObject Object);
}
