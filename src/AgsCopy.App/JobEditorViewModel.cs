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

    public EndpointViewModel(JobEndpoint e)
    {
        _path = e.Path;
        _userName = e.Credential?.UserName ?? "";
        HasStoredPassword = e.Credential?.HasPassword == true;
    }

    public string Path
    {
        get => _path;
        set
        {
            if (!Set(ref _path, value)) return;
            OnPropertyChanged(nameof(IsNetwork));
            OnPropertyChanged(nameof(IsLocal));
            MappedUnc = NetworkDrives.ToUnc(value);
            TestResult = "";
        }
    }

    public bool IsLocal => !IsNetwork;

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
        var e = new JobEndpoint { Path = PathUtil.NormalizeDir(_path) };
        if (IsNetwork && !string.IsNullOrWhiteSpace(_userName))
        {
            e.Credential = new StoredCredential
            {
                UserName = _userName.Trim(),
                Password = Password.Length > 0 ? Password : null,
                HasPassword = Password.Length == 0 && HasStoredPassword,
            };
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
    public const int SummaryStep = 6;

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
        Source = new EndpointViewModel(job.Source);
        Destination = new EndpointViewModel(job.Destination);
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
        VersionRetentionDays = o.VersionRetentionDays;
        ExcludeOlder = o.ExcludeOlder;
        SkipJunctions = o.SkipJunctions;
        FatFileTimes = o.FatFileTimes;
        _fileProperties = (int)o.FileProperties;
        ExcludeFiles = string.Join(Environment.NewLine, o.ExcludeFiles);
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

        Steps = ["step.general", "step.source", "step.destination", "step.mode", "step.options", "step.schedule", "step.summary"];
    }

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
    public EndpointViewModel Source { get; }
    public EndpointViewModel Destination { get; }

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
            DeletionConfirmed = false;
            Preview = null;
        }
    }

    public bool ModeCopy { get => _mode == CopyMode.Copy; set { if (value) Mode = CopyMode.Copy; } }
    public bool ModeMirror { get => _mode == CopyMode.Mirror; set { if (value) Mode = CopyMode.Mirror; } }
    public bool DeletesFiles => _mode != CopyMode.Copy;

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
    public bool KeepVersions
    {
        get => _keepVersions;
        set
        {
            if (Set(ref _keepVersions, value)) OnPropertyChanged(nameof(MirrorVersionsNote));
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
    public bool FatFileTimes { get; set; }
    public int FileProperties { get => _fileProperties; set => Set(ref _fileProperties, value); }
    public string ExcludeFiles { get; set; }
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
        job.Source = Source.ToEndpoint();
        job.Destination = Destination.ToEndpoint();
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
        o.VersionRetentionDays = VersionRetentionDays;
        o.ExcludeOlder = ExcludeOlder;
        o.SkipJunctions = SkipJunctions;
        o.FatFileTimes = FatFileTimes;
        o.FileProperties = (FileProperties)_fileProperties;
        o.ExcludeFiles = Lines(ExcludeFiles);
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
