using System.Management;

namespace NtfsSnapshotMgr.Services;

/// <summary>Local NTFS volume information.</summary>
public record VolumeInfo(
    string DriveLetter,
    string Label,
    ulong TotalSize,
    ulong FreeSpace
);

/// <summary>Snapshot (shadow copy) information.</summary>
public record SnapshotInfo(
    string ID,
    string VolumeName,
    DateTime InstallDate,
    string DeviceObject
);

/// <summary>Shadow storage diff area information for a volume.</summary>
public record StorageInfo(
    long UsedSpace,
    long AllocatedSpace,
    long MaxSpace   // -1 = unbounded
);

/// <summary>Wraps WMI calls to manage Volume Shadow Copies (NTFS snapshots).</summary>
public class VssService
{
    private const string WmiScope = @"\\.\Root\CIMV2";

    // ── Volumes ────────────────────────────────────────────────

    /// <summary>Enumerate all local fixed NTFS volumes.</summary>
    public List<VolumeInfo> GetLocalNtfsVolumes()
    {
        var volumes = new List<VolumeInfo>();

        using var searcher = new ManagementObjectSearcher(
            WmiScope,
            "SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk " +
            "WHERE DriveType = 3 AND FileSystem = 'NTFS'");

        foreach (var obj in searcher.Get())
        {
            volumes.Add(new VolumeInfo(
                DriveLetter: obj["DeviceID"]?.ToString() ?? "",
                Label:       obj["VolumeName"]?.ToString() ?? "",
                TotalSize:   obj["Size"] as ulong? ?? 0,
                FreeSpace:   obj["FreeSpace"] as ulong? ?? 0
            ));
        }

        return volumes;
    }

    // ── Snapshots ──────────────────────────────────────────────

    /// <summary>List all snapshots, optionally filtered by volume (e.g. "C:").</summary>
    public List<SnapshotInfo> GetSnapshots(string? volume = null)
    {
        var snapshots = new List<SnapshotInfo>();

        // VolumeName is a GUID path (e.g. \\?\Volume{...}\) — can't WQL-filter by drive letter.
        // Query all and filter in code.
        string query = "SELECT ID, VolumeName, InstallDate, DeviceObject FROM Win32_ShadowCopy";

        using var searcher = new ManagementObjectSearcher(WmiScope, query);

        // If a volume filter was provided, resolve drive letter → GUID for matching
        string? volumeGuid = null;
        if (!string.IsNullOrEmpty(volume))
            volumeGuid = GetVolumeGuidForDrive(volume.TrimEnd('\\', ':'));

        foreach (var obj in searcher.Get())
        {
            DateTime installDate = DateTime.MinValue;
            if (obj["InstallDate"] is string dmtf && !string.IsNullOrEmpty(dmtf))
                installDate = ManagementDateTimeConverter.ToDateTime(dmtf);

            var snapVolumeName = obj["VolumeName"]?.ToString() ?? "";

            // Filter: match by volume GUID or by direct drive-letter comparison (fallback)
            if (volumeGuid != null)
            {
                if (!snapVolumeName.Equals(volumeGuid, StringComparison.OrdinalIgnoreCase) &&
                    !snapVolumeName.StartsWith(volume + "\\", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            snapshots.Add(new SnapshotInfo(
                ID:           obj["ID"]?.ToString() ?? "",
                VolumeName:   snapVolumeName,
                InstallDate:  installDate,
                DeviceObject: obj["DeviceObject"]?.ToString() ?? ""
            ));
        }

        return snapshots;
    }

    /// <summary>
    /// Resolve a drive letter (e.g. "C") to its volume GUID path
    /// (e.g. "\\?\Volume{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}\").
    /// </summary>
    private static string? GetVolumeGuidForDrive(string driveLetter)
    {
        using var searcher = new ManagementObjectSearcher(
            WmiScope,
            $"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter = '{driveLetter}:'");

        foreach (var obj in searcher.Get())
            return obj["DeviceID"]?.ToString();

        return null;
    }

    /// <summary>Create a client-accessible snapshot on the given volume (e.g. "C:\\").</summary>
    public void CreateSnapshot(string volume)
    {
        ValidateDriveLetter(volume);

        using var cls = new ManagementClass(WmiScope, "Win32_ShadowCopy", null);
        var inParams = cls.GetMethodParameters("Create");
        inParams["Volume"]  = volume;
        inParams["Context"] = "ClientAccessible";

        var result = cls.InvokeMethod("Create", inParams, null);

        if (result is null)
            throw new InvalidOperationException("WMI Create returned null.");

        uint returnValue = (uint)result["ReturnValue"];
        if (returnValue != 0)
            throw new InvalidOperationException(
                $"Failed to create snapshot on {volume}. WMI return code: {returnValue}");
    }

    /// <summary>Delete the snapshot identified by its WMI ID.</summary>
    public void DeleteSnapshot(string snapshotId)
    {
        // Prevent WQL injection — snapshot IDs are GUIDs, reject anything else
        if (string.IsNullOrWhiteSpace(snapshotId) ||
            snapshotId.IndexOfAny(['\'', '"', ';', '\\']) >= 0)
            throw new ArgumentException($"Invalid snapshot ID: {snapshotId}");

        using var searcher = new ManagementObjectSearcher(
            WmiScope,
            $"SELECT * FROM Win32_ShadowCopy WHERE ID = '{snapshotId}'");

        foreach (var obj in searcher.Get())
        {
            using var mo = (ManagementObject)obj;
            mo.Delete();
            return;
        }

        throw new InvalidOperationException($"Snapshot not found: {snapshotId}");
    }

    // ── Storage ────────────────────────────────────────────────

    /// <summary>Get shadow storage info for a volume (e.g. "C:\\").</summary>
    public StorageInfo? GetSnapshotStorage(string volume)
    {
        var driveLetter = ValidateDriveLetter(volume);
        var volumeGuid  = GetVolumeGuidForDrive(driveLetter);

        using var searcher = new ManagementObjectSearcher(
            WmiScope,
            "SELECT UsedSpace, AllocatedSpace, MaxSpace, Volume FROM Win32_ShadowStorage");

        foreach (var obj in searcher.Get())
        {
            if (!VolumeRefMatches(obj["Volume"], driveLetter, volumeGuid))
                continue;

            return new StorageInfo(
                UsedSpace:      ConvertToLong(obj["UsedSpace"]),
                AllocatedSpace: ConvertToLong(obj["AllocatedSpace"]),
                MaxSpace:       ConvertToLong(obj["MaxSpace"])
            );
        }

        return null;
    }

    /// <summary>Set maximum shadow storage size (bytes). Pass -1 for unbounded.</summary>
    public void SetMaxStorage(string volume, long maxBytes)
    {
        // Validate: only -1 (unlimited) or >= ~320 MB
        const long minAllowed = 320L * 1024 * 1024;
        if (maxBytes < -1 || (maxBytes >= 0 && maxBytes < minAllowed))
            throw new ArgumentOutOfRangeException(nameof(maxBytes),
                $"MaxSpace must be -1 (unlimited) or at least {minAllowed / (1024*1024)} MB.");

        var driveLetter = ValidateDriveLetter(volume);
        var volumeGuid  = GetVolumeGuidForDrive(driveLetter);

        using var searcher = new ManagementObjectSearcher(
            WmiScope,
            "SELECT * FROM Win32_ShadowStorage");

        foreach (var obj in searcher.Get())
        {
            if (!VolumeRefMatches(obj["Volume"], driveLetter, volumeGuid))
                continue;

            using var mo = (ManagementObject)obj;
            // MaxSpace is uint64; -1 ("unlimited") → ulong.MaxValue
            mo["MaxSpace"] = maxBytes == -1 ? ulong.MaxValue : (ulong)maxBytes;
            mo.Put();
            return;
        }

        throw new InvalidOperationException(
            $"No shadow storage entry found for volume {volume}. " +
            "The volume may not have shadow copies enabled.");
    }

    /// <summary>
    /// Check whether a Win32_ShadowStorage.Volume REF (pointing to Win32_Volume)
    /// matches the given drive letter.
    /// The REF ToString() looks like:
    ///   Win32_Volume.DeviceID="\\\\?\\Volume{guid}\\"
    /// </summary>
    private static bool VolumeRefMatches(object? volumeRef, string driveLetter, string? volumeGuid)
    {
        if (volumeRef is null) return false;

        // Strategy 1: if the WMI provider returns the referenced object inline,
        // read its DriveLetter property directly.
        if (volumeRef is ManagementBaseObject mbo)
        {
            try
            {
                var dl = mbo.GetPropertyValue("DriveLetter")?.ToString();
                if (dl != null && dl.Equals(driveLetter + ":", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* fall through */ }
        }

        // Strategy 2: match the volume GUID in the REF path string.
        // WMI REF strings double-escape backslashes:
        //   volumeGuid  = "\\?\Volume{guid}\"
        //   REF string  = "Win32_Volume.DeviceID=\"\\\\?\\Volume{guid}\\""
        // So we need to double the backslashes in volumeGuid before matching.
        var s = volumeRef.ToString() ?? "";
        if (volumeGuid != null)
        {
            var escapedGuid = volumeGuid.Replace("\\", "\\\\");
            if (s.Contains(escapedGuid, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Strategy 3: loose fallback — drive letter anywhere in the string.
        if (s.Contains(driveLetter + ":", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    // ── Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Validate and normalize a drive letter input (e.g. "C:", "D:\\" → "C").
    /// </summary>
    private static string ValidateDriveLetter(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Drive letter must not be empty.");

        var letter = input.Trim().TrimEnd('\\', ':').ToUpperInvariant();
        if (letter.Length != 1 || letter[0] < 'A' || letter[0] > 'Z')
            throw new ArgumentException($"Invalid drive letter: '{input}'");

        return letter;
    }

    private static long ConvertToLong(object? value)
    {
        return value switch
        {
            null         => 0,
            long l       => l,
            int i        => i,
            uint ui      => ui,
            ulong ul     => (long)ul,
            string s => long.TryParse(s, out var p) ? p : 0,
            _            => 0,
        };
    }
}
