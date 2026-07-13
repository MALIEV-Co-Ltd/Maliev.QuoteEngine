using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Options;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Maliev.QuoteEngine.Bff.Services;

public interface IQuoteUploadStateStore
{
    Task<UploadState> InitiateAsync(
        InitiateQuoteUploadRequest request,
        Guid? customerId,
        Guid? visitorId,
        CancellationToken cancellationToken);

    Task<QuoteUploadHandoffResponse> ImportHandoffAsync(
        QuoteUploadHandoffRequest request,
        Guid? customerId,
        Guid? visitorId,
        CancellationToken cancellationToken);

    Task<UploadState?> GetAsync(string uploadId, CancellationToken cancellationToken);

    Task<UploadState?> FindByStoragePathAsync(string storagePath, CancellationToken cancellationToken);

    Task<bool> PromoteSessionAsync(
        Guid sessionId,
        Guid expectedVisitorId,
        Guid customerId,
        CancellationToken cancellationToken);

    Task<UploadState> TrackAgentUploadAsync(
        string uploadId,
        Guid fileId,
        string fileName,
        string contentType,
        long fileSizeBytes,
        string storagePath,
        Guid sessionId,
        Guid? customerId,
        Guid? visitorId,
        CancellationToken cancellationToken);

    Task<UploadState> AttachDownstreamAsync(
        string uploadId,
        string downstreamUploadId,
        CancellationToken cancellationToken);

    Task<UploadState?> TryAdvanceAsync(
        string uploadId,
        long expectedReceivedBytes,
        long receivedBytes,
        CancellationToken cancellationToken);

    Task<UploadState> MarkProcessingAsync(
        string uploadId,
        Guid canonicalFileId,
        CancellationToken cancellationToken);

    Task<UploadState> MarkDemoAnalyzedAsync(
        string uploadId,
        DemoModeOptions options,
        CancellationToken cancellationToken);

    Task<UploadState> MarkAnalyzedAsync(string uploadId, CancellationToken cancellationToken);
}

internal sealed class InMemoryQuoteUploadStateStore : IQuoteUploadStateStore
{
    private readonly QuoteEnginePrototypeStore? _prototypeStore;
    private readonly object _gate = new();
    private readonly Dictionary<string, UploadState> _uploads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uploadIdsByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, HashSet<string>> _uploadIdsBySession = [];

    public InMemoryQuoteUploadStateStore()
    {
    }

    public InMemoryQuoteUploadStateStore(QuoteEnginePrototypeStore prototypeStore)
    {
        _prototypeStore = prototypeStore;
    }

    public Task<UploadState> InitiateAsync(
        InitiateQuoteUploadRequest request,
        Guid? customerId,
        Guid? visitorId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_prototypeStore is not null)
        {
            return Task.FromResult(_prototypeStore.InitiateUpload(request, customerId, visitorId));
        }

        var state = QuoteUploadStateRules.Create(request, customerId, visitorId);
        lock (_gate)
        {
            Add(state, overwrite: false);
        }

        return Task.FromResult(state);
    }

    public Task<QuoteUploadHandoffResponse> ImportHandoffAsync(
        QuoteUploadHandoffRequest request,
        Guid? customerId,
        Guid? visitorId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_prototypeStore is not null)
        {
            return Task.FromResult(_prototypeStore.ImportHandoff(request, customerId, visitorId));
        }

        var (states, response) = QuoteUploadStateRules.CreateHandoff(request, customerId, visitorId);
        lock (_gate)
        {
            foreach (var state in states)
            {
                Add(state, overwrite: true);
            }
        }

        return Task.FromResult(response);
    }

    public Task<UploadState?> GetAsync(string uploadId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_prototypeStore is not null)
        {
            return Task.FromResult(_prototypeStore.GetUpload(uploadId));
        }

        lock (_gate)
        {
            return Task.FromResult(
                QuoteUploadStateRules.IsSafeUploadId(uploadId) &&
                _uploads.TryGetValue(uploadId, out var state) &&
                QuoteUploadStateRules.IsValidPersisted(state)
                    ? state
                    : null);
        }
    }

    public Task<UploadState?> FindByStoragePathAsync(
        string storagePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_prototypeStore is not null)
        {
            return Task.FromResult(_prototypeStore.FindUploadByStoragePath(storagePath));
        }

        if (!QuoteAnalysisArtifactPathValidator.IsCanonicalUploadPath(storagePath))
        {
            return Task.FromResult<UploadState?>(null);
        }

        lock (_gate)
        {
            if (!_uploadIdsByPath.TryGetValue(storagePath, out var uploadId) ||
                !_uploads.TryGetValue(uploadId, out var state) ||
                !QuoteUploadStateRules.IsValidPersisted(state) ||
                !string.Equals(state.StoragePath, storagePath, StringComparison.Ordinal))
            {
                return Task.FromResult<UploadState?>(null);
            }

            return Task.FromResult<UploadState?>(state);
        }
    }

    public Task<bool> PromoteSessionAsync(
        Guid sessionId,
        Guid expectedVisitorId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty || expectedVisitorId == Guid.Empty || customerId == Guid.Empty)
        {
            return Task.FromResult(false);
        }

        if (_prototypeStore is not null)
        {
            var candidates = _prototypeStore.GetSessionUploads(sessionId);
            if (candidates.Any(state => state.CustomerId.HasValue
                    ? state.CustomerId != customerId
                    : state.VisitorId != expectedVisitorId))
            {
                return Task.FromResult(false);
            }

            _prototypeStore.PromoteSessionUploads(sessionId, expectedVisitorId, customerId);
            return Task.FromResult(true);
        }

        lock (_gate)
        {
            if (!_uploadIdsBySession.TryGetValue(sessionId, out var uploadIds) || uploadIds.Count == 0)
            {
                return Task.FromResult(true);
            }

            var states = new List<UploadState>(uploadIds.Count);
            foreach (var uploadId in uploadIds)
            {
                if (!_uploads.TryGetValue(uploadId, out var state) ||
                    !QuoteUploadStateRules.IsValidPersisted(state) ||
                    (state.CustomerId.HasValue
                        ? state.CustomerId != customerId
                        : state.VisitorId != expectedVisitorId))
                {
                    return Task.FromResult(false);
                }

                states.Add(state);
            }

            foreach (var state in states)
            {
                _uploads[state.UploadId] = state with { CustomerId = customerId, IsTemporary = false };
            }

            return Task.FromResult(true);
        }
    }

    public Task<UploadState> TrackAgentUploadAsync(
        string uploadId,
        Guid fileId,
        string fileName,
        string contentType,
        long fileSizeBytes,
        string storagePath,
        Guid sessionId,
        Guid? customerId,
        Guid? visitorId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_prototypeStore is not null)
        {
            return Task.FromResult(_prototypeStore.TrackAgentUpload(
                uploadId,
                fileId,
                fileName,
                contentType,
                fileSizeBytes,
                storagePath,
                sessionId,
                customerId,
                visitorId));
        }

        var state = QuoteUploadStateRules.CreateTracked(
            uploadId,
            fileId,
            fileName,
            contentType,
            fileSizeBytes,
            storagePath,
            sessionId,
            customerId,
            visitorId);
        lock (_gate)
        {
            Add(state, overwrite: true);
        }

        return Task.FromResult(state);
    }

    public Task<UploadState> AttachDownstreamAsync(
        string uploadId,
        string downstreamUploadId,
        CancellationToken cancellationToken) =>
        _prototypeStore is not null
            ? FromPrototype(
                () => _prototypeStore.AttachDownstreamUpload(uploadId, downstreamUploadId),
                cancellationToken)
            : UpdateRequiredAsync(
                uploadId,
                state => state with { DownstreamUploadId = QuoteUploadStateRules.RequireSafeUploadId(downstreamUploadId) },
                cancellationToken);

    public Task<UploadState?> TryAdvanceAsync(
        string uploadId,
        long expectedReceivedBytes,
        long receivedBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_prototypeStore is not null)
        {
            return Task.FromResult(_prototypeStore.TryAdvanceUpload(
                uploadId,
                expectedReceivedBytes,
                receivedBytes));
        }

        lock (_gate)
        {
            if (!_uploads.TryGetValue(uploadId, out var current) ||
                current.ReceivedBytes != expectedReceivedBytes ||
                receivedBytes <= expectedReceivedBytes ||
                receivedBytes > current.ExpectedSizeBytes)
            {
                return Task.FromResult<UploadState?>(null);
            }

            var updated = current with
            {
                ReceivedBytes = receivedBytes,
                Status = receivedBytes == current.ExpectedSizeBytes ? "Uploaded" : "Uploading"
            };
            _uploads[uploadId] = updated;
            return Task.FromResult<UploadState?>(updated);
        }
    }

    public Task<UploadState> MarkProcessingAsync(
        string uploadId,
        Guid canonicalFileId,
        CancellationToken cancellationToken) =>
        _prototypeStore is not null
            ? FromPrototype(
                () => _prototypeStore.MarkProcessing(uploadId, canonicalFileId),
                cancellationToken)
            : UpdateRequiredAsync(
                uploadId,
                state => state with { FileId = RequireCanonicalFileId(canonicalFileId), Status = "Processing" },
                cancellationToken);

    public Task<UploadState> MarkDemoAnalyzedAsync(
        string uploadId,
        DemoModeOptions options,
        CancellationToken cancellationToken) =>
        _prototypeStore is not null
            ? FromPrototype(() => _prototypeStore.MarkDemoAnalyzed(uploadId, options), cancellationToken)
            : UpdateRequiredAsync(uploadId, QuoteUploadStateRules.MarkDemoAnalyzed, cancellationToken);

    public Task<UploadState> MarkAnalyzedAsync(
        string uploadId,
        CancellationToken cancellationToken) =>
        _prototypeStore is not null
            ? FromPrototype(() => _prototypeStore.MarkAnalyzed(uploadId), cancellationToken)
            : UpdateRequiredAsync(uploadId, QuoteUploadStateRules.MarkAnalyzed, cancellationToken);

    private static Task<UploadState> FromPrototype(
        Func<UploadState> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(action());
    }

    private static Guid RequireCanonicalFileId(Guid fileId) =>
        fileId != Guid.Empty
            ? fileId
            : throw new ArgumentException("A canonical UploadService file identifier is required.", nameof(fileId));

    private Task<UploadState> UpdateRequiredAsync(
        string uploadId,
        Func<UploadState, UploadState> update,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_uploads.TryGetValue(uploadId, out var current))
            {
                throw new KeyNotFoundException($"Upload '{uploadId}' was not found.");
            }

            var updated = QuoteUploadStateRules.ForPersistence(update(current));
            _uploads[uploadId] = updated;
            return Task.FromResult(updated);
        }
    }

    private void Add(UploadState state, bool overwrite)
    {
        var persisted = QuoteUploadStateRules.ForPersistence(state);
        if (_uploadIdsByPath.TryGetValue(persisted.StoragePath, out var existingId) &&
            !string.Equals(existingId, persisted.UploadId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An upload already owns the canonical storage path.");
        }

        if (!overwrite && _uploads.ContainsKey(persisted.UploadId))
        {
            throw new InvalidOperationException("The upload identifier already exists.");
        }

        if (_uploads.TryGetValue(persisted.UploadId, out var existing))
        {
            if (!overwrite || !QuoteUploadStateRules.HasSameImmutableIdentity(existing, persisted))
            {
                throw new InvalidOperationException("The upload identifier is already bound to different durable metadata.");
            }

            return;
        }

        _uploads[persisted.UploadId] = persisted;
        _uploadIdsByPath[persisted.StoragePath] = persisted.UploadId;
        var sessionId = QuoteUploadStateRules.RequireSessionId(persisted.QuoteSessionId);
        if (!_uploadIdsBySession.TryGetValue(sessionId, out var uploadIds))
        {
            uploadIds = [];
            _uploadIdsBySession.Add(sessionId, uploadIds);
        }

        uploadIds.Add(persisted.UploadId);
    }
}

internal sealed class RedisQuoteUploadStateStore(
    IConnectionMultiplexer redis,
    IOptions<QuoteAgentRetentionOptions>? retentionOptions = null) : IQuoteUploadStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly QuoteAgentRetentionOptions _retention = retentionOptions?.Value ?? new QuoteAgentRetentionOptions();

    private const string CreateScript = """
        local existingPathOwner = redis.call('GET', KEYS[2])
        if existingPathOwner and existingPathOwner ~= ARGV[2] then
            return 0
        end
        local existingState = redis.call('GET', KEYS[1])
        if existingState and ARGV[4] ~= '1' then
            return 0
        end
        local incomingOk, incoming = pcall(cjson.decode, ARGV[1])
        if not incomingOk or type(incoming) ~= 'table' then
            return 0
        end
        if existingState then
            local ok, current = pcall(cjson.decode, existingState)
            if not ok or type(current) ~= 'table' or
               current.uploadId ~= incoming.uploadId or
               current.storagePath ~= incoming.storagePath or
               current.quoteSessionId ~= incoming.quoteSessionId or
               current.fileId ~= incoming.fileId or
               current.customerId ~= incoming.customerId or
               current.visitorId ~= incoming.visitorId or
               current.expectedSizeBytes ~= incoming.expectedSizeBytes or
               current.contentType ~= incoming.contentType then
                return 0
            end
            if not existingPathOwner then
                redis.call('SET', KEYS[2], ARGV[2], 'PX', ARGV[5])
            else
                local pathTtl = redis.call('PTTL', KEYS[2])
                if pathTtl == -1 or (pathTtl >= 0 and pathTtl < tonumber(ARGV[5])) then
                    redis.call('PEXPIRE', KEYS[2], ARGV[5])
                end
            end
            redis.call('SADD', KEYS[3], ARGV[2])
            redis.call('PEXPIRE', KEYS[3], ARGV[5])
            return 1
        end
        redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[5])
        redis.call('SET', KEYS[2], ARGV[2], 'PX', ARGV[5])
        redis.call('SADD', KEYS[3], ARGV[2])
        redis.call('PEXPIRE', KEYS[3], ARGV[5])
        return 1
        """;

    private const string CompareExchangeScript = """
        local current = redis.call('GET', KEYS[1])
        if not current or current ~= ARGV[1] then
            return 0
        end
        local pathOwner = redis.call('GET', KEYS[2])
        if pathOwner and pathOwner ~= ARGV[3] then
            return -1
        end
        redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[4])
        redis.call('SET', KEYS[2], ARGV[3], 'PX', ARGV[4])
        redis.call('SADD', KEYS[3], ARGV[3])
        redis.call('PEXPIRE', KEYS[3], ARGV[4])
        return 1
        """;

    private const string PromoteScript = """
        local members = redis.call('SMEMBERS', KEYS[1])
        local count = tonumber(ARGV[2])
        if #members == 0 or #members ~= count then
            return 0
        end
        local memberSet = {}
        for _, uploadId in ipairs(members) do
            memberSet[uploadId] = true
        end
        for index = 1, count do
            local offset = 2 + ((index - 1) * 4)
            local uploadId = ARGV[offset + 1]
            local expected = ARGV[offset + 2]
            local stateKeyIndex = 2 + ((index - 1) * 2)
            local pathKeyIndex = stateKeyIndex + 1
            local pathOwner = redis.call('GET', KEYS[pathKeyIndex])
            if not memberSet[uploadId] or
               redis.call('GET', KEYS[stateKeyIndex]) ~= expected or
               (pathOwner and pathOwner ~= uploadId) then
                return 0
            end
        end
        for index = 1, count do
            local offset = 2 + ((index - 1) * 4)
            local uploadId = ARGV[offset + 1]
            local stateKeyIndex = 2 + ((index - 1) * 2)
            local pathKeyIndex = stateKeyIndex + 1
            redis.call('SET', KEYS[stateKeyIndex], ARGV[offset + 3], 'PX', ARGV[1])
            redis.call('SET', KEYS[pathKeyIndex], uploadId, 'PX', ARGV[1])
            redis.call('SADD', KEYS[1], uploadId)
        end
        redis.call('PEXPIRE', KEYS[1], ARGV[1])
        return 1
        """;

    public async Task<UploadState> InitiateAsync(
        InitiateQuoteUploadRequest request,
        Guid? customerId,
        Guid? visitorId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = QuoteUploadStateRules.Create(request, customerId, visitorId);
        await CreateAsync(state, overwrite: false, cancellationToken);
        return QuoteUploadStateRules.ForPersistence(state);
    }

    public async Task<QuoteUploadHandoffResponse> ImportHandoffAsync(
        QuoteUploadHandoffRequest request,
        Guid? customerId,
        Guid? visitorId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (states, response) = QuoteUploadStateRules.CreateHandoff(request, customerId, visitorId);
        foreach (var state in states)
        {
            await CreateAsync(state, overwrite: true, cancellationToken);
        }

        return response;
    }

    public async Task<UploadState?> GetAsync(string uploadId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!QuoteUploadStateRules.IsSafeUploadId(uploadId))
        {
            return null;
        }

        var raw = await redis.GetDatabase().StringGetAsync(BuildStateKey(uploadId)).WaitAsync(cancellationToken);
        if (!TryDeserialize(raw, out var state) || !string.Equals(state.UploadId, uploadId, StringComparison.Ordinal))
        {
            return null;
        }

        return state;
    }

    public async Task<UploadState?> FindByStoragePathAsync(
        string storagePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!QuoteAnalysisArtifactPathValidator.IsCanonicalUploadPath(storagePath))
        {
            return null;
        }

        var uploadId = await redis.GetDatabase().StringGetAsync(BuildPathKey(storagePath)).WaitAsync(cancellationToken);
        var state = uploadId.HasValue
            ? await GetAsync(uploadId.ToString(), cancellationToken)
            : null;
        return state is not null && string.Equals(state.StoragePath, storagePath, StringComparison.Ordinal)
            ? state
            : null;
    }

    public async Task<bool> PromoteSessionAsync(
        Guid sessionId,
        Guid expectedVisitorId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty || expectedVisitorId == Guid.Empty || customerId == Guid.Empty)
        {
            return false;
        }

        var database = redis.GetDatabase();
        for (var attempt = 0; attempt < 16; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = await database.SetMembersAsync(BuildSessionKey(sessionId)).WaitAsync(cancellationToken);
            if (members.Length == 0)
            {
                return true;
            }

            var keys = new RedisKey[members.Length * 2 + 1];
            keys[0] = BuildSessionKey(sessionId);
            var arguments = new RedisValue[2 + members.Length * 4];
            arguments[0] = ToMilliseconds(_retention.Customer);
            arguments[1] = members.Length;
            var staleMembers = new List<RedisValue>();
            for (var index = 0; index < members.Length; index++)
            {
                var uploadId = members[index].ToString();
                var stateKeyIndex = 1 + index * 2;
                keys[stateKeyIndex] = BuildStateKey(uploadId);
                var raw = await database.StringGetAsync(keys[stateKeyIndex]).WaitAsync(cancellationToken);
                if (!raw.HasValue)
                {
                    staleMembers.Add(members[index]);
                    continue;
                }

                if (!TryDeserialize(raw, out var current) ||
                    !string.Equals(current.QuoteSessionId, sessionId.ToString("D"), StringComparison.Ordinal) ||
                    (current.CustomerId.HasValue
                        ? current.CustomerId != customerId
                        : current.VisitorId != expectedVisitorId))
                {
                    return false;
                }

                var promoted = QuoteUploadStateRules.ForPersistence(current with
                {
                    CustomerId = customerId,
                    IsTemporary = false
                });
                keys[stateKeyIndex + 1] = BuildPathKey(promoted.StoragePath);
                var offset = 2 + index * 4;
                arguments[offset] = uploadId;
                arguments[offset + 1] = raw;
                arguments[offset + 2] = Serialize(promoted);
                arguments[offset + 3] = promoted.StoragePath;
            }

            if (staleMembers.Count > 0)
            {
                await database.SetRemoveAsync(BuildSessionKey(sessionId), [.. staleMembers])
                    .WaitAsync(cancellationToken);
                continue;
            }

            var result = await database.ScriptEvaluateAsync(PromoteScript, keys, arguments)
                .WaitAsync(cancellationToken);
            if ((int)result == 1)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<UploadState> TrackAgentUploadAsync(
        string uploadId,
        Guid fileId,
        string fileName,
        string contentType,
        long fileSizeBytes,
        string storagePath,
        Guid sessionId,
        Guid? customerId,
        Guid? visitorId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = QuoteUploadStateRules.CreateTracked(
            uploadId,
            fileId,
            fileName,
            contentType,
            fileSizeBytes,
            storagePath,
            sessionId,
            customerId,
            visitorId);
        await CreateAsync(state, overwrite: true, cancellationToken);
        return QuoteUploadStateRules.ForPersistence(state);
    }

    public Task<UploadState> AttachDownstreamAsync(
        string uploadId,
        string downstreamUploadId,
        CancellationToken cancellationToken) =>
        UpdateRequiredAsync(
            uploadId,
            state => state with { DownstreamUploadId = QuoteUploadStateRules.RequireSafeUploadId(downstreamUploadId) },
            cancellationToken);

    public async Task<UploadState?> TryAdvanceAsync(
        string uploadId,
        long expectedReceivedBytes,
        long receivedBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!QuoteUploadStateRules.IsSafeUploadId(uploadId))
        {
            return null;
        }

        var database = redis.GetDatabase();
        var raw = await database.StringGetAsync(BuildStateKey(uploadId)).WaitAsync(cancellationToken);
        if (!TryDeserialize(raw, out var current) ||
            current.ReceivedBytes != expectedReceivedBytes ||
            receivedBytes <= expectedReceivedBytes ||
            receivedBytes > current.ExpectedSizeBytes)
        {
            return null;
        }

        var updated = QuoteUploadStateRules.ForPersistence(current with
        {
            ReceivedBytes = receivedBytes,
            Status = receivedBytes == current.ExpectedSizeBytes ? "Uploaded" : "Uploading"
        });
        var result = await database.ScriptEvaluateAsync(
            CompareExchangeScript,
            [
                BuildStateKey(uploadId),
                BuildPathKey(updated.StoragePath),
                BuildSessionKey(QuoteUploadStateRules.RequireSessionId(updated.QuoteSessionId))
            ],
            [raw, Serialize(updated), updated.UploadId, ToMilliseconds(RetentionFor(updated))])
            .WaitAsync(cancellationToken);
        if ((int)result != 1)
        {
            return null;
        }

        return updated;
    }

    public Task<UploadState> MarkProcessingAsync(
        string uploadId,
        Guid canonicalFileId,
        CancellationToken cancellationToken) =>
        UpdateRequiredAsync(
            uploadId,
            state => state with
            {
                FileId = canonicalFileId != Guid.Empty
                    ? canonicalFileId
                    : throw new ArgumentException(
                        "A canonical UploadService file identifier is required.",
                        nameof(canonicalFileId)),
                Status = "Processing"
            },
            cancellationToken);

    public Task<UploadState> MarkDemoAnalyzedAsync(
        string uploadId,
        DemoModeOptions options,
        CancellationToken cancellationToken) =>
        UpdateRequiredAsync(uploadId, QuoteUploadStateRules.MarkDemoAnalyzed, cancellationToken);

    public Task<UploadState> MarkAnalyzedAsync(
        string uploadId,
        CancellationToken cancellationToken) =>
        UpdateRequiredAsync(uploadId, QuoteUploadStateRules.MarkAnalyzed, cancellationToken);

    private async Task CreateAsync(
        UploadState state,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var persisted = QuoteUploadStateRules.ForPersistence(state);
        var sessionId = QuoteUploadStateRules.RequireSessionId(persisted.QuoteSessionId);
        var result = await redis.GetDatabase().ScriptEvaluateAsync(
            CreateScript,
            [BuildStateKey(persisted.UploadId), BuildPathKey(persisted.StoragePath), BuildSessionKey(sessionId)],
            [
                Serialize(persisted),
                persisted.UploadId,
                persisted.StoragePath,
                overwrite ? 1 : 0,
                ToMilliseconds(RetentionFor(persisted))
            ]).WaitAsync(cancellationToken);
        if ((int)result != 1)
        {
            throw new InvalidOperationException("The upload state conflicts with an existing durable upload.");
        }
    }

    private async Task<UploadState> UpdateRequiredAsync(
        string uploadId,
        Func<UploadState, UploadState> update,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await redis.GetDatabase().StringGetAsync(BuildStateKey(uploadId)).WaitAsync(cancellationToken);
            if (!TryDeserialize(raw, out var current))
            {
                throw new KeyNotFoundException($"Upload '{uploadId}' was not found.");
            }

            var updated = QuoteUploadStateRules.ForPersistence(update(current));
            var result = await redis.GetDatabase().ScriptEvaluateAsync(
                CompareExchangeScript,
                [
                    BuildStateKey(uploadId),
                    BuildPathKey(updated.StoragePath),
                    BuildSessionKey(QuoteUploadStateRules.RequireSessionId(updated.QuoteSessionId))
                ],
                [raw, Serialize(updated), updated.UploadId, ToMilliseconds(RetentionFor(updated))])
                .WaitAsync(cancellationToken);
            if ((int)result == 1)
            {
                return updated;
            }

            if ((int)result == -1)
            {
                throw new InvalidOperationException("The upload path is owned by another durable upload.");
            }
        }

        throw new InvalidOperationException("The upload state could not be updated after concurrent changes.");
    }

    private TimeSpan RetentionFor(UploadState state) =>
        state.CustomerId.HasValue ? _retention.Customer : _retention.Anonymous;

    private static string Serialize(UploadState state) => JsonSerializer.Serialize(state, JsonOptions);

    private static bool TryDeserialize(RedisValue raw, out UploadState state) =>
        TryDeserialize(raw.HasValue ? raw.ToString() : null, out state);

    private static bool TryDeserialize(string? raw, out UploadState state)
    {
        state = null!;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<UploadState>(raw, JsonOptions);
            if (parsed is null || !QuoteUploadStateRules.IsValidPersisted(parsed))
            {
                return false;
            }

            state = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private const string StateKeyPrefix = "quote:upload-state:";
    private const string PathKeyPrefix = "quote:upload-path:";

    private static RedisKey BuildStateKey(string uploadId) => $"{StateKeyPrefix}{uploadId}";

    private static RedisKey BuildSessionKey(Guid sessionId) => $"quote:upload-session:{sessionId:D}";

    private static RedisKey BuildPathKey(string storagePath) =>
        $"{PathKeyPrefix}{QuoteUploadStateRules.HashStoragePath(storagePath)}";

    private static long ToMilliseconds(TimeSpan retention) => checked((long)retention.TotalMilliseconds);
}

internal static class QuoteUploadStateRules
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "WaitingForUpload",
        "Uploading",
        "Uploaded",
        "Processing",
        "Analyzed",
        "DfmAnalysisReady"
    };

    internal static UploadState Create(
        InitiateQuoteUploadRequest request,
        Guid? customerId,
        Guid? visitorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sessionId = RequireSessionId(request.QuoteSessionId);
        RequireOwner(customerId, visitorId);
        if (request.FileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The upload size must be positive.");
        }

        var uploadId = Guid.NewGuid().ToString("N");
        var safeName = SanitizeFileName(request.FileName);
        var storagePath = customerId.HasValue
            ? $"customers/{customerId.Value:N}/quotes/{sessionId:N}/{uploadId}/{safeName}"
            : $"quotes/temp/{sessionId:N}/{uploadId}/{safeName}";
        return ForPersistence(new UploadState(
            uploadId,
            Guid.NewGuid(),
            safeName,
            NormalizeContentType(request.ContentType),
            request.FileSizeBytes,
            storagePath,
            customerId,
            IsTemporary: !customerId.HasValue,
            VisitorId: visitorId,
            QuoteSessionId: sessionId.ToString("D")));
    }

    internal static (IReadOnlyList<UploadState> States, QuoteUploadHandoffResponse Response) CreateHandoff(
        QuoteUploadHandoffRequest request,
        Guid? customerId,
        Guid? visitorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sessionId = RequireSessionId(request.QuoteSessionId);
        RequireOwner(customerId, visitorId);
        var states = new List<UploadState>();
        var parts = new List<QuoteUploadHandoffPartDto>();
        foreach (var file in request.Files.Where(file => !string.IsNullOrWhiteSpace(file.UploadId)))
        {
            if (!IsSafeUploadId(file.UploadId) ||
                !QuoteAnalysisArtifactPathValidator.IsCanonicalUploadPath(file.StoragePath) ||
                file.FileSizeBytes <= 0)
            {
                throw new ArgumentException("The upload handoff contains invalid durable upload metadata.", nameof(request));
            }

            var safeName = SanitizeFileName(file.FileName);
            var state = ForPersistence(new UploadState(
                file.UploadId,
                file.FileId is { } fileId && fileId != Guid.Empty ? fileId : Guid.NewGuid(),
                safeName,
                NormalizeContentType(file.ContentType),
                file.FileSizeBytes,
                file.StoragePath,
                customerId,
                IsTemporary: !customerId.HasValue,
                VisitorId: visitorId,
                QuoteSessionId: sessionId.ToString("D"))
            {
                ReceivedBytes = file.FileSizeBytes,
                Status = "Processing"
            });
            states.Add(state);
            parts.Add(new QuoteUploadHandoffPartDto(
                Guid.NewGuid(),
                state.FileId,
                state.UploadId,
                state.FileName,
                state.StoragePath,
                state.Status,
                0,
                0,
                [],
                null,
                state.StoragePath,
                NormalizeExtension(state.StoragePath),
                null,
                state.ContentType,
                state.ExpectedSizeBytes));
        }

        return (states, new QuoteUploadHandoffResponse(sessionId.ToString("D"), parts));
    }

    internal static UploadState CreateTracked(
        string uploadId,
        Guid fileId,
        string fileName,
        string contentType,
        long fileSizeBytes,
        string storagePath,
        Guid sessionId,
        Guid? customerId,
        Guid? visitorId)
    {
        RequireSafeUploadId(uploadId);
        if (fileId == Guid.Empty || sessionId == Guid.Empty || fileSizeBytes <= 0 ||
            !QuoteAnalysisArtifactPathValidator.IsCanonicalUploadPath(storagePath))
        {
            throw new ArgumentException("The tracked upload contains invalid durable metadata.");
        }

        RequireOwner(customerId, visitorId);
        return ForPersistence(new UploadState(
            uploadId,
            fileId,
            SanitizeFileName(fileName),
            NormalizeContentType(contentType),
            fileSizeBytes,
            storagePath,
            customerId,
            IsTemporary: !customerId.HasValue,
            VisitorId: visitorId,
            QuoteSessionId: sessionId.ToString("D"))
        {
            DownstreamUploadId = uploadId,
            ReceivedBytes = fileSizeBytes,
            Status = "Uploaded"
        });
    }

    internal static UploadState MarkDemoAnalyzed(UploadState current) => ForPersistence(current with
    {
        Status = "Analyzed",
        VolumeCc = 12.4m,
        SurfaceAreaCm2 = 78.2m,
        Findings = []
    });

    internal static UploadState MarkAnalyzed(UploadState current)
    {
        var volume = Math.Max(1.5m, Math.Round(current.ExpectedSizeBytes / 175_000m, 2));
        return ForPersistence(current with
        {
            Status = "Analyzed",
            VolumeCc = volume,
            SurfaceAreaCm2 = Math.Round(volume * 6.4m, 2),
            ViewerGlbUrl = "/models/sample.glb",
            ThumbnailUrl = "/images/generated/sample-part.svg",
            Findings =
            [
                new("Info", "WALL_MIN", "Minimum wall thickness appears acceptable for prototype quoting."),
                new("Warning", "THREAD_REVIEW", "Threaded features should be confirmed before production.")
            ]
        });
    }

    internal static UploadState ForPersistence(UploadState state)
    {
        var sanitized = state with
        {
            ViewerGlbUrl = IsRelativePublicPath(state.ViewerGlbUrl) ? state.ViewerGlbUrl : null,
            ThumbnailUrl = IsRelativePublicPath(state.ThumbnailUrl) ? state.ThumbnailUrl : null,
            Findings = state.Findings?.Take(100).ToArray() ?? []
        };
        if (!IsValidPersisted(sanitized))
        {
            throw new ArgumentException("The upload state is not safe for durable persistence.", nameof(state));
        }

        return sanitized;
    }

    internal static bool IsValidPersisted(UploadState state) =>
        IsSafeUploadId(state.UploadId) &&
        state.FileId != Guid.Empty &&
        string.Equals(state.FileName, SanitizeFileName(state.FileName), StringComparison.Ordinal) &&
        state.FileName.Length <= 260 &&
        !string.IsNullOrWhiteSpace(state.ContentType) &&
        state.ContentType.Length <= 100 &&
        !state.ContentType.Any(char.IsControl) &&
        state.ExpectedSizeBytes > 0 &&
        state.ReceivedBytes >= 0 &&
        state.ReceivedBytes <= state.ExpectedSizeBytes &&
        QuoteAnalysisArtifactPathValidator.IsCanonicalUploadPath(state.StoragePath) &&
        state.StoragePath.Length <= 500 &&
        (!state.CustomerId.HasValue || state.CustomerId != Guid.Empty) &&
        (state.CustomerId.HasValue || state.VisitorId is { } visitorId && visitorId != Guid.Empty) &&
        (!state.VisitorId.HasValue || state.VisitorId != Guid.Empty) &&
        Guid.TryParse(state.QuoteSessionId, out var sessionId) && sessionId != Guid.Empty &&
        state.IsTemporary == !state.CustomerId.HasValue &&
        AllowedStatuses.Contains(state.Status) &&
        (state.DownstreamUploadId is null || IsSafeUploadId(state.DownstreamUploadId)) &&
        state.VolumeCc >= 0 &&
        state.SurfaceAreaCm2 >= 0 &&
        IsRelativePublicPath(state.ViewerGlbUrl) &&
        IsRelativePublicPath(state.ThumbnailUrl) &&
        state.Findings is { Count: <= 100 };

    internal static bool HasSameImmutableIdentity(UploadState left, UploadState right) =>
        string.Equals(left.UploadId, right.UploadId, StringComparison.Ordinal) &&
        left.FileId == right.FileId &&
        string.Equals(left.StoragePath, right.StoragePath, StringComparison.Ordinal) &&
        string.Equals(left.QuoteSessionId, right.QuoteSessionId, StringComparison.Ordinal) &&
        left.CustomerId == right.CustomerId &&
        left.VisitorId == right.VisitorId &&
        left.ExpectedSizeBytes == right.ExpectedSizeBytes &&
        string.Equals(left.ContentType, right.ContentType, StringComparison.Ordinal);

    internal static bool IsSafeUploadId(string? uploadId) =>
        !string.IsNullOrWhiteSpace(uploadId) &&
        uploadId.Length <= 120 &&
        uploadId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    internal static string RequireSafeUploadId(string uploadId) =>
        IsSafeUploadId(uploadId)
            ? uploadId
            : throw new ArgumentException("The upload identifier is invalid.", nameof(uploadId));

    internal static Guid RequireSessionId(string? value)
    {
        if (!Guid.TryParse(value, out var sessionId) || sessionId == Guid.Empty)
        {
            throw new ArgumentException("A valid quote session identifier is required.", nameof(value));
        }

        return sessionId;
    }

    internal static string HashStoragePath(string storagePath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(storagePath)));

    private static void RequireOwner(Guid? customerId, Guid? visitorId)
    {
        if (customerId is { } customer && customer == Guid.Empty ||
            visitorId is { } visitor && visitor == Guid.Empty)
        {
            throw new ArgumentException("Upload ownership identifiers must not be empty.");
        }

        if (!customerId.HasValue && !visitorId.HasValue)
        {
            throw new ArgumentException("Anonymous uploads require a visitor owner.");
        }
    }

    private static string SanitizeFileName(string? fileName)
    {
        var leafName = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/')).Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(leafName
            .Select(character =>
                character == '/' || character == '\\' || char.IsControl(character) || invalid.Contains(character)
                    ? '_'
                    : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or ".."
            ? "upload.bin"
            : sanitized;
    }

    private static string NormalizeContentType(string? contentType)
    {
        var normalized = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType.Trim();
        if (normalized.Length > 100 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The upload content type is invalid.", nameof(contentType));
        }

        return normalized;
    }

    private static string? NormalizeExtension(string? storagePath)
    {
        var extension = Path.GetExtension(storagePath);
        return string.IsNullOrWhiteSpace(extension) ? null : extension.ToLowerInvariant();
    }

    private static bool IsRelativePublicPath(string? value) =>
        value is null ||
        value.StartsWith("/", StringComparison.Ordinal) &&
        !value.StartsWith("//", StringComparison.Ordinal) &&
        !value.Any(char.IsControl);
}
