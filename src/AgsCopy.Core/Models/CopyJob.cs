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
    /// <summary>The first (often the only) source.</summary>
    public JobEndpoint Source { get; set; } = new();

    /// <summary>
    /// Further sources. With more than one source each goes into its own folder of the destination
    /// (<see cref="JobEndpoint.Folder"/>), so they never mix or delete each other's files.
    /// </summary>
    public List<JobEndpoint> ExtraSources { get; set; } = [];

    /// <summary>The first (often the only) destination.</summary>
    public JobEndpoint Destination { get; set; } = new();

    /// <summary>Further destinations: each run copies every source into each of them, one after the other.</summary>
    public List<JobEndpoint> ExtraDestinations { get; set; } = [];

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

    [JsonIgnore]
    public IReadOnlyList<JobEndpoint> Sources => [Source, .. ExtraSources];

    [JsonIgnore]
    public bool MultiSource => ExtraSources.Count > 0;

    [JsonIgnore]
    public IReadOnlyList<JobEndpoint> Destinations => [Destination, .. ExtraDestinations];

    [JsonIgnore]
    public bool MultiDestination => ExtraDestinations.Count > 0;

    /// <summary>For lists: the first destination, and how many more there are.</summary>
    [JsonIgnore]
    public string DestinationSummary => MultiDestination ? $"{Destination.Path} (+{ExtraDestinations.Count})" : Destination.Path;

    /// <summary>For lists: the first source, and how many more there are.</summary>
    [JsonIgnore]
    public string SourceSummary => MultiSource ? $"{Source.Path} (+{ExtraSources.Count})" : Source.Path;

    /// <summary>Where source <paramref name="index"/> goes in a destination: the destination itself, or its own folder in it.</summary>
    public string DestinationFor(int index, int destination = 0) => MultiSource
        ? PathUtil.Combine(Destinations[destination].Path, Sources[index].Folder ?? "")
        : Destinations[destination].Path;

    /// <summary>
    /// The job reduced to one source and one destination, copied into <see cref="DestinationFor"/>:
    /// that is how a run, a preview and a restore handle each pair.
    /// </summary>
    public CopyJob ForSource(int index, int destination = 0)
    {
        var j = Json.Clone(this);
        j.Source = j.Sources[index];
        j.ExtraSources = [];
        j.Destination = j.Destinations[destination];
        j.ExtraDestinations = [];
        j.Destination.Path = DestinationFor(index, destination);
        return j;
    }

    /// <summary>Every (source, destination) pair of a run, destination by destination.</summary>
    [JsonIgnore]
    public IEnumerable<(int Source, int Destination)> Pairs =>
        Enumerable.Range(0, Destinations.Count).SelectMany(d => Enumerable.Range(0, Sources.Count).Select(i => (i, d)));

    /// <summary>"[→ E:\Backup · Data] " in notes and logs, when the job has more than one of either.</summary>
    public string PairLabel(int source, int destination)
    {
        var parts = new List<string>();
        if (MultiDestination) parts.Add("→ " + PathUtil.NormalizeDir(Destinations[destination].Path));
        if (MultiSource) parts.Add(Sources[source].Folder ?? "");
        return parts.Count == 0 ? "" : $"[{string.Join(" · ", parts)}] ";
    }

    /// <summary>Default folder for a source: its last folder name (the server for a share root, "root" for a drive).</summary>
    public static string DefaultFolder(string sourcePath)
    {
        if (DestinationLayout.FolderName(sourcePath) is { Length: > 0 } name) return name;
        if (PathUtil.Server(sourcePath) is { } server) return server + "-" + PathUtil.LastName(sourcePath);
        if (SftpLocation.TryParse(sourcePath) is { } s) return s.Host;
        var p = PathUtil.NormalizeDir(sourcePath);
        return p.Length >= 1 && char.IsAsciiLetter(p[0]) ? "Drive-" + char.ToUpperInvariant(p[0]) : "Source";
    }
}

public sealed class JobEndpoint
{
    public string Path { get; set; } = "";
    public StoredCredential? Credential { get; set; }

    /// <summary>
    /// Sources of a job with several sources: the folder of the destination this one is copied into.
    /// </summary>
    public string? Folder { get; set; }

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

    /// <summary>
    /// Mirror without versions: the extras are deleted before the copy instead of after it, so a full
    /// destination has room for the new files.
    /// </summary>
    public bool DeleteExtrasFirst { get; set; }

    /// <summary>/ZB — restartable mode, falling back to backup mode on access denied.</summary>
    public bool BackupMode { get; set; }

    public FileProperties FileProperties { get; set; } = FileProperties.DataAttributesTimestamps;

    /// <summary>/XO — never overwrite a newer destination file with an older source one.</summary>
    public bool ExcludeOlder { get; set; }

    /// <summary>/XJ — skip junctions and symbolic links (avoids loops).</summary>
    public bool SkipJunctions { get; set; } = true;

    /// <summary>
    /// With <see cref="SkipJunctions"/>: on a \\server\share source, directory junctions are followed
    /// all the same (the file server resolves them on its own disks), except those that lead back to a
    /// folder they are in. See <see cref="LinkedFolders"/>.
    /// </summary>
    public bool FollowShareJunctions { get; set; } = true;

    /// <summary>
    /// How many levels of folders below a share source are searched for junctions and DFS links;
    /// 0 = all of them. Links further down are not followed (robocopy skips them). The DFS links a
    /// namespace lists are followed at any depth: asking for them costs nothing.
    /// </summary>
    public int LinkSearchDepth { get; set; }

    public const int MaxLinkSearchDepth = 10;

    /// <summary>Whether a run looks for junctions to follow below a share source.</summary>
    public bool FollowsJunctionsOn(JobEndpoint source) => SkipJunctions && FollowShareJunctions && source.IsNetwork;

    /// <summary>/FFT — 2-second timestamp tolerance, for NAS and non-NTFS destinations.</summary>
    public bool FatFileTimes { get; set; }

    /// <summary>/XF patterns, such as *.tmp or ~$*.</summary>
    public List<string> ExcludeFiles { get; set; } = [];

    /// <summary>
    /// The temporary and lock files of Office (and LibreOffice) are left out: they are open, or gone,
    /// while the copy runs and only bring warnings. On by default, jobs saved before included.
    /// </summary>
    public bool ExcludeOfficeTemp { get; set; } = true;

    /// <summary>
    /// ~$name owner files of Word, Excel and PowerPoint, ~WRL0001.tmp and the like of Word, ppt*.tmp of
    /// PowerPoint, Access lock files, LibreOffice .~lock files. Excel's own temporary files have no
    /// extension (8 hex digits) and no pattern tells them from other files.
    /// </summary>
    public static readonly IReadOnlyList<string> OfficeTempFiles = ["~$*", "~*.tmp", "ppt*.tmp", "*.laccdb", "*.ldb", ".~lock.*#"];

    /// <summary>The file patterns left out: the job's own and, when on, the Office temporary files.</summary>
    [JsonIgnore]
    public IEnumerable<string> FilesExcluded => ExcludeOfficeTemp ? ExcludeFiles.Concat(OfficeTempFiles.Where(p => !ExcludeFiles.Contains(p, StringComparer.OrdinalIgnoreCase))) : ExcludeFiles;

    /// <summary>/XD names or full paths.</summary>
    public List<string> ExcludeDirectories { get; set; } = [];

    /// <summary>Advanced: extra robocopy switches appended as typed.</summary>
    public string? ExtraArguments { get; set; }
}
