using System.ComponentModel;
using System.Runtime.CompilerServices;
using GHPC_Mod_Manager.Models;

namespace GHPC_Mod_Manager.Helpers;

public class ThemeTracker : INotifyPropertyChanged
{
    private static ThemeTracker? _instance;
    public static ThemeTracker Instance => _instance ??= new ThemeTracker();

    private AppTheme _currentTheme = AppTheme.Light;
    public AppTheme CurrentTheme
    {
        get => _currentTheme;
        set
        {
            if (_currentTheme != value)
            {
                _currentTheme = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
