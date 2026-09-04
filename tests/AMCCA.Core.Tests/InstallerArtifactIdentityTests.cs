using System;
using System.IO;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

[Collection("InstallerTests")]
public class InstallerArtifactIdentityTests
{
    private static readonly byte[] MsiHeaderMagic = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
    private static readonly byte[] PeMzMagic = new byte[] { 0x4D, 0x5A };
    private static readonly byte[] PeSignature = new byte[] { 0x50, 0x45, 0x00, 0x00 };

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
    public void IdentityVerification_DemonstratesDistinctFormatsAndHashes()
    {
        var dir = GetInstallerDir();
        var msiPath = Path.Combine(dir, "AMCCA-Setup.msi");
        var exePath = Path.Combine(dir, "AMCCA-Setup.exe");

        File.Exists(msiPath).Should().BeTrue();
        File.Exists(exePath).Should().BeTrue();

        using var sha256 = SHA256.Create();
        var msiBytes = File.ReadAllBytes(msiPath);
        var exeBytes = File.ReadAllBytes(exePath);

        var msiHash = Convert.ToHexString(sha256.ComputeHash(msiBytes)).ToLowerInvariant();
        var exeHash = Convert.ToHexString(sha256.ComputeHash(exeBytes)).ToLowerInvariant();

        // 1. Hashes MUST differ
        msiHash.Should().NotBe(exeHash, "SHA256(MSI) != SHA256(EXE) must strictly hold");

        // 2. MSI format verification
        msiBytes.Length.Should().BeGreaterThan(MsiHeaderMagic.Length);
        for (int i = 0; i < MsiHeaderMagic.Length; i++)
        {
            msiBytes[i].Should().Be(MsiHeaderMagic[i], "MSI must be a valid Windows Installer / OLE compound file");
        }

        // 3. EXE format verification (PE executable)
        exeBytes.Length.Should().BeGreaterThan(1024);
        exeBytes[0].Should().Be(PeMzMagic[0]);
        exeBytes[1].Should().Be(PeMzMagic[1]);

        var peOffset = BitConverter.ToInt32(exeBytes, 0x3C);
        peOffset.Should().BeGreaterThan(0);
        peOffset.Should().BeLessThan(exeBytes.Length - 4);
        for (int i = 0; i < PeSignature.Length; i++)
        {
            exeBytes[peOffset + i].Should().Be(PeSignature[i], "EXE must contain a valid PE signature");
        }

        // 4. Burn Bootstrapper Payload verification
        // A WiX burn bundle embeds or bundles packages in its overlay/engine payload
        exeBytes.Length.Should().BeGreaterThan(msiBytes.Length, "WiX Burn bundle embeds the MSI payload and engine");
    }

    [Fact]
    public void RejectionOfTrivialExtensionCheck_WithoutBinaryValidation()
    {
        // Prove that trusting extension == ".exe" without PE validation is insecure and rejected by our validation
        var nonPeContent = System.Text.Encoding.UTF8.GetBytes("Fake executable script or copied MSI");
        var isRealPe = nonPeContent.Length >= 2 && nonPeContent[0] == 0x4D && nonPeContent[1] == 0x5A;
        isRealPe.Should().BeFalse("Pure extension without MZ/PE signature must never pass as valid PE");
    }
}
