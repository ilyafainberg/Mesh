using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mesh.App.Domain;

namespace Mesh.App.Services;

// Mesh 1.17 skill-package storage: normalized, SQLCipher-encrypted tables that hold the complete,
// validated folder structure of a desktop skill package. Mobile devices never write these rows (they
// keep only the Skill.md body as an asset). File bytes are content-addressed and reference-counted so
// identical files shared across package versions are stored once.
public sealed partial class MeshDb
{
    private static readonly JsonSerializerOptions SkillPackageJson = new(JsonSerializerDefaults.Web);

    // Called from CreateSchema() after Foundation 1.17.
    internal void CreateSkillPackagesSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS skill_package_blobs(
                sha256 TEXT PRIMARY KEY,
                bytes BLOB NOT NULL,
                byte_count INTEGER NOT NULL,
                refcount INTEGER NOT NULL DEFAULT 0);

            CREATE TABLE IF NOT EXISTS skill_packages(
                skill_id TEXT NOT NULL,
                package_hash TEXT NOT NULL,
                version TEXT,
                source TEXT,
                trust TEXT NOT NULL DEFAULT 'Untrusted',
                compatibility_json TEXT,
                total_bytes INTEGER NOT NULL DEFAULT 0,
                file_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                PRIMARY KEY(skill_id, package_hash));

            CREATE TABLE IF NOT EXISTS skill_package_files(
                skill_id TEXT NOT NULL,
                package_hash TEXT NOT NULL,
                path TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                role TEXT NOT NULL,
                executable INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(skill_id, package_hash, path));
            CREATE INDEX IF NOT EXISTS ix_skill_package_files_blob
                ON skill_package_files(sha256);
            """);
    }

    /// <summary>
    /// Transactionally install (or replace) the complete file set of a skill package for
    /// <paramref name="skillId"/>. Every file's bytes are re-hashed and verified against the manifest
    /// before any row is written; a mismatch or a missing file aborts with no mutation. Blobs are
    /// content-addressed and reference-counted, so re-installing shared content never duplicates bytes.
    /// </summary>
    public void InstallSkillPackage(
        string skillId, SkillPackageManifest manifest, IReadOnlyDictionary<string, byte[]> files)
    {
        using var tx = conn.BeginTransaction(deferred: false);
        SkillPackageRows.Install(conn, tx, skillId, manifest, files);
        tx.Commit();
    }

    /// <summary>Transactionally remove one installed package and release its blob references.</summary>
    public void DeleteSkillPackage(string skillId, string packageHash)
    {
        using var tx = conn.BeginTransaction(deferred: false);
        SkillPackageRows.Delete(conn, tx, skillId, packageHash);
        tx.Commit();
    }

    /// <summary>Transactionally remove every installed package for a skill (used on skill delete).</summary>
    public void DeleteAllSkillPackages(string skillId)
    {
        using var tx = conn.BeginTransaction(deferred: false);
        SkillPackageRows.DeleteAll(conn, tx, skillId);
        tx.Commit();
    }

    /// <summary>List the installed package hashes for a skill (newest first).</summary>
    public IReadOnlyList<string> ListSkillPackageHashes(string skillId)
    {
        var list = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT package_hash FROM skill_packages WHERE skill_id = $s ORDER BY created_at DESC;";
        cmd.Parameters.AddWithValue("$s", skillId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    internal long CountSkillPackageBlobsForTest()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM skill_package_blobs;";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Load a package manifest (no file bytes) or null if not installed.</summary>
    public SkillPackageManifest? GetSkillPackageManifest(string skillId, string packageHash)
    {
        SkillPackageManifest manifest;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT version, source, trust, compatibility_json
                FROM skill_packages WHERE skill_id = $s AND package_hash = $h;
                """;
            cmd.Parameters.AddWithValue("$s", skillId);
            cmd.Parameters.AddWithValue("$h", packageHash);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            manifest = new SkillPackageManifest
            {
                PackageHash = packageHash,
                Version = r.IsDBNull(0) ? null : r.GetString(0),
                Source = r.IsDBNull(1) ? null : r.GetString(1),
                Trust = ParseTrust(r.IsDBNull(2) ? null : r.GetString(2)),
                Compatibility = DecodeCompatibility(r.IsDBNull(3) ? null : r.GetString(3)),
                Files = new List<SkillFileManifest>()
            };
        }

        manifest.Files.AddRange(ReadPackageFiles(skillId, packageHash));
        return manifest.Files.Count == 0 ? null : manifest;
    }

    /// <summary>
    /// Load the full validated package content (manifest + file bytes) or null if not installed. Used
    /// only on desktop when materializing the immutable folder.
    /// </summary>
    public SkillPackageContent? LoadSkillPackageContent(string skillId, string packageHash)
    {
        var manifest = GetSkillPackageManifest(skillId, packageHash);
        if (manifest is null) return null;

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            var bytes = ReadBlob(file.Sha256);
            if (bytes is null)
                throw new InvalidOperationException(
                    $"Package '{packageHash}' references missing blob for '{file.Path}'.");
            files[file.Path] = bytes;
        }
        return new SkillPackageContent(manifest, files);
    }


    private List<SkillFileManifest> ReadPackageFiles(string skillId, string packageHash)
    {
        var files = new List<SkillFileManifest>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT path, sha256, size, role, executable
            FROM skill_package_files
            WHERE skill_id = $s AND package_hash = $h
            ORDER BY path;
            """;
        cmd.Parameters.AddWithValue("$s", skillId);
        cmd.Parameters.AddWithValue("$h", packageHash);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            files.Add(new SkillFileManifest
            {
                Path = r.GetString(0),
                Sha256 = r.GetString(1),
                Size = r.GetInt64(2),
                Role = Enum.TryParse<SkillFileRole>(r.GetString(3), out var role) ? role : SkillFileRole.Resource,
                Executable = !r.IsDBNull(4) && r.GetInt64(4) != 0
            });
        }
        return files;
    }

    private byte[]? ReadBlob(string sha)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT bytes FROM skill_package_blobs WHERE sha256 = $sha;";
        cmd.Parameters.AddWithValue("$sha", sha);
        using var r = cmd.ExecuteReader();
        return r.Read() && !r.IsDBNull(0) ? (byte[])r["bytes"] : null;
    }

    private static SkillPackageTrust ParseTrust(string? value)
        => Enum.TryParse<SkillPackageTrust>(value, out var t) ? t : SkillPackageTrust.Untrusted;

    private static SkillCompatibility DecodeCompatibility(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new SkillCompatibility();
        try
        {
            return JsonSerializer.Deserialize<SkillCompatibility>(json, SkillPackageJson) ?? new SkillCompatibility();
        }
        catch (JsonException)
        {
            return new SkillCompatibility();
        }
    }
}

/// <summary>
/// Transaction-composable skill-package row writer. The local installer and the replicated
/// package-transfer receiver share this single implementation, so an install can join the same
/// transaction that appends the signed replication events and their outbox references.
/// </summary>
public static class SkillPackageRows
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Installs (or replaces) the complete file set of a package inside the caller's transaction.
    /// Every file is re-hashed and size-checked against the manifest first; any mismatch throws
    /// before a single row is written, so the enclosing transaction rolls back intact.
    /// </summary>
    public static void Install(
        SqliteConnection conn,
        SqliteTransaction tx,
        string skillId,
        SkillPackageManifest manifest,
        IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(files);

        foreach (var file in manifest.Files)
        {
            if (!files.TryGetValue(file.Path, out var bytes))
                throw new InvalidOperationException(
                    $"Package content is missing file '{file.Path}' declared in the manifest.");
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"File '{file.Path}' failed hash validation during install.");
            if (bytes.LongLength != file.Size)
                throw new InvalidOperationException(
                    $"File '{file.Path}' size {bytes.LongLength} does not match manifest size {file.Size}.");
        }

        ReleaseBlobs(conn, tx, skillId, manifest.PackageHash);
        DeleteRows(conn, tx, skillId, manifest.PackageHash);

        WritePackageRow(conn, tx, skillId, manifest);
        foreach (var file in manifest.Files)
        {
            UpsertBlob(conn, tx, file.Sha256, files[file.Path]);
            WriteFileRow(conn, tx, skillId, manifest.PackageHash, file);
        }
    }

    /// <summary>Removes one installed package identity and releases its blob references.</summary>
    public static void Delete(
        SqliteConnection conn, SqliteTransaction tx, string skillId, string packageHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageHash);
        ReleaseBlobs(conn, tx, skillId, packageHash);
        DeleteRows(conn, tx, skillId, packageHash);
    }

    /// <summary>Removes every installed package for a skill, current and superseded.</summary>
    public static void DeleteAll(SqliteConnection conn, SqliteTransaction tx, string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        foreach (var hash in ListHashes(conn, tx, skillId))
        {
            ReleaseBlobs(conn, tx, skillId, hash);
            DeleteRows(conn, tx, skillId, hash);
        }
    }

    /// <summary>True when the skill-package tables exist on this connection.</summary>
    public static bool SchemaPresent(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'skill_packages';";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture) > 0;
    }

    public static IReadOnlyList<string> ListHashes(
        SqliteConnection conn, SqliteTransaction tx, string skillId)
    {
        var list = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT package_hash FROM skill_packages WHERE skill_id = $s;";
        cmd.Parameters.AddWithValue("$s", skillId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static void WritePackageRow(
        SqliteConnection conn, SqliteTransaction tx, string skillId, SkillPackageManifest m)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO skill_packages(
                skill_id, package_hash, version, source, trust,
                compatibility_json, total_bytes, file_count, created_at)
            VALUES($s, $h, $v, $src, $trust, $compat, $total, $count, $created);
            """;
        cmd.Parameters.AddWithValue("$s", skillId);
        cmd.Parameters.AddWithValue("$h", m.PackageHash);
        cmd.Parameters.AddWithValue("$v", (object?)m.Version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$src", (object?)m.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$trust", m.Trust.ToString());
        cmd.Parameters.AddWithValue("$compat", JsonSerializer.Serialize(m.Compatibility, Json));
        cmd.Parameters.AddWithValue("$total", m.TotalSize);
        cmd.Parameters.AddWithValue("$count", (long)m.Files.Count);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void WriteFileRow(
        SqliteConnection conn, SqliteTransaction tx, string skillId, string packageHash,
        SkillFileManifest f)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO skill_package_files(
                skill_id, package_hash, path, sha256, size, role, executable)
            VALUES($s, $h, $p, $sha, $size, $role, $exec);
            """;
        cmd.Parameters.AddWithValue("$s", skillId);
        cmd.Parameters.AddWithValue("$h", packageHash);
        cmd.Parameters.AddWithValue("$p", f.Path);
        cmd.Parameters.AddWithValue("$sha", f.Sha256);
        cmd.Parameters.AddWithValue("$size", f.Size);
        cmd.Parameters.AddWithValue("$role", f.Role.ToString());
        cmd.Parameters.AddWithValue("$exec", f.Executable ? 1L : 0L);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertBlob(
        SqliteConnection conn, SqliteTransaction tx, string sha, byte[] bytes)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO skill_package_blobs(sha256, bytes, byte_count, refcount)
            VALUES($sha, $bytes, $count, 1)
            ON CONFLICT(sha256) DO UPDATE SET refcount = refcount + 1;
            """;
        cmd.Parameters.AddWithValue("$sha", sha);
        cmd.Parameters.AddWithValue("$bytes", bytes);
        cmd.Parameters.AddWithValue("$count", bytes.LongLength);
        cmd.ExecuteNonQuery();
    }

    private static void ReleaseBlobs(
        SqliteConnection conn, SqliteTransaction tx, string skillId, string packageHash)
    {
        var hashes = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "SELECT sha256 FROM skill_package_files WHERE skill_id = $s AND package_hash = $h;";
            cmd.Parameters.AddWithValue("$s", skillId);
            cmd.Parameters.AddWithValue("$h", packageHash);
            using var r = cmd.ExecuteReader();
            while (r.Read()) hashes.Add(r.GetString(0));
        }

        foreach (var sha in hashes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE skill_package_blobs SET refcount = refcount - 1 WHERE sha256 = $sha;
                DELETE FROM skill_package_blobs WHERE sha256 = $sha AND refcount <= 0;
                """;
            cmd.Parameters.AddWithValue("$sha", sha);
            cmd.ExecuteNonQuery();
        }
    }

    private static void DeleteRows(
        SqliteConnection conn, SqliteTransaction tx, string skillId, string packageHash)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM skill_package_files WHERE skill_id = $s AND package_hash = $h;
            DELETE FROM skill_packages WHERE skill_id = $s AND package_hash = $h;
            """;
        cmd.Parameters.AddWithValue("$s", skillId);
        cmd.Parameters.AddWithValue("$h", packageHash);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Deterministic wire form of a complete skill package. The payload is canonical: files are
/// ordered by ordinal path so the same package always serialises to the same bytes, which makes
/// the transfer hash and its chunk boundaries reproducible on every device.
/// </summary>
public static class SkillPackageTransfer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Maximum raw bytes carried by a single package-transfer chunk.</summary>
    public const int MaxChunkBytes = 400 * 1024;

    private sealed record TransferFile(string Path, string Sha256, long Size, string Role, bool Executable, string BytesB64);

    private sealed record TransferPayload(
        Skill Skill,
        string PackageHash,
        string? Version,
        string? Source,
        string Trust,
        SkillCompatibility Compatibility,
        IReadOnlyList<TransferFile> Files);

    /// <summary>Serialises a validated package to its canonical transfer bytes.</summary>
    public static byte[] Serialize(Skill skill, SkillPackageContent content)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentException.ThrowIfNullOrWhiteSpace(skill.Id);
        ArgumentNullException.ThrowIfNull(content);
        var files = content.Manifest.Files
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => new TransferFile(
                f.Path, f.Sha256, f.Size, f.Role.ToString(), f.Executable,
                Convert.ToBase64String(content.Files[f.Path])))
            .ToList();
        var payload = new TransferPayload(
            skill, content.Manifest.PackageHash, content.Manifest.Version, content.Manifest.Source,
            content.Manifest.Trust.ToString(), content.Manifest.Compatibility, files);
        return JsonSerializer.SerializeToUtf8Bytes(payload, Json);
    }

    public static byte[] Serialize(string skillId, SkillPackageContent content)
        => Serialize(
            new Skill
            {
                Id = skillId,
                Name = skillId,
                Instructions = content.SkillMarkdownText,
                Compatibility = content.Manifest.Compatibility.Clone(),
                PackageHash = content.Manifest.PackageHash,
                PackageVersion = content.Manifest.Version
            },
            content);

    /// <summary>
    /// Parses transfer bytes back into a skill id plus a validated package. Throws
    /// <see cref="InvalidOperationException"/> when the payload is malformed, so a caller inside a
    /// replication transaction fails closed rather than installing a partial package.
    /// </summary>
    public static (Skill Skill, SkillPackageManifest Manifest, IReadOnlyDictionary<string, byte[]> Files)
        Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        TransferPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TransferPayload>(bytes, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Skill-package transfer payload was malformed: " + ex.Message);
        }
        if (payload is null
            || payload.Skill is null
            || string.IsNullOrWhiteSpace(payload.Skill.Id)
            || payload.Files is null)
            throw new InvalidOperationException("Skill-package transfer payload was incomplete.");

        var manifest = new SkillPackageManifest
        {
            PackageHash = payload.PackageHash,
            Version = payload.Version,
            Source = payload.Source,
            Trust = Enum.TryParse<SkillPackageTrust>(payload.Trust, out var trust)
                ? trust : SkillPackageTrust.Untrusted,
            Compatibility = payload.Compatibility ?? new SkillCompatibility(),
            Files = new List<SkillFileManifest>()
        };
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in payload.Files)
        {
            byte[] raw;
            try { raw = Convert.FromBase64String(file.BytesB64 ?? ""); }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"Skill-package file '{file.Path}' carried invalid base64: " + ex.Message);
            }
            manifest.Files.Add(new SkillFileManifest
            {
                Path = file.Path,
                Sha256 = file.Sha256,
                Size = file.Size,
                Role = Enum.TryParse<SkillFileRole>(file.Role, out var role) ? role : SkillFileRole.Resource,
                Executable = file.Executable
            });
            files[file.Path] = raw;
        }
        if (manifest.Files.Count == 0)
            throw new InvalidOperationException("Skill-package transfer payload declared no files.");
        payload.Skill.Compatibility = manifest.Compatibility.Clone();
        payload.Skill.PackageHash = manifest.PackageHash;
        payload.Skill.PackageVersion = manifest.Version;
        return (payload.Skill, manifest, files);
    }

    /// <summary>
    /// Splits transfer bytes into deterministic, bounded chunks. Chunk boundaries are a pure
    /// function of the payload length, so a retry produces byte-identical chunks.
    /// </summary>
    public static IReadOnlyList<byte[]> Chunk(byte[] payload, int maxChunkBytes = MaxChunkBytes)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (maxChunkBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxChunkBytes));
        if (payload.Length == 0) return [Array.Empty<byte>()];
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < payload.Length; offset += maxChunkBytes)
        {
            var take = Math.Min(maxChunkBytes, payload.Length - offset);
            chunks.Add(payload.AsSpan(offset, take).ToArray());
        }
        return chunks;
    }
}
