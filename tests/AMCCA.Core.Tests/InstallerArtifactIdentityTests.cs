using System;
using System.IO;
using System.Security.Cryptography;
using AMCCA.Core.Packaging;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

[Collection("InstallerTests")]
public class InstallerArtifactIdentityTests
{
    private static readonly byte[] MsiHeaderMagic = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

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

    private static byte[] CreateSkeletonPe(ushort machine, ushort magic)
    {
        var bytes = new byte[512];
        bytes[0] = 0x4D; bytes[1] = 0x5A; // MZ
        int e_lfanew = 128;
        BitConverter.GetBytes(e_lfanew).CopyTo(bytes, 0x3C);

        // PE signature
        bytes[e_lfanew] = 0x50; bytes[e_lfanew + 1] = 0x45;

        // COFF header (e_lfanew + 4)
        int coff = e_lfanew + 4;
        BitConverter.GetBytes(machine).CopyTo(bytes, coff); // Machine
        BitConverter.GetBytes((ushort)3).CopyTo(bytes, coff + 2); // NumberOfSections = 3
        BitConverter.GetBytes((ushort)240).CopyTo(bytes, coff + 16); // SizeOfOptionalHeader = 240

        // Optional header (coff + 20)
        int opt = coff + 20;
        BitConverter.GetBytes(magic).CopyTo(bytes, opt); // Magic
        BitConverter.GetBytes((ulong)0x00400000).CopyTo(bytes, opt + 24); // ImageBase
        BitConverter.GetBytes((uint)4096).CopyTo(bytes, opt + 32); // SectionAlignment
        BitConverter.GetBytes((uint)512).CopyTo(bytes, opt + 36); // FileAlignment
        BitConverter.GetBytes((uint)65536).CopyTo(bytes, opt + 56); // SizeOfImage

        return bytes;
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

        // 3. EXE format verification using PeBinaryValidator
        var peResult = PeBinaryValidator.Validate(exeBytes);
        peResult.IsValid.Should().BeTrue(peResult.FailureReason);
        peResult.Machine.Should().Be(0x8664);
        peResult.Magic.Should().Be(0x020B);

        // 4. Burn Bootstrapper Payload verification
        exeBytes.Length.Should().BeGreaterThan(msiBytes.Length, "WiX Burn bundle embeds the MSI payload and engine");
    }

    // --- DEF-CERT-001: 10 MANDATORY ADVERSARIAL TESTS (Section 5.2) ---

    [Fact]
    public void Test01_MsiRenamedAsExe_IsRejected()
    {
        var dir = GetInstallerDir();
        var msiBytes = File.ReadAllBytes(Path.Combine(dir, "AMCCA-Setup.msi"));
        var result = PeBinaryValidator.Validate(msiBytes);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("Invalid DOS header");
    }

    [Fact]
    public void Test02_EmptyFile_IsRejected()
    {
        var result = PeBinaryValidator.Validate(Array.Empty<byte>());
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("File too small");
    }

    [Fact]
    public void Test03_FileWithMz_ButNoPe_IsRejected()
    {
        var bytes = new byte[128];
        bytes[0] = 0x4D; bytes[1] = 0x5A; // MZ
        BitConverter.GetBytes(64).CopyTo(bytes, 0x3C);
        var result = PeBinaryValidator.Validate(bytes);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("Invalid PE signature");
    }

    [Fact]
    public void Test04_MzAndCorruptPeSignature_IsRejected()
    {
        var bytes = new byte[128];
        bytes[0] = 0x4D; bytes[1] = 0x5A; // MZ
        BitConverter.GetBytes(64).CopyTo(bytes, 0x3C);
        bytes[64] = (byte)'X'; bytes[65] = (byte)'X'; bytes[66] = (byte)'X'; bytes[67] = (byte)'X';
        var result = PeBinaryValidator.Validate(bytes);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("Invalid PE signature");
    }

    [Fact]
    public void Test05_Pe32InsteadOfPe32Plus_IsRejected()
    {
        var bytes = CreateSkeletonPe(machine: 0x8664, magic: 0x010B); // PE32
        var result = PeBinaryValidator.Validate(bytes);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("PE32 standard is rejected");
    }

    [Fact]
    public void Test06_PeArm64InsteadOfAmd64_IsRejected()
    {
        var bytes = CreateSkeletonPe(machine: 0xAA64, magic: 0x020B); // ARM64
        var result = PeBinaryValidator.Validate(bytes);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("0xAA64");
    }

    [Fact]
    public void Test07_ELfanewOutsideFile_IsRejected()
    {
        var bytes = new byte[128];
        bytes[0] = 0x4D; bytes[1] = 0x5A;
        BitConverter.GetBytes(10000).CopyTo(bytes, 0x3C);
        var result = PeBinaryValidator.Validate(bytes);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("outside file boundaries");
    }

    [Fact]
    public void Test08_PeSignatureDisplacedIncorrectly_IsRejected()
    {
        var bytes = new byte[256];
        bytes[0] = 0x4D; bytes[1] = 0x5A;
        BitConverter.GetBytes(100).CopyTo(bytes, 0x3C); // points to 100
        bytes[120] = 0x50; bytes[121] = 0x45; // placed at 120 instead
        var result = PeBinaryValidator.Validate(bytes);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("Invalid PE signature");
    }

    [Fact]
    public void Test09_TruncatedOptionalHeader_IsRejected()
    {
        var fullSkeleton = CreateSkeletonPe(machine: 0x8664, magic: 0x020B);
        // e_lfanew = 128, coffOffset = 132, optOffset = 152. Truncate at optOffset + 50 (< 112 required)
        var truncated = new byte[152 + 50];
        Array.Copy(fullSkeleton, truncated, truncated.Length);
        var result = PeBinaryValidator.Validate(truncated);
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Contain("Truncated");
    }

    [Fact]
    public void Test10_RealWixBurnArtifact_IsValidAmd64Pe32Plus()
    {
        var dir = GetInstallerDir();
        var exePath = Path.Combine(dir, "AMCCA-Setup.exe");
        var exeBytes = File.ReadAllBytes(exePath);

        var result = PeBinaryValidator.Validate(exeBytes);
        result.IsValid.Should().BeTrue(result.FailureReason);
        result.Machine.Should().Be(0x8664, "Must be AMD64");
        result.Magic.Should().Be(0x020B, "Must be PE32+ (64-bit)");
        result.NumberOfSections.Should().BeGreaterThan(0);
        result.ImageBase.Should().BeGreaterThan(0);
        result.SectionAlignment.Should().BeGreaterThan(0);
        result.SizeOfImage.Should().BeGreaterThan(0);
    }
}
