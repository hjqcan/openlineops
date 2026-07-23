using OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;

namespace OpenLineOps.Devices.Tests;

public sealed class ExternalProgramOutputInspectionTests
{
    [Theory]
    [InlineData(".result.json.0123456789abcdef0123456789abcdef.tmp")]
    [InlineData("nested/.result.json.0123456789ABCDEF0123456789ABCDEF.tmp")]
    public void AtomicWriteResidueRequiresPortableDestinationAndExactIdentifier(string relativePath)
    {
        Assert.True(ExternalProgramHost.IsIncompleteAtomicWriteResidue(relativePath));
    }

    [Theory]
    [InlineData("result.json.0123456789abcdef0123456789abcdef.tmp")]
    [InlineData(".result.json.0123456789abcdef0123456789abcde.tmp")]
    [InlineData(".result.json.0123456789abcdef0123456789abcdeg.tmp")]
    [InlineData(".result json.0123456789abcdef0123456789abcdef.tmp")]
    [InlineData("nested/.result.json.0123456789abcdef0123456789abcdef.tmp/child")]
    [InlineData("../.result.json.0123456789abcdef0123456789abcdef.tmp")]
    [InlineData("unexpected.tmp")]
    public void OtherNonCanonicalOutputIsNotAtomicWriteResidue(string relativePath)
    {
        Assert.False(ExternalProgramHost.IsIncompleteAtomicWriteResidue(relativePath));
    }

    [Fact]
    public void SnapshotInspectionRetriesAFileThatWasAtomicallyMoved()
    {
        var attempts = 0;

        var violation = ExternalProgramHost.InspectOutputDirectoryWithBoundedSnapshotRetries(
            () =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new FileNotFoundException("The temporary file was atomically moved.");
                }

                return null;
            });

        Assert.Null(violation);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotInspectionFailsClosedAfterBoundedNamespaceChurn(bool directoryMissing)
    {
        var attempts = 0;

        var violation = ExternalProgramHost.InspectOutputDirectoryWithBoundedSnapshotRetries(
            () =>
            {
                attempts++;
                throw directoryMissing
                    ? new DirectoryNotFoundException("The directory was renamed.")
                    : new FileNotFoundException("The file was renamed.");
            });

        Assert.Equal(4, attempts);
        Assert.Contains("output workspace could not be inspected", violation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotInspectionDoesNotRetryOtherInspectionFailures(bool unauthorized)
    {
        var attempts = 0;

        var violation = ExternalProgramHost.InspectOutputDirectoryWithBoundedSnapshotRetries(
            () =>
            {
                attempts++;
                throw unauthorized
                    ? new UnauthorizedAccessException("Access was denied.")
                    : new IOException("The file is locked.");
            });

        Assert.Equal(1, attempts);
        Assert.Contains("output workspace could not be inspected", violation, StringComparison.Ordinal);
    }
}
