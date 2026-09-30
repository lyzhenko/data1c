namespace Data1c.Store;

/// <summary>
/// Схема индекса. Все таблицы создаются идемпотентно, версия схемы хранится в <c>meta</c>.
/// </summary>
/// <remarks>
/// Смысл таблиц:
/// <list type="bullet">
/// <item><c>files</c> — файлы выгрузки с размером и временем правки: основа инкрементальной переиндексации;</item>
/// <item><c>nodes</c>, <c>edges</c> — граф зависимостей (объекты, модули, процедуры, платформа, внешние цели);</item>
/// <item><c>symbols</c> — процедуры и функции с номерами строк: поиск и проверка кода;</item>
/// <item><c>calls</c> — вызовы с текстом цели: работает даже когда цель не разрешена;</item>
/// <item><c>metadata_objects</c>, <c>metadata_items</c> — объекты конфигурации, реквизиты, формы, макеты;</item>
/// <item><c>metadata_refs</c> — обращения к метаданным из кода, из текстов запросов, типы и права;</item>
/// <item><c>*_fts</c> — полнотекстовый поиск: триграммы для имён (поиск по подстроке), unicode61 для термов и BM25.</item>
/// </list>
/// </remarks>
internal static class IndexSchema
{
    /// <summary>Версия схемы. Меняется вместе с DDL.</summary>
    internal const int Version = 3;

    internal static readonly string[] Statements =
    [
        "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT)",

        """
        CREATE TABLE IF NOT EXISTS files (
            path TEXT PRIMARY KEY,
            size INTEGER NOT NULL,
            mtime INTEGER NOT NULL
        )
        """,

        """
        CREATE TABLE IF NOT EXISTS nodes (
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            name_lower TEXT NOT NULL,
            source_path TEXT,
            metadata_kind TEXT,
            is_external INTEGER NOT NULL DEFAULT 0,
            platform_title TEXT,
            platform_version TEXT
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_nodes_kind ON nodes(kind)",
        "CREATE INDEX IF NOT EXISTS idx_nodes_name ON nodes(name_lower)",
        "CREATE INDEX IF NOT EXISTS idx_nodes_path ON nodes(source_path)",

        """
        CREATE TABLE IF NOT EXISTS edges (
            source_id TEXT NOT NULL,
            target_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            line INTEGER,
            detail TEXT
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_edges_source ON edges(source_id, kind)",
        "CREATE INDEX IF NOT EXISTS idx_edges_target ON edges(target_id, kind)",
        "CREATE INDEX IF NOT EXISTS idx_edges_kind ON edges(kind)",

        // Поиск вызывающих по тексту вызова (имя цели) — частый запрос агента: имя известно, узла нет.
        "CREATE INDEX IF NOT EXISTS idx_edges_calls_detail ON edges(detail) WHERE kind = 'Calls'",

        """
        CREATE TABLE IF NOT EXISTS symbols (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            node_id TEXT NOT NULL,
            module_path TEXT NOT NULL,
            owner_id TEXT,
            name TEXT NOT NULL,
            name_lower TEXT NOT NULL,
            kind TEXT NOT NULL,
            is_export INTEGER NOT NULL DEFAULT 0,
            start_line INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            region TEXT,
            parameters TEXT,
            directives TEXT,
            comment_head TEXT
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name_lower)",
        "CREATE INDEX IF NOT EXISTS idx_symbols_module ON symbols(module_path)",
        "CREATE INDEX IF NOT EXISTS idx_symbols_owner ON symbols(owner_id)",
        "CREATE INDEX IF NOT EXISTS idx_symbols_node ON symbols(node_id)",

        """
        CREATE TABLE IF NOT EXISTS metadata_objects (
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            name_lower TEXT NOT NULL,
            synonym TEXT,
            uuid TEXT,
            source_path TEXT
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_objects_kind ON metadata_objects(kind)",
        "CREATE INDEX IF NOT EXISTS idx_objects_name ON metadata_objects(name_lower)",

        """
        CREATE TABLE IF NOT EXISTS metadata_items (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            object_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            name_lower TEXT NOT NULL,
            type_info TEXT,
            parent_id TEXT
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_items_object ON metadata_items(object_id)",
        "CREATE INDEX IF NOT EXISTS idx_items_name ON metadata_items(name_lower)",

        """
        CREATE TABLE IF NOT EXISTS metadata_refs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source_id TEXT NOT NULL,
            target_id TEXT NOT NULL,
            context TEXT NOT NULL,
            line INTEGER,
            detail TEXT
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_refs_target ON metadata_refs(target_id)",
        "CREATE INDEX IF NOT EXISTS idx_refs_source ON metadata_refs(source_id)",

        // Префиксный поиск по именам узлов: unicode61 с prefix занимает несопоставимо меньше места,
        // чем триграммы, и закрывает обычный случай — имя набирают с начала.
        """
        CREATE VIRTUAL TABLE IF NOT EXISTS nodes_fts USING fts5(
            node_id UNINDEXED,
            name,
            tokenize='unicode61 remove_diacritics 1',
            prefix='2 3 4'
        )
        """,

        // Смысловые термы для ранжирования: имя по частям, шапка комментария, имена параметров.
        """
        CREATE VIRTUAL TABLE IF NOT EXISTS terms_fts USING fts5(
            symbol_id UNINDEXED,
            name_tokens,
            comment_head,
            param_tokens,
            tokenize='unicode61 remove_diacritics 1'
        )
        """,
    ];
}
