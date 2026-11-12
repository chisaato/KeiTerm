using System.Diagnostics;
using Kei.Term.App.Services;

namespace Kei.Term.Tests;

[CollectionDefinition(nameof(MacEditorLaunchTests), DisableParallelization = true)]
public sealed class MacEditorLaunchCollection;

[Collection(nameof(MacEditorLaunchTests))]
public class MacEditorLaunchTests
{
    [PlatformFact(TestPlatform.MacOS)]
    public void CodeEntrypointsAndSymlinks_ResolveToOneApplicationWithoutNewInstances()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"keiterm-editor-{Guid.NewGuid():N}");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            foreach ((string bundleName, string cliName, string binaryName) in new[]
            {
                ("Visual Studio Code.app", "code", "Code"),
                ("Visual Studio Code - Insiders.app", "code-insiders", "Electron")
            })
            {
                string bundle = Path.Combine(directory, bundleName);
                string cli = Path.Combine(bundle, "Contents", "Resources", "app", "bin", cliName);
                string binary = Path.Combine(bundle, "Contents", "MacOS", binaryName);
                string link = Path.Combine(directory, cliName);
                Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
                Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
                File.WriteAllText(cli, "CLI fixture");
                File.WriteAllText(binary, "App fixture");
                File.CreateSymbolicLink(link, cli);
                Environment.SetEnvironmentVariable("PATH", directory);

                foreach (string selected in new[] { bundle, bundle + "/", cli, binary, link, cliName })
                {
                    foreach (string document in new[] { "first file.txt", "配置 \"quoted\".json" })
                    {
                        string file = Path.Combine(directory, document);
                        ProcessStartInfo start = FileEditorLauncher.CreateEditorStartInfo(file, selected);
                        Assert.Equal("/usr/bin/open", start.FileName);
                        Assert.Equal(new[] { "-W", "-a", bundle, file }, start.ArgumentList);
                        Assert.False(start.UseShellExecute);
                        Assert.Empty(start.Arguments);
                    }
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [PlatformFact(TestPlatform.MacOS)]
    public void StandaloneCodeWrapper_KeepsItsDocumentWaitProtocol()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"keiterm-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string command = Path.Combine(directory, "code");
            File.WriteAllText(command, "Standalone command fixture");
            string file = Path.Combine(directory, "document with spaces.txt");
            ProcessStartInfo start = FileEditorLauncher.CreateEditorStartInfo(file, command);
            Assert.Equal(command, start.FileName);
            Assert.Equal($"--wait \"{file}\"", start.Arguments);
            Assert.False(start.UseShellExecute);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
