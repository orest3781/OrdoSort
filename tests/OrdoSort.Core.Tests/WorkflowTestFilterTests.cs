using System.Text.RegularExpressions;
using OrdoSort.TestSupport;

namespace OrdoSort.Core.Tests;

/// <summary>The NetworkShare tests fail on purpose when the loopback share is
/// missing, and GitHub's runners never have it. So every GitHub workflow that
/// runs tests must leave them out, or a tag push fails before any zip is built
/// (the 1.8.0 release review caught release.yml running them).</summary>
public sealed class WorkflowTestFilterTests
{
    [Fact]
    public void EveryWorkflowTestRunLeavesOutTheNetworkShareTests()
    {
        var workflows = Directory.GetFiles(Path.Combine(Repo.Root, ".github", "workflows"), "*.yml");
        Assert.NotEmpty(workflows);

        var unfiltered = new List<string>();
        foreach (var file in workflows)
        {
            foreach (var command in TestCommands(File.ReadAllText(file)))
            {
                // A run that picks one category, or one named test, can't reach
                // the share tests; any other run has to exclude them.
                var picksOnly = Regex.IsMatch(command, @"--filter\s+""?(Category=(?!NetworkShare)|NonDeterministic)");
                var excludes = command.Contains("Category!=NetworkShare", StringComparison.Ordinal);
                if (!picksOnly && !excludes)
                    unfiltered.Add($"{Path.GetFileName(file)}: {command}");
            }
        }

        Assert.True(unfiltered.Count == 0,
            "These workflow test runs would start the NetworkShare tests:\n" + string.Join("\n", unfiltered));
    }

    /// <summary>Each <c>dotnet test</c> command, with YAML's folded
    /// continuation lines joined on, so a filter on the next line counts.</summary>
    private static IEnumerable<string> TestCommands(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var at = lines[i].IndexOf("dotnet test", StringComparison.Ordinal);
            if (at < 0)
                continue;
            var command = lines[i][at..].Trim();
            var indent = lines[i].Length - lines[i].TrimStart().Length;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var next = lines[j];
                var nextIndent = next.Length - next.TrimStart().Length;
                if (next.Trim().Length == 0 || nextIndent < indent || next.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                    || next.TrimStart().StartsWith("run:", StringComparison.Ordinal)
                    || next.TrimStart().StartsWith("name:", StringComparison.Ordinal))
                    break;
                command += " " + next.Trim();
            }
            yield return command;
        }
    }
}
