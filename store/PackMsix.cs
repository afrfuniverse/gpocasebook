using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

internal static class PackMsix
{
    // The Windows packaging API writes and validates the manifest and block map.
    [ComImport, Guid("BEB94909-E451-438B-B5A7-D79E767B75D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAppxFactory
    {
        void CreatePackageWriter(IStream stream, ref PackageSettings settings, out IAppxPackageWriter writer);
        void CreatePackageReader(IStream stream, [MarshalAs(UnmanagedType.IUnknown)] out object reader);
    }

    [ComImport, Guid("9099E33B-246F-41E4-881A-008EB613F858"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAppxPackageWriter
    {
        void AddPayloadFile([MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string contentType, uint compression, IStream input);
        void Close(IStream manifest);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PackageSettings
    {
        [MarshalAs(UnmanagedType.Bool)] public bool ForceZip32;
        public IntPtr HashMethod;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr template, out IStream stream);

    [DllImport("urlmon.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CreateUri(string uri, uint flags, UIntPtr reserved, out IntPtr result);

    private const uint ReadOnly = 0x20;
    private const uint CreateReadWrite = 0x1012;
    private const uint CompressionNormal = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: PackMsix <payload folder> <output .msix>");
            return 2;
        }

        try
        {
            string source = Path.GetFullPath(args[0]);
            string destination = Path.GetFullPath(args[1]);
            Build(source, destination);
            Console.WriteLine("Windows packaging API verified " + Path.GetFileName(destination));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("MSIX packaging failed. " + error.Message + " (0x" + error.HResult.ToString("X8") + ")");
            return 1;
        }
    }

    private static void Build(string source, string destination)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var factory = (IAppxFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("5842A140-FF9F-4166-8F5C-62F5B7B0C781"), true));
        IntPtr uri = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(CreateUri("http://www.w3.org/2001/04/xmlenc#sha256", 0, UIntPtr.Zero, out uri));
            var settings = new PackageSettings { ForceZip32 = true, HashMethod = uri };
            var output = Open(temporary, CreateReadWrite, true);
            try
            {
                IAppxPackageWriter writer;
                factory.CreatePackageWriter(output, ref settings, out writer);
                try
                {
                    AddFile(writer, source, "GPOCasebook.exe", "application/octet-stream");
                    AddFile(writer, source, "LICENSE.txt", "text/plain");
                    AddFile(writer, source, "Assets\\StoreLogo.png", "image/png");
                    AddFile(writer, source, "Assets\\Square44x44Logo.png", "image/png");
                    AddFile(writer, source, "Assets\\Square150x150Logo.png", "image/png");
                    var manifest = Open(Path.Combine(source, "AppxManifest.xml"), ReadOnly, false);
                    try { writer.Close(manifest); }
                    finally { Marshal.FinalReleaseComObject(manifest); }
                }
                finally { Marshal.FinalReleaseComObject(writer); }
            }
            finally { Marshal.FinalReleaseComObject(output); }

            var input = Open(temporary, ReadOnly, false);
            try
            {
                object reader;
                factory.CreatePackageReader(input, out reader);
                Marshal.FinalReleaseComObject(reader);
            }
            finally { Marshal.FinalReleaseComObject(input); }

            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally
        {
            if (uri != IntPtr.Zero) Marshal.Release(uri);
            Marshal.FinalReleaseComObject(factory);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void AddFile(IAppxPackageWriter writer, string source, string name, string type)
    {
        var input = Open(Path.Combine(source, name), ReadOnly, false);
        try { writer.AddPayloadFile(name, type, CompressionNormal, input); }
        finally { Marshal.FinalReleaseComObject(input); }
    }

    private static IStream Open(string path, uint mode, bool create)
    {
        IStream stream;
        Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, mode, 0, create, IntPtr.Zero, out stream));
        return stream;
    }
}
