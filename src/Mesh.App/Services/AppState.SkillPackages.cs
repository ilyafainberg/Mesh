using Mesh.App.Domain;

namespace Mesh.App.Services;

/// <summary>
/// Mesh 1.17 skill-package install/load/delete APIs layered on the active identity DB and the lazy
/// asset-save surface. Search is deliberately NOT here: it is a stateless service
/// (<see cref="ISkillCatalogService"/>) the UI calls directly. This partial only owns the durable side
/// of a package - compatibility gating, DB package rows (desktop), the Skill.md asset body, and the
/// desktop immutable materialization cache. Every durable mutation is gated by the authoritative
/// <see cref="SkillCompatibilityChecker"/> first; an incompatible package is refused with a clear
/// reason and nothing is written.
///
/// Device rules (per product decision):
/// <list type="bullet">
///   <item><b>Desktop</b> stores the complete validated folder structure in encrypted package rows and
///   materializes the immutable folder only on demand.</item>
///   <item><b>Mobile</b> stores and uses ONLY the Skill.md body; it never writes package rows and never
///   materializes a folder.</item>
/// </list>
/// </summary>
public sealed partial class AppState
{
    private readonly SkillPackageCache skillPackageCache =
        new(Path.Combine(StoragePaths.Root, "Cache", "Skills"));

    /// <summary>
    /// CLI probe used by skill compatibility checks. Injectable for tests; defaults to a filesystem
    /// PATH scan on desktop and "nothing available" on mobile. Never spawns a process.
    /// </summary>
    public ICliToolProbe SkillCliProbe { get; set; } =
        PlatformCaps.IsMobile ? NoCliToolsProbe.Instance : new PathCliToolProbe();

    /// <summary>Build a compatibility checker for THIS device using the current CLI probe.</summary>
    private SkillCompatibilityChecker CompatibilityChecker()
        => SkillCompatibilityChecker.ForCurrentDevice(SkillCliProbe);

    /// <summary>
    /// Evaluate whether a package may be installed/run on this device. Pure: performs no mutation.
    /// </summary>
    public SkillCompatibilityResult CheckPackageCompatibility(SkillPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return CompatibilityChecker().CheckPackage(manifest);
    }

    /// <summary>Evaluate an installed skill's compatibility (Skill.md-only skills are universal).</summary>
    public SkillCompatibilityResult CheckSkillCompatibility(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return CompatibilityChecker().Check(skill.Compatibility);
    }

    /// <summary>
    /// Install a validated skill package. Runs the authoritative compatibility check BEFORE any durable
    /// mutation; if the package is incompatible with this device the install is refused and returned
    /// with a clear reason (nothing is written).
    ///
    /// On mobile only the Skill.md body is persisted (via <see cref="SaveAssetContent"/>): no package
    /// rows, no materialization. On desktop the complete package rows are written to the active DB first
    /// and then the Skill summary/body asset; if the asset save fails the package rows are rolled back so
    /// the two stores never diverge.
    /// </summary>
    public async Task<SkillPackageInstallResult> InstallSkillPackageAsync(
        SkillPackageContent content,
        string? name = null,
        string? description = null,
        string visibility = "private",
        string? sourceMarketplaceId = null,
        string? sourceSkillId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var manifest = content.Manifest;

        // Authoritative gate - before ANY durable mutation.
        var verdict = CompatibilityChecker().CheckPackage(manifest);
        if (!verdict.CanInstall)
        {
            return SkillPackageInstallResult.Rejected(
                verdict,
                verdict.Reasons.Count > 0
                    ? string.Join(" ", verdict.Reasons)
                    : "This skill is not compatible with this device.");
        }

        var skill = new Skill
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = string.IsNullOrWhiteSpace(name) ? DeriveName(content) : name.Trim(),
            Description = description?.Trim() ?? "",
            Instructions = content.SkillMarkdownText,
            Visibility = string.IsNullOrWhiteSpace(visibility) ? "private" : visibility,
            Enabled = verdict.CanRun,
            Compatibility = manifest.Compatibility.Clone(),
            PackageHash = manifest.PackageHash,
            PackageVersion = manifest.Version,
            SourceMarketplaceId = sourceMarketplaceId,
            SourceSkillId = sourceSkillId
        };

        if (PlatformCaps.IsMobile)
        {
            // Mobile: Skill.md instructions only, zero package rows, zero package bytes, zero
            // materialization and zero package-transfer events. This device permanently rejects the
            // package transfer surface, so the archive files are never routed anywhere from here.
            SaveAssetContent(AssetKind.Skill, skill.Id, profile => profile.Skills.Add(skill));
            return SkillPackageInstallResult.Installed(skill, verdict);
        }

        // Desktop: the package rows and EVERY package-transfer event row (with its outbox
        // references and sequence allocation) commit in ONE transaction, so an installed package
        // can never be observable without its durable pending transfer.
        var db = ActiveDbOrThrow();
        var payload = SkillPackageTransfer.Serialize(skill, content);
        var transferHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload))
            .ToLowerInvariant();
        var chunks = SkillPackageTransfer.Chunk(payload, (int)ReplicationDomainStore.MaxChunkBytes);
        var targets = TargetsForOwnerState();

        var envelopes = new List<ReplicationPayloadCodec.DomainEnvelope>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var body = System.Text.Json.JsonSerializer.Serialize(
                new ReplicationDomainStore.PackageChunkBody(
                    index, chunks.Count, payload.LongLength, transferHash,
                    Convert.ToBase64String(chunks[index]), skill.Name, "application/vnd.mesh.skill-package"),
                ReplicationJson);
            envelopes.Add(new ReplicationPayloadCodec.DomainEnvelope(
                Mesh.Shared.ReplicationOpKinds.Asset,
                ReplicationPayloadCodec.DomainAction.PackageTransfer,
                skill.Id, null, NewReplicationVersion(), body));
        }

        var (skillRecord, skillContent) = AssetPersistenceModels.ToRecord(
            skill,
            LocalDeviceId() ?? throw new ReplicationIdentityMissingException(
                "No local device identity exists for the skill package."),
            localOnly: false,
            version: 1);

        await ReplicateLocalBatchAsync(
            envelopes,
            targets,
            (conn, tx, index) =>
            {
                if (index == 0)
                {
                    SkillPackageRows.Install(conn, tx, skill.Id, manifest, content.Files);
                    Protocol9DomainTables.UpsertAsset(
                        conn,
                        tx,
                        AssetKind.Skill,
                        skill.Id,
                        skillRecord.Name,
                        skillRecord.MetadataJson,
                        skillRecord.ContentMime,
                        skillContent,
                        skillRecord.Version,
                        skillRecord.SourceDeviceId,
                        skillRecord.UpdatedAt,
                        localOnly: false);
                }
            },
            cancellationToken).ConfigureAwait(false);

        lock (profileSyncGate)
        {
            Profile.Skills.RemoveAll(existing =>
                string.Equals(existing.Id, skill.Id, StringComparison.Ordinal));
            Profile.Skills.Add(skill);
        }
        assetContentCache.Set(CacheKey(AssetKind.Skill, skill.Id), skillContent);
        NotifyChanged();

        return SkillPackageInstallResult.Installed(skill, verdict);
    }

    /// <summary>
    /// Delete an installed skill package end to end: the DB package rows (desktop), any materialized
    /// cache folders, and the Skill asset itself. Safe to call on mobile (only the asset is removed).
    /// </summary>
    public async Task DeleteSkillPackageAsync(string skillId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        if (!PlatformCaps.IsMobile)
        {
            var db = ActiveDb();
            if (db is not null)
                await db.ExecuteDurableWriteAsync(
                    () => db.DeleteAllSkillPackages(skillId), cancellationToken).ConfigureAwait(false);
            skillPackageCache.RemoveAll(skillId);
        }

        RemoveAsset(AssetKind.Skill, skillId,
            profile => profile.Skills.RemoveAll(s => string.Equals(s.Id, skillId, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Desktop only: lazily materialize the immutable, validated folder for a package-backed skill and
    /// return its absolute path. Throws <see cref="PlatformNotSupportedException"/> on mobile before any
    /// filesystem write. Throws if the skill is not package-backed or its rows are missing.
    /// </summary>
    public async Task<string> MaterializeSkillPackageAsync(string skillId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        if (PlatformCaps.IsMobile)
            throw new PlatformNotSupportedException(
                "Skill packages are never materialized on mobile devices; only the Skill.md body is stored.");

        var skill = Profile.Skills.FirstOrDefault(s => string.Equals(s.Id, skillId, StringComparison.Ordinal));
        var packageHash = skill?.PackageHash;
        if (string.IsNullOrWhiteSpace(packageHash))
            throw new InvalidOperationException($"Skill '{skillId}' is not backed by an installed package.");

        var db = ActiveDbOrThrow();
        return await Task.Run(() =>
        {
            var content = db.LoadSkillPackageContent(skillId, packageHash!)
                ?? throw new InvalidOperationException(
                    $"No installed package '{packageHash}' found for skill '{skillId}'.");
            return skillPackageCache.Materialize(skillId, content);
        }, cancellationToken).ConfigureAwait(false);
    }

    private MeshExportBundle BuildExportBundle()
    {
        var db = ActiveDbOrThrow();
        var profile = BuildFullProfileForExport(db);
        var packages = new List<MeshExportSkillPackage>();
        if (!PlatformCaps.IsMobile)
        {
            foreach (var skill in profile.Skills)
            {
                if (string.IsNullOrWhiteSpace(skill.PackageHash)) continue;
                var content = db.LoadSkillPackageContent(skill.Id, skill.PackageHash);
                if (content is null)
                    throw new InvalidOperationException(
                        $"Skill '{skill.Name}' references package '{skill.PackageHash}' but its package rows are missing.");
                packages.Add(new MeshExportSkillPackage
                {
                    SkillId = skill.Id,
                    Manifest = content.Manifest,
                    Files = content.Files.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.ToArray(),
                        StringComparer.Ordinal)
                });
            }
        }
        return new MeshExportBundle { Profile = profile, SkillPackages = packages };
    }

    private void ImportSkillPackages(
        IReadOnlyList<MeshExportSkillPackage> packages,
        string importedAccountId)
    {
        if (PlatformCaps.IsMobile || packages.Count == 0) return;
        var db = ActiveDbOrThrow();
        try
        {
            foreach (var package in packages)
            {
                if (!Profile.Skills.Any(skill =>
                        string.Equals(skill.Id, package.SkillId, StringComparison.Ordinal)
                        && string.Equals(
                            skill.PackageHash,
                            package.Manifest.PackageHash,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"Backup package '{package.Manifest.PackageHash}' has no matching skill.");
                }
                db.InstallSkillPackage(package.SkillId, package.Manifest, package.Files);
            }
        }
        catch
        {
            DeleteAccount(importedAccountId);
            throw;
        }
    }

    // ---- internals ---------------------------------------------------------

    private MeshDb? ActiveDb()
    {
        lock (profileSyncGate)
            return activeDb;
    }

    private MeshDb ActiveDbOrThrow()
        => ActiveDb() ?? throw new InvalidOperationException("No active identity database is open.");

    private static string DeriveName(SkillPackageContent content)
    {
        // Prefer the Skill.md's first markdown heading; fall back to a generic label.
        foreach (var raw in content.SkillMarkdownText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                var heading = line.TrimStart('#').Trim();
                if (heading.Length > 0) return heading.Length > 80 ? heading[..80] : heading;
            }
        }
        return "Imported skill";
    }
}

/// <summary>Outcome of <see cref="AppState.InstallSkillPackageAsync"/>.</summary>
public sealed class SkillPackageInstallResult
{
    /// <summary>True when the package was installed on this device.</summary>
    public bool Success { get; init; }

    /// <summary>The installed skill (null when refused).</summary>
    public Skill? Skill { get; init; }

    /// <summary>The authoritative compatibility verdict that gated the install.</summary>
    public SkillCompatibilityResult Compatibility { get; init; } = SkillCompatibilityResult.Ok();

    /// <summary>A clear, user-facing reason when the install was refused.</summary>
    public string? Error { get; init; }

    internal static SkillPackageInstallResult Installed(Skill skill, SkillCompatibilityResult verdict)
        => new() { Success = true, Skill = skill, Compatibility = verdict };

    internal static SkillPackageInstallResult Rejected(SkillCompatibilityResult verdict, string error)
        => new() { Success = false, Compatibility = verdict, Error = error };
}
