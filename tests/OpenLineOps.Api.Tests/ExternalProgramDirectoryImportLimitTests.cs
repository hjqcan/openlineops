using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Projects.Api.Controllers;

namespace OpenLineOps.Api.Tests;

public sealed class ExternalProgramDirectoryImportLimitTests
{
    [Fact]
    public void ExactContentBoundaryIsAcceptedAndOneAdditionalByteIsRejected()
    {
        Assert.True(ExternalProgramDirectoryImportLimits.CanAccumulateContentBytes(
            ExternalProgramDirectoryImportLimits.MaximumContentBytes
                - ExternalProgramDirectoryImportLimits.MaximumFileBytes,
            ExternalProgramDirectoryImportLimits.MaximumFileBytes));
        Assert.False(ExternalProgramDirectoryImportLimits.CanAccumulateContentBytes(
            ExternalProgramDirectoryImportLimits.MaximumContentBytes,
            1));
        Assert.False(ExternalProgramDirectoryImportLimits.CanAccumulateContentBytes(-1, 1));
        Assert.False(ExternalProgramDirectoryImportLimits.CanAccumulateContentBytes(0, -1));
    }

    [Fact]
    public void MultipartRequestLimitHasIndependentBoundedMetadataAllowance()
    {
        var method = typeof(ExternalProgramResourcesController).GetMethod(
            nameof(ExternalProgramResourcesController.ImportDirectoryAsync),
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var requestSize = Assert.Single(
            method.GetCustomAttributesData(),
            attribute => attribute.AttributeType == typeof(RequestSizeLimitAttribute));
        var formLimits = Assert.Single(method.GetCustomAttributes<RequestFormLimitsAttribute>());

        Assert.Equal(
            ExternalProgramDirectoryImportLimits.MaximumRequestBytes,
            Assert.IsType<long>(Assert.Single(requestSize.ConstructorArguments).Value));
        Assert.Equal(
            ExternalProgramDirectoryImportLimits.MaximumRequestBytes,
            formLimits.MultipartBodyLengthLimit);
        Assert.True(
            ExternalProgramDirectoryImportLimits.MaximumRequestBytes
                > ExternalProgramDirectoryImportLimits.MaximumContentBytes);
        Assert.Equal(
            ExternalProgramDirectoryImportLimits.MaximumRequestMetadataBytes,
            ExternalProgramDirectoryImportLimits.MaximumRequestBytes
                - ExternalProgramDirectoryImportLimits.MaximumContentBytes);
        Assert.InRange(
            ExternalProgramDirectoryImportLimits.MaximumRequestMetadataBytes,
            1,
            8L * 1024 * 1024);
    }

    [Fact]
    public void FormValueLimitAcceptsWorstCaseCanonicalManifestMetadata()
    {
        var segment = new string('a', 250);
        var maximumPath = $"files/{segment}/{segment}/{segment}/{segment}/0123456789abcd";
        Assert.Equal(1_024, maximumPath.Length);
        var manifest = Enumerable.Range(0, ExternalProgramDirectoryImportLimits.MaximumFileCount)
            .Select(index => new
            {
                fieldName = $"file-{index + 1}",
                resourceRelativePath = maximumPath,
                sizeBytes = ExternalProgramDirectoryImportLimits.MaximumFileBytes,
                sha256 = new string('a', 64)
            })
            .ToArray();
        var serializedBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(manifest));

        Assert.InRange(serializedBytes, 1, ExternalProgramDirectoryImportLimits.MaximumFormValueBytes);
        Assert.True(
            ExternalProgramDirectoryImportLimits.MaximumFormValueBytes
                < ExternalProgramDirectoryImportLimits.MaximumRequestMetadataBytes);
    }
}
