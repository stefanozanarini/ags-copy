using System.Collections.ObjectModel;
using System.Globalization;
using AgsCopy.Core.Models;
using AgsCopy.Core.Robocopy;

namespace AgsCopy.App;

/// <summary>A row of the folder preview shown after a successful access check.</summary>
public sealed record ListingItem(string Glyph, string Name, string Detail);

/// <summary>One side of the copy: path and, for network paths, the account to use.</summary>
public sealed class EndpointViewModel : ObservableObject
{
    private string? _mappedUnc;
    private string _listingMore = "";
    private string _path;
    private string _userName;
    private string _testResult = "";
    private bool? _testOk;
    private string? _hostKey;
    private string? _privateKey;
    private string _keyName = "";
    private bool _hasStoredKey;
    private readonly SftpLocation? _originalServer;
    private string? _folder;

    public EndpointViewModel(JobEndpoint e)
    {
        _path = e.Path;
        // A folder equal to the default keeps following the path; a typed one stays.
        if (!string.IsNullOrEmpty(e.Folder) && !e.Folder.Equals(DefaultFolder(e.Path), StringComparison.OrdinalIgnoreCase)) _folder = e.Folder;
        _userName = e.Credential?.UserName ?? "";
        HasStoredPassword = e.Credential?.HasPassword == true;
        _hasStoredKey = e.Credential?.HasPrivateKey == true;
        _hostKey = e.HostKey;
        _originalServer = SftpLocation.TryParse(e.Path);
    }

    public string Path
    {
        get => _path;
        set
        {
            if (!Set(ref _path, value)) return;
            OnPropertyChanged(nameof(IsNetwork));
            OnPropertyChanged(nameof(IsSftp));
            OnPropertyChanged(nameof(IsRemote));
            OnPropertyChanged(nameof(IsLocal));
            OnPropertyChanged(nameof(Folder));
            OnPropertyChanged(nameof(Title));
            MappedUnc = NetworkDrives.ToUnc(value);
            // A recorded host key belongs to one server: another host or port must be checked again.
            var now = SftpLocation.TryParse(value);
            if (_hostKey != null && (now == null || _originalServer == null || now.Host != _originalServer.Host || now.Port != _originalServer.Port))
                HostKey = null;
            TestResult = "";
        }
    }

    /// <summary>
    /// Sources of a job with several sources: the folder of the destination this one goes into.
    /// Until typed, it follows the source's own folder name.
    /// </summary>
    public string Folder
    {
        get => _folder ?? DefaultFolder(_path);
        set
        {
            var v = value.Trim();
            _folder = v.Length == 0 || v.Equals(DefaultFolder(_path), StringComparison.OrdinalIgnoreCase) ? null : v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
        }
    }

    private static string DefaultFolder(string path) => PathUtil.IsAbsolute(path) ? CopyJob.DefaultFolder(path) : "";

    /// <summary>A copy to edit in a dialog, secrets typed in this session included; the original stays as it was until OK.</summary>
    public EndpointViewModel Copy()
    {
        var c = new EndpointViewModel(ToEndpoint()) { Password = Password, _keyName = _keyName, _hasStoredKey = _hasStoredKey };
        c._privateKey = _privateKey;
        c._folder = _folder;
        return c;
    }

    /// <summary>The account column of the lists.</summary>
    public string AccountText => !IsRemote ? Loc.T("endpoint.account.local")
        : _userName.Trim().Length == 0 ? Loc.T("endpoint.account.service")
        : _userName.Trim();

    /// <summary>The row of the source list.</summary>
    public string Title => _path.Trim().Length == 0 ? Loc.T("sources.new") : PathUtil.NormalizeDir(_path);

    public bool IsLocal => !IsRemote;
    public bool IsSftp => PathUtil.IsSftp(_path);
    public bool IsRemote => IsNetwork || IsSftp;

    /// <summary>SFTP: fingerprint of the server's key, recorded by the access check and required by runs.</summary>
    public string? HostKey
    {
        get => _hostKey;
        set
        {
            if (!Set(ref _hostKey, value)) return;
            OnPropertyChanged(nameof(HostKeyText));
        }
    }

    public string HostKeyText => _hostKey == null ? Loc.T("endpoint.hostkey.none") : Loc.F("endpoint.hostkey", _hostKey);

    /// <summary>SFTP private key loaded from a file in this session; sent once to the service, which encrypts it.</summary>
    public void SetPrivateKey(string text, string fileName)
    {
        _privateKey = text;
        KeyName = fileName;
        TestResult = "";
    }

    public void ClearPrivateKey()
    {
        _privateKey = null;
        _hasStoredKey = false;
        KeyName = "";
        TestResult = "";
    }

    public bool UsesKey => _privateKey != null || _hasStoredKey;

    public string KeyName
    {
        get => _keyName;
        private set
        {
            Set(ref _keyName, value);
            OnPropertyChanged(nameof(UsesKey));
            OnPropertyChanged(nameof(KeyText));
        }
    }

    public string KeyText => _privateKey != null ? Loc.F("endpoint.key.loaded", _keyName)
        : _hasStoredKey ? Loc.T("endpoint.key.stored")
        : Loc.T("endpoint.key.none");

    /// <summary>The \\server\share path behind a mapped drive letter, offered in its place.</summary>
    public string? MappedUnc
    {
        get => _mappedUnc;
        private set
        {
            if (!Set(ref _mappedUnc, value)) return;
            OnPropertyChanged(nameof(HasMappedUnc));
            OnPropertyChanged(nameof(MappedText));
        }
    }

    public bool HasMappedUnc => _mappedUnc != null;
    public string MappedText => _mappedUnc == null ? "" : Loc.F("endpoint.mapped", _mappedUnc);

    /// <summary>First items of the folder, read by the service with the given account.</summary>
    public ObservableCollection<ListingItem> Listing { get; } = [];

    public string ListingMore
    {
        get => _listingMore;
        set => Set(ref _listingMore, value);
    }

    public bool HasListing => Listing.Count > 0;

    public void ShowListing(IEnumerable<ListingItem> items, int more)
    {
        Listing.Clear();
        foreach (var i in items) Listing.Add(i);
        ListingMore = more > 0 ? Loc.F("layout.more", Fmt.Count(more)) : "";
        OnPropertyChanged(nameof(HasListing));
    }

    public bool IsNetwork => PathUtil.IsUnc(_path);

    public string UserName
    {
        get => _userName;
        set
        {
            if (Set(ref _userName, value)) TestResult = "";
        }
    }

    /// <summary>Set from the PasswordBox (which cannot be bound).</summary>
    public string Password { get; set; } = "";

    public bool HasStoredPassword { get; }

    public string TestResult
    {
        get => _testResult;
        set
        {
            if (!Set(ref _testResult, value) || value.Length > 0) return;
            // Any change to path or account makes the previous check and its listing stale.
            TestOk = null;
            ShowListing([], 0);
        }
    }

    public bool? TestOk
    {
        get => _testOk;
        set => Set(ref _testOk, value);
    }

    public JobEndpoint ToEndpoint()
    {
        var e = new JobEndpoint { Path = PathUtil.NormalizeDir(_path), HostKey = IsSftp ? _hostKey : null, Folder = Folder };
        if (IsRemote && !string.IsNullOrWhiteSpace(_userName))
        {
            e.Credential = new StoredCredential
            {
                UserName = _userName.Trim(),
                Password = Password.Length > 0 ? Password : null,
                HasPassword = Password.Length == 0 && HasStoredPassword,
            };
            if (IsSftp)
            {
                e.Credential.PrivateKey = _privateKey;
                e.Credential.HasPrivateKey = _privateKey == null && _hasStoredKey;
            }
        }
        return e;
    }
}

public sealed class DayOption(DayOfWeek day, bool selected) : ObservableObject
{
    private bool _selected = selected;

    public DayOfWeek Day { get; } = day;
    public string Name => Loc.Culture.DateTimeFormat.GetAbbreviatedDayName(Day);

    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}

public sealed class JobEditorViewModel : ObservableObject
{
    /// <summary>Pages of a job: sources and destinations, mode and options, schedule, summary.</summary>
    public const int EndpointsStep = 0;
    public const int OptionsStep = 1;
    public const int ScheduleStep = 2;
    public const int SummaryStep = 3;

    private readonly CopyJob _original;
    private int _step;
    private string _name;
    private bool _enabled;
    private CopyMode _mode;
    private int _threads;
    private int _processes;
    private bool _autoTune;
    private int _fileProperties;
    private int _scheduleKind;
    private DateTime? _startDate;
    private string _startTime;
    private bool _deletionConfirmed;
    private bool _previewRunning;
    private PreviewResult? _preview;
    private string _errors = "";
    private bool _keepVersions;
    private int _versionRetentionDays;

    public JobEditorViewModel(CopyJob job, bool isNew)
    {
        _original = job;
        IsNew = isNew;
        _name = job.Name;
        _enabled = job.Enabled;
        // A new job starts with empty lists: its blank endpoints are not shown.
        Sources = new ObservableCollection<EndpointViewModel>(job.Sources.Where(e => e.Path.Trim().Length > 0).Select(e => new EndpointViewModel(e)));
        Destinations = new ObservableCollection<EndpointViewModel>(job.Destinations.Where(e => e.Path.Trim().Length > 0).Select(e => new EndpointViewModel(e)));
        Sources.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsMultiSource));
        Destinations.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsMultiDestination));
        _deleteExtrasFirst = job.Options.DeleteExtrasFirst;
        _mode = job.Mode;

        var o = job.Options;
        _threads = o.Threads;
        _processes = o.Processes;
        _autoTune = o.AutoTune;
        Retries = o.Retries;
        RetryWaitSeconds = o.RetryWaitSeconds;
        UnbufferedIO = o.UnbufferedIO;
        BackupMode = o.BackupMode;
        UseShadowCopy = o.UseShadowCopy;
        KeepVersions = o.KeepVersions;
        Compress = o.Compress;
        VersionRetentionDays = o.VersionRetentionDays;
        ExcludeOlder = o.ExcludeOlder;
        SkipJunctions = o.SkipJunctions;
        FollowShareJunctions = o.FollowShareJunctions;
        LinkSearchDepth = Math.Clamp(o.LinkSearchDepth, 0, CopyOptions.MaxLinkSearchDepth);
        FatFileTimes = o.FatFileTimes;
        _fileProperties = (int)o.FileProperties;
        ExcludeFiles = string.Join(Environment.NewLine, o.ExcludeFiles);
        ExcludeOfficeTemp = o.ExcludeOfficeTemp;
        ExcludeDirectories = string.Join(Environment.NewLine, o.ExcludeDirectories);
        ExtraArguments = o.ExtraArguments ?? "";

        var s = job.Schedule;
        _scheduleKind = (int)s.Kind;
        _startDate = s.Start.Date;
        _startTime = s.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        Days = new ObservableCollection<DayOption>(
            new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
                .Select(d => new DayOption(d, s.DaysOfWeek.Contains(d))));
        DayOfMonth = s.DayOfMonth;
        IntervalMinutes = s.IntervalMinutes;
        RunMissed = s.RunMissed;

        Steps = ["page.endpoints", "page.options", "page.schedule", "page.summary"];
        _baseline = System.Text.Json.JsonSerializer.Serialize(ToJob(), Json.Options);
    }

    private string _baseline;

    /// <summary>The job differs from the one loaded (or last saved).</summary>
    public bool IsDirty => IsNew || System.Text.Json.JsonSerializer.Serialize(ToJob(), Json.Options) != _baseline;

    /// <summary>After a save, the job as saved is the new starting point.</summary>
    public void MarkSaved() => _baseline = System.Text.Json.JsonSerializer.Serialize(ToJob(), Json.Options);

    public bool IsNew { get; }
    public Guid JobId => _original.Id;
    public List<string> Steps { get; }
    public IEnumerable<string> StepTitles => Steps.Select((k, i) => $"{i + 1}. {Loc.T(k)}");

    public int Step
    {
        get => _step;
        set
        {
            if (!Set(ref _step, Math.Clamp(value, 0, SummaryStep))) return;
            OnPropertyChanged(nameof(IsFirstStep));
            OnPropertyChanged(nameof(IsSummaryStep));
            if (_step != SummaryStep) return;
            // The preview always describes the job as it is now.
            Preview = null;
            RefreshSummary();
        }
    }

    public bool IsFirstStep => _step == 0;
    public bool IsSummaryStep => _step == SummaryStep;

    // General
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    // Endpoints
    /// <summary>The sources; with more than one, each goes into its own folder of each destination.</summary>
    public ObservableCollection<EndpointViewModel> Sources { get; }

    /// <summary>The destinations; each run copies every source into each of them.</summary>
    public ObservableCollection<EndpointViewModel> Destinations { get; }

    private EndpointViewModel? _selectedSource;
    private EndpointViewModel? _selectedDestination;

    public EndpointViewModel? SelectedSource
    {
        get => _selectedSource;
        set => Set(ref _selectedSource, value);
    }

    public EndpointViewModel? SelectedDestination
    {
        get => _selectedDestination;
        set => Set(ref _selectedDestination, value);
    }

    public bool IsMultiSource => Sources.Count > 1;
    public bool IsMultiDestination => Destinations.Count > 1;

    public bool IsDestination(EndpointViewModel e) => Destinations.Contains(e);

    /// <summary>Adds an endpoint, or puts an edited copy in place of the one it was made from.</summary>
    public void Put(ObservableCollection<EndpointViewModel> list, EndpointViewModel? original, EndpointViewModel edited)
    {
        var i = original == null ? -1 : list.IndexOf(original);
        if (i >= 0) list[i] = edited;
        else list.Add(edited);
        if (ReferenceEquals(list, Sources)) SelectedSource = edited;
        else SelectedDestination = edited;
    }

    public void Remove(ObservableCollection<EndpointViewModel> list, EndpointViewModel e)
    {
        var i = list.IndexOf(e);
        if (i < 0) return;
        list.RemoveAt(i);
        var next = list.Count == 0 ? null : list[Math.Min(i, list.Count - 1)];
        if (ReferenceEquals(list, Sources)) SelectedSource = next;
        else SelectedDestination = next;
    }

    private bool _deleteExtrasFirst;

    /// <summary>Mirror without versions: delete the extras before copying, for a full destination.</summary>
    public bool DeleteExtrasFirst
    {
        get => _deleteExtrasFirst;
        set => Set(ref _deleteExtrasFirst, value);
    }

    // Mode
    public CopyMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            OnPropertyChanged(nameof(ModeCopy));
            OnPropertyChanged(nameof(ModeMirror));
            OnPropertyChanged(nameof(DeletesFiles));
            OnPropertyChanged(nameof(CanDeleteExtrasFirst));
            DeletionConfirmed = false;
            Preview = null;
        }
    }

    public bool ModeCopy { get => _mode == CopyMode.Copy; set { if (value) Mode = CopyMode.Copy; } }
    public bool ModeMirror { get => _mode == CopyMode.Mirror; set { if (value) Mode = CopyMode.Mirror; } }
    public bool DeletesFiles => _mode != CopyMode.Copy;

    /// <summary>Only a mirror deletes extras, and with versions they are kept, not deleted.</summary>
    public bool CanDeleteExtrasFirst => _mode == CopyMode.Mirror && !_keepVersions;

    // Options
    public int Threads
    {
        get => _threads;
        set
        {
            if (Set(ref _threads, Math.Clamp(value, 1, CopyOptions.MaxThreads))) OnPropertyChanged(nameof(SpeedText));
        }
    }

    public int Processes
    {
        get => _processes;
        set
        {
            if (Set(ref _processes, Math.Clamp(value, 1, CopyOptions.MaxProcesses))) OnPropertyChanged(nameof(SpeedText));
        }
    }

    public bool AutoTune
    {
        get => _autoTune;
        set
        {
            if (Set(ref _autoTune, value)) OnPropertyChanged(nameof(SpeedText));
        }
    }

    public string SpeedText => _autoTune ? Loc.T("options.auto.text") : Loc.F("options.total", _threads * _processes);

    public int Retries { get; set; }
    public int RetryWaitSeconds { get; set; }
    public bool UnbufferedIO { get; set; }
    public bool BackupMode { get; set; }
    public bool UseShadowCopy { get; set; }
    public bool Compress { get; set; }

    public bool KeepVersions
    {
        get => _keepVersions;
        set
        {
            if (!Set(ref _keepVersions, value)) return;
            OnPropertyChanged(nameof(MirrorVersionsNote));
            OnPropertyChanged(nameof(CanDeleteExtrasFirst));
        }
    }

    public int VersionRetentionDays
    {
        get => _versionRetentionDays;
        set
        {
            if (Set(ref _versionRetentionDays, value)) OnPropertyChanged(nameof(MirrorVersionsNote));
        }
    }

    /// <summary>Under the mirror mode: versions turn deletions into a dated, recoverable archive.</summary>
    public string MirrorVersionsNote => !_keepVersions ? Loc.T("mode.Mirror.versions.off")
        : _versionRetentionDays == 0 ? Loc.T("mode.Mirror.versions.forever")
        : Loc.F("mode.Mirror.versions.on", _versionRetentionDays);
    public bool ExcludeOlder { get; set; }
    public bool SkipJunctions { get; set; }
    public bool FollowShareJunctions { get; set; }

    /// <summary>The combo's index: 0 = every level, n = n levels.</summary>
    public int LinkSearchDepth { get; set; }

    public IReadOnlyList<string> LinkSearchDepths { get; } =
        [Loc.T("options.linkdepth.all"), .. Enumerable.Range(1, CopyOptions.MaxLinkSearchDepth).Select(n => n.ToString())];
    public bool FatFileTimes { get; set; }
    public int FileProperties { get => _fileProperties; set => Set(ref _fileProperties, value); }
    public string ExcludeFiles { get; set; }
    public bool ExcludeOfficeTemp { get; set; }
    public string ExcludeDirectories { get; set; }
    public string ExtraArguments { get; set; }

    // Schedule
    public int ScheduleIndex
    {
        get => _scheduleKind;
        set
        {
            if (!Set(ref _scheduleKind, value)) return;
            OnPropertyChanged(nameof(ShowDate));
            OnPropertyChanged(nameof(ShowTime));
            OnPropertyChanged(nameof(ShowDays));
            OnPropertyChanged(nameof(ShowDayOfMonth));
            OnPropertyChanged(nameof(ShowInterval));
            OnPropertyChanged(nameof(ShowRunMissed));
        }
    }

    private Core.Models.ScheduleKind Kind => (Core.Models.ScheduleKind)_scheduleKind;
    public bool ShowDate => Kind is Core.Models.ScheduleKind.Once or Core.Models.ScheduleKind.Interval;
    public bool ShowTime => Kind != Core.Models.ScheduleKind.Manual;
    public bool ShowDays => Kind == Core.Models.ScheduleKind.Weekly;
    public bool ShowDayOfMonth => Kind == Core.Models.ScheduleKind.Monthly;
    public bool ShowInterval => Kind == Core.Models.ScheduleKind.Interval;
    public bool ShowRunMissed => Kind is not (Core.Models.ScheduleKind.Manual or Core.Models.ScheduleKind.Once);

    public DateTime? StartDate { get => _startDate; set => Set(ref _startDate, value); }
    public string StartTime { get => _startTime; set => Set(ref _startTime, value); }
    public ObservableCollection<DayOption> Days { get; }
    public int DayOfMonth { get; set; }
    public int IntervalMinutes { get; set; }
    public bool RunMissed { get; set; }

    // Summary
    public ObservableCollection<SummaryLine> SummaryLines { get; } = [];
    public ObservableCollection<SummaryLine> PreviewLines { get; } = [];

    public string Errors
    {
        get => _errors;
        set
        {
            Set(ref _errors, value);
            OnPropertyChanged(nameof(HasErrors));
        }
    }

    public bool HasErrors => _errors.Length > 0;

    public bool DeletionConfirmed { get => _deletionConfirmed; set => Set(ref _deletionConfirmed, value); }

    public bool PreviewRunning
    {
        get => _previewRunning;
        set => Set(ref _previewRunning, value);
    }

    public PreviewResult? Preview
    {
        get => _preview;
        set
        {
            Set(ref _preview, value);
            PreviewLines.Clear();
            if (value != null)
                foreach (var l in JobDescriber.DescribePreview(value)) PreviewLines.Add(l);
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(ExtraItems));
            OnPropertyChanged(nameof(CopyItems));
            OnPropertyChanged(nameof(ExtraItemsTitle));
            OnPropertyChanged(nameof(CopyItemsTitle));
            OnPropertyChanged(nameof(HasExtraItems));
        }
    }

    public bool HasPreview => _preview != null;
    public bool HasExtraItems => _preview?.ExtraItems.Count > 0;
    public IEnumerable<PreviewItemRow> ExtraItems => _preview?.ExtraItems.Select(i => new PreviewItemRow(i)) ?? [];
    public IEnumerable<PreviewItemRow> CopyItems => _preview?.CopyItems.Select(i => new PreviewItemRow(i)) ?? [];

    public string ExtraItemsTitle => _preview == null ? "" :
        Loc.F(!_preview.ExtrasDeleted ? "preview.extralist" : _preview.KeepVersions ? "preview.versionlist" : "preview.deletelist", Fmt.Count(_preview.ExtraItems.Count))
        + (_preview.ExtraItemsTruncated ? " " + Loc.T("preview.truncated") : "");

    public string CopyItemsTitle => _preview == null ? "" :
        Loc.F("preview.copylist", Fmt.Count(_preview.CopyItems.Count))
        + (_preview.CopyItemsTruncated ? " " + Loc.T("preview.truncated") : "");

    public void RefreshSummary()
    {
        var job = ToJob();
        SummaryLines.Clear();
        foreach (var l in JobDescriber.Describe(job)) SummaryLines.Add(l);
        var errors = JobValidator.Validate(job).Where(e => e != Loc.T("val.confirm"));
        Errors = string.Join(Environment.NewLine, errors);
    }

    /// <summary>The job as edited. Errors in free-text fields fall back to defaults; the validator reports the rest.</summary>
    public CopyJob ToJob()
    {
        var job = Json.Clone(_original);
        job.Name = _name.Trim();
        job.Enabled = _enabled;
        var sources = Sources.Select(s => s.ToEndpoint()).DefaultIfEmpty(new JobEndpoint()).ToList();
        job.Source = sources[0];
        job.ExtraSources = sources.Skip(1).ToList();
        if (sources.Count == 1) job.Source.Folder = null;
        var destinations = Destinations.Select(d => d.ToEndpoint()).DefaultIfEmpty(new JobEndpoint()).ToList();
        foreach (var d in destinations) d.Folder = null;
        job.Destination = destinations[0];
        job.ExtraDestinations = destinations.Skip(1).ToList();
        job.Mode = _mode;
        job.DeletionConfirmed = _deletionConfirmed;

        var o = job.Options;
        o.Threads = _threads;
        o.Processes = _processes;
        o.AutoTune = _autoTune;
        o.Retries = Retries;
        o.RetryWaitSeconds = RetryWaitSeconds;
        o.UnbufferedIO = UnbufferedIO;
        o.BackupMode = BackupMode;
        o.UseShadowCopy = UseShadowCopy;
        o.KeepVersions = KeepVersions;
        o.Compress = Compress;
        o.VersionRetentionDays = VersionRetentionDays;
        o.ExcludeOlder = ExcludeOlder;
        o.SkipJunctions = SkipJunctions;
        o.DeleteExtrasFirst = _deleteExtrasFirst;
        o.FollowShareJunctions = FollowShareJunctions;
        o.LinkSearchDepth = LinkSearchDepth;
        o.FatFileTimes = FatFileTimes;
        o.FileProperties = (FileProperties)_fileProperties;
        o.ExcludeFiles = Lines(ExcludeFiles);
        o.ExcludeOfficeTemp = ExcludeOfficeTemp;
        o.ExcludeDirectories = Lines(ExcludeDirectories);
        o.ExtraArguments = string.IsNullOrWhiteSpace(ExtraArguments) ? null : ExtraArguments.Trim();

        var s = job.Schedule;
        s.Kind = Kind;
        var time = TimeSpan.TryParseExact(_startTime?.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.FromHours(22);
        s.Start = (_startDate ?? DateTime.Today).Date + time;
        s.DaysOfWeek = Days.Where(d => d.Selected).Select(d => d.Day).ToList();
        s.DayOfMonth = DayOfMonth;
        s.IntervalMinutes = IntervalMinutes;
        s.RunMissed = RunMissed;
        return job;
    }

    /// <summary>Exclusions are typed one per line (or separated by ;).</summary>
    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public sealed class PreviewItemRow(PreviewItem item)
{
    public string Path => item.Path;
    public string Size => item.IsDirectory
        ? (item.Files is { } f ? Loc.F("preview.folder", Fmt.Count(f), Fmt.Bytes(item.Size)) : Loc.T("preview.folder.short"))
        : Fmt.Bytes(item.Size);
}
