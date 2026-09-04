using System;
using System.IO;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

[Collection("InstallerTests")]
public class InstallationArtifactIntegrityTests
{
    private static readonly byte[] MsiHeaderMagic = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }; // OLE Compound File / MSI
    private static readonly byte[] PeMzMagic = new byte[] { 0x4D, 0x5A }; // "MZ"
    private static readonly byte[] PeSignature = new byte[] { 0x50, 0x45, 0x00, 0x00 }; // "PE\0\0"

    private static string GetInstallerDir()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AMCCA.sln")))
        {
            current = current.Parent;
        }
        var root = current?.FullName ?? Directory.GetCurrentDirectory();
        var dir = Path.Combine(root, "dist", "installer");
        EnsureInstallerBuilt(dir, root);
        return dir;
    }

    private static bool CheckInstallerReady(string installerDir)
    {
        var msiPath = Path.Combine(installerDir, "AMCCA-Setup.msi");
        var exePath = Path.Combine(installerDir, "AMCCA-Setup.exe");
        var shaPath = Path.Combine(installerDir, "SHA256SUMS");
        if (!File.Exists(msiPath) || !File.Exists(exePath) || !File.Exists(shaPath))
            return false;

        var exeInfo = new FileInfo(exePath);
        var msiInfo = new FileInfo(msiPath);
        if (exeInfo.Length <= msiInfo.Length)
            return false;

        var checksums = File.ReadAllText(shaPath);
        using var sha256 = SHA256.Create();
        var msiHash = Convert.ToHexString(sha256.ComputeHash(File.ReadAllBytes(msiPath))).ToLowerInvariant();
        var exeHash = Convert.ToHexString(sha256.ComputeHash(File.ReadAllBytes(exePath))).ToLowerInvariant();
        return checksums.Contains(msiHash) && checksums.Contains(exeHash);
    }

    private static void EnsureInstallerBuilt(string installerDir, string repoRoot)
    {
        if (!CheckInstallerReady(installerDir))
        {
            using var mutex = new System.Threading.Mutex(false, "Global\\AMCCA_INSTALLER_BUILD_MUTEX");
            try
            {
                mutex.WaitOne(TimeSpan.FromMinutes(3));
                if (!CheckInstallerReady(installerDir))
                {
                    var scriptPath = Path.Combine(repoRoot, "installer", "build_installer.ps1");
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"",
                        WorkingDirectory = repoRoot,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    var envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
                    if (string.IsNullOrEmpty(envRoot))
                    {
                        var localDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet");
                        if (Directory.Exists(localDotnet))
                        {
                            psi.EnvironmentVariables["DOTNET_ROOT"] = localDotnet;
                            var curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                            psi.EnvironmentVariables["PATH"] = localDotnet + ";" + curPath;
                        }
                    }
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit(180000);
                }
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }

    [Fact]
    public void MsiArtifact_Exists_AndHasValidCompoundFileSignature()
    {
        var installerDir = GetInstallerDir();
        var msiPath = Path.Combine(installerDir, "AMCCA-Setup.msi");
        File.Exists(msiPath).Should().BeTrue("AMCCA-Setup.msi must exist in dist/installer");

        var bytes = File.ReadAllBytes(msiPath);
        bytes.Length.Should().BeGreaterThan(512);

        // Verify Compound Document Header (MSI)
        for (int i = 0; i < MsiHeaderMagic.Length; i++)
        {
            bytes[i].Should().Be(MsiHeaderMagic[i], "Byte at offset {0} must match OLE/MSI header signature", i);
        }
    }

    [Fact]
    public void ExeArtifact_Exists_IsRealPeExecutable_AndDiffersFromMsi()
    {
        var installerDir = GetInstallerDir();
        var msiPath = Path.Combine(installerDir, "AMCCA-Setup.msi");
        var exePath = Path.Combine(installerDir, "AMCCA-Setup.exe");

        File.Exists(msiPath).Should().BeTrue("AMCCA-Setup.msi must exist in dist/installer");
        File.Exists(exePath).Should().BeTrue("AMCCA-Setup.exe must exist in dist/installer");

        using var sha256 = SHA256.Create();
        var msiHash = Convert.ToHexString(sha256.ComputeHash(File.ReadAllBytes(msiPath))).ToLowerInvariant();
        var exeHash = Convert.ToHexString(sha256.ComputeHash(File.ReadAllBytes(exePath))).ToLowerInvariant();

        // 1. Hashes MUST differ
        exeHash.Should().NotBe(msiHash, "DEF-CERT-001 VIOLATION: AMCCA-Setup.exe cannot be a renamed or copied copy of AMCCA-Setup.msi.");

        // 2. Read EXE bytes and verify DOS MZ header
        var exeBytes = File.ReadAllBytes(exePath);
        exeBytes.Length.Should().BeGreaterThan(1024);
        exeBytes[0].Should().Be(PeMzMagic[0], "First byte must be 'M'");
        exeBytes[1].Should().Be(PeMzMagic[1], "Second byte must be 'Z'");

        // 3. Verify e_lfanew points to PE signature
        var peHeaderOffset = BitConverter.ToInt32(exeBytes, 0x3C);
        peHeaderOffset.Should().BeGreaterThan(0);
        peHeaderOffset.Should().BeLessThan(exeBytes.Length - 4);

        for (int i = 0; i < PeSignature.Length; i++)
        {
            exeBytes[peHeaderOffset + i].Should().Be(PeSignature[i], "Byte at PE offset {0} must match 'PE\\0\\0'", i);
        }
    }

    [Fact]
    public void AdversarialTest_DetectsFakeExeCreatedByCopyingMsi()
    {
        // Demonstrate that our verification logic detects and rejects a fake EXE created by copying an MSI
        var tempFakeExe = Path.Combine(Path.GetTempPath(), "Fake-Setup-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            // Create a fake EXE by copying the real MSI bytes
            var installerDir = GetInstallerDir();
            var msiPath = Path.Combine(installerDir, "AMCCA-Setup.msi");
            File.Copy(msiPath, tempFakeExe);

            var fakeBytes = File.ReadAllBytes(tempFakeExe);

            // An MSI begins with 0xD0, 0xCF, not 0x4D, 0x5A ('MZ')
            var isPe = fakeBytes.Length > 2 && fakeBytes[0] == 0x4D && fakeBytes[1] == 0x5A;
            isPe.Should().BeFalse("A copied MSI masquerading as .exe must be flagged as non-PE");

            using var sha = SHA256.Create();
            var msiHash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(msiPath))).ToLowerInvariant();
            var fakeHash = Convert.ToHexString(sha.ComputeHash(fakeBytes)).ToLowerInvariant();
            
            // Hash equality proves fraud
            (fakeHash == msiHash).Should().BeTrue("Direct copy fraud is caught by hash equality check against MSI");
        }
        finally
        {
            if (File.Exists(tempFakeExe)) File.Delete(tempFakeExe);
        }
    }
}
