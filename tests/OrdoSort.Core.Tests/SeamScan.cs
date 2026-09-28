using System.Text.RegularExpressions;

namespace OrdoSort.Core.Tests;

/// <summary>Reads the test sources to find which classes touch a
/// process-wide seam, so the collection-membership guards derive their class
/// set instead of keeping a list by hand. A hand list misses the class
/// nobody remembered to add, and that class then runs in parallel with the
/// seam's owner and flakes (DW-83). First written for the Commit seam in
/// UndoFailureTests (QC-03, 2026-08-22), now shared by the AtomicPlace and
/// Unlock guards too.</summary>
internal static class SeamScan
{
    /// <summary>The xUnit collection a test class declares, or null. Reads
    /// the <c>[Collection("...")]</c> constructor argument via
    /// CustomAttributeData rather than CollectionAttribute.Name: robust
    /// against which xunit.core build resolves at compile time, and it's the
    /// constructor argument that xUnit's own discovery groups on.</summary>
    internal static string? CollectionNameOf(Type type) =>
        type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Xunit.CollectionAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    // A class declaration up to its opening brace. [^{]* covers a base/
    // interface list ("class Foo : IDisposable") without crossing into the
    // NEXT class — a base list can't itself contain a raw '{' in valid C#.
    private static readonly Regex ClassDeclaration =
        new(@"\bpublic\s+(?:sealed\s+)?class\s+(?<name>\w+)\b[^{]*\{", RegexOptions.Compiled);

    /// <summary>The distinct names of the top-level classes under
    /// tests/OrdoSort.Core.Tests whose own code (comments stripped) matches
    /// <paramref name="use"/>.
    ///
    /// Attributes each match to the CLOSEST class declaration textually
    /// before it, rather than brace-matching each class's closing brace. This
    /// project's test classes are always siblings in a file, never nested, so
    /// "nearest declaration above" identifies the owner. Brace-counting was
    /// tried first and broke on real fixture data: ConfigSplitTests.cs writes
    /// the malformed JSON literal "{ not json", an unmatched '{' inside a
    /// string that a depth counter can't tell from code without tokenizing
    /// every kind of C# string.</summary>
    internal static List<string> ClassesWhoseCodeMatches(Regex use)
    {
        var dir = Path.Combine(Repo.Root, "tests", "OrdoSort.Core.Tests");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
        {
            var source = StripComments(File.ReadAllText(file));
            var classStarts = ClassDeclaration.Matches(source)
                .Select(m => (Index: m.Index, Name: m.Groups["name"].Value))
                .OrderBy(x => x.Index)
                .ToList();
            foreach (Match match in use.Matches(source))
            {
                var owner = classStarts.LastOrDefault(c => c.Index <= match.Index);
                if (owner.Name is not null)
                    found.Add(owner.Name);
            }
        }
        return found.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Each of <paramref name="classNames"/> that does not declare
    /// <paramref name="collection"/>, described for a failure message.</summary>
    internal static List<string> NotInCollection(IEnumerable<string> classNames, string collection)
    {
        var assembly = typeof(SeamScan).Assembly;
        var outside = new List<string>();
        foreach (var name in classNames)
        {
            var type = assembly.GetTypes()
                .FirstOrDefault(t => t.Namespace == "OrdoSort.Core.Tests" && t.Name == name);
            if (type is null)
            {
                outside.Add($"{name} (found in the source, but no compiled type by that name in " +
                            "OrdoSort.Core.Tests — check for a typo in the class name)");
                continue;
            }
            var declared = CollectionNameOf(type);
            if (declared != collection)
                outside.Add($"{name} (in \"{declared ?? "<no [Collection] attribute>"}\")");
        }
        return outside;
    }

    // The test sources talk ABOUT the seams in prose, so the scan must not
    // match its own commentary. Strips block comments, then truncates every
    // line at its first "//" (which also removes "///" doc comments). Good
    // enough for this project's style: no file here puts "//" inside a
    // string literal on a line that also declares a class or uses a seam.
    private static string StripComments(string source)
    {
        var noBlocks = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var lines = noBlocks.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var slashes = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (slashes >= 0) lines[i] = lines[i][..slashes];
        }
        return string.Join('\n', lines);
    }
}
