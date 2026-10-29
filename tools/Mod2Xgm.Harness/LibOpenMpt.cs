using System.Reflection;
using System.Runtime.InteropServices;

namespace Mod2Xgm.Harness;

/// <summary>
/// The few libopenmpt C functions the harness needs (libopenmpt.h, BSD-3-Clause; ADR 0047). libopenmpt is OpenMPT's
/// playback engine, the accuracy reference: the harness loads it at run time, it is never part of the converter.
/// </summary>
internal static partial class LibOpenMpt
{
    /// <summary>The name the imports use; <see cref="Resolve"/> maps it to the file on each OS.</summary>
    private const string Library = "libopenmpt";

    private static string? _directory;

    static LibOpenMpt() => NativeLibrary.SetDllImportResolver(typeof(LibOpenMpt).Assembly, Resolve);

    /// <summary>
    /// Loads libopenmpt from <paramref name="directory"/> instead of the system's search path (<c>--libopenmpt</c>).
    /// Call before the first use.
    /// </summary>
    public static void UseDirectory(string? directory) => _directory = directory;

    /// <summary>The library's file name on this OS, as its packages install it.</summary>
    internal static string FileName =>
        OperatingSystem.IsWindows() ? "libopenmpt.dll" : OperatingSystem.IsMacOS() ? "libopenmpt.0.dylib" : "libopenmpt.so.0";

    /// <summary>
    /// libopenmpt's version as text (e.g. <c>0.8.9</c>). Also the check that the library loads; throws
    /// <see cref="HarnessException"/> with what to do when it doesn't.
    /// </summary>
    public static string Version()
    {
        uint version;
        try
        {
            version = GetLibraryVersion();
        }
        catch (DllNotFoundException ex)
        {
            throw new HarnessException(
                $"libopenmpt can't be loaded ({ex.Message}). Install it (docs/harness.md#setup) and put its folder on PATH, or pass --libopenmpt <folder>.",
                ex);
        }
        catch (BadImageFormatException ex)
        {
            throw new HarnessException(
                $"The libopenmpt found is built for another CPU ({ex.Message}). On 64-bit Windows use the bin\\amd64 folder of the libopenmpt package.",
                ex);
        }

        // libopenmpt.h: 0xMMmmpppp (major, minor, patch).
        return FormattableString.Invariant($"{version >> 24}.{(version >> 16) & 0xFF}.{version & 0xFFFF}");
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Library)
        {
            return IntPtr.Zero;
        }

        if (_directory is not null)
        {
            return NativeLibrary.Load(Path.Combine(_directory, FileName));
        }

        // The usual names first, then the versioned file Linux and macOS packages install without the -dev package, then
        // the Windows package unpacked to Program Files (a PATH entry for the wrong CPU's folder fails to load and is skipped).
        if (NativeLibrary.TryLoad(Library, assembly, searchPath, out IntPtr handle) || NativeLibrary.TryLoad(FileName, assembly, searchPath, out handle))
        {
            return handle;
        }

        return WindowsInstallFolder() is { } folder && NativeLibrary.TryLoad(Path.Combine(folder, FileName), out handle) ? handle : IntPtr.Zero;
    }

    /// <summary>
    /// <c>Program Files\libopenmpt\bin\&lt;cpu&gt;</c>, where the Windows package (lib.openmpt.org) keeps one folder per
    /// CPU; <c>null</c> on other systems.
    /// </summary>
    internal static string? WindowsInstallFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string cpu = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "unknown",
        };
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "libopenmpt", "bin", cpu);
    }

    [LibraryImport(Library, EntryPoint = "openmpt_get_library_version")]
    internal static partial uint GetLibraryVersion();

    [LibraryImport(Library, EntryPoint = "openmpt_module_create_from_memory2")]
    internal static unsafe partial IntPtr CreateFromMemory(
        byte* fileData,
        nuint fileSize,
        IntPtr logFunc,
        IntPtr logUser,
        IntPtr errorFunc,
        IntPtr errorUser,
        out int error,
        IntPtr errorMessage,
        IntPtr initialCtls);

    /// <summary>libopenmpt.h: the text for an <c>OPENMPT_ERROR_*</c> code; free it with <see cref="FreeString"/>.</summary>
    [LibraryImport(Library, EntryPoint = "openmpt_error_string")]
    internal static partial IntPtr ErrorString(int error);

    [LibraryImport(Library, EntryPoint = "openmpt_free_string")]
    internal static partial void FreeString(IntPtr text);

    /// <summary>
    /// A log function that drops libopenmpt's messages (its default prints them to stderr); errors still reach the
    /// harness through the error code.
    /// </summary>
    internal static unsafe IntPtr SilentLog => (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&Silent;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void Silent(IntPtr message, IntPtr user)
    {
    }

    /// <summary>The text for libopenmpt error <paramref name="error"/>.</summary>
    internal static string DescribeError(int error)
    {
        IntPtr text = ErrorString(error);
        try
        {
            return Marshal.PtrToStringUTF8(text) ?? string.Empty;
        }
        finally
        {
            FreeString(text);
        }
    }

    [LibraryImport(Library, EntryPoint = "openmpt_module_destroy")]
    internal static partial void Destroy(IntPtr module);

    [LibraryImport(Library, EntryPoint = "openmpt_module_get_duration_seconds")]
    internal static partial double GetDurationSeconds(IntPtr module);

    /// <summary>libopenmpt.h: seeks to an order and row and returns the song time there, in seconds.</summary>
    [LibraryImport(Library, EntryPoint = "openmpt_module_set_position_order_row")]
    internal static partial double SetPositionOrderRow(IntPtr module, int order, int row);

    [LibraryImport(Library, EntryPoint = "openmpt_module_read_float_stereo")]
    internal static unsafe partial nuint ReadFloatStereo(IntPtr module, int sampleRate, nuint count, float* left, float* right);
}
