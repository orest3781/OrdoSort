using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>ConverterChain (Task 8): the no-silent-downgrade rule. A link
/// that CLAIMS an extension and then fails must not be quietly overruled by
/// the next one in line — only a link that never claimed the extension at
/// all is skipped.</summary>
public class ConverterChainTests
{
    /// <summary>Claims exactly one extension (or none at all, when
    /// <paramref name="handles"/> is null — models "this app isn't
    /// installed"), so a test can tell "tried and failed" apart from "never
    /// claimed it" without any real conversion work.</summary>
    private sealed class StubConverter : IDocumentConverter
    {
        private readonly string? _handles;
        public string Status = "ok";
        public int Calls;

        public StubConverter(string? handles) => _handles = handles;

        public bool Handles(string extension) =>
            _handles is not null && extension.Equals(_handles, StringComparison.OrdinalIgnoreCase);

        public ConversionResult ToPdf(byte[] source, string displayName,
            IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
        {
            Calls++;
            return Status == "ok"
                ? new("ok", new byte[] { 1 })
                : new(Status, null, "stub failure", displayName);
        }
    }

    private sealed class DisposableStubConverter : IDocumentConverter, IDisposable
    {
        public bool Disposed { get; private set; }
        public bool Handles(string extension) => false;
        public ConversionResult ToPdf(byte[] source, string displayName,
            IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask) =>
            new("unsupported", null);
        public void Dispose() => Disposed = true;
    }

    // ---- the brief's own two facts, verbatim -----------------------------

    [Fact]
    public void TheChainPrefersOfficeAndDoesNotDowngradeWhenItFails()
    {
        // Office present and failing must NOT fall through to the table
        // renderer: a lesser rendering of a document the user believes
        // converted properly is worse than a clear failure.
        var office = new StubConverter("xlsx") { Status = "error" };
        var fallback = new StubConverter("xlsx") { Status = "ok" };
        var result = new ConverterChain(office, fallback).ToPdf(new byte[] { 1 }, "a.xlsx", Array.Empty<string>(), null);
        Assert.Equal("error", result.Status);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public void TheChainUsesTheFallbackWhenOfficeDoesNotHandleTheTypeAtAll()
    {
        var office = new StubConverter(handles: null);      // Office absent
        var fallback = new StubConverter("xlsx") { Status = "ok" };
        Assert.Equal("ok", new ConverterChain(office, fallback)
            .ToPdf(new byte[] { 1 }, "a.xlsx", Array.Empty<string>(), null).Status);
    }

    // ---- supplementary facts ----------------------------------------------

    [Fact]
    public void HandlesIsTrueWhenAnyLinkHandlesEvenIfAnEarlierOneDoesNot()
    {
        var chain = new ConverterChain(new StubConverter("docx"), new StubConverter("xlsx"));
        Assert.True(chain.Handles("xlsx"));
        Assert.False(chain.Handles("pptx"));
    }

    [Fact]
    public void ToPdfIsUnsupportedWhenNoLinkHandlesTheExtensionAtAll()
    {
        var chain = new ConverterChain(new StubConverter("docx"));
        var result = chain.ToPdf(new byte[] { 1 }, "a.pptx", Array.Empty<string>(), null);
        Assert.Equal("unsupported", result.Status);
        Assert.Contains("a.pptx", result.Message);
    }

    /// <summary>The chain's own IDisposable cascades to every link that
    /// needs it — the mechanism MergePdfsViewModel.Dispose relies on to
    /// reach the OfficeConverter link buried inside its default chain
    /// without knowing it is there.</summary>
    [Fact]
    public void DisposeDisposesEveryDisposableLinkAndLeavesOthersAlone()
    {
        var disposable = new DisposableStubConverter();
        var plain = new StubConverter("docx");
        var chain = new ConverterChain(disposable, plain);

        chain.Dispose();

        Assert.True(disposable.Disposed);
    }

    /// <summary>A weak but real smoke test: RestorationWarnings aggregates
    /// through an actual OfficeConverter link by TYPE, not a marker
    /// interface. It can only prove the trivial case without real Office and
    /// a forced restore failure (RestorationWarnings is only ever populated
    /// by OfficeConverter.Dispose, called here with nothing ever converted
    /// or borrowed) — proving it stays empty when nothing went wrong is
    /// still worth pinning: it is what tells a future change to this
    /// aggregation "you broke the wiring" even without an Office-dependent
    /// fixture.</summary>
    [Fact]
    public void RestorationWarningsAggregatesFromOfficeConverterLinksByType()
    {
        using var office = new OfficeConverter();
        var chain = new ConverterChain(office, new StubConverter(handles: null));

        Assert.Empty(chain.RestorationWarnings);

        chain.Dispose();
        Assert.Empty(chain.RestorationWarnings);   // nothing was ever started or borrowed
    }
}
