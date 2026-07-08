using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Maliev.QuoteEngine.Tests;

/// <summary>
/// Runs the behavioral Node.js tests for the composer "+" upload picker as part
/// of <c>dotnet test</c>. Those tests execute the real
/// quote-agent-composer.js listener lifecycle against a tiny fake DOM and assert
/// that a click inside the upload menu keeps the menu open (so "+ -> 3D files"
/// opens the native file picker) even after the composer is recreated in place.
///
/// This is deliberately NOT a source-string assertion: the picker regressed five
/// times behind pins that only checked that strings existed. Node ships on the CI
/// runners and is already required for the client's esbuild build, so a missing
/// node is treated as a hard failure rather than a silent skip.
/// </summary>
public sealed class ComposerUploadMenuJsTests
{
    [Fact]
    public void Composer_upload_menu_behaviour_passes_node_tests()
    {
        var repoRoot = FindRepoRoot();
        var clientDir = Path.Combine(repoRoot, "Maliev.QuoteEngine.Client");
        var testFile = Path.Combine(clientDir, "js-tests", "composer-upload-menu.test.mjs");
        Assert.True(File.Exists(testFile), $"Missing JS test file: {testFile}");

        var (exitCode, output, launchFailure) = RunNode($"--test \"{testFile}\"", clientDir);

        Assert.True(
            launchFailure is null,
            $"Could not launch 'node' to run the composer upload-menu tests. Node.js is required " +
            $"(it already builds the client via esbuild). Failure: {launchFailure}");

        Assert.True(
            exitCode == 0,
            $"Composer upload-menu Node.js tests failed (exit {exitCode}). Output:\n{output}");
    }

    private static (int ExitCode, string Output, string? LaunchFailure) RunNode(string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var output = new StringBuilder();
        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, output.ToString(), "node test run timed out after 120s");
        }

        process.WaitForExit();
        return (process.ExitCode, output.ToString(), null);
    }

    private static string FindRepoRoot()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("MALIEV_QUOTEENGINE_REPO_ROOT"),
            GetSourceDirectory(),
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var start in candidates)
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.slnx"))
                    && Directory.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.Client")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Maliev.QuoteEngine repository root.");
    }

    private static string GetSourceDirectory([CallerFilePath] string sourceFile = "")
        => Path.GetDirectoryName(sourceFile) ?? Directory.GetCurrentDirectory();
}
