using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Cefiro.App.Infrastructure;

namespace Cefiro.App.Services;

/// <summary>
/// Atajos de teclado globales (funcionan aunque Céfiro esté en la bandeja). Usa RegisterHotKey de
/// Windows sobre la ventana principal, que existe aunque esté oculta.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModWin = 0x8;
    private const uint ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private readonly IntPtr _handle;
    private readonly Dictionary<int, string> _registered = [];
    private int _nextId = 0xB000;

    public HotkeyService(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _handle = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("No hay ventana para los atajos.");
        _source.AddHook(WndProc);
    }

    /// <summary>Se ha pulsado el atajo de un perfil (se lanza en el hilo de la interfaz).</summary>
    public event Action<string>? ProfileHotkeyPressed;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Registra los atajos de los perfiles. Devuelve los que Windows no ha aceptado (los usa otro programa).</summary>
    public IReadOnlyList<string> Register(IEnumerable<(string ProfileId, string Hotkey)> hotkeys)
    {
        UnregisterAll();
        var failed = new List<string>();
        foreach (var (profileId, text) in hotkeys)
        {
            if (!TryParse(text, out var modifiers, out var key))
            {
                failed.Add(text);
                continue;
            }

            var id = _nextId++;
            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (RegisterHotKey(_handle, id, ToNative(modifiers) | ModNoRepeat, vk))
            {
                _registered[id] = profileId;
            }
            else
            {
                failed.Add(text);
                UiLog.Error($"No se pudo registrar el atajo {text}: lo está usando otro programa (error {Marshal.GetLastWin32Error()})");
            }
        }

        return failed;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
            UnregisterHotKey(_handle, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var profileId))
        {
            handled = true;
            ProfileHotkeyPressed?.Invoke(profileId);
        }

        return IntPtr.Zero;
    }

    private static uint ToNative(ModifierKeys m) =>
        (m.HasFlag(ModifierKeys.Alt) ? ModAlt : 0)
        | (m.HasFlag(ModifierKeys.Control) ? ModControl : 0)
        | (m.HasFlag(ModifierKeys.Shift) ? ModShift : 0)
        | (m.HasFlag(ModifierKeys.Windows) ? ModWin : 0);

    /// <summary>"Ctrl+Alt+1" → (Control | Alt, D1). Hace falta al menos un modificador.</summary>
    public static bool TryParse(string? text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "mayús" or "mayus" or "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse(name, ignoreCase: true, out key))
                        return false;
                    break;
            }
        }

        return modifiers != ModifierKeys.None && key != Key.None;
    }

    /// <summary>Texto de un atajo, o null si no sirve (sin modificador o solo modificadores).</summary>
    public static string? Format(ModifierKeys modifiers, Key key)
    {
        if (modifiers == ModifierKeys.None || key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return null;

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Mayús");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var keyText = key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString();
        parts.Add(keyText);
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
