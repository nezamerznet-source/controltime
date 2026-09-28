using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FamilyTime.Core;

internal sealed class Sqlite : IDisposable
{
    private IntPtr db;
    static Sqlite()
    {
        NativeLibrary.SetDllImportResolver(typeof(Sqlite).Assembly, (name, assembly, path) =>
            name == "familytime_sqlite" ? NativeLibrary.Load(OperatingSystem.IsWindows() ? "winsqlite3.dll" : "libsqlite3.so.0") : IntPtr.Zero);
    }
    public Sqlite(string path)
    {
        int result = Native.sqlite3_open_v2(path, out db, 2 | 4 | 0x10000, IntPtr.Zero);
        if (result != 0) { if (db != IntPtr.Zero) Native.sqlite3_close_v2(db); db = IntPtr.Zero; throw new IOException("Не удалось открыть локальную базу."); }
        Native.sqlite3_busy_timeout(db, 5000);
        Run("PRAGMA journal_mode=WAL"); Run("PRAGMA synchronous=FULL"); Run("PRAGMA foreign_keys=ON");
    }
    public void Run(string sql, params object?[] args) { using var s = Prepare(sql, args); while (s.Step()) { } }
    public List<string[]> Query(string sql, params object?[] args)
    {
        using var s = Prepare(sql, args); var rows = new List<string[]>();
        while (s.Step())
        {
            var n = Native.sqlite3_column_count(s.Pointer); var row = new string[n];
            for (var i = 0; i < n; i++) row[i] = Marshal.PtrToStringUTF8(Native.sqlite3_column_text(s.Pointer, i)) ?? "";
            rows.Add(row);
        }
        return rows;
    }
    public long Changed => Native.sqlite3_changes(db);
    private Statement Prepare(string sql, object?[] args)
    {
        if (Native.sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero) != 0) throw new IOException("Ошибка подготовки запроса локальной базы.");
        var s = new Statement(statement);
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                var v = args[i]; int rc;
                if (v is null) rc = Native.sqlite3_bind_null(statement, i + 1);
                else if (v is bool b) rc = Native.sqlite3_bind_int64(statement, i + 1, b ? 1 : 0);
                else if (v is int or long) rc = Native.sqlite3_bind_int64(statement, i + 1, Convert.ToInt64(v));
                else if (v is double or float or decimal) rc = Native.sqlite3_bind_double(statement, i + 1, Convert.ToDouble(v, CultureInfo.InvariantCulture));
                else rc = Native.sqlite3_bind_text(statement, i + 1, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "", -1, new IntPtr(-1));
                if (rc != 0) throw new IOException("Ошибка параметра локальной базы.");
            }
            return s;
        }
        catch { s.Dispose(); throw; }
    }
    public void Dispose() { if (db != IntPtr.Zero) { Native.sqlite3_close_v2(db); db = IntPtr.Zero; } }
    private sealed class Statement(IntPtr pointer) : IDisposable
    {
        public IntPtr Pointer => pointer;
        public bool Step()
        {
            var rc = Native.sqlite3_step(pointer);
            if (rc == 100) return true;
            if (rc == 101) return false;
            throw new IOException("Ошибка записи или чтения локальной базы.");
        }
        public void Dispose() => Native.sqlite3_finalize(pointer);
    }
    private static class Native
    {
        const string Lib = "familytime_sqlite";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int sqlite3_changes(IntPtr db);
    }
}
