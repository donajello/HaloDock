using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace HaloDock;

/// <summary>What happens when files are dropped on a menu item: folders receive them, apps open them.</summary>
public static class DropTargets
{
    static readonly string[] AppExtensions = [".exe", ".lnk", ".bat", ".cmd", ".com"];

    static bool IsFolder(LaunchItem item) => Directory.Exists(item.Path);

    static bool IsApp(LaunchItem item) =>
        File.Exists(item.Path) && AppExtensions.Contains(Path.GetExtension(item.Path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Explorer's rules: move within a drive, copy across drives; Ctrl forces copy, Shift forces move.</summary>
    public static DragDropEffects EffectFor(LaunchItem item, string[] files, DragDropKeyStates keys, DragDropEffects allowed)
    {
        if (files.Length == 0) return DragDropEffects.None;

        if (IsFolder(item))
        {
            var target = Path.GetFullPath(item.Path).TrimEnd('\\');
            var pointless = files.All(f =>
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(f))?.TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFullPath(f).TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase));
            if (pointless) return DragDropEffects.None;

            var copy = keys.HasFlag(DragDropKeyStates.ControlKey) ? true
                : keys.HasFlag(DragDropKeyStates.ShiftKey) ? false
                : !string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(item.Path), StringComparison.OrdinalIgnoreCase);
            var wanted = copy ? DragDropEffects.Copy : DragDropEffects.Move;
            if (allowed.HasFlag(wanted)) return wanted;
            return allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        if (IsApp(item)) return allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.Link & allowed;

        return DragDropEffects.None;
    }

    public static string Describe(LaunchItem item, DragDropEffects effect) => effect switch
    {
        DragDropEffects.Move => "Move to " + item.DisplayName,
        DragDropEffects.Copy when IsFolder(item) => "Copy to " + item.DisplayName,
        DragDropEffects.Copy or DragDropEffects.Link => "Open with " + item.DisplayName,
        _ => item.DisplayName,
    };

    public static void Perform(LaunchItem item, string[] files, DragDropEffects effect)
    {
        if (effect == DragDropEffects.None) return;

        if (IsFolder(item))
        {
            var op = new NativeMethods.SHFILEOPSTRUCT
            {
                wFunc = effect == DragDropEffects.Move ? NativeMethods.FO_MOVE : NativeMethods.FO_COPY,
                pFrom = string.Join('\0', files) + "\0\0", // double-null-terminated list
                pTo = item.Path + "\0\0",
                fFlags = NativeMethods.FOF_ALLOWUNDO,
            };
            // Runs on its own thread so a big copy (and its progress dialog) never freezes the menu.
            var thread = new Thread(() => NativeMethods.SHFileOperation(ref op)) { IsBackground = false };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return;
        }

        NativeMethods.AllowSetForegroundWindow(-1);
        Process.Start(new ProcessStartInfo(item.Path)
        {
            UseShellExecute = true,
            Arguments = string.Join(' ', files.Select(f => $"\"{f}\"")),
            WorkingDirectory = Path.GetDirectoryName(item.Path) ?? "",
        });
    }
}
