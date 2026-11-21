using System.Text.Json.Serialization;

namespace AgsCopy.Core.Models;

public enum CopyMode
{
    /// <summary>New and changed files are copied; nothing is ever deleted.</summary>
    Copy,
    /// <summary>Destination becomes identical to the source: extra files are deleted.</summary>
    Mirror,

    // No "move" mode: the source is only ever read.
}

public enum FileProperties
{
    /// <summary>/COPY:DAT</summary>
    DataAttributesTimestamps,
    /// <summary>/COPY:DATS</summary>
    WithSecurity,
    /// <summary>/COPY:DATSOU</summary>
    All,
}

public sealed class CopyJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public JobEndpoint Source { get; set; } = new();
    public JobEndpoint Destination { get; set; } = new();
    public CopyMode Mode { get; set; } = CopyMode.Copy;
    public CopyOptions Options { get; set; } = new();
    public Schedule Schedule { get; set; } = new();

    /// <summary>
    /// The user ticked "I understand files will be deleted". Required for Mirror.
    /// </summary>
    public bool DeletionConfirmed { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool DeletesFiles => Mode == CopyMode.Mirror;
}

public sealed class JobEndpoint
{
    public string Path { get; set; } = "";
    public StoredCredential? Credential { get; set; }

    /// <summary>For SFTP: the server's host key fingerprint ("SHA256:..."), recorded by the access check.</summary>
    public string? HostKey { get; set; }

    [JsonIgnore]
    public bool IsNetwork => PathUtil.IsUnc(Path);

    [JsonIgnore]
    public bool IsSftp => PathUtil.IsSftp(Path);

    /// <summary>Remote endpoints take an account: Windows shares and SFTP servers.</summary>
    [JsonIgnore]
    public bool IsRemote => IsNetwork || IsSftp;

    [JsonIgnore]
    public bool HasCredential => Credential is { UserName.Length: > 0 };
}

public sealed class StoredCredential
{
    /// <summary>DOMAIN\user, user@domain or a local account name of the remote machine.</summary>
    public string UserName { get; set; } = "";

    /// <summary>
    /// Plain password, only while travelling from the GUI to the service over the local pipe.
    /// The service encrypts it and never writes or returns it.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>DPAPI blob (base64) bound to the service account. Never leaves the service.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>Set by the service on jobs it sends to clients: a password is stored.</summary>
    public bool HasPassword { get; set; }

    /// <summary>
    /// SFTP private key (OpenSSH or PEM text), only in transit like <see cref="Password"/>; the password
    /// is then the key's passphrase, if it has one.
    /// </summary>
    public string? PrivateKey { get; set; }

    /// <summary>DPAPI blob of the private key. Never leaves the service.</summary>
    public string? ProtectedPrivateKey { get; set; }

    /// <summary>Set by the service on jobs it sends to clients: a private key is stored.</summary>
    public bool HasPrivateKey { get; set; }

    [JsonIgnore]
    public bool UsesKey => !string.IsNullOrEmpty(PrivateKey) || !string.IsNullOrEmpty(ProtectedPrivateKey) || HasPrivateKey;
}

public sealed class CopyOptions
{
    public const int MaxThreads = 128;
    public const int MaxProcesses = 16;

    /// <summary>
    /// Threads and processes are computed at every run from the free CPU and RAM
    /// (<see cref="Robocopy.AutoTune"/>); <see cref="Threads"/> and <see cref="Processes"/> are then ignored.
    /// </summary>
    public bool AutoTune { get; set; } = true;

    /// <summary>/MT:n — threads inside each robocopy process.</summary>
    public int Threads { get; set; } = 16;

    /// <summary>Robocopy processes run side by side, one per top-level folder.</summary>
    public int Processes { get; set; } = 1;

    /// <summary>/R:n</summary>
    public int Retries { get; set; } = 2;

    /// <summary>/W:n</summary>
    public int RetryWaitSeconds { get; set; } = 5;

    /// <summary>/J — unbuffered I/O, faster for very large files.</summary>
    public bool UnbufferedIO { get; set; }

    /// <summary>
    /// Local sources only: copy from a Volume Shadow Copy (VSS) snapshot taken when the run
    /// starts, so files that are open or locked are copied too, as they were at that moment.
    /// </summary>
    public bool UseShadowCopy { get; set; } = true;

    /// <summary>
    /// Before a run, destination files about to be overwritten or deleted are moved to
    /// Destination\_AGS-Copy-Versions\&lt;date&gt;\ (see <see cref="Versions"/>).
    /// </summary>
    public bool KeepVersions { get; set; }

    /// <summary>Version folders older than this are deleted after each run.</summary>
    public int VersionRetentionDays { get; set; } = 30;

    /// <summary>
    /// Compress the copies of compressible files (<see cref="Compression"/>): NTFS compression when the
    /// destination supports it, otherwise each such file is written as name.gz.
    /// </summary>
    public bool Compress { get; set; }

    /// <summary>/ZB — restartable mode, falling back to backup mode on access denied.</summary>
    public bool BackupMode { get; set; }

    public FileProperties FileProperties { get; set; } = FileProperties.DataAttributesTimestamps;

    /// <summary>/XO — never overwrite a newer destination file with an older source one.</summary>
    public bool ExcludeOlder { get; set; }

    /// <summary>/XJ — skip junctions and symbolic links (avoids loops).</summary>
    public bool SkipJunctions { get; set; } = true;

    /// <summary>/FFT — 2-second timestamp tolerance, for NAS and non-NTFS destinations.</summary>
    public bool FatFileTimes { get; set; }

    /// <summary>/XF patterns, such as *.tmp or ~$*.</summary>
    public List<string> ExcludeFiles { get; set; } = [];

    /// <summary>/XD names or full paths.</summary>
    public List<string> ExcludeDirectories { get; set; } = [];

    /// <summary>Advanced: extra robocopy switches appended as typed.</summary>
    public string? ExtraArguments { get; set; }
}
