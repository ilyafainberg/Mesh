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
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(files);

        // Verify content against the manifest BEFORE opening a transaction.
        foreach (var file in manifest.Files)
        {
            if (!files.TryGetValue(file.Path, out var bytes))
                throw new InvalidOperationException(
                    $"Package content is missing file '{file.Path}' declared in the manifest.");
            var (sha, count) = HashContent(bytes);
            if (!string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"File '{file.Path}' failed hash validation during install.");
            if (count != file.Size)
                throw new InvalidOperationException(
                    $"File '{file.Path}' size {count} does not match manifest size {file.Size}.");
        }

        using var tx = conn.BeginTransaction(deferred: false);

        // Replace any prior copy of this exact package identity, releasing its blob references first.
        ReleasePackageBlobsInTransaction(tx, skillId, manifest.PackageHash);
        DeletePackageRowsInTransaction(tx, skillId, manifest.PackageHash);

        WritePackageRow(tx, skillId, manifest);
        foreach (var file in manifest.Files)
        {
            UpsertBlobInTransaction(tx, file.Sha256, files[file.Path]);
            WritePackageFileRow(tx, skillId, manifest.PackageHash, file);
        }

        tx.Commit();
    }

    /// <summary>Transactionally remove one installed package and release its blob references.</summary>
    public void DeleteSkillPackage(string skillId, string packageHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageHash);
        using var tx = conn.BeginTransaction(deferred: false);
        ReleasePackageBlobsInTransaction(tx, skillId, packageHash);
        DeletePackageRowsInTransaction(tx, skillId, packageHash);
        tx.Commit();
    }

    /// <summary>Transactionally remove every installed package for a skill (used on skill delete).</summary>
    public void DeleteAllSkillPackages(string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        using var tx = conn.BeginTransaction(deferred: false);
        foreach (var hash in ListPackageHashesInTransaction(tx, skillId))
        {
            ReleasePackageBlobsInTransaction(tx, skillId, hash);
            DeletePackageRowsInTransaction(tx, skillId, hash);
        }
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

    // ---- row helpers -------------------------------------------------------

    private void WritePackageRow(SqliteTransaction tx, string skillId, SkillPackageManifest m)
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
        cmd.Parameters.AddWithValue("$compat",
            (object?)JsonSerializer.Serialize(m.Compatibility, SkillPackageJson) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$total", m.TotalSize);
        cmd.Parameters.AddWithValue("$count", (long)m.Files.Count);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private void WritePackageFileRow(
        SqliteTransaction tx, string skillId, string packageHash, SkillFileManifest f)
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

    private void UpsertBlobInTransaction(SqliteTransaction tx, string sha, byte[] bytes)
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
        cmd.Parameters.AddWithValue("$count", (long)bytes.LongLength);
        cmd.ExecuteNonQuery();
    }

    private void ReleasePackageBlobsInTransaction(SqliteTransaction tx, string skillId, string packageHash)
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

    private void DeletePackageRowsInTransaction(SqliteTransaction tx, string skillId, string packageHash)
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

    private IReadOnlyList<string> ListPackageHashesInTransaction(SqliteTransaction tx, string skillId)
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
