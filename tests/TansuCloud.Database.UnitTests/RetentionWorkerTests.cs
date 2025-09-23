// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TansuCloud.Database.Services;
using TansuCloud.Observability.Auditing;

namespace TansuCloud.Database.UnitTests;

public sealed class RetentionWorkerTests
{
    [Fact]
    public async Task RunOnceAsync_Deletes_WhenRedactFalse_WithoutHolds()
    {
        // Arrange
        var auditOpts = Options.Create(
            new AuditOptions { ConnectionString = "Host=ignored;", Table = "audit_events" }
        );
        var retention = Options.Create(
            new AuditRetentionOptions
            {
                Days = 30,
                RedactInsteadOfDelete = false,
                LegalHoldTenants = Array.Empty<string>()
            }
        );
        var fakeDb = new FakeDb();
        var factory = new FakeConnFactory(fakeDb);
        var audit = new FakeAuditLogger();
        var lifetime = new FakeLifetime();
        var logger = NullLogger<AuditRetentionWorker>.Instance;

        var worker = new AuditRetentionWorker(
            auditOpts,
            retention,
            logger,
            lifetime,
            audit,
            factory
        );

        // Act
        await worker.RunOnceAsync(CancellationToken.None);

        // Assert
        fakeDb.LastCommandText.Should().NotBeNull();
        fakeDb
            .LastCommandText!.TrimStart()
            .StartsWith("DELETE FROM audit_events", StringComparison.OrdinalIgnoreCase)
            .Should()
            .BeTrue();
        fakeDb.LastCommandText.Should().Contain("WHERE when_utc < @cutoff");
        fakeDb.LastCommandText.Should().NotContain("tenant_id <> ALL(@holds)");
        fakeDb.LastParameters.Select(p => p.ParameterName).Should().Contain("@cutoff");
        fakeDb.LastParameters.Select(p => p.ParameterName).Should().NotContain("@holds");

        audit.Enqueued.Should().Be(1);
        audit.LastEvent.Should().NotBeNull();
        audit.LastEvent!.Category.Should().Be("Admin");
        audit.LastEvent!.Action.Should().Be("AuditRetention");
        audit.LastEvent!.Outcome.Should().Be("Success");
    } // End of Method RunOnceAsync_Deletes_WhenRedactFalse_WithoutHolds

    [Fact]
    public async Task RunOnceAsync_Redacts_WhenRedactTrue_WithHolds()
    {
        // Arrange
        var auditOpts = Options.Create(
            new AuditOptions { ConnectionString = "Host=ignored;", Table = "audit_events" }
        );
        var retention = Options.Create(
            new AuditRetentionOptions
            {
                Days = 90,
                RedactInsteadOfDelete = true,
                LegalHoldTenants = new[] { "tenantA", "tenantB" }
            }
        );
        var fakeDb = new FakeDb();
        var factory = new FakeConnFactory(fakeDb);
        var audit = new FakeAuditLogger();
        var lifetime = new FakeLifetime();
        var logger = NullLogger<AuditRetentionWorker>.Instance;

        var worker = new AuditRetentionWorker(
            auditOpts,
            retention,
            logger,
            lifetime,
            audit,
            factory
        );

        // Act
        await worker.RunOnceAsync(CancellationToken.None);

        // Assert
        fakeDb.LastCommandText.Should().NotBeNull();
        fakeDb
            .LastCommandText!.TrimStart()
            .StartsWith("UPDATE audit_events", StringComparison.OrdinalIgnoreCase)
            .Should()
            .BeTrue();
        fakeDb.LastCommandText.Should().Contain("SET details = NULL");
        fakeDb.LastCommandText.Should().Contain("reason_code = 'Retention'");
        fakeDb.LastCommandText.Should().Contain("WHERE when_utc < @cutoff");
        fakeDb.LastCommandText.Should().Contain("tenant_id <> ALL(@holds)");
        fakeDb
            .LastParameters.Select(p => p.ParameterName)
            .Should()
            .Contain(new[] { "@cutoff", "@holds" });

        audit.Enqueued.Should().Be(1);
        audit.LastEvent.Should().NotBeNull();
        audit.LastEvent!.Category.Should().Be("Admin");
        audit.LastEvent!.Action.Should().Be("AuditRetention");
        audit.LastEvent!.Outcome.Should().Be("Success");
    } // End of Method RunOnceAsync_Redacts_WhenRedactTrue_WithHolds

    // Minimal fake IAuditLogger capturing last enqueued event
    private sealed class FakeAuditLogger : IAuditLogger
    {
        public int Enqueued { get; private set; }
        public AuditEvent? LastEvent { get; private set; }

        public bool TryEnqueue(AuditEvent evt)
        {
            Enqueued++;
            LastEvent = evt;
            return true;
        } // End of Method TryEnqueue
    } // End of Class FakeAuditLogger

    // Fake host lifetime (unused by RunOnceAsync)
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() { }
    } // End of Class FakeLifetime

    // Shared fake DB wiring
    private sealed class FakeConnFactory : IAuditDbConnectionFactory
    {
        private readonly FakeDb _db;

        public FakeConnFactory(FakeDb db) => _db = db;

        public Task<DbConnection> CreateAsync(CancellationToken ct) =>
            Task.FromResult<DbConnection>(new FakeConnection(_db));
    } // End of Class FakeConnFactory

    private sealed class FakeDb
    {
        public string? LastCommandText { get; set; }
        public List<DbParameter> LastParameters { get; } = new();
        public int NextResult { get; set; } = 1;
    } // End of Class FakeDb

    private sealed class FakeConnection : DbConnection
    {
        private readonly FakeDb _db;
        private ConnectionState _state = ConnectionState.Closed;

        public FakeConnection(FakeDb db) => _db = db;

        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) { }

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            _state = ConnectionState.Open;
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new FakeCommand(_db);
    } // End of Class FakeConnection

    private sealed class FakeCommand : DbCommand
    {
        private readonly FakeDb _db;
        private readonly FakeParameterCollection _parameters = new();

        public FakeCommand(FakeDb db) => _db = db;

        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = 30;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }

        public override int ExecuteNonQuery()
        {
            _db.LastCommandText = CommandText;
            _db.LastParameters.Clear();
            _db.LastParameters.AddRange(_parameters.Items);
            return _db.NextResult;
        }

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() { }

        protected override DbParameter CreateDbParameter() => new FakeParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException();

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            _db.LastCommandText = CommandText;
            _db.LastParameters.Clear();
            _db.LastParameters.AddRange(_parameters.Items);
            return Task.FromResult(_db.NextResult);
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task PrepareAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    } // End of Class FakeCommand

    private sealed class FakeParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        public override string ParameterName { get; set; } = string.Empty;
        public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }

        public override void ResetDbType() { }
    } // End of Class FakeParameter

    private sealed class FakeParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = new();
        public IReadOnlyList<DbParameter> Items => _items;
        public override int Count => _items.Count;
        public override object SyncRoot { get; } = new();

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var v in values)
                Add(v!);
        }

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains((DbParameter)value);

        public override bool Contains(string value) => _items.Any(p => p.ParameterName == value);

        public override void CopyTo(Array array, int index) =>
            _items.ToArray().CopyTo(array, index);

        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        protected override DbParameter GetParameter(int index) => _items[index];

        protected override DbParameter GetParameter(string parameterName) =>
            _items.First(p => p.ParameterName == parameterName);

        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) =>
            _items.FindIndex(p => p.ParameterName == parameterName);

        public override void Insert(int index, object value) =>
            _items.Insert(index, (DbParameter)value);

        public override void Remove(object value) => _items.Remove((DbParameter)value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName)
        {
            var i = IndexOf(parameterName);
            if (i >= 0)
                _items.RemoveAt(i);
        }

        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value)
        {
            var i = IndexOf(parameterName);
            if (i >= 0)
                _items[i] = value;
            else
                _items.Add(value);
        }

        public override bool IsFixedSize => false;
        public override bool IsReadOnly => false;
        public override bool IsSynchronized => false;
    } // End of Class FakeParameterCollection
} // End of Class RetentionWorkerTests
