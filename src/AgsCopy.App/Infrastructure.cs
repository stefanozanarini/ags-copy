using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using AgsCopy.Core.Ipc;

namespace AgsCopy.App;

/// <summary>{l:T key} — a translated string, resolved when the window loads.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Per-user preferences in %AppData%\AGS-Copy\settings.json.</summary>
public sealed class UserSettings
{
    public string Language { get; set; } = "it";

    public static UserSettings Current { get; private set; } = new();

    private static string FilePath => Path.Combine(AppPaths.UserDir, "settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), Json.Options) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        Loc.Language = Loc.Languages.Contains(Current.Language) ? Current.Language : "it";
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json.Indented));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AGS-Copy.exe");

    /// <summary>Starts AGS-Copy with a UAC prompt. False when the user declines.</summary>
    public static bool StartElevated(params string[] args)
    {
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return false; // UAC prompt cancelled
        }
    }
}

public static class Ui
{
    public static void Error(Window? owner, Exception e) => Error(owner, Describe(e));

    public static void Error(Window? owner, string message)
    {
        if (owner != null) MessageBox.Show(owner, message, "AGS-Copy", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(message, "AGS-Copy", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static bool Confirm(Window owner, string message, bool danger = false) =>
        MessageBox.Show(owner, message, "AGS-Copy", MessageBoxButton.YesNo, danger ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No)
        == MessageBoxResult.Yes;

    public static string Describe(Exception e) => e switch
    {
        ServiceUnavailableException => Loc.T("err.service"),
        OperationCanceledException => Loc.T("err.timeout"),
        _ => e.Message,
    };
}

public sealed class Not : System.Windows.Data.IValueConverter
{
    public static readonly Not Instance = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is not true;
}

/// <summary>True → Collapsed, false → Visible.</summary>
public sealed class NotVisible : System.Windows.Data.IValueConverter
{
    public static readonly NotVisible Instance = new();

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
