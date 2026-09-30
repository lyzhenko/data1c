using Microsoft.Data.Sqlite;

namespace Data1c.Store;

/// <summary>
/// Соединение с индексом выгрузки. Одна база — один индекс; создание схемы идемпотентно,
/// версия схемы хранится в таблице <c>meta</c>.
/// </summary>
/// <remarks>
/// Соединение SQLite не потокобезопасно, а MCP-сервер вызывает инструменты параллельно,
/// поэтому запись и чтение выполняются под общим замком <see cref="SyncRoot"/>.
/// </remarks>
public sealed class SqliteIndex : IDisposable
{
    private static readonly Lock ProviderGate = new();
    private static bool _providerReady;

    private readonly Lock _gate = new();
    private bool _disposed;

    private SqliteIndex(string path, SqliteConnection connection, bool readOnly)
    {
        Path = path;
        Connection = connection;
        IsReadOnly = readOnly;
    }

    /// <summary>Версия схемы, которую поддерживает библиотека.</summary>
    public static int SupportedSchemaVersion => IndexSchema.Version;

    /// <summary>Путь к файлу базы (или <c>:memory:</c>).</summary>
    public string Path { get; }

    /// <summary>Индекс открыт только на чтение.</summary>
    public bool IsReadOnly { get; }

    internal SqliteConnection Connection { get; }

    /// <summary>Общий замок для операций с соединением.</summary>
    public Lock SyncRoot => _gate;

    /// <summary>Версия схемы в открытой базе.</summary>
    public int SchemaVersion => int.TryParse(GetMeta("schema_version"), out var version) ? version : 0;

    /// <summary>Открывает или создаёт индекс по указанному пути.</summary>
    public static SqliteIndex Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureProvider();

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        connection.Open();
        var index = new SqliteIndex(path, connection, readOnly: false);
        index.ApplyPragmas();
        index.EnsureSchema();
        return index;
    }

    /// <summary>Открывает существующий индекс только на чтение.</summary>
    public static SqliteIndex OpenReadOnly(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureProvider();

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

        connection.Open();
        var index = new SqliteIndex(path, connection, readOnly: true);
        index.ApplyPragmas(readOnly: true);
        if (index.SchemaVersion != IndexSchema.Version)
        {
            index.Dispose();
            throw new InvalidOperationException(
                $"Индекс «{path}» имеет версию схемы {index.SchemaVersion}, ожидается {IndexSchema.Version}. Пересоберите индекс.");
        }

        return index;
    }

    /// <summary>Индекс в памяти: нужен для тестов.</summary>
    public static SqliteIndex OpenInMemory()
    {
        EnsureProvider();
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var index = new SqliteIndex(":memory:", connection, readOnly: false);
        index.ApplyPragmas(inMemory: true);
        index.EnsureSchema();
        return index;
    }

    /// <summary>Проверяет, что файл похож на индекс нужной версии, не открывая его на запись.</summary>
    public static bool LooksLikeIndex(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var index = OpenReadOnly(path);
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            return false;
        }
    }

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var command = CreateCommand("SELECT value FROM meta WHERE key = @key");
            command.Parameters.AddWithValue("@key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string? value)
    {
        lock (_gate)
        {
            using var command = CreateCommand("INSERT OR REPLACE INTO meta (key, value) VALUES (@key, @value)");
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Создаёт команду соединения. Именно <see cref="SqliteConnection.CreateCommand"/>, а не
    /// <c>new SqliteCommand(sql, connection)</c>: первый подставляет текущую транзакцию соединения,
    /// без чего выполнение внутри открытой транзакции запрещено.
    /// </summary>
    internal SqliteCommand CreateCommand(string sql)
    {
        var command = Connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    internal void Execute(string sql)
    {
        using var command = CreateCommand(sql);
        command.ExecuteNonQuery();
    }

    internal long QueryScalar(string sql)
    {
        using var command = CreateCommand(sql);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Выполняет действие под замком соединения.</summary>
    public T WithLock<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            return action();
        }
    }

    /// <summary>Выполняет действие под замком соединения.</summary>
    public void WithLock(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            action();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Connection.Dispose();
    }

    private static void EnsureProvider()
    {
        if (_providerReady)
        {
            return;
        }

        lock (ProviderGate)
        {
            if (_providerReady)
            {
                return;
            }

            // Microsoft.Data.Sqlite.Core не тянет за собой нативную библиотеку: её подключает bundle_e_sqlite3.
            SQLitePCL.Batteries_V2.Init();
            _providerReady = true;
        }
    }

    private void ApplyPragmas(bool readOnly = false, bool inMemory = false)
    {
        if (!readOnly && !inMemory)
        {
            Execute("PRAGMA journal_mode=WAL");
        }

        Execute(readOnly ? "PRAGMA query_only=ON" : "PRAGMA synchronous=NORMAL");
        Execute("PRAGMA foreign_keys=ON");
        Execute("PRAGMA cache_size=-65536");
        Execute("PRAGMA temp_store=MEMORY");
        Execute("PRAGMA busy_timeout=5000");

        // Значения в индексе уже приведены к нижнему регистру, поэтому LIKE можно сделать
        // регистрозависимым: тогда 'префикс%' использует индекс, а не просматривает таблицу.
        Execute("PRAGMA case_sensitive_like=ON");
    }

    private void EnsureSchema()
    {
        lock (_gate)
        {
            using (var transaction = Connection.BeginTransaction())
            {
                foreach (var statement in IndexSchema.Statements)
                {
                    using var command = CreateCommand(statement);
                    command.Transaction = transaction;
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            var version = GetMeta("schema_version");
            if (version is null)
            {
                SetMeta("schema_version", IndexSchema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SetMeta("created_at", DateTimeOffset.UtcNow.ToString("O"));
            }
            else if (version != IndexSchema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException(
                    $"Индекс создан схемой версии {version}, библиотека ожидает {IndexSchema.Version}. Пересоберите индекс.");
            }
        }
    }
}
