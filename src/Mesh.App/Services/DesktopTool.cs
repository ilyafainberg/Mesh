using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Mesh.App.Services;

/// <summary>
/// Owner-gated desktop-control tool. Injects real mouse and keyboard input and captures the
/// screen using native Win32 (P/Invoke, no external server). Windows only; on other platforms it
/// reports that it is unavailable.
/// </summary>
public sealed class DesktopTool : IAgentTool
{
    public string Name => "desktop";
    public string Description =>
        "Control the local desktop: move/click the mouse, type text, press keys or chords, and " +
        "capture the screen. Use for GUI automation of native apps. Windows only.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            action = new { type = "string", description = "move | click | type | key | screenshot | cursor" },
            x = new { type = "integer", description = "X screen coordinate for move/click." },
            y = new { type = "integer", description = "Y screen coordinate for move/click." },
            text = new { type = "string", description = "Text to type (action=type) or a key/chord like Enter or Ctrl+S (action=key)." },
            button = new { type = "string", description = "Mouse button for click: left | right | middle (default left)." },
            @double = new { type = "boolean", description = "If true, perform a double-click (action=click)." }
        },
        required = new[] { "action" }
    };

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult("ERROR: the desktop tool is Windows only.");

        try
        {
            var action = ToolArgs.GetString(args, "action").Trim().ToLowerInvariant();
            var result = action switch
            {
                "move" => DoMove(args),
                "click" => DoClick(args),
                "type" => DoType(args),
                "key" => DoKey(args),
                "screenshot" => DoScreenshot(),
                "cursor" => DoCursor(),
                _ => "ERROR: unknown action '" + action + "'. Valid actions: move, click, type, key, screenshot, cursor."
            };
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult("ERROR: desktop action failed: " + ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string DoMove(JsonElement args)
    {
        var x = ToolArgs.GetInt(args, "x", int.MinValue);
        var y = ToolArgs.GetInt(args, "y", int.MinValue);
        if (x == int.MinValue || y == int.MinValue)
            return "ERROR: move requires integer x and y.";
        if (!SetCursorPos(x, y))
            return "ERROR: SetCursorPos failed for (" + x + ", " + y + ").";
        return "ok: moved cursor to (" + x + ", " + y + ").";
    }

    [SupportedOSPlatform("windows")]
    private static string DoClick(JsonElement args)
    {
        var x = ToolArgs.GetInt(args, "x", int.MinValue);
        var y = ToolArgs.GetInt(args, "y", int.MinValue);
        var moved = false;
        if (x != int.MinValue && y != int.MinValue)
        {
            if (!SetCursorPos(x, y))
                return "ERROR: SetCursorPos failed for (" + x + ", " + y + ").";
            moved = true;
        }

        var button = ToolArgs.GetString(args, "button", "left").Trim().ToLowerInvariant();
        var doubleClick = GetBool(args, "double");

        uint down, up;
        switch (button)
        {
            case "right":
                down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; break;
            case "middle":
                down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; break;
            case "left":
                down = MOUSEEVENTF_LEFTDOWN; up = MOUSEEVENTF_LEFTUP; break;
            default:
                return "ERROR: unknown button '" + button + "'. Valid: left, right, middle.";
        }

        var clicks = doubleClick ? 2 : 1;
        for (var i = 0; i < clicks; i++)
        {
            SendMouseClick(down, up);
        }

        var where = moved ? " at (" + x + ", " + y + ")" : " at current position";
        return "ok: " + (doubleClick ? "double-" : "") + button + "-clicked" + where + ".";
    }

    [SupportedOSPlatform("windows")]
    private static void SendMouseClick(uint down, uint up)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].u.mi = new MOUSEINPUT { dwFlags = down };
        inputs[1].type = INPUT_MOUSE;
        inputs[1].u.mi = new MOUSEINPUT { dwFlags = up };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [SupportedOSPlatform("windows")]
    private static string DoType(JsonElement args)
    {
        var text = ToolArgs.GetString(args, "text");
        if (text.Length == 0)
            return "ERROR: type requires a non-empty text argument.";

        foreach (var ch in text)
        {
            SendUnicodeChar(ch);
        }
        return "ok: typed " + text.Length + " character(s).";
    }

    [SupportedOSPlatform("windows")]
    private static void SendUnicodeChar(char ch)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE };
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [SupportedOSPlatform("windows")]
    private static string DoKey(JsonElement args)
    {
        var text = ToolArgs.GetString(args, "text").Trim();
        if (text.Length == 0)
            return "ERROR: key requires a non-empty text argument (e.g. Enter or Ctrl+S).";

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = new List<ushort>();
        string? mainToken = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers.Add(VK_CONTROL); break;
                case "alt":
                    modifiers.Add(VK_MENU); break;
                case "shift":
                    modifiers.Add(VK_SHIFT); break;
                case "win":
                case "windows":
                case "meta":
                    modifiers.Add(VK_LWIN); break;
                default:
                    if (mainToken != null)
                        return "ERROR: only one main key is allowed in a chord (got '" + mainToken + "' and '" + part + "').";
                    mainToken = part; break;
            }
        }

        if (mainToken == null)
            return "ERROR: no main key found in '" + text + "'.";

        if (!TryResolveVk(mainToken, out var mainVk))
            return "ERROR: unrecognized key '" + mainToken + "'.";

        // Hold modifiers down.
        foreach (var mod in modifiers)
            SendVirtualKey(mod, false);

        // Press and release the main key by VK so OS hotkeys fire.
        SendVirtualKey(mainVk, false);
        SendVirtualKey(mainVk, true);

        // Release modifiers in reverse order.
        for (var i = modifiers.Count - 1; i >= 0; i--)
            SendVirtualKey(modifiers[i], true);

        return "ok: pressed key '" + text + "'.";
    }

    [SupportedOSPlatform("windows")]
    private static void SendVirtualKey(ushort vk, bool keyUp)
    {
        var inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki = new KEYBDINPUT
        {
            wVk = vk,
            dwFlags = keyUp ? KEYEVENTF_KEYUP : 0
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [SupportedOSPlatform("windows")]
    private static bool TryResolveVk(string token, out ushort vk)
    {
        vk = 0;
        switch (token.ToLowerInvariant())
        {
            case "enter":
            case "return": vk = 0x0D; return true;
            case "tab": vk = 0x09; return true;
            case "esc":
            case "escape": vk = 0x1B; return true;
            case "space":
            case "spacebar": vk = 0x20; return true;
            case "backspace":
            case "back": vk = 0x08; return true;
            case "delete":
            case "del": vk = 0x2E; return true;
            case "insert":
            case "ins": vk = 0x2D; return true;
            case "home": vk = 0x24; return true;
            case "end": vk = 0x23; return true;
            case "pageup":
            case "pgup": vk = 0x21; return true;
            case "pagedown":
            case "pgdn": vk = 0x22; return true;
            case "up": vk = 0x26; return true;
            case "down": vk = 0x28; return true;
            case "left": vk = 0x25; return true;
            case "right": vk = 0x27; return true;
            case "win":
            case "windows":
            case "meta": vk = VK_LWIN; return true;
            case "capslock": vk = 0x14; return true;
            case "printscreen":
            case "prtsc": vk = 0x2C; return true;
        }

        // Function keys F1-F24.
        if ((token.Length == 2 || token.Length == 3) &&
            (token[0] == 'F' || token[0] == 'f') &&
            int.TryParse(token.AsSpan(1), out var fn) && fn >= 1 && fn <= 24)
        {
            vk = (ushort)(0x70 + (fn - 1));
            return true;
        }

        // Single printable character: resolve via VkKeyScanW (low byte is the VK).
        if (token.Length == 1)
        {
            var scan = VkKeyScanW(token[0]);
            if (scan == -1)
                return false;
            vk = (ushort)(scan & 0xFF);
            return true;
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static string DoScreenshot()
    {
        var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
        {
            x = 0;
            y = 0;
            width = GetSystemMetrics(SM_CXSCREEN);
            height = GetSystemMetrics(SM_CYSCREEN);
        }

        if (width <= 0 || height <= 0)
            return "ERROR: could not determine screen dimensions.";

        var path = Path.Combine(Path.GetTempPath(), $"mesh-desktop-{Guid.NewGuid():N}.png");
        using (var bmp = new System.Drawing.Bitmap(width, height))
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        return "ok: saved screenshot (" + width + "x" + height + ") to " + path;
    }

    [SupportedOSPlatform("windows")]
    private static string DoCursor()
    {
        if (!GetCursorPos(out var pt))
            return "ERROR: GetCursorPos failed.";
        return "ok: cursor at (" + pt.X + ", " + pt.Y + ").";
    }

    private static bool GetBool(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object &&
           args.TryGetProperty(name, out var v) &&
           (v.ValueKind == JsonValueKind.True ||
            (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    // ----- Win32 interop -----

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LWIN = 0x5B;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
