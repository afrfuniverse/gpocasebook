using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GPOCasebook
{
    internal static class PackageIdentity
    {
        private const int NoPackage = 15700;
        private const int InsufficientBuffer = 122;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder family);

        internal static string CurrentFamily()
        {
            uint length = 0;
            int status = GetCurrentPackageFamilyName(ref length, null);
            if (status == NoPackage) return null;
            if (status != InsufficientBuffer) throw new Win32Exception(status);
            var family = new StringBuilder((int)length);
            status = GetCurrentPackageFamilyName(ref length, family);
            if (status != 0) throw new Win32Exception(status);
            return family.ToString();
        }

        internal static bool IsValidFamily(string family)
        {
            return family != null && Regex.IsMatch(family, @"\A[A-Za-z0-9.-]{3,50}_[a-z0-9]{13}\z",
                RegexOptions.CultureInvariant);
        }

        internal static string ArchiveDirectory(string localData, string family)
        {
            if (family == null) return Path.Combine(localData, "GPOCasebook", "Archive");
            if (!IsValidFamily(family)) throw new ArgumentException("Invalid Windows package family.");
            return Path.Combine(localData, "Packages", family, "LocalState", "Archive");
        }
    }
}
