using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WallNut.Shared;
using WallNut.Shared.Notifications;

namespace WallNut.App.Views;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One event type's checkbox.</summary>
public sealed class KindItem(NotificationKind kind, bool enabled) : Observable
{
    private bool _enabled = enabled;
    private bool _groupOn = true;

    public NotificationKind Kind { get; } = kind;
    public string Title => Kind.Title;
    public bool Locked => Kind.AlwaysShown;
    public bool Enabled { get => Locked || _enabled; set => Set(ref _enabled, value); }
    public bool CanEdit => !Locked && _groupOn;
    public string Tip => Locked ? "Always shown: you need to know before your computer goes down." : Kind.Kind;

    internal void GroupChanged(bool on) { _groupOn = on; Raise(nameof(CanEdit)); }
}

/// <summary>One group card: a switch for the whole group plus its event types.</summary>
public sealed class GroupItem : Observable
{
    private bool _enabled;

    public GroupItem(NotificationGroup group, bool enabled, IEnumerable<KindItem> kinds)
    {
        Group = group;
        Kinds = new ObservableCollection<KindItem>(kinds);
        Locked = Kinds.All(k => k.Locked) || group == NotificationGroup.Shutdown;
        _enabled = enabled || Locked;
        foreach (var k in Kinds) k.GroupChanged(_enabled);
    }

    public NotificationGroup Group { get; }
    public string Title => Group.ToString();
    public string Description => NotificationCatalog.Describe(Group);
    public bool Locked { get; }
    public bool CanEdit => !Locked;
    public ObservableCollection<KindItem> Kinds { get; }

    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value || Locked)) foreach (var k in Kinds) k.GroupChanged(_enabled); }
    }
}

/// <summary>One UPS the service knows about.</summary>
public sealed class UpsItem(string id, string name, string detail, bool notify) : Observable
{
    private bool _notify = notify;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public bool Notify { get => _notify; set => Set(ref _notify, value); }
}

public sealed class SettingsViewModel : Observable
{
    private bool _enabled;
    private decimal _delay;
    private string _serviceStatus = "Checking the WALLNut service...";
    private string _message = "";
    private readonly HashSet<string> _mutedUnknown = new(StringComparer.OrdinalIgnoreCase);

    public SettingsViewModel(NotificationPreferences prefs)
    {
        _enabled = prefs.Enabled;
        _delay = (decimal)prefs.OnBatteryDelaySeconds;
        Groups = new ObservableCollection<GroupItem>(
            NotificationCatalog.All
                .GroupBy(k => k.Group)
                .OrderBy(g => g.Key)
                .Select(g => new GroupItem(g.Key, prefs.GroupEnabled(g.Key),
                    g.Select(k => new KindItem(k, prefs.KindEnabled(k))))));
        // Keep mutes for UPSes the service isn't reporting right now.
        foreach (var id in prefs.MutedUps) _mutedUnknown.Add(id);
    }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public decimal OnBatteryDelaySeconds { get => _delay; set => Set(ref _delay, value); }
    public ObservableCollection<GroupItem> Groups { get; }
    public ObservableCollection<UpsItem> Ups { get; } = [];
    public bool HasUps => Ups.Count > 0;
    public string ServiceStatus { get => _serviceStatus; set => Set(ref _serviceStatus, value); }
    public string Message { get => _message; set => Set(ref _message, value); }

    /// <summary>Refreshes the UPS list and service line, keeping any boxes the user already changed.</summary>
    public void UpdateService(ServiceInfo? info)
    {
        if (info is null)
        {
            ServiceStatus = "The WALLNut service isn't running. These settings still save, and apply once it is.";
            return;
        }

        var mode = !info.ShutdownEnabled ? "automatic shutdown is off"
            : info.ShutdownTestMode ? "automatic shutdown is in TEST MODE"
            : $"automatic {info.ShutdownAction.ToLowerInvariant()} is armed";
        ServiceStatus = $"Connected to the WALLNut service on {info.Machine}: watching {info.Ups.Count} UPS{(info.Ups.Count == 1 ? "" : "es")}, {mode}.";

        foreach (var u in info.Ups)
        {
            if (Ups.Any(x => x.Id.Equals(u.Id, StringComparison.OrdinalIgnoreCase))) continue;
            var detail = $"{u.Ups}@{u.Host}" + (u.Model is { } m ? $"  ·  {m}" : "") + (u.Role == "PowersThisMachine" ? "  ·  powers this computer" : "");
            Ups.Add(new UpsItem(u.Id, u.DisplayName, detail, !_mutedUnknown.Contains(u.Id)));
        }
        Raise(nameof(HasUps));
    }

    public NotificationPreferences ToPreferences()
    {
        var p = new NotificationPreferences
        {
            Enabled = Enabled,
            OnBatteryDelaySeconds = (double)Math.Clamp(OnBatteryDelaySeconds, 0, 300),
        };
        foreach (var g in Groups)
        {
            if (!g.Locked) p.Groups[g.Group.ToString()] = g.Enabled;
            foreach (var k in g.Kinds.Where(k => !k.Locked && k.Enabled != k.Kind.OnByDefault))
                p.Kinds[k.Kind.Kind] = k.Enabled;
        }
        foreach (var id in _mutedUnknown.Where(id => !Ups.Any(u => u.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            p.MutedUps.Add(id);
        foreach (var u in Ups.Where(u => !u.Notify)) p.MutedUps.Add(u.Id);
        return p;
    }
}
